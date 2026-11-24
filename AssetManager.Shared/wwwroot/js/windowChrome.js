// Window chrome (design D1, D2): in the desktop app the window's top strip is the system caption area, so it drags
// the window. The app bar's interactive elements, marked in app.css with `app-region: no-drag`, are reported to the
// host as rectangles in CSS pixels, which it turns into passthrough regions that take clicks.
// Only loaded when the host has a custom title bar.
let bar = null;
let dotNet = null;
let pending = 0;
let last = '';

export function watch(ref) {
    dotNet = ref;
    bar = document.querySelector('.mud-appbar');
    if (!bar) return;
    new ResizeObserver(schedule).observe(bar);
    new MutationObserver(schedule).observe(bar, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style'] });
    window.addEventListener('resize', schedule);
    schedule();
}

function schedule() {
    if (pending) return;
    pending = requestAnimationFrame(() => {
        pending = 0;
        report();
    });
}

const noDrag = el => getComputedStyle(el).appRegion === 'no-drag';

function report() {
    const rects = [];
    for (const el of bar.querySelectorAll('*')) {
        if (!noDrag(el)) continue;
        // Only the outermost no-drag element of a group counts.
        let inner = false;
        for (let p = el.parentElement; p && p !== bar; p = p.parentElement) {
            if (noDrag(p)) { inner = true; break; }
        }
        if (inner) continue;
        const r = el.getBoundingClientRect();
        if (r.width > 0 && r.height > 0) rects.push({ x: r.left, y: r.top, width: r.width, height: r.height });
    }
    const json = JSON.stringify(rects);
    if (json === last) return;
    last = json;
    dotNet.invokeMethodAsync('SetInteractiveRegions', rects);
}
