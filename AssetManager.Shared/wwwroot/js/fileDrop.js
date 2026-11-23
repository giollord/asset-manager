// Drag and drop of files from Explorer (design D9). The browser never exposes real paths, but WebView2 does:
// postMessageWithAdditionalObjects hands the dropped File objects to the host as CoreWebView2File with a Path.
// Outside WebView2 (the web dev host) drops are ignored.
let installed = false;

export function install(dotNet) {
    if (installed) return;
    installed = true;
    let depth = 0;
    const hasFiles = e => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
    document.addEventListener('dragenter', e => {
        if (!hasFiles(e)) return;
        depth++;
        document.body.classList.add('file-dragging');
    });
    document.addEventListener('dragleave', e => {
        if (!hasFiles(e)) return;
        depth = Math.max(0, depth - 1);
        if (depth === 0) document.body.classList.remove('file-dragging');
    });
    document.addEventListener('dragover', e => {
        if (!hasFiles(e)) return;
        e.preventDefault(); // otherwise WebView2 navigates to the dropped file
        e.dataTransfer.dropEffect = 'copy';
    });
    document.addEventListener('drop', e => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        depth = 0;
        document.body.classList.remove('file-dragging');
        const files = e.dataTransfer.files;
        if (window.chrome && window.chrome.webview && window.chrome.webview.postMessageWithAdditionalObjects) {
            window.chrome.webview.postMessageWithAdditionalObjects('FilesDropped', files);
        } else if (dotNet) {
            dotNet.invokeMethodAsync('DropUnsupported');
        }
    });
}
