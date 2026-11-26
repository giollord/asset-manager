// Sprite sheet grid overlay and frame viewer (spec "Frame viewer", design D5). Works from the stored preview only:
// frame rectangles are in original pixels and are scaled by preview width ÷ original width.

const players = new WeakMap();
const images = new Map();

function load(url) {
    let p = images.get(url);
    if (!p) {
        p = new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => resolve(img);
            img.onerror = reject;
            img.src = url;
        });
        images.clear(); // keep only the current sheet
        images.set(url, p);
    }
    return p;
}

function setup(canvas, width, height, pixelated) {
    canvas.width = Math.max(1, Math.round(width));
    canvas.height = Math.max(1, Math.round(height));
    canvas.style.imageRendering = pixelated ? 'pixelated' : 'auto';
    const ctx = canvas.getContext('2d');
    ctx.imageSmoothingEnabled = !pixelated;
    return ctx;
}

/** Draws the sheet with the frame rectangles over it. */
export async function overlay(canvas, url, frames, originalWidth, pixelated) {
    if (!canvas || !url) return;
    const img = await load(url);
    const s = img.naturalWidth / Math.max(1, originalWidth);
    // Small previews are drawn larger so thin grid lines stay visible.
    const zoom = Math.max(1, Math.floor(640 / Math.max(img.naturalWidth, img.naturalHeight)));
    const ctx = setup(canvas, img.naturalWidth * zoom, img.naturalHeight * zoom, pixelated);
    ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
    ctx.lineWidth = 1;
    ctx.strokeStyle = 'rgba(255, 64, 129, 0.9)';
    for (const f of frames) ctx.strokeRect(f.x * s * zoom + 0.5, f.y * s * zoom + 0.5, Math.max(1, f.w * s * zoom - 1), Math.max(1, f.h * s * zoom - 1));
}

/** Plays the frames in order at fps (1-30), each scaled to fit a box of the given size. */
export async function play(canvas, url, frames, originalWidth, fps, pixelated, box) {
    stop(canvas);
    if (!canvas || !url || frames.length === 0) return;
    const img = await load(url);
    const s = img.naturalWidth / Math.max(1, originalWidth);
    const maxW = Math.max(...frames.map(f => f.w)) * s;
    const maxH = Math.max(...frames.map(f => f.h)) * s;
    const zoom = Math.min(box / maxW, box / maxH);
    const ctx = setup(canvas, maxW * zoom, maxH * zoom, pixelated);
    let i = 0;
    const draw = () => {
        const f = frames[i % frames.length];
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        ctx.drawImage(img, f.x * s, f.y * s, f.w * s, f.h * s, 0, 0, f.w * s * zoom, f.h * s * zoom);
        canvas.dataset.frame = String(i % frames.length);
        i++;
    };
    draw();
    players.set(canvas, setInterval(draw, 1000 / Math.min(30, Math.max(1, fps))));
}

export function stop(canvas) {
    const id = canvas && players.get(canvas);
    if (id) clearInterval(id);
    if (canvas) players.delete(canvas);
}
