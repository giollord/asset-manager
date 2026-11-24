// Browse grid helpers: report how many cards fit per row, and play audio previews inline.
let observer = null;

export function observeWidth(element, dotNet, cardWidth, gap) {
    unobserve();
    if (!element) return 1;
    const columns = () => Math.max(1, Math.floor((element.clientWidth + gap) / (cardWidth + gap)));
    let last = columns();
    observer = new ResizeObserver(() => {
        const now = columns();
        if (now !== last) {
            last = now;
            dotNet.invokeMethodAsync('ColumnsChanged', now);
        }
    });
    observer.observe(element);
    return last;
}

export function unobserve() {
    if (observer) observer.disconnect();
    observer = null;
}

let audio = null;

export function play(url, dotNet) {
    stop();
    audio = new Audio(url);
    audio.onended = () => dotNet.invokeMethodAsync('PlaybackEnded');
    return audio.play().then(() => true, () => false);
}

export function stop() {
    if (audio) {
        audio.onended = null;
        audio.pause();
        audio = null;
    }
}

// Ctrl+A on the Browse page selects all assets, not the page's text. Text fields keep their own Ctrl+A; with the
// details panel open it selects the panel's text only, and dialogs keep the browser's behaviour.
let selectAllHandler = null;

export function watchSelectAll(dotNet) {
    unwatchSelectAll();
    selectAllHandler = e => {
        if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey || e.key.toLowerCase() !== 'a') return;
        if (e.target instanceof Element && e.target.closest('input, textarea, [contenteditable]')) return;
        if (document.querySelector('.mud-dialog-container')) return;
        e.preventDefault();
        const details = document.querySelector('.asset-details.mud-drawer--open .mud-drawer-content');
        if (details) getSelection().selectAllChildren(details);
        else dotNet.invokeMethodAsync('SelectAllFromKeyboard');
    };
    document.addEventListener('keydown', selectAllHandler, true);
}

export function unwatchSelectAll() {
    if (selectAllHandler) document.removeEventListener('keydown', selectAllHandler, true);
    selectAllHandler = null;
}
