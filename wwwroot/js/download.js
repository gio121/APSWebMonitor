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
