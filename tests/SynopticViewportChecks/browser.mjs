// Run against an isolated headless Chrome profile; never attaches to the user's browser.
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { spawn } from 'node:child_process';
import assert from 'node:assert/strict';
const directory = resolve(process.argv[2] ?? 'artifacts/synoptic-viewport-checks');
const viewer = process.argv.includes('--viewer');
const delay = ms => new Promise(r => setTimeout(r, ms));
const routes = {
    '/': [join(directory, 'fixture.html'), 'text/html'],
    '/styles.css': [resolve('obj/Debug/net10.0/scopedcss/bundle/ApsMonitor.styles.css'), 'text/css'],
    '/layout.css': [join(directory, 'layout.css'), 'text/css'],
    '/mud.css': [join(process.env.USERPROFILE, '.nuget/packages/mudblazor/9.9.0/staticwebassets/MudBlazor.min.css'), 'text/css'],
    '/viewport.js': [resolve('wwwroot/js/synoptic-viewport.js'), 'text/javascript'],
    '/bootstrap.mjs': [resolve('tests/SynopticViewportChecks/bootstrap.mjs'), 'text/javascript']
};
const server = createServer(async (req, res) => {
    const route = routes[new URL(req.url, 'http://localhost').pathname];
    if (!route) { res.writeHead(404).end(); return; }
    try { res.setHeader('Content-Type', route[1]); res.end(await readFile(route[0])); }
    catch (e) { res.writeHead(500).end(String(e)); }
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
await mkdir(directory, { recursive: true });
const profile = join(directory, `chrome-${Date.now()}`);
const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', [
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0',
    `--user-data-dir=${profile}`, 'about:blank'
], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
let browserLog = '';
chrome.stderr.on('data', d => { browserLog += d; });
let socket;
let checks = 0;
const check = (value, message) => { assert.ok(value, message); checks++; };
try {
    let port;
    for (let i = 0; i < 200 && !port; i++) {
        try { port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; }
        catch { await delay(50); }
    }
    if (!port) throw new Error('Headless Chrome did not start: ' + browserLog.slice(-1200));
    const info = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
    socket = new WebSocket(info.webSocketDebuggerUrl);
    await new Promise((r, reject) => { socket.addEventListener('open', r, { once: true }); socket.addEventListener('error', reject, { once: true }); });
    let id = 0, session;
    const pending = new Map();
    socket.addEventListener('message', e => {
        const response = JSON.parse(e.data);
        if (!response.id) return;
        const entry = pending.get(response.id); if (!entry) return;
        pending.delete(response.id); clearTimeout(entry.timer);
        if (response.error) entry.reject(new Error(JSON.stringify(response.error))); else entry.resolve(response.result);
    });
    const send = (method, params = {}, sessionId = session) => new Promise((resolve, reject) => {
        const messageId = ++id;
        const timer = setTimeout(() => { pending.delete(messageId); reject(new Error('CDP timed out: ' + method + ' ' + browserLog.slice(-1000))); }, 10000);
        pending.set(messageId, { resolve, reject, timer }); socket.send(JSON.stringify({ id: messageId, method, params, sessionId }));
    });
    const target = await send('Target.createTarget', { url: 'about:blank' }, undefined);
    session = (await send('Target.attachToTarget', { targetId: target.targetId, flatten: true }, undefined)).sessionId;
    await send('Page.enable'); await send('Runtime.enable');
    const evaluate = async expression => {
        const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
        if (result.exceptionDetails) throw new Error(result.exceptionDetails.text + ': ' + result.exceptionDetails.exception?.description);
        return result.result.value;
    };
    const settle = () => evaluate('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
    async function load(width, height) {
        await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
        await send('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/${viewer ? '?mode=viewer' : ''}` });
        for (let i = 0; i < 100; i++) { if (await evaluate('!!window.viewportCheck')) break; await delay(30); }
        await settle();
        return evaluate('viewportCheck.state()');
    }
    const click = async action => {
        const button = await rect(`[data-view-action="${action}"]`);
        await mouse('mousePressed', button.x + button.width / 2, button.y + button.height / 2, { button: 'left', buttons: 1, clickCount: 1 });
        await mouse('mouseReleased', button.x + button.width / 2, button.y + button.height / 2, { button: 'left', buttons: 0, clickCount: 1 });
        await settle();
    };
    const percent = async value => {
        await evaluate(`(()=>{const input=document.querySelector('.synoptic-percentage input');input.value=${JSON.stringify(String(value))};input.dispatchEvent(new Event('change',{bubbles:true}))})()`);
        await settle();
    };
    const state = () => evaluate('viewportCheck.state()');
    const rect = selector => evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height}})()`);
    async function mouse(type, x, y, extra = {}) { await send('Input.dispatchMouseEvent', { type, x, y, ...extra }); }
    async function drag(x, y, dx, dy) {
        await mouse('mousePressed', x, y, { button: 'left', buttons: 1, clickCount: 1 });
        await mouse('mouseMoved', x + dx, y + dy, { button: 'left', buttons: 1 });
        await mouse('mouseReleased', x + dx, y + dy, { button: 'left', buttons: 0, clickCount: 1 });
        await settle();
    }
    for (const [width, height] of [[360, 800], [768, 1024], [1024, 768], [1280, 720], [1366, 768], [1920, 1080], [2560, 1440]]) {
        const before = await load(width, height);
        check(before.documentWidth <= width, `No page overflow at ${width}x${height}`);
        check(before.scrollWidth <= before.clientWidth + 1 && before.scrollHeight <= before.clientHeight + 1, 'Fit all contains the scene');
        check(before.height >= 200 && before.top + before.height <= height, 'Work area fits available height');
        const frames = await evaluate(`new Promise(resolve => {
            const samples = [];
            function sample() {
                const s = viewportCheck.state();
                samples.push([s.scale, s.clientWidth, s.clientHeight, s.left, s.scrollTop].join(','));
                if (samples.length === 20) resolve(samples); else requestAnimationFrame(sample);
            }
            requestAnimationFrame(sample);
        })`);
        check(new Set(frames).size === 1, 'Initial fit stays stable across consecutive frames');
        check(await evaluate("getComputedStyle(document.querySelector('.synoptic-canvas')).visibility === 'visible'"), 'Canvas is revealed after initial layout');
        check((await rect('.fixture-header')).y === 16, 'Header keeps a small top margin without the unused app bar gap');
        check(await evaluate("document.querySelectorAll('[data-view-action]').length === 3"), 'Toolbar only contains minus, plus and center');
        await click('in'); await click('out'); await percent(125); await click('center');
        check(Math.abs((await state()).scale - 1.25) < 1e-6, 'Editable percentage is applied and centering preserves it');
        check((await state()).calls.length === 0, 'Zoom controls never commit configuration changes');
        await evaluate("document.querySelector('#fixture-cards').style.height='180px'"); await settle(); await settle();
        const after = await state();
        check(after.height < before.height, 'Opening cards reallocates work area height');
        check(Math.abs(after.scale - 1.25) < 1e-6, 'Card height changes preserve the chosen percentage');
        await evaluate("document.querySelector('#fixture-cards').style.height='0px'"); await settle(); await settle();
        check(Math.abs((await state()).height - before.height) <= 1, 'Collapsing cards restores the full available height');
    }
    await load(1366, 768);
    if (viewer) {
        const original = await state();
        await evaluate(`(() => {
            document.querySelector('.monitor-page-content').classList.add('sinoptico-viewer-layout');
            const bar = document.createElement('div'); bar.className = 'session-player-global-bar';
            bar.style.cssText = 'position:fixed;left:0;right:0;bottom:32px;height:100px';
            document.querySelector('.mud-main-content').append(bar);
        })()`);
        await settle(); await settle();
        check((await state()).height === original.height && (await state()).scale === original.scale, 'Floating session player does not shrink or rescale the viewer');
        await evaluate("document.querySelector('.session-player-global-bar').remove()");
        await settle(); await settle();
        check((await state()).height === original.height, 'Closing the player preserves viewer height');
    }
    if (!viewer) {
        const editorSource = await readFile(resolve('Components/Pages/SinopticoEditor.razor'), 'utf8');
        const editorStyle = editorSource.match(/<style>([\s\S]*?)<\/style>/)[1].replaceAll('@@', '@');
        await evaluate(`(() => {
            const style = document.createElement('style'); style.textContent = ${JSON.stringify(editorStyle)}; document.head.append(style);
            const root = document.querySelector('.synoptic-viewport');
            const container = document.createElement('div'); container.className = 'editor-container d-flex'; root.before(container);
            container.innerHTML = '<div class="sidebar left-sidebar panel-open pa-2"><div>Elements</div><div>Label</div></div><div class="main-canvas-area flex-grow-1"></div><div class="sidebar right-sidebar panel-open pa-4"><div>Properties</div><div>Capacitor</div><div class="mud-grid"><div class="mud-grid-item mud-grid-item-xs-6">X</div><div class="mud-grid-item mud-grid-item-xs-6">Y</div></div></div>';
            container.querySelector('.main-canvas-area').append(root);
        })()`);
        await settle(); await settle();
        check(await evaluate(`(() => { const p = document.querySelector('.right-sidebar'); const c = [...p.children]; return getComputedStyle(p).flexDirection === 'column' && c[1].getBoundingClientRect().top >= c[0].getBoundingClientRect().bottom && p.scrollWidth <= p.clientWidth + 1; })()`), 'Properties stack vertically without horizontal clipping');
        check((await rect('.left-sidebar')).height === (await rect('.synoptic-viewport')).height, 'Panels use the available canvas height');
        await load(1366, 768);
    }
    await evaluate("document.querySelector('.synoptic-percentage input').focus(); document.querySelector('.synoptic-percentage input').value='175'");
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
    await settle(); check(Math.abs((await state()).scale - 1.75) < 1e-6, 'Enter applies the typed percentage');
    const synthetic = !viewer && await evaluate('viewportCheck.elements.some(e => e.id === "move")');
    if (synthetic) {
    for (const targetScale of [.25, .5, 1, 2, 4]) {
        await percent(100);
        const scroller = await rect('.synoptic-scroll');
        const deltaY = -Math.log(targetScale) / .002;
        await mouse('mouseWheel', scroller.x + scroller.width / 2, scroller.y + scroller.height / 2, { deltaX: 0, deltaY, modifiers: 2 });
        await settle(); await delay(60);
        check(Math.abs((await state()).scale - targetScale) < 1e-4, 'Wheel zoom reaches requested scale');
        const visible = await evaluate(`(()=>{const e=document.querySelector('[data-synoptic-id="move"]');e.scrollIntoView({block:'center',inline:'center'});return true})()`);
        await settle();
        const before = await evaluate('({...viewportCheck.elements.find(e=>e.id==="move")})');
        const box = await rect('[data-synoptic-id="move"]');
        await drag(box.x + box.width / 2, box.y + box.height / 2, 20 * targetScale, 12 * targetScale);
        const moved = await evaluate('viewportCheck.elements.find(e=>e.id==="move")');
        check(moved.x === before.x + 20 && moved.y === before.y + 12, `Real pointer drag at ${targetScale}`);
        check((await state()).calls.some(c => c.method === 'CommitEdit'), 'Pointer release commits a logical edit');
        const handle = await rect('[data-synoptic-id="move"] .resize-handle');
        await drag(handle.x + handle.width / 2, handle.y + handle.height / 2, 20 * targetScale, 10 * targetScale);
        const resized = await evaluate('viewportCheck.elements.find(e=>e.id==="move")');
        check(resized.width === moved.width + 20 && resized.height === moved.height + 10, `Real resize at ${targetScale}`);
    }
    }
    await percent(50); await click('center');
    check(Math.abs((await state()).scale - .5) < 1e-6, 'Center does not change the selected percentage');
    await percent(900); check(Math.abs((await state()).scale - 4) < 1e-6, 'Percentage clamps to the maximum');
    await percent(1); check(Math.abs((await state()).scale - .1) < 1e-6, 'Percentage clamps to the minimum');
    await percent(''); check(Math.abs((await state()).scale - .1) < 1e-6, 'Empty percentage keeps the current scale');
    // Dragging the background pans the scene and does not alter any element geometry.
    await percent(100);
    await click('in'); await click('in');
    const scrollRect = await rect('.synoptic-scroll');
    const beforePan = await state();
    const dataBeforePan = await evaluate('JSON.stringify(viewportCheck.elements)');
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: ' ', code: 'Space', windowsVirtualKeyCode: 32 });
    await evaluate('document.querySelector(".synoptic-scroll").focus()');
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: ' ', code: 'Space', windowsVirtualKeyCode: 32 });
    await drag(scrollRect.x + scrollRect.width / 2, scrollRect.y + scrollRect.height / 2, -80, -40);
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: ' ', code: 'Space', windowsVirtualKeyCode: 32 });
    const afterPan = await state();
    check(afterPan.left !== beforePan.left || afterPan.scrollTop !== beforePan.scrollTop, 'Space plus drag pans the scene');
    check(dataBeforePan === await evaluate('JSON.stringify(viewportCheck.elements)'), 'Panning preserves geometry');
    await click('center');
    const centered = await evaluate('({point:viewportCheck.controller.center(),bounds:viewportCheck.bounds()})');
    check(Math.abs(centered.point.x - centered.bounds.x - centered.bounds.width / 2) <= 1 &&
        Math.abs(centered.point.y - centered.bounds.y - centered.bounds.height / 2) <= 1, 'Center returns the scene midpoint to the viewport center');
    check(Math.abs((await state()).scale - afterPan.scale) < 1e-6, 'Center after panning preserves zoom');
    await send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 2 });
    const pinchRect = await rect('.synoptic-scroll'), cx = pinchRect.x + pinchRect.width / 2, cy = pinchRect.y + pinchRect.height / 2;
    const beforePinch = (await state()).scale;
    await send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: cx - 40, y: cy, id: 1 }, { x: cx + 40, y: cy, id: 2 }] });
    await send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: cx - 70, y: cy, id: 1 }, { x: cx + 70, y: cy, id: 2 }] });
    await send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await settle();
    check((await state()).scale > beforePinch, 'Two-finger gesture increases zoom');
    check(dataBeforePan === await evaluate('JSON.stringify(viewportCheck.elements)'), 'Pinching preserves geometry');
    await send('Emulation.setTouchEmulationEnabled', { enabled: false });
    for (const dpi of [1.25, 1.5]) {
        await send('Emulation.setDeviceMetricsOverride', { width: Math.floor(1366 / dpi), height: Math.floor(768 / dpi), deviceScaleFactor: dpi, mobile: false });
        await click('center');
        const dpiState = await state();
        check(dpiState.documentWidth <= dpiState.windowWidth, 'Manual zoom stays inside the page at 125/150 percent DPI');
    }
    await send('Emulation.setDeviceMetricsOverride', { width: 1366, height: 768, deviceScaleFactor: 1, mobile: false });
    await percent(100); await click('center');
    const drop = await evaluate(`(()=>{const scroll=document.querySelector('.synoptic-scroll'),r=scroll.getBoundingClientRect(),dataTransfer=new DataTransfer();dataTransfer.setData('application/x-aps-synoptic','Caja');scroll.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer,clientX:r.left+50,clientY:r.top+60}));return true})()`);
    await settle();
    if (viewer) check(!(await state()).calls.some(c => c.method === 'DropPalette' || c.method === 'CommitEdit'), 'Viewer cannot edit or add elements');
    else check((await state()).calls.some(c => c.method === 'DropPalette' && c.value.type === 'Caja' && Number.isFinite(c.value.x)), 'Palette drop resolves canvas coordinates');
    check((await state()).failures.length === 0, 'Browser has no script errors');
    const screenshot = await send('Page.captureScreenshot', { format: 'png' });
    await (await import('node:fs/promises')).writeFile(join(directory, 'viewport.png'), Buffer.from(screenshot.data, 'base64'));
    await evaluate('viewportCheck.controller.dispose()');
    const beforeDisposeClick = await state(); await click('in');
    check((await state()).scale === beforeDisposeClick.scale, 'Disposal detaches interaction handlers');
    console.log(`PASS: ${checks} headless browser viewport checks across seven resolutions.`);
    await send('Browser.close', {}, undefined).catch(() => {});
} finally {
    socket?.close(); chrome.kill(); server.close();
}
