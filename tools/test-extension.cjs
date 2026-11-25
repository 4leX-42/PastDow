const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '..');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json')));
assert.equal(manifest.manifest_version, 3);
assert.ok(manifest.permissions.includes('scripting'));
for (const icon of Object.values(manifest.icons)) assert.equal(fs.readFileSync(path.join(root, 'extension', icon)).subarray(1, 4).toString(), 'PNG');
for (const file of ['background.js', 'popup.js']) new vm.Script(fs.readFileSync(path.join(root, 'extension', file), 'utf8'));
new vm.Script(fs.readFileSync(path.join(root, 'ui/index.html'), 'utf8').match(/<script>([\s\S]*?)<\/script>/)[1]);
const session = {}, local = { key: 'a'.repeat(64) }, sent = [], wire = [], events = {};
let scriptGate = null;
const timers = new Map(); let timerId = 0, posterFetches = 0;
const probe = { readyState: 2, videoWidth: 640, videoHeight: 360, currentTime: 1, paused: false, controls: false };
let captureFails = false, activeTab = 1, captures = 0, scriptFails = false, posterMode = false, jpegBytes = [1, 2, 3];
const thumbnail = 'data:image/jpeg;base64,AQID';
let offline = false, reject = false, impostor = false, pairingFile = null;
const nodeCrypto = require('node:crypto');
const storage = data => ({
  get: async key => ({ [key]: data[key] }),
  set: async value => Object.assign(data, structuredClone(value)),
  remove: async key => { delete data[key]; }
});
const chrome = {
  storage: { session: storage(session), local: storage(local) },
  cookies: { getAll: async ({ url }) => [{ name: 'session', value: new URL(url).host, path: '/', domain: new URL(url).host }] },
  scripting: { executeScript: async options => { assert.equal(options.target.allFrames, true); if (scriptFails) throw Error('restricted'); if (scriptGate) await scriptGate; return [{ result: { ...probe, poster: posterMode ? 'https://images.example/poster.jpg' : null, rect: { x: 10, y: 20, width: 640, height: 360 }, area: 230400, devicePixelRatio: 2, duration: 125 } }]; } },
  tabs: { query: async () => [{ id: activeTab }], captureVisibleTab: async (windowId, options) => { captures++; assert.equal(options.format, 'jpeg'); if (captureFails) throw Error('capture failed'); return thumbnail; }, get: async id => ({ id, windowId: 1, url: `https://page.example/${id}`, title: 'Recording' }), onRemoved: { addListener: f => events.remove = f } },
  action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async ({ color }) => assert.equal(color, '#f5f5f3'), setBadgeTextColor: async ({ color }) => assert.equal(color, '#000000') },
  webRequest: { onHeadersReceived: { addListener: (f, filter, extra) => { events.headers = f; assert.deepEqual(extra, ['responseHeaders']); } } },
  webNavigation: { onBeforeNavigate: { addListener: f => events.navigate = f } },
  runtime: { id: 'test', onMessage: { addListener: f => events.message = f } }
};
const context = vm.createContext({
  setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
  clearTimeout: id => timers.delete(id),
  createImageBitmap: async () => ({ width: 1920, height: 1080, close() {} }), OffscreenCanvas: class { constructor(w, h) { assert.ok(w <= 320); this.width = w; this.height = h; } getContext() { return { drawImage: (...args) => { if (args[1] !== 0) { assert.equal(args[1], 20); assert.equal(args[2], 40); assert.equal(args[3], 1280); assert.equal(args[4], 720); } } }; } async convertToBlob(options) { assert.equal(options.quality, .7); return { arrayBuffer: async () => new Uint8Array(jpegBytes).buffer }; } }, chrome, URL, navigator: { userAgent: 'Browser Test' }, TextEncoder, AbortSignal, crypto: globalThis.crypto, Uint8Array,
  btoa: value => Buffer.from(value, 'binary').toString('base64'),
  fetch: async (url, options) => {
    if (url === 'https://images.example/poster.jpg') { posterFetches++; return { ok: true, headers: { get: () => 'image/jpeg' }, blob: async () => ({ size: 1000 }) }; }
    if (url === thumbnail) return { blob: async () => ({}) };
    if (offline) throw new TypeError('Failed to fetch');
    if (url.endsWith('/pairing.json')) return { ok: !!pairingFile, status: pairingFile ? 200 : 404, json: async () => pairingFile };
    // what actually goes over the wire, for the "no key, no cookie in clear" checks
    wire.push(options.body || '');
    assert.equal(url, 'http://127.0.0.1:47821/msg');
    assert.equal(JSON.stringify(Object.keys(options.headers)), '["Content-Type"]');
    if (impostor) return { ok: true, status: 200, json: async () => ({ proof: 'f'.repeat(64) }) };
    if (reject) return { ok: false, status: 403 };
    // PastDow's side of the envelope (Sealed in BrowserLink.cs)
    const derive = label => nodeCrypto.createHmac('sha256', local.key).update(label).digest();
    const env = JSON.parse(options.body), macKey = derive('pastdow-mac');
    const mac = nodeCrypto.createHmac('sha256', macKey).update(`pastdow-msg:${env.iv}:${env.ct}`).digest('hex');
    assert.equal(mac, env.mac);
    const decipher = nodeCrypto.createDecipheriv('aes-256-cbc', derive('pastdow-enc'), Buffer.from(env.iv, 'hex'));
    const msg = JSON.parse(Buffer.concat([decipher.update(Buffer.from(env.ct, 'base64')), decipher.final()]).toString());
    assert.ok(Math.abs(Date.now() - msg.ts) < 5000); assert.match(msg.nonce, /^[a-f0-9]{32}$/);
    sent.push({ url: '/' + msg.op, options: { body: JSON.stringify(msg.media || {}) } });
    const proof = nodeCrypto.createHmac('sha256', macKey).update('pastdow-ok:' + msg.nonce).digest('hex');
    return { ok: true, status: 200, json: async () => ({ proof }) };
  } });
const source = fs.readFileSync(path.join(root, 'extension/background.js'), 'utf8');
// Assertions involving VM arrays compare values rather than cross-realm prototypes.
chrome.webRequest.onHeadersReceived.addListener = (f, filter, extra) => { events.headers = f; assert.equal(JSON.stringify(extra), '["responseHeaders"]'); };
vm.runInContext(source, context);
const classify = (url, type, length, range) => context.classify({ tabId: 1, url, statusCode: 200,
  responseHeaders: [{ name: 'Content-Type', value: type }, { name: 'Content-Length', value: String(length || 0) }, { name: 'Content-Range', value: range || '' }] });
const message = body => new Promise(resolve => events.message(body, { id: 'test' }, resolve));
const flush = async () => {
  await vm.runInContext('Promise.all([...chains.values()])', context);
  await vm.runInContext('Promise.all([...previews.values()])', context);
  await vm.runInContext('Promise.all([...chains.values()])', context);
  // pushes to PastDow are sealed with WebCrypto first: wait until none is in flight
  for (let i = 0; i < 2000 && (i < 10 || vm.runInContext('pushes.size', context) > 0); i++) await new Promise(setImmediate);
};
const detect = (url, type, length, tabId = 1) => events.headers({ tabId, url, statusCode: 200, responseHeaders: [
  { name: 'Content-Type', value: type }, { name: 'Content-Length', value: String(length) }] });
(async () => {
  const fakeVideo = (width, height, duration, visibility = 'visible') => ({
    getBoundingClientRect: () => ({ left: 10, top: 20, right: 10 + width, bottom: 20 + height, width, height }),
    ...probe, hasAttribute: () => true, duration, poster: 'https://images.example/poster.jpg', style: { display: 'block', visibility, opacity: '1' }
  });
  const frame = {}; frame.top = frame;
  const inspected = vm.runInNewContext('(' + context.inspectVideo.toString() + ')()', {
    window: frame, innerWidth: 1000, innerHeight: 800, devicePixelRatio: 2, getComputedStyle: v => v.style,
    document: { querySelector: () => ({ content: 'https://images.example/og.jpg' }), querySelectorAll: () => [fakeVideo(800, 700, 999, 'hidden'), fakeVideo(640, 360, 125), fakeVideo(100, 100, Infinity)] }
  });
  assert.equal(inspected.duration, 125); assert.equal(inspected.rect.width, 640); assert.equal(inspected.rect.height, 360);
  assert.equal(inspected.devicePixelRatio, 2); assert.equal(inspected.og, 'https://images.example/og.jpg');
  assert.equal(inspected.readyState, 2); assert.equal(inspected.videoWidth, 640); assert.equal(inspected.videoHeight, 360);
  assert.equal(inspected.currentTime, 1); assert.equal(inspected.paused, false); assert.equal(inspected.controls, true);
  const readyVideo = { ...probe, rect: { x: 10, y: 20, width: 640, height: 360 } };
  assert.equal(context.readyForThumbnail(readyVideo), true);
  for (const invalid of [{ readyState: 1 }, { videoWidth: 0 }, { currentTime: .3, paused: true }, { rect: { width: 119, height: 68 } }, { rect: { width: 120, height: 67 } }])
    assert.equal(context.readyForThumbnail({ ...readyVideo, ...invalid }), false);
  assert.equal(context.readyForThumbnail({ ...readyVideo, currentTime: 0, paused: false }), true);
  assert.equal(context.readyForThumbnail({ ...readyVideo, currentTime: .31, paused: true }), true);
  const cropped = context.frameRect({ ...readyVideo, controls: true });
  assert.ok(Math.abs(cropped.width / cropped.height - 16 / 9) < .0001);
  assert.ok(cropped.y + cropped.height <= 20 + 360 * .82);

  assert.equal(classify('https://cdn.example/a.ts', 'video/mp2t', 999999), null);
  assert.equal(classify('https://cdn.example/a.m4s', 'video/iso.segment', 999999), null);
  assert.equal(classify('https://cdn.example/a.aac', 'audio/aac', 999999), null);
  assert.equal(classify('https://cdn.example/a.mp4', 'video/mp4', 1024), null);
  assert.equal(classify('https://cdn.example/a.mp4', 'video/mp4', 1024, 'bytes 0-1023/900000').size, 900000);
  assert.equal(classify('https://cdn.example/a.m3u8', 'application/vnd.apple.mpegurl', 0).kind, 'HLS');
  // manifests are a few KB: the 200 KB floor for direct files must not hide them
  assert.equal(classify('https://cdn.example/master.m3u8', 'application/vnd.apple.mpegurl', 2048).kind, 'HLS');
  assert.equal(classify('https://cdn.example/manifest.mpd', 'application/dash+xml', 4096).kind, 'DASH');
  assert.equal(classify('https://cdn.example/ad.mp4', 'video/mp4', 50000), null);
  assert.equal(classify('https://cdn.example/a.mpd', 'application/dash+xml', 900000).kind, 'DASH');
  assert.equal(classify('https://cdn.example/clip', 'audio/ogg', 900000).kind, 'OGG');
  assert.equal(classify('https://subs.example/tiny.spa.vtt', 'text/vtt', 4).lang, 'es');
  assert.equal(classify('https://subs.example/a.eng.srt', 'application/x-subrip', 4).lang, 'en');
  assert.equal(classify('https://subs.example/unknown', 'text/vtt', 4).lang, 'und');
  detect('https://cdn.example/video.mp4?signature=old', 'video/mp4', 900000);
  await flush();
  assert.equal(session['tab:1'].media[0].thumb, thumbnail);
  assert.equal(session['tab:1'].media[0].duration, 125);
  detect('https://cdn.example/video.mp4?signature=new', 'video/mp4', 400000);
  detect('https://subs.example/caption.es.vtt?token=1', 'text/vtt', 20);
  detect('https://cdn.example/other.webm', 'video/webm', 700000, 2);
  await flush();
  assert.equal(session['tab:1'].media.length, 1);
  assert.equal(session['tab:1'].media[0].size, 900000);
  assert.match(session['tab:1'].media[0].url, /signature=new/);
  assert.equal(session['tab:1'].subtitles.length, 1);
  const found = JSON.parse(sent.filter(x => x.url.endsWith('/found')).find(x => JSON.parse(x.options.body).url.includes('video.mp4')).options.body);
  // neither the key nor any cookie is ever readable on the wire
  assert.ok(wire.length > 0 && wire.every(b => !b.includes(local.key) && !b.includes('session=')));
  assert.ok(sent.some(x => JSON.parse(x.options.body || '{}').subtitles?.[0]?.cookie === 'session=subs.example'));
  assert.ok(found.userAgent);
  const id = session['tab:1'].media[0].id;
  assert.equal((await message({ t: 'download', tabId: 1, id })).ok, true);
  const queuedMedia = JSON.parse(sent.filter(x => x.url === '/queue').at(-1).options.body);
  assert.equal(queuedMedia.thumb, thumbnail); assert.equal(queuedMedia.duration, 125);
  assert.ok(sent.some(x => x.url === '/found' && JSON.parse(x.options.body).thumb === thumbnail));
  const count = captures;
  await message({ t: 'list', tabId: 2, refresh: true });
  assert.equal(captures, count, 'inactive tabs must never capture');
  captureFails = true;
  detect('https://cdn.example/no-thumb.mp4', 'video/mp4', 900000, 3); await flush();
  assert.equal(session['tab:3'].media.length, 1); assert.equal(session['tab:3'].media[0].thumb, undefined);
  activeTab = 1; await message({ t: 'list', tabId: 1, refresh: true });
  assert.equal(session['tab:1'].media[0].thumb, thumbnail, 'failed recapture preserves thumbnail');
  scriptFails = true; detect('https://cdn.example/restricted.mp4', 'video/mp4', 900000, 4); await flush();
  assert.equal(session['tab:4'].media.length, 1); scriptFails = false; captureFails = false;
  posterMode = true; captureFails = true; activeTab = 5; jpegBytes = [4, 5, 6];
  detect('https://cdn.example/poster.mp4', 'video/mp4', 900000, 5); await flush();
  assert.equal(session['tab:5'].media[0].thumb, 'data:image/jpeg;base64,BAUG');
  assert.equal(session['tab:5'].media[0].thumbQuality, 1);
  captureFails = false; jpegBytes = [1, 2, 3];
  await message({ t: 'list', tabId: 5 }); await flush();
  assert.equal(session['tab:5'].media[0].thumb, thumbnail, 'visible capture replaces poster');
  assert.equal(session['tab:5'].media[0].thumbQuality, 3);
  captureFails = true; jpegBytes = [7, 8, 9];
  await message({ t: 'list', tabId: 5 }); await flush();
  assert.equal(session['tab:5'].media[0].thumb, thumbnail, 'poster must not replace better capture');
  jpegBytes = Array(60000).fill(1);
  assert.equal(await context.smallJpeg({}), null, 'oversized encoded thumbnails are skipped');
  posterMode = false; captureFails = false; activeTab = 1; jpegBytes = [1, 2, 3];
  let releaseScript;
  scriptGate = new Promise(resolve => { releaseScript = resolve; });
  detect('https://cdn.example/slow.mp4', 'video/mp4', 900000, 6);
  await vm.runInContext('Promise.all([...chains.values()])', context);
  assert.equal(session['tab:6'].media.length, 1, 'detection does not wait for thumbnail');
  events.navigate({ tabId: 6, frameId: 0 });
  await vm.runInContext('Promise.all([...chains.values()])', context);
  scriptGate = null; releaseScript(); await flush();
  assert.equal(session['tab:6'], undefined, 'late thumbnail must not restore a navigated tab');
  // The first response arrives before the script-created player has decoded a frame.
  activeTab = 7; probe.readyState = 0; probe.videoWidth = 0; probe.videoHeight = 0; probe.currentTime = 0; probe.paused = true;
  const beforeReady = captures;
  detect('https://cdn.example/delayed.webm', 'video/webm', 900000, 7); await flush();
  assert.equal(captures, beforeReady, 'not-ready video must never capture');
  assert.equal(session['tab:7'].media[0].thumb, undefined);
  const retryHandles = vm.runInContext('previewRetries.get(7).timers', context);
  assert.equal(JSON.stringify(Array.from(retryHandles, id => timers.get(id).delay)), '[1500,4000,9000,20000]');
  const runRetry = async id => { const timer = timers.get(id); assert.ok(timer); timers.delete(id); await timer.callback(); await flush(); };
  await runRetry(retryHandles[0]);
  assert.equal(captures, beforeReady, 'retry still waits for readiness');
  posterMode = true; jpegBytes = [4, 5, 6];
  await runRetry(retryHandles[1]);
  assert.equal(captures, beforeReady);
  assert.equal(session['tab:7'].media[0].thumbQuality, 1, 'poster is a fallback while no frame can be taken');
  assert.equal(session['tab:7'].media[0].thumb, 'data:image/jpeg;base64,BAUG');
  Object.assign(probe, { readyState: 2, videoWidth: 640, videoHeight: 360, currentTime: .5, paused: false });
  jpegBytes = [1, 2, 3];
  const beforePosterFetch = posterFetches;
  await runRetry(retryHandles[2]);
  assert.equal(captures, beforeReady + 1, 'ready retry captures a decoded frame');
  assert.equal(posterFetches, beforePosterFetch, 'poster must not be fetched when a frame capture succeeds');
  assert.equal(session['tab:7'].media[0].thumb, thumbnail);
  assert.equal(session['tab:7'].media[0].thumbQuality, 3);
  assert.ok(sent.some(x => x.url === '/found' && JSON.parse(x.options.body).url.endsWith('/delayed.webm') && JSON.parse(x.options.body).thumb === thumbnail), 'ready thumbnail update is pushed to PastDow');
  assert.equal(timers.has(retryHandles[3]), false, 'remaining retries stop once a good frame exists');
  await message({ t: 'list', tabId: 7 }); await flush();
  assert.equal(captures, beforeReady + 1, 'opening popup preserves a good thumbnail');
  events.navigate({ tabId: 2, frameId: 0 }); await flush();
  assert.equal(vm.runInContext('previewRetries.has(2)', context), false, 'navigation cancels pending retries');
  detect('https://cdn.example/other.webm', 'video/webm', 700000, 2); await flush();
  posterMode = false; activeTab = 1;
  offline = true;
  const fallback = await message({ t: 'download', tabId: 1, id });
  const body = JSON.parse(Buffer.from(fallback.protocol.split('d=')[1], 'base64url').toString());
  assert.equal(body.key, local.key); assert.equal(body.subtitles[0].lang, 'es');
  assert.equal(body.cookie, 'session=cdn.example');
  offline = false; impostor = true;
  const before = sent.length, wired = wire.length;
  // something else on the port: nothing is sent and the protocol fallback is not used either
  const squat = await message({ t: 'download', tabId: 1, id });
  assert.match(squat.error, /another program/); assert.equal(squat.protocol, undefined); assert.equal(sent.length, before);
  assert.ok(wire.slice(wired).every(b => !b.includes('session=') && !b.includes(local.key)));
  impostor = false; reject = true;
  assert.match((await message({ t: 'download', tabId: 1, id })).error, /rejected/);
  // installed from PastDow: the folder's pairing.json pairs the extension by itself; a stored key wins
  reject = false; delete local.key; chrome.runtime.getURL = file => 'chrome-extension://test/' + file;
  pairingFile = { key: 'c'.repeat(64) };
  assert.equal(await vm.runInContext('storedKey()', context), 'c'.repeat(64)); assert.equal(local.key, 'c'.repeat(64));
  pairingFile = { key: 'd'.repeat(64) }; assert.equal(await vm.runInContext('storedKey()', context), 'c'.repeat(64));
  delete local.key; pairingFile = { key: 'not-a-key' }; assert.equal(await vm.runInContext('storedKey()', context), undefined);
  local.key = 'a'.repeat(64);
  events.navigate({ tabId: 1, frameId: 1 }); await flush(); assert.equal(session['tab:1'].media.length, 1);
  events.navigate({ tabId: 1, frameId: 0 }); await flush(); assert.equal(session['tab:1'], undefined);
  assert.equal(session['tab:2'].media.length, 1);
  console.log('Extension: readiness gating, scheduled retries, controls crop, fallback-only posters, ready-frame updates, thumbnails, duration, active-tab capture, poster fallback and upgrade, size limit, visible-video selection, capture/script failures, preservation, found/queue payloads, manifest, icons, JavaScript syntax, filters, sizes, subtitles, dedupe, per-tab state, headers, queue, fallback, sealed messages, server proof, auto-pairing, auth rejection and navigation passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
