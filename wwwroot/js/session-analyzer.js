window.sessionAnalyzer = {
  theme: (chart) => {
    const css = getComputedStyle(chart);
    const surface = css.getPropertyValue('--mud-palette-surface').trim() || '#ffffff';
    const text = css.getPropertyValue('--mud-palette-text-primary').trim() || '#1e293b';
    const lines = css.getPropertyValue('--mud-palette-lines-default').trim() || '#dce2e9';
    return { paper_bgcolor: surface, plot_bgcolor: surface, 'font.color': text,
      'xaxis.gridcolor': lines, 'yaxis.gridcolor': lines, 'xaxis.zerolinecolor': lines,
      'yaxis.zerolinecolor': lines, 'legend.bgcolor': surface, 'legend.bordercolor': lines };
  },
  refreshTheme: () => {
    if (!window.Plotly) return;
    document.querySelectorAll('.js-plotly-plot').forEach(chart => {
      if (chart.data) Plotly.relayout(chart, window.sessionAnalyzer.theme(chart));
    });
  },
  draw: (id, sessions) => {
    const chart = document.getElementById(id);
    if (!chart) return;
    const traces = [];
    const colors = ['#3b82f6','#22c55e','#f59e0b','#ec4899','#a855f7','#06b6d4'];
    sessions.forEach((s, si) => s.keys.forEach((key, ki) => {
      const x = [], y = [];
      s.rows.forEach(r => { if (Object.prototype.hasOwnProperty.call(r.values, key)) { x.push(r.timestamp); y.push(r.values[key]); } });
      traces.push({ x, y, type:'scattergl', mode:'lines', name:`${s.name} · ${key}`, line:{width:1.5, color:colors[(si + ki) % colors.length]}, hovertemplate:'%{x}<br>%{y}<extra>'+s.name+' · '+key+'</extra>' });
    }));
    Plotly.react(chart, traces, {
      paper_bgcolor:'#1a222c', plot_bgcolor:'#1a222c', font:{color:'#dbeafe'},
      width:chart.clientWidth, height:chart.clientHeight, autosize:false,
      margin:{l:70,r:330,t:55,b:80},
      xaxis:{title:{text:'Tiempo', standoff:14}, gridcolor:'rgba(255,255,255,.1)', zerolinecolor:'rgba(255,255,255,.1)'},
      yaxis:{title:{text:'Valor', standoff:12}, gridcolor:'rgba(255,255,255,.1)', zerolinecolor:'rgba(255,255,255,.1)'},
      legend:{orientation:'v', x:1, xanchor:'left', y:1, yanchor:'top', bgcolor:'rgba(26,34,44,.9)', bordercolor:'rgba(255,255,255,.12)', borderwidth:1, font:{size:11}},
      hovermode:'x unified'
    }, {responsive:false, displayModeBar:true, scrollZoom:true, displaylogo:false})
      .then(() => Plotly.relayout(chart, window.sessionAnalyzer.theme(chart)));
  },
  resize: (id) => { const chart = document.getElementById(id); if (chart && chart.data) Plotly.relayout(chart, {width:chart.clientWidth, height:chart.clientHeight}); },
  download: (name, content) => { const a=document.createElement('a'); a.href=URL.createObjectURL(new Blob([content],{type:'text/csv;charset=utf-8'})); a.download=name; a.click(); URL.revokeObjectURL(a.href); }
};
