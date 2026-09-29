window.apsMonitor = window.apsMonitor || {};
window.apsMonitor.downloadBase64File = (fileName, contentType, base64) => {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);
};

window.apsMonitor.saveJsonFileWithDirectoryPicker = async (fileName, jsonText) => {
    if (window.showSaveFilePicker) {
        try {
            const handle = await window.showSaveFilePicker({
                suggestedName: fileName,
                types: [{
                    description: 'JSON Configuration File (*.json)',
                    accept: { 'application/json': ['.json'] }
                }]
            });
            const writable = await handle.createWritable();
            await writable.write(jsonText);
            await writable.close();
            return { success: true, savedAs: handle.name };
        } catch (err) {
            if (err.name === 'AbortError') {
                return { success: false, cancelled: true };
            }
            console.warn('showSaveFilePicker failed, falling back to download:', err);
        }
    }
    const blob = new Blob([jsonText], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    URL.revokeObjectURL(url);
    return { success: true, fallback: true };
};

