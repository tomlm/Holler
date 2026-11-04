// Blazor JS initializer: loads the Holler stylesheet so apps need no extra <link>.
function addStylesheet() {
    if (document.querySelector('link[data-holler]')) return;
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = new URL('holler.css', import.meta.url).href;
    link.dataset.holler = '';
    document.head.appendChild(link);
}

export function beforeWebStart() { addStylesheet(); }
export function beforeStart() { addStylesheet(); }
