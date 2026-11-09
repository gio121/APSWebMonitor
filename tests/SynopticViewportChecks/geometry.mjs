import assert from 'node:assert/strict';
import { boundsOf, fitScale, toCanvas, editGeometry } from '../../wwwroot/js/synoptic-viewport.js';
let checks = 0;
const check = (ok, message) => { assert.ok(ok, message); checks++; };
check(boundsOf([]).width === 800, 'Empty canvases have usable dimensions');
const rotated = boundsOf([{ x: -100, y: -100, width: 100, height: 20, rotation: 90 }]);
check(Math.abs(rotated.width - 28) < 1e-8 && Math.abs(rotated.height - 108) < 1e-8, 'Rotated bounds include stroke padding');
check(rotated.x < 0 && rotated.y < 0, 'Negative coordinates remain reachable');
for (const scale of [.25, .5, 1, 2, 4]) {
    const origin = { x: 150, y: -40 }, start = { id: 'a', x: -30, y: 20, width: 100, height: 50, rotation: 0, proportionalScale: false };
    const p = toCanvas(start.x * scale + origin.x, start.y * scale + origin.y, scale, origin);
    check(p.x === start.x && p.y === start.y, `Round trip at ${scale}`);
    const moved = editGeometry(start, 20 * scale / scale, -12 * scale / scale, false);
    check(moved.x === -10 && moved.y === 8, `Dragging uses canvas units at ${scale}`);
    const resized = editGeometry(start, 30 * scale / scale, 15 * scale / scale, true);
    check(resized.width === 130 && resized.height === 65 && resized.x === start.x && resized.y === start.y, 'Unrotated resize preserves position');
}
for (const angle of [0, 30, 90, 180, 270]) {
    const start = { id: 'r', x: -20, y: -10, width: 100, height: 50, rotation: angle, proportionalScale: true };
    const resized = editGeometry(start, 50, 30, true);
    const corner = e => {
        const a = angle * Math.PI / 180;
        return [e.x + e.width / 2 - Math.cos(a) * e.width / 2 + Math.sin(a) * e.height / 2,
            e.y + e.height / 2 - Math.sin(a) * e.width / 2 - Math.cos(a) * e.height / 2];
    };
    const before = corner(start), after = corner(resized);
    check(before.every((v, i) => Math.abs(v - after[i]) < 1e-8), 'Rotated resize anchors the opposite corner');
    check(Math.abs(resized.width / resized.height - 2) < 1e-8, 'Proportional resizing keeps aspect ratio');
}
for (const [w, h] of [[360, 500], [768, 800], [1366, 600], [2560, 1200]]) {
    const bounds = boundsOf([{ x: -200, y: -40, width: 4000, height: 1800, rotation: 0 }]);
    const scale = fitScale(bounds, w, h);
    check(bounds.width * scale <= w - 48 + 1e-7 && bounds.height * scale <= h - 48 + 1e-7, 'Fit all uses both viewport dimensions');
    check(Number.isFinite(scale) && scale > 0, 'Automatic initial scale remains usable');
}
check(fitScale({ width: 100000, height: 100000 }, 360, 400) < .1, 'Fit remains available below manual minimum zoom');
console.log(`PASS: ${checks} viewport geometry checks.`);
