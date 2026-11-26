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

// Browse keyboard shortcuts (design D10). One keydown handler while the Browse page is shown; .NET passes the key
// combinations from its shortcut table (BrowseShortcuts) and does the work. Keys typed into a text field never count,
// dialogs keep their own keys, and Ctrl+A with the details panel open selects the panel's text only.
let keyHandler = null;

function combo(e) {
    const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    return (e.ctrlKey || e.metaKey ? 'Control+' : '') + (e.shiftKey && e.key.length > 1 ? 'Shift+' : '') + (e.altKey ? 'Alt+' : '') + key;
}

export function watchKeys(dotNet, keys) {
    unwatchKeys();
    const wanted = new Set(keys);
    keyHandler = e => {
        if (e.isComposing) return;
        const target = e.target instanceof Element ? e.target : null;
        if (target && target.closest('input, textarea, select, [contenteditable]:not([contenteditable=false])')) return;
        if (target && target.closest('.model-turntable')) return; // the turntable steps with the arrow keys itself
        if (document.querySelector('.mud-dialog-container')) return;
        if (document.getElementById('asset-menu')) return; // the open context menu handles its own keys
        const c = combo(e);
        if (!wanted.has(c)) return;
        // Enter and Space on a focused button or menu item keep their own meaning.
        if ((c === 'Enter' || c === ' ') && target && target.closest('button, a, [role=button], [role=menuitem], [role=option]')) return;
        e.preventDefault();
        // The menu key also raises a native "contextmenu" when released, wherever the focus is by then.
        if (c === 'ContextMenu' || c === 'Shift+F10') suppressMenuUntil = performance.now() + 1500;
        if (c === 'Control+a') {
            const details = document.querySelector('.asset-details.mud-drawer--open .mud-drawer-content');
            if (details) { getSelection().selectAllChildren(details); return; }
        }
        dotNet.invokeMethodAsync('OnShortcut', c);
    };
    document.addEventListener('keydown', keyHandler, true);
    // The WebView's own menu never opens over the grid: cards open the asset menu, and the menu key opens it from .NET.
    menuHandler = e => {
        const own = e.target instanceof Element && e.target.closest('.browse-grid, #asset-menu, .asset-menu-backdrop');
        if (own || performance.now() < suppressMenuUntil) e.preventDefault();
    };
    document.addEventListener('contextmenu', menuHandler, true);
}

let menuHandler = null;
let suppressMenuUntil = 0;

// Drag-out (design D7): pressing a card and moving past a small threshold asks the desktop host to start a native drag
// of the card's files (or the selection's). The HTML drag of cards is suppressed so it cannot cancel the gesture.
let dragOutInstalled = false;

export function installDragOut() {
    if (dragOutInstalled || !(window.chrome && window.chrome.webview)) return false;
    dragOutInstalled = true;
    let start = null;
    const card = t => t instanceof Element && !t.closest('input, button, .asset-select, .asset-card-overlay, .mud-checkbox')
        ? t.closest('.browse-grid .asset-card') : null;
    document.addEventListener('pointerdown', e => {
        const c = e.button === 0 ? card(e.target) : null;
        start = c && c.dataset.id ? { x: e.clientX, y: e.clientY, id: c.dataset.id } : null;
    }, true);
    document.addEventListener('pointermove', e => {
        if (!start || !(e.buttons & 1)) return;
        if (Math.hypot(e.clientX - start.x, e.clientY - start.y) < 6) return;
        const id = start.id;
        start = null;
        window.chrome.webview.postMessage('DragOut:' + id);
    }, true);
    document.addEventListener('pointerup', () => start = null, true);
    document.addEventListener('dragstart', e => { if (card(e.target)) e.preventDefault(); }, true);
    return true;
}

export function unwatchKeys() {
    if (keyHandler) document.removeEventListener('keydown', keyHandler, true);
    if (menuHandler) document.removeEventListener('contextmenu', menuHandler, true);
    keyHandler = null;
    menuHandler = null;
}

// Card context menu (design D17). focusMenu(0) focuses the first item; ±1 moves between the enabled items, wrapping.
export function focusMenu(delta) {
    const items = [...document.querySelectorAll('#asset-menu .asset-menu-item:not(:disabled)')];
    if (items.length === 0) { document.getElementById('asset-menu')?.focus(); return; }
    const at = items.indexOf(document.activeElement);
    const next = delta === 0 || at < 0 ? 0 : (at + delta + items.length) % items.length;
    items[next].focus();
}

// Where the keyboard opens the menu of a card: just inside its top-left corner, or null when it is not rendered.
export function focusGrid() {
    document.getElementById('browse-grid')?.focus({ preventScroll: true });
}

export function cardPoint(id) {
    const card = document.querySelector(`.browse-grid .asset-card[data-id="${id}"]`);
    if (!card) return null;
    const r = card.getBoundingClientRect();
    return [r.left + 12, r.top + 24];
}

export function copyText(text) {
    return navigator.clipboard.writeText(text).then(() => true, () => false);
}

function scrollParent(element) {
    for (let p = element.parentElement; p; p = p.parentElement) {
        const o = getComputedStyle(p).overflowY;
        if ((o === 'auto' || o === 'scroll') && p.scrollHeight > p.clientHeight) return p;
    }
    return document.scrollingElement;
}

// Scrolls the virtualized grid so that row `row` is visible ({block: 'nearest'}); the row may not be rendered yet.
export function reveal(grid, row, rowHeight, headerHeight) {
    if (!grid) return;
    const scroller = scrollParent(grid);
    const view = scroller === document.scrollingElement ? { top: 0, height: innerHeight } : scroller.getBoundingClientRect();
    const top = grid.getBoundingClientRect().top + row * rowHeight;
    const visibleTop = view.top + (headerHeight || 0);
    if (top < visibleTop) scroller.scrollTop -= visibleTop - top;
    else if (top + rowHeight > view.top + view.height) scroller.scrollTop += top + rowHeight - (view.top + view.height);
}

export function focusSearch() {
    const input = document.querySelector('#search-text input, input#search-text');
    if (input) { input.focus(); input.select(); }
}
