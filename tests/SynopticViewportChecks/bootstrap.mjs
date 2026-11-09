import { create, boundsOf } from '/viewport.js';
const elements = JSON.parse(document.querySelector('#geometry').textContent);
const calls = [], failures = [];
window.addEventListener('error', e => failures.push(e.message));
window.addEventListener('unhandledrejection', e => failures.push(String(e.reason)));
let controller;
if (new URL(location.href).searchParams.get('mode') === 'viewer') {
    document.querySelector('.monitor-page-content').classList.add('sinoptico-viewer-layout');
    document.querySelector('#fixture-cards').classList.add('failures-top-panel');
}
controller = create(document.querySelector('.synoptic-viewport'), elements, new URL(location.href).searchParams.get('mode') !== 'viewer', {
    async invokeMethodAsync(method, value) {
        calls.push({ method, value });
        if (method === 'CommitEdit') {
            Object.assign(elements.find(e => e.id === value.id), value);
            controller.update(elements.map(e => ({ ...e })));
        }
    }
});
window.viewportCheck = {
    controller, elements, calls, failures, bounds: () => boundsOf(elements),
    state() {
        const root = document.querySelector('.synoptic-viewport'), scroll = root.querySelector('.synoptic-scroll');
        const canvas = root.querySelector('.synoptic-canvas'), transform = new DOMMatrix(getComputedStyle(canvas).transform);
        const rect = root.getBoundingClientRect();
        return { scale: transform.a, width: rect.width, height: rect.height, top: rect.top,
            clientWidth: scroll.clientWidth, clientHeight: scroll.clientHeight, left: scroll.scrollLeft, scrollTop: scroll.scrollTop,
            scrollWidth: scroll.scrollWidth, scrollHeight: scroll.scrollHeight, expanded: root.classList.contains('is-expanded'),
            documentWidth: document.documentElement.scrollWidth, windowWidth: window.innerWidth,
            calls: calls.map(c => ({ ...c })), failures: [...failures] };
    }
};
