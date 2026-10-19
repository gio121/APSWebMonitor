window.controlEvents = {
    observers: new Map(),
    draw: async (id, series, xTitle) => {
        const chart = document.getElementById(id);
        if (!chart) return;
        if (!window.Plotly) throw new Error('Plotly no está disponible.');
        const units = [...new Set(series.map(s => s.units))];
        const traces = series.map(s => {
            const axis = units.indexOf(s.units) + 1;
            return { x: s.x, y: s.y, type: 'scattergl', mode: 'lines', name: `${s.name} (${s.key})`,
                xaxis: axis === 1 ? 'x' : `x${axis}`, yaxis: axis === 1 ? 'y' : `y${axis}`,
                line: { width: 1.5 }, hovertemplate: '%{x}<br>%{y}<extra>%{fullData.name}</extra>' };
        });
        const css = getComputedStyle(chart);
        const surface = css.getPropertyValue('--mud-palette-surface').trim();
        const color = css.getPropertyValue('--mud-palette-text-primary').trim();
        const lines = css.getPropertyValue('--mud-palette-lines-default').trim();
        const layout = { autosize: true, height: Math.max(350, units.length * 230), paper_bgcolor: surface, plot_bgcolor: surface,
            font: { color }, margin: { l: 65, r: 25, t: 25, b: 65 }, hovermode: 'x unified',
            legend: { orientation: 'h', y: -0.2 }, grid: { rows: Math.max(1, units.length), columns: 1, pattern: 'independent' } };
        units.forEach((unit, i) => {
            const suffix = i === 0 ? '' : `${i + 1}`;
            layout[`xaxis${suffix}`] = { title: { text: xTitle }, gridcolor: lines, matches: i ? 'x' : undefined };
            layout[`yaxis${suffix}`] = { title: { text: unit || 'Valor' }, gridcolor: lines };
        });
        if (!series.length) layout.annotations = [{ text: 'Selecciona señales para mostrar la traza', xref: 'paper', yref: 'paper', x: 0.5, y: 0.5, showarrow: false }];
        await Plotly.react(chart, traces, layout, { responsive: true, scrollZoom: true, displaylogo: false });
        const existing = window.controlEvents.observers.get(id);
        if (existing?.element !== chart) {
            existing?.observer.disconnect();
            const observer = new ResizeObserver(() => { if (chart.isConnected && chart.data) Plotly.Plots.resize(chart); });
            observer.observe(chart);
            window.controlEvents.observers.set(id, { element: chart, observer });
        }
    },
    dispose: id => {
        window.controlEvents.observers.get(id)?.observer.disconnect();
        window.controlEvents.observers.delete(id);
        const chart = document.getElementById(id);
        if (chart && window.Plotly) Plotly.purge(chart);
    }
};
