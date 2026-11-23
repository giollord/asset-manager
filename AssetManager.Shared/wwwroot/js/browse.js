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
