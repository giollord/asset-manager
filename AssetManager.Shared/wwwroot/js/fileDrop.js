// Drag and drop of files from Explorer (design D9). The browser never exposes real paths, but WebView2 does:
// postMessageWithAdditionalObjects hands the dropped File objects to the host as CoreWebView2File with a Path.
// Outside WebView2 (the web dev host) drops are ignored.
let installed = false;

// Search by image (design D6): Browse registers its drop zone. Drops inside it, and image pastes while Browse is
// open, go to image search; every other drop stays an import.
const maxImageBytes = 25 * 1024 * 1024;
let imageZone = null;

function firstImage(files) {
    return Array.from(files || []).find(f => (f.type || '').startsWith('image/'));
}

function readBase64(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''));
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(file);
    });
}

async function sendImage(zone, file) {
    if (!file) return zone.dotNet.invokeMethodAsync('ImageRejected', 'That is not an image.');
    if (file.size > maxImageBytes) return zone.dotNet.invokeMethodAsync('ImageRejected', 'The image is larger than 25 MB.');
    await zone.dotNet.invokeMethodAsync('OnImageDropped', await readBase64(file));
}

function onPaste(e) {
    const zone = imageZone;
    if (!zone || !e.clipboardData) return;
    const item = Array.from(e.clipboardData.items || []).find(i => i.kind === 'file' && (i.type || '').startsWith('image/'));
    if (!item) return; // text pastes are left alone
    e.preventDefault();
    sendImage(zone, item.getAsFile());
}

export function registerImageZone(element, dotNet) {
    imageZone = { element, dotNet };
    document.addEventListener('paste', onPaste);
}

export function unregisterImageZone() {
    imageZone = null;
    document.removeEventListener('paste', onPaste);
}

function inImageZone(target) {
    return imageZone && imageZone.element && target instanceof Node && imageZone.element.contains(target) ? imageZone : null;
}

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
        if (imageZone && imageZone.element) imageZone.element.classList.toggle('image-drop-active', !!inImageZone(e.target));
    });
    document.addEventListener('drop', e => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        depth = 0;
        document.body.classList.remove('file-dragging');
        const files = e.dataTransfer.files;
        const zone = inImageZone(e.target);
        if (zone) {
            zone.element.classList.remove('image-drop-active');
            sendImage(zone, firstImage(files));
            return;
        }
        if (window.chrome && window.chrome.webview && window.chrome.webview.postMessageWithAdditionalObjects) {
            window.chrome.webview.postMessageWithAdditionalObjects('FilesDropped', files);
        } else if (dotNet) {
            dotNet.invokeMethodAsync('DropUnsupported');
        }
    });
}
