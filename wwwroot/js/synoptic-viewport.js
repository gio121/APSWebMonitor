// Coordinates in this module always refer to the original saved canvas, never CSS pixels.
const finite = (v, fallback = 0) => Number.isFinite(v) ? v : fallback;
const clamp = (v, min, max) => Math.max(min, Math.min(max, v));
export function boundsOf(elements) {
    if (!elements.length) return { x: 0, y: 0, width: 800, height: 500 };
    let x = Infinity, y = Infinity, right = -Infinity, bottom = -Infinity;
    for (const e of elements) {
        const w = Math.max(1, finite(e.width, 1)), h = Math.max(1, finite(e.height, 1));
        const a = finite(e.rotation) * Math.PI / 180;
        const rx = (Math.abs(Math.cos(a)) * w + Math.abs(Math.sin(a)) * h) / 2;
        const ry = (Math.abs(Math.sin(a)) * w + Math.abs(Math.cos(a)) * h) / 2;
        const cx = finite(e.x) + w / 2, cy = finite(e.y) + h / 2;
        const margin = Math.max(4, finite(e.strokeWidth, 1.5) / 2);
        x = Math.min(x, cx - rx - margin); y = Math.min(y, cy - ry - margin);
        right = Math.max(right, cx + rx + margin); bottom = Math.max(bottom, cy + ry + margin);
    }
    return { x, y, width: Math.max(1, right - x), height: Math.max(1, bottom - y) };
}
export function fitScale(bounds, width, height, padding = 24) {
    const sx = Math.max(1, width - padding * 2) / bounds.width;
    const sy = Math.max(1, height - padding * 2) / bounds.height;
    return Math.max(0.0001, Math.min(4, sx, sy));
}
export function toCanvas(x, y, scale, origin) { return { x: (x - origin.x) / scale, y: (y - origin.y) / scale }; }
export function editGeometry(start, dx, dy, resize) {
    if (!resize) return { id: start.id, x: Math.round(start.x + dx), y: Math.round(start.y + dy), width: start.width, height: start.height };
    const a = finite(start.rotation) * Math.PI / 180, c = Math.cos(a), s = Math.sin(a);
    const dw = dx * c + dy * s, dh = -dx * s + dy * c;
    const minimumWidth = start.proportionalScale ? Math.max(10, 10 * start.width / Math.max(1, start.height)) : 10;
    const width = Math.max(minimumWidth, Math.round(start.width + dw));
    const height = start.proportionalScale ? width * start.height / Math.max(1, start.width) : Math.max(10, Math.round(start.height + dh));
    // Keep the opposite rotated corner stationary while the box changes size.
    return { id: start.id, x: start.x + (start.width - width) / 2 + c * (width - start.width) / 2 - s * (height - start.height) / 2,
        y: start.y + (start.height - height) / 2 + s * (width - start.width) / 2 + c * (height - start.height) / 2, width, height };
}

export function create(root, initialElements, editable, dotnet) {
    const scroll = root.querySelector('.synoptic-scroll'), surface = root.querySelector('.synoptic-surface');
    const canvas = root.querySelector('.synoptic-canvas');
    const percentage = root.querySelector('.synoptic-percentage input'), abort = new AbortController(), signal = abort.signal;
    const padding = 24;
    let elements = initialElements, bounds = boundsOf(elements), scale = 1, origin = { x: 0, y: 0 };
    let mode = 'auto', metrics = null, frame = 0, disposed = false;
    let gesture = null, space = false, suppressClick = false, committing = false;
    const pointers = new Map();
    const listen = (target, name, fn, options = {}) => target.addEventListener(name, fn, { ...options, signal });
    const invoke = (method, value) => disposed ? Promise.resolve() : dotnet.invokeMethodAsync(method, value).catch(error => {
        if (!disposed) { console.error('Synoptic interaction failed', error); root.dispatchEvent(new CustomEvent('synoptic-error', { detail: error })); }
    });
    const center = () => toCanvas(scroll.scrollLeft + scroll.clientWidth / 2, scroll.scrollTop + scroll.clientHeight / 2, scale, origin);
    const localPoint = (clientX, clientY) => {
        const rect = scroll.getBoundingClientRect();
        return { x: clientX - rect.left - scroll.clientLeft, y: clientY - rect.top - scroll.clientTop };
    };
    const point = (clientX, clientY) => {
        const p = localPoint(clientX, clientY);
        return toCanvas(p.x + scroll.scrollLeft, p.y + scroll.scrollTop, scale, origin);
    };
    function paint(anchor = null, pixel = null) {
        const w = scroll.clientWidth, h = scroll.clientHeight;
        const width = Math.max(w, bounds.width * scale + padding * 2), height = Math.max(h, bounds.height * scale + padding * 2);
        origin = { x: Math.max(padding, (width - bounds.width * scale) / 2) - bounds.x * scale,
            y: Math.max(padding, (height - bounds.height * scale) / 2) - bounds.y * scale };
        surface.style.width = `${width}px`; surface.style.height = `${height}px`;
        canvas.style.width = `${Math.max(1, bounds.x + bounds.width)}px`;
        canvas.style.height = `${Math.max(1, bounds.y + bounds.height)}px`;
        canvas.style.left = `${origin.x}px`; canvas.style.top = `${origin.y}px`; canvas.style.transform = `scale(${scale})`;
        canvas.style.setProperty('--synoptic-inverse-scale', 1 / scale);
        const grid = canvas.querySelector('.grid-background');
        if (grid) Object.assign(grid.style, { left: `${bounds.x}px`, top: `${bounds.y}px`, width: `${bounds.width}px`, height: `${bounds.height}px`,
            backgroundPosition: `${-bounds.x}px ${-bounds.y}px` });
        if (anchor) {
            scroll.scrollLeft = origin.x + anchor.x * scale - (pixel?.x ?? w / 2);
            scroll.scrollTop = origin.y + anchor.y * scale - (pixel?.y ?? h / 2);
        } else { scroll.scrollLeft = 0; scroll.scrollTop = 0; }
        if (document.activeElement !== percentage) percentage.value = Math.round(scale * 1000) / 10;
        metrics = { w, h };
        root.dataset.ready = '';
    }
    function availableHeight() {
        let bottom = window.innerHeight - 12;
        // Fixed runtime bars reserve space only when they overlap this viewport horizontally.
        for (const bar of document.querySelectorAll('.session-player-global-bar, .session-player-minimized, .device-connection-status')) {
            // In the viewer the player floats over the drawing; it never resizes the scene.
            if (root.closest('.sinoptico-viewer-layout') && !bar.matches('.device-connection-status')) continue;
            const r = bar.getBoundingClientRect(), own = root.getBoundingClientRect();
            if (r.width && r.height && r.right > own.left && r.left < own.right && r.top > own.top)
                bottom = Math.min(bottom, r.top - 8);
        }
        return Math.max(200, bottom - root.getBoundingClientRect().top);
    }
    function layout() {
        frame = 0;
        if (disposed || gesture) return;
        const anchor = metrics ? toCanvas(scroll.scrollLeft + metrics.w / 2, scroll.scrollTop + metrics.h / 2, scale, origin) : null;
        const height = `${availableHeight()}px`;
        if (root.style.getPropertyValue('--synoptic-height') !== height)
            root.style.setProperty('--synoptic-height', height);
        root.closest('.editor-container')?.style.setProperty('--synoptic-panel-height', height);
        if (mode === 'auto') scale = fitScale(bounds, scroll.clientWidth, scroll.clientHeight);
        paint(mode === 'manual' ? anchor : null);
    }
    const schedule = () => { if (!frame && !disposed) frame = requestAnimationFrame(layout); };
    function zoom(next, pixel = { x: scroll.clientWidth / 2, y: scroll.clientHeight / 2 }) {
        if (gesture || committing) return;
        const anchor = toCanvas(pixel.x + scroll.scrollLeft, pixel.y + scroll.scrollTop, scale, origin);
        mode = 'manual'; scale = clamp(next, 0.1, 4); paint(anchor, pixel);
    }
    function action(name) {
        if (name === 'in' || name === 'out') zoom(scale * (name === 'in' ? 1.2 : 1 / 1.2));
        else if (name === 'center') {
            if (gesture || committing) return;
            mode = 'manual'; paint({ x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 });
        }
    }
    listen(root, 'click', e => { const button = e.target.closest('[data-view-action]'); if (button) action(button.dataset.viewAction); });
    function applyPercentage() {
        if (Number.isFinite(percentage.valueAsNumber)) zoom(percentage.valueAsNumber / 100);
        percentage.value = Math.round(scale * 1000) / 10;
    }
    listen(percentage, 'change', applyPercentage);
    listen(percentage, 'keydown', e => {
        if (e.key === 'Enter') { e.preventDefault(); applyPercentage(); }
        if (e.key === 'Escape') { percentage.value = Math.round(scale * 1000) / 10; percentage.blur(); }
    });
    listen(scroll, 'wheel', e => {
        if (!e.ctrlKey) return;
        e.preventDefault(); zoom(scale * Math.exp(-e.deltaY * 0.002), localPoint(e.clientX, e.clientY));
    }, { passive: false });
    function applyElement(element, geometry) {
        element.style.left = `${geometry.x}px`; element.style.top = `${geometry.y}px`;
        element.style.width = `${geometry.width}px`; element.style.height = `${geometry.height}px`;
    }
    function cancelEdit() { if (gesture?.kind === 'edit') applyElement(gesture.element, gesture.start); }
    listen(scroll, 'pointerdown', e => {
        if ((e.button !== 0 && e.button !== 1) || committing || (e.target.closest('button, input, select, textarea, a') && !space && e.button !== 1)) return;
        scroll.focus({ preventScroll: true });
        pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        scroll.setPointerCapture(e.pointerId);
        if (pointers.size === 2) {
            cancelEdit(); const [a, b] = [...pointers.values()];
            gesture = { kind: 'pinch', distance: Math.hypot(a.x - b.x, a.y - b.y), scale, anchor: point((a.x + b.x) / 2, (a.y + b.y) / 2) };
            mode = 'manual'; suppressClick = true; e.preventDefault(); return;
        }
        const element = e.target.closest('[data-synoptic-id]');
        const start = element && elements.find(g => g.id === element.dataset.synopticId);
        const pan = !editable || space || e.button === 1 || !start;
        gesture = pan ? { kind: 'pan', x: e.clientX, y: e.clientY, left: scroll.scrollLeft, top: scroll.scrollTop, moved: false }
            : { kind: 'edit', x: e.clientX, y: e.clientY, start: { ...start }, element, resize: !!e.target.closest('.resize-handle'), moved: false };
        if (!pan) { mode = 'manual'; invoke('Select', start.id); }
        scroll.classList.toggle('is-panning', pan); e.preventDefault();
    });
    listen(scroll, 'pointermove', e => {
        if (!pointers.has(e.pointerId) || !gesture) return;
        pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (gesture.kind === 'pinch') {
            if (pointers.size !== 2) return;
            const [a, b] = [...pointers.values()];
            scale = clamp(gesture.scale * Math.hypot(a.x - b.x, a.y - b.y) / Math.max(1, gesture.distance), 0.1, 4);
            paint(gesture.anchor, localPoint((a.x + b.x) / 2, (a.y + b.y) / 2)); return;
        }
        const dx = e.clientX - gesture.x, dy = e.clientY - gesture.y;
        if (Math.hypot(dx, dy) > 3) gesture.moved = true;
        if (gesture.kind === 'pan') { if (gesture.moved) mode = 'manual'; scroll.scrollLeft = gesture.left - dx; scroll.scrollTop = gesture.top - dy; }
        else if (gesture.moved) {
            gesture.result = editGeometry(gesture.start, dx / scale, dy / scale, gesture.resize);
            applyElement(gesture.element, gesture.result);
        }
    });
    function endPointer(e, canceled = false) {
        if (!pointers.has(e.pointerId)) return;
        pointers.delete(e.pointerId);
        if (scroll.hasPointerCapture(e.pointerId)) scroll.releasePointerCapture(e.pointerId);
        if (!gesture) return;
        if (gesture.kind === 'pinch') {
            if (pointers.size) return;
            gesture = null; scroll.classList.remove('is-panning'); schedule(); return;
        }
        const finished = gesture; gesture = null; scroll.classList.remove('is-panning');
        if (canceled) { if (finished.kind === 'edit') applyElement(finished.element, finished.start); schedule(); return; }
        suppressClick = finished.moved;
        if (finished.kind === 'edit' && finished.result) {
            Object.assign(elements.find(g => g.id === finished.start.id), finished.result);
            committing = true;
            invoke('CommitEdit', finished.result).finally(() => { committing = false; bounds = boundsOf(elements); schedule(); });
        } else if (editable && finished.kind === 'pan' && !finished.moved && !space) invoke('Select', null);
        schedule();
    }
    listen(scroll, 'pointerup', e => endPointer(e));
    listen(scroll, 'pointercancel', e => endPointer(e, true));
    listen(scroll, 'lostpointercapture', e => { if (pointers.has(e.pointerId)) endPointer(e, true); });
    listen(scroll, 'click', e => { if (suppressClick) { e.preventDefault(); e.stopImmediatePropagation(); suppressClick = false; } }, { capture: true });
    listen(scroll, 'keydown', e => {
        if (e.target.closest('input, textarea, select, [contenteditable="true"]')) return;
        if (e.code === 'Space') { space = true; e.preventDefault(); }
        if (e.key === '+' || e.key === '=') { e.preventDefault(); action('in'); }
        if (e.key === '-') { e.preventDefault(); action('out'); }
        if (e.key === '0') { e.preventDefault(); action('center'); }
        if (e.key === 'Escape') { cancelEdit(); pointers.clear(); gesture = null; scroll.classList.remove('is-panning'); schedule(); }
    });
    listen(window, 'keyup', e => { if (e.code === 'Space') space = false; });
    listen(window, 'blur', () => { space = false; cancelEdit(); gesture = null; pointers.clear(); scroll.classList.remove('is-panning'); schedule(); });
    const paletteType = 'application/x-aps-synoptic';
    listen(document, 'dragstart', e => {
        const item = e.target.closest('[data-synoptic-type]');
        if (editable && item) { e.dataTransfer.setData(paletteType, item.dataset.synopticType); e.dataTransfer.effectAllowed = 'copy'; }
    });
    listen(scroll, 'dragover', e => { if (editable && e.dataTransfer.types.includes(paletteType)) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
    listen(scroll, 'drop', e => {
        const type = e.dataTransfer.getData(paletteType);
        if (!editable || !type) return;
        e.preventDefault(); e.stopPropagation(); mode = 'manual'; invoke('DropPalette', { type, ...point(e.clientX, e.clientY) });
    });
    const observer = new ResizeObserver(schedule);
    observer.observe(scroll);
    for (let ancestor = root.parentElement, i = 0; ancestor && i < 6; ancestor = ancestor.parentElement, i++) observer.observe(ancestor);
    // A sibling can move the viewport without changing any ancestor's size.
    // Observe the collapsible cards themselves, including each animation frame.
    const viewer = root.closest('.sinoptico-viewer-layout');
    const observeViewerPanels = () => {
        for (const panel of viewer?.querySelectorAll('.failures-top-panel, .editor-header, .synoptic-status-summary') ?? []) observer.observe(panel);
        schedule();
    };
    const panelObserver = new MutationObserver(observeViewerPanels);
    if (viewer) {
        observeViewerPanels();
        panelObserver.observe(viewer.querySelector('.main-view') ?? viewer, { childList: true });
        listen(viewer, 'transitionend', schedule);
        listen(viewer, 'transitioncancel', schedule);
    }
    const runtimeObserver = new MutationObserver(() => { for (const e of document.querySelectorAll('.session-player-global-bar, .session-player-minimized')) observer.observe(e); schedule(); });
    runtimeObserver.observe(document.querySelector('.mud-main-content') ?? document.body, { childList: true });
    const contentObserver = new MutationObserver(schedule);
    contentObserver.observe(canvas, { childList: true });
    listen(window, 'resize', schedule);
    layout();
    return {
        update(next) { elements = next; if (!gesture && !committing) { bounds = boundsOf(elements); schedule(); } },
        center,
        dispose() { disposed = true; cancelEdit(); abort.abort(); observer.disconnect(); panelObserver.disconnect(); runtimeObserver.disconnect(); contentObserver.disconnect(); cancelAnimationFrame(frame); }
    };
}
