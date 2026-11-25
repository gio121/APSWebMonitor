// Keep the original single-row layout and scale all controls together when
// browser zoom (or a smaller window) reduces the available CSS width.
export function create(bar) {
    const content = bar.querySelector('.session-player-content');
    const observer = new ResizeObserver(entries => {
        const available = entries[0].contentRect.width;
        if (available <= 0) return;
        const layoutWidth = Math.max(1400, available);
        content.style.width = `${layoutWidth}px`;
        content.style.zoom = String(available / layoutWidth);
    });
    observer.observe(bar);
    return { dispose() { observer.disconnect(); } };
}
