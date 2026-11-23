// Off-screen model renderer (design D6). Runs in the app's WebView; .NET streams a GLB plus textures in and gets
// transparent WebP renders back. The same loading code powers the interactive viewer.
import * as THREE from '../lib/three/three.module.js';
import { GLTFLoader } from '../lib/three/addons/GLTFLoader.js';
import { OrbitControls } from '../lib/three/addons/OrbitControls.js';

const EMPTY_PNG = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==';
const NEUTRAL = 0xb4b4b4;
const deg = d => d * Math.PI / 180;

let shared = null;

function sharedRenderer(width, height) {
    if (!shared) {
        const canvas = document.createElement('canvas');
        const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true, preserveDrawingBuffer: true });
        renderer.setClearColor(0x000000, 0);
        renderer.setPixelRatio(1);
        renderer.outputColorSpace = THREE.SRGBColorSpace;
        shared = { canvas, renderer };
    }
    shared.renderer.setSize(width, height, false);
    return shared;
}

async function readStream(ref) {
    return ref ? await ref.arrayBuffer() : null;
}

/** Loads the GLB and applies the streamed textures by material name; materials with missing textures turn neutral. */
async function loadModel(glbRef, textures, neutralMaterials) {
    const buffer = await readStream(glbRef);
    const manager = new THREE.LoadingManager();
    // External image URIs in the GLB point at files the browser cannot reach; textures come from .NET instead.
    manager.setURLModifier(url => (url.startsWith('data:') || url.startsWith('blob:')) ? url : EMPTY_PNG);
    const gltf = await new GLTFLoader(manager).parseAsync(buffer, '');
    const model = gltf.scene;
    model.animations = gltf.animations ?? [];

    const byKey = new Map();
    for (const t of textures ?? []) {
        const bytes = await readStream(t.stream);
        const bitmap = await createImageBitmap(new Blob([bytes], { type: t.mime }), { imageOrientation: 'from-image', premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
        const tex = new THREE.Texture(bitmap);
        tex.flipY = false; // glTF UV convention
        tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
        tex.colorSpace = t.slot === 'normalMap' ? THREE.NoColorSpace : THREE.SRGBColorSpace;
        tex.needsUpdate = true;
        byKey.set(`${t.material}|${t.slot}`, tex);
    }
    const neutral = new Set(neutralMaterials ?? []);

    model.traverse(o => {
        if (!o.isMesh) return;
        const mats = Array.isArray(o.material) ? o.material : [o.material];
        for (const m of mats) {
            if (!m) continue;
            const map = byKey.get(`${m.name}|map`);
            if (map) { m.map = map; if (m.color) m.color.set(0xffffff); }
            else if (neutral.has(m.name)) { m.map = null; if (m.color) m.color.set(NEUTRAL); }
            const normal = byKey.get(`${m.name}|normalMap`);
            if (normal) m.normalMap = normal;
            const emissive = byKey.get(`${m.name}|emissiveMap`);
            if (emissive) { m.emissiveMap = emissive; if (m.emissive) m.emissive.set(0xffffff); }
            m.side = THREE.DoubleSide;
            m.needsUpdate = true;
        }
        if (o.isSkinnedMesh) o.frustumCulled = false; // its bounding sphere is the rest pose, not the animated one
    });
    return model;
}

/**
 * A clip without a mesh (skeleton only) is drawn as a stick figure: a thin cylinder per bone, re-posed by update().
 * Returns null when the model has a mesh.
 */
function addSticks(model) {
    let hasMesh = false;
    model.traverse(o => { if (o.isMesh) hasMesh = true; });
    if (hasMesh) return null;
    const pairs = [];
    model.traverse(o => { if (o !== model && o.parent && o.parent !== model && o.parent.parent !== model) pairs.push([o.parent, o]); });
    if (pairs.length === 0) return null;

    model.updateMatrixWorld(true);
    const inv = new THREE.Matrix4().copy(model.matrixWorld).invert();
    const extent = new THREE.Box3();
    const p = new THREE.Vector3();
    model.traverse(o => { if (o !== model) extent.expandByPoint(p.setFromMatrixPosition(o.matrixWorld).applyMatrix4(inv)); });
    const radius = Math.max(extent.getSize(new THREE.Vector3()).length(), 1e-3) * 0.012;

    const geometry = new THREE.CylinderGeometry(1, 1, 1, 8);
    geometry.translate(0, 0.5, 0);
    const material = new THREE.MeshStandardMaterial({ color: 0x4fc3f7, roughness: 0.6 });
    const group = new THREE.Group();
    model.add(group);
    const sticks = pairs.map(([a, b]) => {
        const mesh = new THREE.Mesh(geometry, material);
        group.add(mesh);
        return { a, b, mesh };
    });
    const up = new THREE.Vector3(0, 1, 0), pa = new THREE.Vector3(), pb = new THREE.Vector3(), dir = new THREE.Vector3();
    const update = () => {
        inv.copy(model.matrixWorld).invert();
        for (const s of sticks) {
            pa.setFromMatrixPosition(s.a.matrixWorld).applyMatrix4(inv);
            pb.setFromMatrixPosition(s.b.matrixWorld).applyMatrix4(inv);
            dir.subVectors(pb, pa);
            const length = dir.length();
            s.mesh.visible = length > 1e-6;
            if (!s.mesh.visible) continue;
            s.mesh.position.copy(pa);
            s.mesh.quaternion.setFromUnitVectors(up, dir.divideScalar(length));
            s.mesh.scale.set(radius, length, radius);
        }
        group.updateMatrixWorld(true);
    };
    update();
    return { update };
}

/**
 * Wraps the model: the orientation correction is a root rotation (X, then Y, then Z, in degrees), and the model is
 * centered on its bounding sphere so the framing never depends on its scale.
 */
function stage(model, orientation, measure) {
    const oriented = new THREE.Group();
    oriented.add(model);
    oriented.rotation.set(deg(orientation?.rotX ?? 0), deg(orientation?.rotY ?? 0), deg(orientation?.rotZ ?? 0), 'ZYX');
    const centered = new THREE.Group();
    centered.add(oriented);
    centered.updateMatrixWorld(true);
    const box = measure ? measure(centered) : new THREE.Box3().setFromObject(centered, true);
    if (box.isEmpty()) throw new Error('The model has no visible geometry.');
    const sphere = box.getBoundingSphere(new THREE.Sphere());
    centered.position.copy(sphere.center).negate();
    centered.updateMatrixWorld(true);

    const scene = new THREE.Scene();
    scene.add(centered);
    scene.add(new THREE.HemisphereLight(0xffffff, 0x404040, 1.6));
    scene.add(new THREE.AmbientLight(0xffffff, 0.35));
    const camera = new THREE.PerspectiveCamera(30, 1, sphere.radius / 100, sphere.radius * 100);
    const key = new THREE.DirectionalLight(0xffffff, 1.6);
    key.position.set(1, 1.5, 2);
    camera.add(key);
    scene.add(camera);
    return { scene, camera, radius: sphere.radius };
}

function place(camera, radius, elevation, azimuth, aspect = 1) {
    const fov = deg(camera.fov);
    const distance = radius / Math.sin(Math.min(fov, fov * aspect) / 2) * 1.04;
    const e = deg(elevation), a = deg(azimuth);
    camera.aspect = aspect;
    camera.position.set(distance * Math.cos(e) * Math.sin(a), distance * Math.sin(e), distance * Math.cos(e) * Math.cos(a));
    camera.lookAt(0, 0, 0);
    camera.updateProjectionMatrix();
}

function toBlob(canvas, quality) {
    return new Promise((resolve, reject) => canvas.toBlob(b => b ? resolve(b) : reject(new Error('Encoding failed.')), 'image/webp', quality / 100));
}

function dispose(object) {
    object.traverse(o => {
        o.geometry?.dispose?.();
        const mats = Array.isArray(o.material) ? o.material : o.material ? [o.material] : [];
        for (const m of mats) {
            for (const v of Object.values(m)) if (v?.isTexture) v.dispose();
            m.dispose();
        }
    });
}

function concat(parts) {
    const total = parts.reduce((n, p) => n + p.length, 0);
    const out = new Uint8Array(total);
    let at = 0;
    for (const p of parts) { out.set(p, at); at += p.length; }
    return out;
}

/** Renders rings × azimuths views. Returns { lengths, width, height, data } with all WebP files concatenated. */
export async function renderViews(glbRef, textures, request) {
    const model = await loadModel(glbRef, textures, request.neutralMaterials);
    const { scene, camera, radius } = stage(model, request.orientation);
    const { canvas, renderer } = sharedRenderer(request.size, request.size);
    const parts = [];
    try {
        for (const elevation of request.elevations) {
            for (let i = 0; i < request.azimuths; i++) {
                place(camera, radius, elevation, i * 360 / request.azimuths);
                renderer.render(scene, camera);
                const blob = await toBlob(canvas, request.quality);
                parts.push(new Uint8Array(await blob.arrayBuffer()));
            }
        }
    } finally {
        dispose(scene);
    }
    return { lengths: parts.map(p => p.length), width: request.size, height: request.size, data: DotNet.createJSStreamReference(concat(parts)) };
}

/**
 * Plays the first clip and renders it from one three-quarter view at request.fps for at most request.maxSeconds.
 * The framing covers the whole motion (bounds over sampled poses). Returns the frames like renderViews.
 */
export async function renderAnimation(glbRef, textures, request) {
    const model = await loadModel(glbRef, textures, request.neutralMaterials);
    const clip = model.animations[0];
    if (!clip) throw new Error('The file has no animation clip.');
    const sticks = addSticks(model);
    const mixer = new THREE.AnimationMixer(model);
    mixer.clipAction(clip).play();
    const duration = Math.min(clip.duration, request.maxSeconds);
    const count = Math.max(1, Math.round(duration * request.fps));
    const times = Array.from({ length: count }, (_, i) => i / request.fps);
    let root = model;
    const pose = t => {
        mixer.setTime(t);
        root.updateMatrixWorld(true);
        sticks?.update();
    };
    const every = Math.max(1, Math.ceil(count / 8));
    const { scene, camera, radius } = stage(model, request.orientation, centered => {
        root = centered;
        const box = new THREE.Box3();
        times.forEach((t, i) => {
            if (i % every !== 0 && i !== count - 1) return;
            pose(t);
            box.union(new THREE.Box3().setFromObject(centered, true));
        });
        return box;
    });
    const { canvas, renderer } = sharedRenderer(request.size, request.size);
    place(camera, radius, 15, 30);
    const parts = [];
    try {
        for (const t of times) {
            pose(t);
            renderer.render(scene, camera);
            const blob = await toBlob(canvas, request.quality);
            parts.push(new Uint8Array(await blob.arrayBuffer()));
        }
    } finally {
        mixer.stopAllAction();
        dispose(scene);
    }
    return { lengths: parts.map(p => p.length), width: request.size, height: request.size, data: DotNet.createJSStreamReference(concat(parts)) };
}

/** Up-axis candidates 1–6, in the same order as OrientationProbe.Candidates in .NET. */
const CANDIDATES = [
    { label: '1', rotX: 0, rotY: 0, rotZ: 0 },    // +Y up
    { label: '2', rotX: 180, rotY: 0, rotZ: 0 },  // -Y up
    { label: '3', rotX: 270, rotY: 0, rotZ: 0 },  // +Z up
    { label: '4', rotX: 90, rotY: 0, rotZ: 0 },   // -Z up
    { label: '5', rotX: 0, rotY: 0, rotZ: 90 },   // +X up
    { label: '6', rotX: 0, rotY: 0, rotZ: 270 },  // -X up
];

/** A 3×2 contact sheet of the six candidate up-axes from the same three-quarter view, labeled 1–6. */
export async function renderProbe(glbRef, textures, request) {
    const tile = request.tileSize;
    const sheet = document.createElement('canvas');
    sheet.width = tile * 3;
    sheet.height = tile * 2;
    const ctx = sheet.getContext('2d');
    const model = await loadModel(glbRef, textures, request.neutralMaterials);
    const { canvas, renderer } = sharedRenderer(tile, tile);
    try {
        for (let i = 0; i < CANDIDATES.length; i++) {
            const c = CANDIDATES[i];
            const { scene, camera, radius } = stage(model, c);
            place(camera, radius, 20, 45);
            renderer.render(scene, camera);
            const x = (i % 3) * tile, y = Math.floor(i / 3) * tile;
            ctx.drawImage(canvas, x, y);
            ctx.font = `bold ${Math.round(tile / 7)}px sans-serif`;
            ctx.textBaseline = 'top';
            ctx.lineWidth = Math.max(2, tile / 64);
            ctx.strokeStyle = 'white';
            ctx.fillStyle = '#d32f2f';
            ctx.strokeText(c.label, x + tile / 24, y + tile / 24);
            ctx.fillText(c.label, x + tile / 24, y + tile / 24);
        }
    } finally {
        dispose(model);
    }
    const blob = await toBlob(sheet, request.quality);
    const bytes = new Uint8Array(await blob.arrayBuffer());
    return { lengths: [bytes.length], width: sheet.width, height: sheet.height, data: DotNet.createJSStreamReference(bytes) };
}

/** Interactive viewer for the asset details ("open original" preview). Returns a handle with dispose(). */
export async function createViewer(container, glbRef, textures, request) {
    const model = await loadModel(glbRef, textures, request.neutralMaterials);
    const sticks = addSticks(model);
    // Clips play in a loop.
    const mixer = model.animations.length > 0 ? new THREE.AnimationMixer(model) : null;
    mixer?.clipAction(model.animations[0]).play();
    const clock = new THREE.Clock();
    const { scene, camera, radius } = stage(model, request.orientation);
    const renderer = new THREE.WebGLRenderer({ alpha: true, antialias: true });
    renderer.setClearColor(0x000000, 0);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.setPixelRatio(window.devicePixelRatio || 1);
    container.replaceChildren(renderer.domElement);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    let frame = 0, disposed = false;
    const resize = () => {
        const w = container.clientWidth || 400, h = container.clientHeight || 400;
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
    };
    place(camera, radius, 20, 45, (container.clientWidth || 1) / (container.clientHeight || 1));
    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(container);
    const loop = () => {
        if (disposed) return;
        controls.update();
        if (mixer) {
            mixer.update(clock.getDelta());
            sticks?.update();
        }
        renderer.render(scene, camera);
        frame = requestAnimationFrame(loop);
    };
    loop();
    return {
        setOrientation(o) {
            const centered = scene.children[0];
            const oriented = centered.children[0];
            oriented.rotation.set(deg(o.rotX ?? 0), deg(o.rotY ?? 0), deg(o.rotZ ?? 0), 'ZYX');
            // Re-center: the rotated model's bounds differ from the authored ones.
            centered.position.set(0, 0, 0);
            centered.updateMatrixWorld(true);
            const sphere = new THREE.Box3().setFromObject(centered, true).getBoundingSphere(new THREE.Sphere());
            centered.position.copy(sphere.center).negate();
            centered.updateMatrixWorld(true);
        },
        dispose() {
            disposed = true;
            cancelAnimationFrame(frame);
            mixer?.stopAllAction();
            observer.disconnect();
            controls.dispose();
            dispose(scene);
            renderer.dispose();
            container.replaceChildren();
        },
    };
}
