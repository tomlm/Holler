// Holler WebRTC engine. One engine per HollerClient; peers are addressed by an opaque key
// chosen by .NET. Negotiation follows the WebRTC "perfect negotiation" pattern.

const LOCAL = 'local';

class Engine {
    constructor(dotnet, iceConfiguration) {
        this.dotnet = dotnet;
        this.setIceConfiguration(iceConfiguration);
        this.localStream = null;
        this.peers = new Map();   // key -> peer
        this.streams = new Map(); // key -> MediaStream
        this.views = new Map();   // key -> Set<HTMLMediaElement>
        // Upper bounds for our outgoing video; each viewer is sent only what it renders (see _applyEncoding).
        this.videoSettings = { maxWidth: 1280, maxHeight: 720, maxFrameRate: 30, maxBitrateKbps: 1500 };

        // Watch how big each peer's video is drawn here, and tell that peer (see _reportViews).
        this.resizeObserver = new ResizeObserver(() => this._scheduleViewReport());
        this.onDevicePixelRatioChange = () => this._scheduleViewReport();
        window.addEventListener('resize', this.onDevicePixelRatioChange);

        // Browsers may block audible autoplay until the user interacts with the page (e.g. when a room is
        // joined on page load). Retry any paused media on the next click or key press.
        this.resumePlayback = () => {
            for (const set of this.views.values())
                for (const element of set)
                    if (element.srcObject && element.paused) element.play?.().catch(() => { });
        };
        // Touch screens only grant playback permission when the finger lifts (pointerup/touchend/click),
        // mice on pointerdown; listen for all of them.
        this.resumeEvents = ['pointerdown', 'pointerup', 'touchend', 'click', 'keydown'];
        for (const type of this.resumeEvents)
            document.addEventListener(type, this.resumePlayback, { capture: true, passive: true });
        this.blockedPlayback = new WeakSet();
        this._log('info', `browser: ${navigator.userAgent}`);
    }

    // Applies to peer connections created afterwards (e.g. refreshed TURN credentials).
    setIceConfiguration(configuration) {
        this.iceServers = (configuration?.iceServers || []).map(s => {
            const server = { urls: s.urls };
            if (s.username) server.username = s.username;
            if (s.credential) server.credential = s.credential;
            return server;
        });
        this.relayOnly = !!configuration?.relayOnly;
    }

    async startMedia(audio, video, videoSettings) {
        if (videoSettings) this.videoSettings = { ...this.videoSettings, ...videoSettings };
        let failure = null;
        if (!this.localStream) {
            try {
                this.localStream = await navigator.mediaDevices.getUserMedia({
                    audio: audio && this._preferred('audio'),
                    video: video && { ...this._preferredObject('video'), ...this._videoConstraints() },
                });
            } catch (e) {
                failure = e;
                if (!(audio && video)) return this._mediaResult(failure);
                // Fall back to audio only (no camera, or camera denied).
                try {
                    this.localStream = await navigator.mediaDevices.getUserMedia({ audio: this._preferred('audio'), video: false });
                } catch {
                    return this._mediaResult(failure);
                }
            }
            this._setStream(LOCAL, this.localStream);
            for (const peer of this.peers.values()) this._addLocalTracks(peer);
        }
        return this._mediaResult(failure);
    }

    // Adds whichever requested kinds are missing (e.g. the camera after the user unblocks it), without
    // disturbing what is already running. New tracks reach existing peers through renegotiation.
    async retryMedia(audio, video) {
        if (!this.localStream) return this.startMedia(audio, video);
        const missing = [];
        if (audio && this.localStream.getAudioTracks().length === 0) missing.push('audio');
        if (video && this.localStream.getVideoTracks().length === 0) missing.push('video');
        let failure = null, added = 0;
        for (const kind of missing) {
            try {
                const constraints = kind === 'video'
                    ? { ...this._preferredObject('video'), ...this._videoConstraints() }
                    : this._preferred('audio');
                const stream = await navigator.mediaDevices.getUserMedia({ [kind]: constraints });
                this.localStream = new MediaStream([...this.localStream.getTracks(), ...stream.getTracks()]);
                added++;
            } catch (e) {
                failure = e;
            }
        }
        if (added > 0) {
            this._setStream(LOCAL, this.localStream);
            for (const peer of this.peers.values()) this._addLocalTracks(peer);
        }
        return this._mediaResult(failure);
    }

    _mediaResult(failure) {
        const audio = this.localStream?.getAudioTracks().length > 0;
        const video = this.localStream?.getVideoTracks().length > 0;
        const permissionDenied = failure?.name === 'NotAllowedError' || failure?.name === 'SecurityError';
        if (permissionDenied) this._watchPermissions();
        return {
            ok: !!this.localStream,
            audio, video,
            error: failure ? `${failure.name}: ${failure.message}` : null,
            permissionDenied,
        };
    }

    // Tell .NET when the user changes camera/microphone permission (e.g. switches a block to Allow in the
    // address bar), so it can retry without a reload. Not every browser can query these permissions.
    _watchPermissions() {
        if (this.watchingPermissions || !navigator.permissions?.query) return;
        this.watchingPermissions = true;
        for (const name of ['camera', 'microphone']) {
            navigator.permissions.query({ name }).then(status => {
                status.onchange = () => this._notify('OnMediaPermissionChanged', name, status.state);
            }).catch(() => { /* unsupported permission name */ });
        }
    }

    // Cameras and microphones. Labels are only filled in once the user has granted media permission.
    async listDevices() {
        const devices = await navigator.mediaDevices.enumerateDevices();
        return devices
            .filter(d => d.kind === 'videoinput' || d.kind === 'audioinput')
            .map(d => ({ deviceId: d.deviceId, kind: d.kind === 'videoinput' ? 'video' : 'audio', label: d.label }));
    }

    // The deviceId of the camera/microphone currently in use, or null.
    currentDevice(kind) {
        const track = this._localTrack(kind);
        return track?.getSettings().deviceId ?? null;
    }

    // Swaps the camera or microphone in place: every peer gets the new track via replaceTrack, so no
    // renegotiation is needed and the call carries on uninterrupted.
    async switchDevice(kind, deviceId) {
        if (!this.localStream) return { ok: false, error: 'Media not started' };
        const old = this._localTrack(kind);
        let track;
        try {
            const constraints = { deviceId: { exact: deviceId }, ...(kind === 'video' ? this._videoConstraints() : {}) };
            const stream = await navigator.mediaDevices.getUserMedia({ [kind]: constraints });
            track = stream.getTracks()[0];
        } catch (e) {
            return { ok: false, error: `${e.name}: ${e.message}` };
        }
        track.enabled = old ? old.enabled : true;

        for (const peer of this.peers.values()) {
            const sender = peer.pc.getSenders().find(s => s.track && s.track.kind === kind);
            if (sender) await sender.replaceTrack(track);
            else this._addLocalTracks(peer);
        }
        if (old) {
            this.localStream.removeTrack(old);
            old.stop();
        }
        this.localStream.addTrack(track);
        try { localStorage.setItem(`holler.device.${kind}`, deviceId); } catch { /* storage unavailable */ }
        // A fresh stream object makes video elements pick up the new camera reliably.
        this.localStream = new MediaStream(this.localStream.getTracks());
        this._setStream(LOCAL, this.localStream);
        // The new camera may capture at a different size, which changes the scaling for every viewer.
        if (kind === 'video') for (const peer of this.peers.values()) this._applyEncoding(peer);
        return { ok: true, error: null };
    }

    // Capture no more than the configured maximum; viewers then get scaled-down copies as they need.
    _videoConstraints() {
        const s = this.videoSettings;
        return {
            width: { ideal: s.maxWidth, max: s.maxWidth },
            height: { ideal: s.maxHeight, max: s.maxHeight },
            frameRate: { ideal: s.maxFrameRate, max: s.maxFrameRate },
        };
    }

    _preferredObject(kind) {
        const preferred = this._preferred(kind);
        return preferred === true ? {} : preferred;
    }

    // The device picked last time, as a preference (not exact), so a missing device falls back to the default.
    _preferred(kind) {
        let deviceId = null;
        try { deviceId = localStorage.getItem(`holler.device.${kind}`); } catch { /* storage unavailable */ }
        return deviceId ? { deviceId: { ideal: deviceId } } : true;
    }

    _localTrack(kind) {
        if (!this.localStream) return null;
        return (kind === 'audio' ? this.localStream.getAudioTracks() : this.localStream.getVideoTracks())[0] ?? null;
    }

    stopMedia() {
        if (!this.localStream) return;
        this.localStream.getTracks().forEach(t => t.stop());
        this.localStream = null;
        this._setStream(LOCAL, null);
    }

    setTrackEnabled(kind, enabled) {
        if (!this.localStream) return;
        const tracks = kind === 'audio' ? this.localStream.getAudioTracks() : this.localStream.getVideoTracks();
        tracks.forEach(t => t.enabled = enabled);
    }

    attach(element, key, muted) {
        if (!element) return;
        let set = this.views.get(key);
        if (!set) this.views.set(key, set = new Set());
        set.add(element);
        element.muted = !!muted;
        element.disablePictureInPicture = true;
        element.autoplay = true;
        element.playsInline = true;
        this._bind(element, this.streams.get(key) || null);
        if (element.tagName === 'VIDEO' && key !== LOCAL) this.resizeObserver.observe(element);
        this._scheduleViewReport();
    }

    detach(element, key) {
        if (!element) return;
        this.views.get(key)?.delete(element);
        element.srcObject = null;
        this.resizeObserver.unobserve(element);
        this._scheduleViewReport();
    }

    createPeer(key, polite, initiator) {
        if (this.peers.has(key)) return;
        const pc = new RTCPeerConnection({
            iceServers: this.iceServers,
            iceTransportPolicy: this.relayOnly ? 'relay' : 'all',
        });
        const peer = {
            key, pc, polite,
            makingOffer: false, ignoreOffer: false, settingRemoteAnswer: false,
            ready: false, dc: null, stream: new MediaStream(), pendingIce: []
        };
        this.peers.set(key, peer);

        pc.ontrack = ({ track }) => {
            if (!peer.stream.getTracks().includes(track)) peer.stream.addTrack(track);
            this._setStream(key, peer.stream);
            track.onended = () => peer.stream.removeTrack(track);
            this._notify('OnRemoteTrack', key, track.kind);
        };
        pc.onicecandidate = ({ candidate }) => {
            if (candidate) this._notify('OnLocalSignal', key, 'ice', JSON.stringify(candidate));
        };
        pc.onnegotiationneeded = async () => {
            try {
                peer.makingOffer = true;
                await pc.setLocalDescription();
                this._notify('OnLocalSignal', key, 'description', JSON.stringify(pc.localDescription));
            } catch (e) {
                this._log('warn', `${key} negotiation failed: ${e.name}: ${e.message}`);
            } finally {
                peer.makingOffer = false;
            }
        };
        pc.onconnectionstatechange = () => {
            // Encodings only exist once negotiated, so apply any size the viewer already asked for.
            if (pc.connectionState === 'connected') {
                this._applyEncoding(peer);
                this._reportRoute(peer);
            }
            this._notify('OnPeerState', key, pc.connectionState);
        };

        // The initiator sets up media and the data channel immediately, which triggers the offer.
        // The other side waits until the offer is applied so its tracks bind to the offered transceivers.
        if (initiator) this._ready(peer, true);
    }

    async signal(key, type, payload) {
        const peer = this.peers.get(key);
        if (!peer) return;
        const pc = peer.pc;
        if (type === 'description') {
            const description = JSON.parse(payload);
            const readyForOffer = !peer.makingOffer && (pc.signalingState === 'stable' || peer.settingRemoteAnswer);
            const offerCollision = description.type === 'offer' && !readyForOffer;
            peer.ignoreOffer = !peer.polite && offerCollision;
            if (peer.ignoreOffer) return;

            peer.settingRemoteAnswer = description.type === 'answer';
            await pc.setRemoteDescription(description);
            peer.settingRemoteAnswer = false;
            if (description.type === 'offer') {
                this._ready(peer, false);
                await pc.setLocalDescription();
                this._notify('OnLocalSignal', key, 'description', JSON.stringify(pc.localDescription));
            }
            for (const candidate of peer.pendingIce.splice(0)) await this._addIce(peer, candidate);
            // A renegotiation may have added our video sender (e.g. camera unblocked mid-call): size it.
            if (pc.signalingState === 'stable') this._applyEncoding(peer);
        } else if (type === 'ice') {
            const candidate = JSON.parse(payload);
            if (!pc.remoteDescription) peer.pendingIce.push(candidate);
            else await this._addIce(peer, candidate);
        }
    }

    sendData(key, text) {
        const dc = this.peers.get(key)?.dc;
        if (!dc || dc.readyState !== 'open') return false;
        dc.send(text);
        return true;
    }

    closePeer(key) {
        const peer = this.peers.get(key);
        if (!peer) return;
        this.peers.delete(key);
        try { peer.dc?.close(); } catch { }
        try { peer.pc.close(); } catch { }
        this._setStream(key, null);
    }

    dispose() {
        for (const type of this.resumeEvents)
            document.removeEventListener(type, this.resumePlayback, { capture: true });
        for (const key of [...this.peers.keys()]) this.closePeer(key);
        this.stopMedia();
        this.resizeObserver.disconnect();
        window.removeEventListener('resize', this.onDevicePixelRatioChange);
        clearTimeout(this.viewReportTimer);
        this.views.clear();
        this.dotnet = null;
    }

    _ready(peer, initiator) {
        if (peer.ready) return;
        peer.ready = true;
        const dc = peer.pc.createDataChannel('holler', { negotiated: true, id: 0 });
        dc.onmessage = e => {
            // Viewer size reports are handled here; everything else (chat, media state) goes to .NET.
            if (typeof e.data === 'string' && e.data.startsWith('{"hv":')) {
                try {
                    peer.remoteView = JSON.parse(e.data).hv;
                    this._applyEncoding(peer);
                } catch { /* ignore malformed */ }
                return;
            }
            this._notify('OnData', peer.key, e.data);
        };
        dc.onopen = () => {
            peer.reportedView = undefined; // (re)send our view size on a fresh channel
            this._scheduleViewReport();
            this._notify('OnPeerState', peer.key, 'datachannel-open');
        };
        peer.dc = dc;
        this._addLocalTracks(peer);
        if (initiator) {
            // Always offer to receive audio and video even when we have nothing to send.
            const kinds = new Set(peer.pc.getTransceivers().map(t => t.receiver.track.kind));
            for (const kind of ['audio', 'video'])
                if (!kinds.has(kind)) peer.pc.addTransceiver(kind, { direction: 'recvonly' });
        }
    }

    _addLocalTracks(peer) {
        if (!this.localStream || !peer.ready) return;
        const sending = new Set(peer.pc.getSenders().map(s => s.track).filter(t => t));
        for (const track of this.localStream.getTracks()) {
            if (sending.has(track)) continue;
            // Reuse a transceiver that is only receiving this kind, otherwise add a new one.
            const idle = peer.pc.getTransceivers().find(t =>
                !t.sender.track && t.receiver.track.kind === track.kind && t.direction !== 'stopped');
            if (idle) {
                idle.sender.replaceTrack(track);
                idle.sender.setStreams?.(this.localStream);
                idle.direction = 'sendrecv';
            } else {
                peer.pc.addTrack(track, this.localStream);
            }
        }
    }

    // ---- receiver-driven sizing ----
    // Each side reports, per peer, the largest size (in device pixels) it draws that peer's video at,
    // or 0x0 when it draws none. The sender then encodes for that viewer only: scaled down to that size,
    // bitrate scaled with the pixel count, or switched off entirely while nobody is looking.

    _scheduleViewReport() {
        if (this.viewReportTimer) return;
        this.viewReportTimer = setTimeout(() => {
            this.viewReportTimer = null;
            this._reportViews();
        }, 200);
    }

    _reportViews() {
        for (const peer of this.peers.values()) {
            if (peer.dc?.readyState !== 'open') continue;
            const view = this._renderedSize(peer.key);
            if (peer.reportedView && peer.reportedView[0] === view[0] && peer.reportedView[1] === view[1]) continue;
            peer.reportedView = view;
            peer.dc.send(JSON.stringify({ hv: view }));
        }
    }

    _renderedSize(key) {
        let width = 0, height = 0;
        for (const element of this.views.get(key) || []) {
            if (element.tagName !== 'VIDEO' || !element.isConnected) continue;
            width = Math.max(width, element.clientWidth);
            height = Math.max(height, element.clientHeight);
        }
        // Round up to 32 device pixels so small layout jitter doesn't cause re-encoding churn.
        const ratio = window.devicePixelRatio || 1;
        const round = n => n <= 0 ? 0 : Math.ceil(n * ratio / 32) * 32;
        return [round(width), round(height)];
    }

    _applyEncoding(peer) {
        // setParameters must not overlap on one sender, so chain the updates.
        peer.encodingUpdate = (peer.encodingUpdate || Promise.resolve()).then(async () => {
            const sender = peer.pc.getSenders().find(s => s.track?.kind === 'video');
            if (!sender) return;
            const parameters = sender.getParameters();
            const encoding = parameters.encodings?.[0];
            if (!encoding) return; // not negotiated yet; applied again on 'connected'

            const s = this.videoSettings;
            const view = peer.remoteView;
            if (view && (view[0] === 0 || view[1] === 0)) {
                encoding.active = false;
            } else {
                const captured = sender.track.getSettings();
                const width = captured.width || s.maxWidth, height = captured.height || s.maxHeight;
                // Until the viewer reports, send the configured maximum.
                const scale = view ? Math.max(1, Math.min(width / view[0], height / view[1])) : 1;
                encoding.active = true;
                encoding.scaleResolutionDownBy = scale;
                encoding.maxFramerate = s.maxFrameRate;
                encoding.maxBitrate = Math.round(Math.max(30, s.maxBitrateKbps / (scale * scale)) * 1000);
                // We already send only the size the viewer draws, so when bandwidth is short drop frames rather
                // than starting blurry and stepping resolution back up over many seconds.
                parameters.degradationPreference = 'maintain-resolution';
            }
            try {
                await sender.setParameters(parameters);
            } catch (e) {
                this._log('warn', `${peer.key} could not apply video encoding: ${e.name}: ${e.message}`);
            }
        });
    }

    async _addIce(peer, candidate) {
        try {
            await peer.pc.addIceCandidate(candidate);
        } catch (e) {
            if (!peer.ignoreOffer) this._log('warn', `${peer.key} failed to add ICE candidate: ${e.name}: ${e.message}`);
        }
    }

    _setStream(key, stream) {
        if (stream) this.streams.set(key, stream);
        else this.streams.delete(key);
        for (const element of this.views.get(key) || []) this._bind(element, stream);
    }

    _bind(element, stream) {
        if (element.srcObject !== stream) element.srcObject = stream;
        if (stream) element.play?.().catch(e => {
            // Usually autoplay policy: resumePlayback retries on the next tap, click or key press.
            if (this.blockedPlayback.has(element)) return;
            this.blockedPlayback.add(element);
            this._log('info', `${element.tagName.toLowerCase()} playback blocked until the user interacts: ${e.name}`);
        });
    }

    // How a connected peer is actually reached: "host"/"srflx" are direct, "relay" goes through TURN.
    async _reportRoute(peer) {
        try {
            const stats = await peer.pc.getStats();
            let pair = null;
            stats.forEach(report => {
                if (report.type === 'transport' && report.selectedCandidatePairId) pair = stats.get(report.selectedCandidatePairId);
            });
            if (!pair) stats.forEach(report => {
                if (report.type === 'candidate-pair' && report.nominated && report.state === 'succeeded') pair = report;
            });
            const local = pair && stats.get(pair.localCandidateId);
            const remote = pair && stats.get(pair.remoteCandidateId);
            if (!local || !remote) return;
            const protocol = local.relayProtocol || local.protocol || '';
            this._notify('OnPeerRoute', peer.key, local.candidateType, remote.candidateType, protocol);
        } catch (e) {
            this._log('warn', `${peer.key} could not read route: ${e.name}: ${e.message}`);
        }
    }

    // Diagnostics go to the server's log (HollerClient's ILogger), where they can be read after the fact.
    _log(level, message) {
        if (level === 'warn') console.warn('holler:', message);
        this._notify('OnLog', level, message);
    }

    _notify(method, ...args) {
        this.dotnet?.invokeMethodAsync(method, ...args).catch(e => console.warn(`holler: ${method} failed`, e));
    }
}

export function createEngine(dotnet, iceConfiguration) {
    return new Engine(dotnet, iceConfiguration);
}
