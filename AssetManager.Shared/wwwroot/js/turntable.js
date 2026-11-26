// Turntable of a model's stored renders (design D18). All views are <img> elements already in the DOM (decoded up
// front); a step only swaps which one is visible, so turning needs no loading and no .NET round trip.
//
// Views are indexed ring * azimuths + azimuth; ring 0 is the lowest elevation. The renderer places the camera at
// azimuth i * 360 / n going from +Z towards +X, so a higher index shows the model turned to the left: dragging to the
// right steps the index down, and the model turns with the pointer.
export function init(element, rings, azimuths, primary) {
    const views = [...element.querySelectorAll('.turntable-view')];
    if (views.length === 0) return null;
    views.forEach(v => v.decode?.().catch(() => { }));
    rings = Math.max(1, rings);
    azimuths = Math.max(1, azimuths);
    let ring = Math.floor(primary / azimuths), azimuth = primary % azimuths;
    let drag = null;

    const show = () => {
        const index = ring * azimuths + azimuth;
        views.forEach((v, i) => { v.hidden = i !== index; });
        element.dataset.ring = ring;
        element.dataset.azimuth = azimuth;
    };
    const turn = steps => { azimuth = ((azimuth + steps) % azimuths + azimuths) % azimuths; };
    const tilt = steps => { ring = Math.min(rings - 1, Math.max(0, ring + steps)); };

    const down = e => {
        if (e.button !== 0) return;
        element.setPointerCapture(e.pointerId);
        drag = { x: e.clientX, y: e.clientY, ring, azimuth };
        element.classList.add('dragging');
        element.focus({ preventScroll: true });
        e.preventDefault();
    };
    const move = e => {
        if (!drag) return;
        const rect = element.getBoundingClientRect();
        // A drag across the full width is one turn; the height is split into one step more than there are rings.
        const across = Math.round((e.clientX - drag.x) / (rect.width / azimuths));
        const lift = Math.round((drag.y - e.clientY) / (rect.height / (rings + 1)));
        ring = drag.ring;
        azimuth = drag.azimuth;
        turn(-across);
        tilt(lift);
        show();
    };
    const up = e => {
        if (!drag) return;
        drag = null;
        element.classList.remove('dragging');
        if (element.hasPointerCapture(e.pointerId)) element.releasePointerCapture(e.pointerId);
    };
    const key = e => {
        const steps = { ArrowLeft: [1, 0], ArrowRight: [-1, 0], ArrowUp: [0, 1], ArrowDown: [0, -1] }[e.key];
        if (!steps) return;
        e.preventDefault();
        e.stopPropagation(); // not a Browse shortcut while the turntable has focus
        turn(steps[0]);
        tilt(steps[1]);
        show();
    };

    element.addEventListener('pointerdown', down);
    element.addEventListener('pointermove', move);
    element.addEventListener('pointerup', up);
    element.addEventListener('pointercancel', up);
    element.addEventListener('keydown', key);
    show();
    return {
        dispose() {
            element.removeEventListener('pointerdown', down);
            element.removeEventListener('pointermove', move);
            element.removeEventListener('pointerup', up);
            element.removeEventListener('pointercancel', up);
            element.removeEventListener('keydown', key);
        },
    };
}
