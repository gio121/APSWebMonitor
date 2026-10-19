const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

let chart = { isConnected: true, data: [], clientWidth: 900 };
let drawing, disconnected = 0, purged = 0;
const sandbox = {
    window: {}, document: { getElementById: () => chart },
    getComputedStyle: () => ({ getPropertyValue: key => key.includes('surface') ? '#fff' : '#333' }),
    ResizeObserver: class { observe() {} disconnect() { disconnected++; } },
    Plotly: { react: async (...args) => { drawing = args; }, Plots: { resize() {} }, purge: () => { purged++; } }
};
sandbox.window.Plotly = sandbox.Plotly;
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../wwwroot/js/control-events.js'), 'utf8'), sandbox);
(async () => {
    const api = sandbox.window.controlEvents;
    await api.draw('chart', [
        { key: 'voltage', name: 'Tensión', units: 'V', x: [0, 1], y: [2, 4] },
        { key: 'current', name: 'Corriente', units: 'A', x: [0, 1], y: [1, 3] }
    ], 'Muestra');
    assert.equal(drawing[1][0].yaxis, 'y');
    assert.equal(drawing[1][1].yaxis, 'y2');
    assert.equal(drawing[2].yaxis.title.text, 'V');
    assert.equal(drawing[2].yaxis2.title.text, 'A');
    assert.equal(drawing[2].xaxis2.matches, 'x');
    assert.equal(drawing[3].scrollZoom, true);
    await api.draw('chart', [], 'Tiempo FPGA (µs)');
    assert.equal(drawing[1].length, 0);
    assert.equal(drawing[2].annotations.length, 1);
    chart = { isConnected: true, data: [] };
    await api.draw('chart', [], 'Muestra');
    assert.equal(disconnected, 1, 'Replace observer when loading another DAT creates a new chart element.');
    api.dispose('chart');
    assert.equal(disconnected, 2);
    assert.equal(purged, 1);
    assert.equal(api.observers.size, 0);
    sandbox.window.Plotly = null;
    await assert.rejects(api.draw('chart', [], 'Muestra'));
    console.log('PASS: chart axes, empty state, zoom, observer replacement, disposal and missing-library handling.');
})().catch(error => { console.error(error); process.exitCode = 1; });
