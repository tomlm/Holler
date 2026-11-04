// Holler WebRTC engine. One engine per HollerClient; peers are addressed by an opaque key
// chosen by .NET. Negotiation follows the WebRTC "perfect negotiation" pattern.

const LOCAL = 'local';

class Engine {
    constructor(dotnet, iceServers) {
        this.dotnet = dotnet;
        this.setIceServers(iceServers);
        this.localStream = null;
        this.peers = new Map();   // key -> peer
        this.streams = new Map(); // key -> MediaStream
        this.views = new Map();   // key -> Set<HTMLMediaElement>
    }

    // Applies to peer connections created afterwards (e.g. refreshed TURN credentials).
    setIceServers(iceServers) {
        this.iceServers = (iceServers || []).map(s => {
            const server = { urls: s.urls };
            if (s.username) server.username = s.username;
            if (s.credential) server.credential = s.credential;
            return server;
        });
    }

    async startMedia(audio, video) {
        if (!this.localStream) {
            try {
                this.localStream = await navigator.mediaDevices.getUserMedia({ audio, video });
            } catch (e) {
                if (!(audio && video)) return { ok: false, audio: false, video: false, error: `${e.name}: ${e.message}` };
                // Fall back to audio only (no camera, or camera denied).
                try {
                    this.localStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
                } catch (e2) {
                    return { ok: false, audio: false, video: false, error: `${e.name}: ${e.message}` };
                }
            }
            this._setStream(LOCAL, this.localStream);
            for (const peer of this.peers.values()) this._addLocalTracks(peer);
        }
        return {
            ok: true,
            audio: this.localStream.getAudioTracks().length > 0,
            video: this.localStream.getVideoTracks().length > 0,
            error: null
        };
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
        element.autoplay = true;
        element.playsInline = true;
        this._bind(element, this.streams.get(key) || null);
    }

    detach(element, key) {
        if (!element) return;
        this.views.get(key)?.delete(element);
        element.srcObject = null;
    }

    createPeer(key, polite, initiator) {
        if (this.peers.has(key)) return;
        const pc = new RTCPeerConnection({ iceServers: this.iceServers });
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
                console.error('holler: negotiation failed', e);
            } finally {
                peer.makingOffer = false;
            }
        };
        pc.onconnectionstatechange = () => this._notify('OnPeerState', key, pc.connectionState);

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
        for (const key of [...this.peers.keys()]) this.closePeer(key);
        this.stopMedia();
        this.views.clear();
        this.dotnet = null;
    }

    _ready(peer, initiator) {
        if (peer.ready) return;
        peer.ready = true;
        const dc = peer.pc.createDataChannel('holler', { negotiated: true, id: 0 });
        dc.onmessage = e => this._notify('OnData', peer.key, e.data);
        dc.onopen = () => this._notify('OnPeerState', peer.key, 'datachannel-open');
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

    async _addIce(peer, candidate) {
        try {
            await peer.pc.addIceCandidate(candidate);
        } catch (e) {
            if (!peer.ignoreOffer) console.warn('holler: failed to add ICE candidate', e);
        }
    }

    _setStream(key, stream) {
        if (stream) this.streams.set(key, stream);
        else this.streams.delete(key);
        for (const element of this.views.get(key) || []) this._bind(element, stream);
    }

    _bind(element, stream) {
        if (element.srcObject !== stream) element.srcObject = stream;
        if (stream) element.play?.().catch(() => { /* autoplay blocked until user gesture */ });
    }

    _notify(method, ...args) {
        this.dotnet?.invokeMethodAsync(method, ...args).catch(e => console.warn(`holler: ${method} failed`, e));
    }
}

export function createEngine(dotnet, iceServers) {
    return new Engine(dotnet, iceServers);
}
