const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '..');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json')));
assert.equal(manifest.manifest_version, 3);
for (const icon of Object.values(manifest.icons)) assert.equal(fs.readFileSync(path.join(root, 'extension', icon)).subarray(1, 4).toString(), 'PNG');
for (const file of ['background.js', 'popup.js']) new vm.Script(fs.readFileSync(path.join(root, 'extension', file), 'utf8'));
new vm.Script(fs.readFileSync(path.join(root, 'ui/index.html'), 'utf8').match(/<script>([\s\S]*?)<\/script>/)[1]);
const session = {}, local = { key: 'a'.repeat(64) }, sent = [], events = {};
let offline = false, reject = false, impostor = false;
const nodeCrypto = require('node:crypto');
const storage = data => ({
  get: async key => ({ [key]: data[key] }),
  set: async value => Object.assign(data, structuredClone(value)),
  remove: async key => { delete data[key]; }
});
const chrome = {
  storage: { session: storage(session), local: storage(local) },
  cookies: { getAll: async ({ url }) => [{ name: 'session', value: new URL(url).host, path: '/', domain: new URL(url).host }] },
  tabs: { get: async id => ({ url: `https://page.example/${id}`, title: 'Recording' }), onRemoved: { addListener: f => events.remove = f } },
  action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {} },
  webRequest: { onHeadersReceived: { addListener: (f, filter, extra) => { events.headers = f; assert.deepEqual(extra, ['responseHeaders']); } } },
  webNavigation: { onBeforeNavigate: { addListener: f => events.navigate = f } },
  runtime: { id: 'test', onMessage: { addListener: f => events.message = f } }
};
const context = vm.createContext({ chrome, URL, navigator: { userAgent: 'Browser Test' }, TextEncoder, AbortSignal, crypto: globalThis.crypto, Uint8Array,
  btoa: value => Buffer.from(value, 'binary').toString('base64'),
  fetch: async (url, options) => {
    if (offline) throw new TypeError('Failed to fetch');
    if (url.includes('/hello?c=')) {
      // PastDow signs the challenge with the pairing key; an impostor on the port cannot
      const challenge = new URL(url).searchParams.get('c');
      const proof = impostor ? 'f'.repeat(64) : nodeCrypto.createHmac('sha256', local.key).update('pastdow-hello:' + challenge).digest('hex');
      return { ok: true, status: 200, json: async () => ({ proof }) };
    }
    sent.push({ url, options }); return { ok: !reject, status: reject ? 403 : 200 };
  } });
const source = fs.readFileSync(path.join(root, 'extension/background.js'), 'utf8');
// Assertions involving VM arrays compare values rather than cross-realm prototypes.
chrome.webRequest.onHeadersReceived.addListener = (f, filter, extra) => { events.headers = f; assert.equal(JSON.stringify(extra), '["responseHeaders"]'); };
vm.runInContext(source, context);
const classify = (url, type, length, range) => context.classify({ tabId: 1, url, statusCode: 200,
  responseHeaders: [{ name: 'Content-Type', value: type }, { name: 'Content-Length', value: String(length || 0) }, { name: 'Content-Range', value: range || '' }] });
const message = body => new Promise(resolve => events.message(body, { id: 'test' }, resolve));
const flush = async () => { await vm.runInContext('Promise.all([...chains.values()])', context); for (let i = 0; i < 10; i++) await new Promise(setImmediate); };
const detect = (url, type, length, tabId = 1) => events.headers({ tabId, url, statusCode: 200, responseHeaders: [
  { name: 'Content-Type', value: type }, { name: 'Content-Length', value: String(length) }] });
(async () => {
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
  detect('https://cdn.example/video.mp4?signature=new', 'video/mp4', 400000);
  detect('https://subs.example/caption.es.vtt?token=1', 'text/vtt', 20);
  detect('https://cdn.example/other.webm', 'video/webm', 700000, 2);
  await flush();
  assert.equal(session['tab:1'].media.length, 1);
  assert.equal(session['tab:1'].media[0].size, 900000);
  assert.match(session['tab:1'].media[0].url, /signature=new/);
  assert.equal(session['tab:1'].subtitles.length, 1);
  const found = JSON.parse(sent.filter(x => x.url.endsWith('/found')).at(-2).options.body);
  assert.ok(sent.some(x => x.options.headers['X-PastDow-Key'] === local.key));
  assert.ok(sent.some(x => JSON.parse(x.options.body || '{}').subtitles?.[0]?.cookie === 'session=subs.example'));
  assert.ok(found.userAgent);
  const id = session['tab:1'].media[0].id;
  assert.equal((await message({ t: 'download', tabId: 1, id })).ok, true);
  offline = true;
  const fallback = await message({ t: 'download', tabId: 1, id });
  const body = JSON.parse(Buffer.from(fallback.protocol.split('d=')[1], 'base64url').toString());
  assert.equal(body.key, local.key); assert.equal(body.subtitles[0].lang, 'es');
  assert.equal(body.cookie, 'session=cdn.example');
  offline = false; impostor = true;
  const before = sent.length;
  // something else on the port: nothing is sent and the protocol fallback is not used either
  const squat = await message({ t: 'download', tabId: 1, id });
  assert.match(squat.error, /another program/); assert.equal(squat.protocol, undefined); assert.equal(sent.length, before);
  impostor = false; reject = true;
  assert.match((await message({ t: 'download', tabId: 1, id })).error, /rejected/);
  events.navigate({ tabId: 1, frameId: 1 }); await flush(); assert.equal(session['tab:1'].media.length, 1);
  events.navigate({ tabId: 1, frameId: 0 }); await flush(); assert.equal(session['tab:1'], undefined);
  assert.equal(session['tab:2'].media.length, 1);
  console.log('Extension: manifest, icons, JavaScript syntax, filters, sizes, subtitles, dedupe, per-tab state, headers, queue, fallback, server proof, auth rejection and navigation passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
