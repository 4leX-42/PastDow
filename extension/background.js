const ENDPOINT = 'http://127.0.0.1:47821';
const chains = new Map(), generations = new Map(), pushes = new Map();
const http = url => /^https?:\/\//i.test(url);
const identity = url => { const u = new URL(url); u.search = ''; u.hash = ''; return u.href; };
const language = url => {
  const tokens = decodeURIComponent(url).toLowerCase().split(/[^a-z]+/);
  return tokens.some(t => ['es', 'spa'].includes(t)) ? 'es' : tokens.some(t => ['en', 'eng'].includes(t)) ? 'en' : 'und';
};

function classify(details) {
  if (details.tabId < 0 || !http(details.url) || details.statusCode < 200 || details.statusCode >= 300) return null;
  const headers = Object.fromEntries((details.responseHeaders || []).map(h => [h.name.toLowerCase(), h.value || '']));
  const type = (headers['content-type'] || '').toLowerCase(), path = new URL(details.url).pathname.toLowerCase();
  if (/\.(vtt|srt)$/.test(path) || type.includes('text/vtt') || type.includes('application/x-subrip'))
    return { subtitle: true, ext: path.endsWith('.srt') || type.includes('subrip') ? 'srt' : 'vtt', lang: language(details.url) };
  if (/\.(ts|m4s|aac)$/.test(path) || type.includes('mp2t') || type.includes('iso.segment')) return null;
  const size = Number((headers['content-range'] || '').match(/\/(\d+)\s*$/)?.[1] || headers['content-length']) || 0;
  // stream manifests are always small text files: the size floor only applies to direct media files
  if (type.includes('mpegurl') || path.endsWith('.m3u8')) return { kind: 'HLS', size: 0 };
  if (type.includes('dash+xml') || path.endsWith('.mpd')) return { kind: 'DASH', size: 0 };
  if (!(/^(video|audio)\//.test(type) || /\.(mp4|m4v|webm|mov)$/.test(path))) return null;
  if (size > 0 && size < 200 * 1024) return null;   // previews, ads, tiny clips
  const kind = path.match(/\.(mp4|m4v|webm|mov)$/)?.[1]?.toUpperCase() || type.split('/')[1].split(';')[0].toUpperCase();
  return { kind, size };
}

function serial(tabId, work) {
  const next = (chains.get(tabId) || Promise.resolve()).catch(() => {}).then(work);
  chains.set(tabId, next);
  void next.catch(() => {}).finally(() => { if (chains.get(tabId) === next) chains.delete(tabId); });
  return next;
}
async function state(tabId) {
  const name = `tab:${tabId}`;
  return (await chrome.storage.session.get(name))[name] || { media: [], subtitles: [] };
}
async function cookie(url, tabId) {
  let cookies = await chrome.cookies.getAll({ url });
  // Newer Chromium can also expose the partition used by an embedded recording player.
  if (chrome.cookies.getPartitionKey) {
    try {
      const { partitionKey } = await chrome.cookies.getPartitionKey({ tabId, frameId: 0 });
      cookies = cookies.concat(await chrome.cookies.getAll({ url, partitionKey }));
    } catch { }
  }
  return [...new Map(cookies.map(c => [`${c.name}|${c.domain}|${c.path}`, c])).values()]
    .sort((a, b) => b.path.length - a.path.length).map(c => `${c.name}=${c.value}`).join('; ');
}
async function payload(item, data, tabId) {
  const tab = await chrome.tabs.get(tabId);
  return { url: item.url, kind: item.kind, size: item.size, page: tab.url, title: tab.title || 'Browser video',
    userAgent: navigator.userAgent, cookie: await cookie(item.url, tabId),
    subtitles: await Promise.all(data.subtitles.map(async s => ({ url: s.url, lang: s.lang, ext: s.ext, cookie: await cookie(s.url, tabId) }))) };
}
const hex = bytes => Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, '0')).join('');
const utf8 = text => new TextEncoder().encode(text);
const b64 = bytes => btoa(Array.from(new Uint8Array(bytes), b => String.fromCharCode(b)).join(''));
async function hmac(keyBytes, text) {
  const k = await crypto.subtle.importKey('raw', keyBytes, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await crypto.subtle.sign('HMAC', k, utf8(text)));
}
// The pairing key never leaves the browser. Every message to PastDow is sealed with keys derived from it
// (AES-256-CBC, then HMAC-SHA256 over iv and ciphertext; same scheme as Sealed in BrowserLink.cs), carries a
// one-time nonce and a timestamp, and PastDow's answer must sign that nonce. Anything else that receives the
// request, a program on the port or a more specific http.sys prefix, sees only ciphertext and cannot answer.
async function seal(key, message) {
  const encKey = await hmac(utf8(key), 'pastdow-enc'), macKey = await hmac(utf8(key), 'pastdow-mac');
  const iv = crypto.getRandomValues(new Uint8Array(16));
  const aes = await crypto.subtle.importKey('raw', encKey, { name: 'AES-CBC' }, false, ['encrypt']);
  const ct = b64(await crypto.subtle.encrypt({ name: 'AES-CBC', iv }, aes, utf8(JSON.stringify(message))));
  const ivHex = hex(iv);
  return { envelope: { iv: ivHex, ct, mac: hex(await hmac(macKey, `pastdow-msg:${ivHex}:${ct}`)) }, macKey };
}
// Installed from PastDow (MENU > BROWSER EXTENSION) the folder carries pairing.json with the key: pair by itself.
// A key pasted in the popup is kept in storage and wins over the file.
async function storedKey() {
  let { key } = await chrome.storage.local.get('key');
  if (key) return key;
  try {
    const r = await fetch(chrome.runtime.getURL('pairing.json'));
    const file = r.ok ? await r.json() : null;
    if (file && /^[a-f0-9]{64}$/.test(file.key)) { key = file.key; await chrome.storage.local.set({ key }); }
  } catch { /* loaded by hand without PastDow's file: paste the key in the popup */ }
  return key;
}
chrome.runtime.onInstalled?.addListener(() => { void storedKey(); });
async function request(op, media) {
  const key = await storedKey();
  if (!key) throw new Error('Paste your PastDow pairing key first');
  const nonce = hex(crypto.getRandomValues(new Uint8Array(16)));
  const { envelope, macKey } = await seal(key, { op, ts: Date.now(), nonce, ...(media ? { media } : {}) });
  const response = await fetch(ENDPOINT + '/msg', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(envelope), signal: AbortSignal.timeout(2500) });
  if (!response.ok) throw new Error(response.status === 403 ? 'Pairing key rejected, copy it again from PastDow' : `PastDow returned ${response.status}`);
  const { proof } = await response.json().catch(() => ({}));
  if (proof !== hex(await hmac(macKey, 'pastdow-ok:' + nonce)))
    throw new Error('Pairing key rejected, or another program answers on PastDow\'s port; copy the key again from PastDow');
  return true;
}
async function push(item, data, tabId, generation) {
  const id = `${tabId}:${item.id}`;
  const current = pushes.get(id);
  if (current) { current.latest = { item, data, generation }; return; }
  const pending = { latest: { item, data, generation } }; pushes.set(id, pending);
  try {
    while (pending.latest) {
      const latest = pending.latest; pending.latest = null;
      if ((generations.get(tabId) || 0) !== latest.generation) continue;
      try {
        const body = await payload(latest.item, latest.data, tabId);
        if ((generations.get(tabId) || 0) === latest.generation) await request('found', body);
      } catch { /* The popup can resend detections when PastDow is opened later. */ }
    }
  } finally { pushes.delete(id); }
}

chrome.webRequest.onHeadersReceived.addListener(details => {
  let detection;
  try { detection = classify(details); } catch { return; }
  if (!detection) return;
  const tabId = details.tabId, generation = generations.get(tabId) || 0;
  void serial(tabId, async () => {
    if ((generations.get(tabId) || 0) !== generation) return;
    const data = await state(tabId), list = detection.subtitle ? data.subtitles : data.media;
    const id = identity(details.url), previous = list.find(x => x.id === id);
    const item = { ...detection, id, url: details.url, size: Math.max(detection.size || 0, previous?.size || 0) };
    if (previous) list.splice(list.indexOf(previous), 1);
    list.unshift(item);
    list.splice(detection.subtitle ? 100 : 200);
    if ((generations.get(tabId) || 0) !== generation) return;
    await chrome.storage.session.set({ [`tab:${tabId}`]: data });
    await chrome.action.setBadgeText({ tabId, text: data.media.length ? String(data.media.length) : '' });
    await chrome.action.setBadgeBackgroundColor({ tabId, color: '#e6c34a' });
    // A later subtitle response refreshes videos already sent to the app.
    for (const media of detection.subtitle ? data.media : [item]) void push(media, data, tabId, generation);
  });
}, { urls: ['<all_urls>'] }, ['responseHeaders']);

function reset(tabId) {
  generations.set(tabId, (generations.get(tabId) || 0) + 1);
  void serial(tabId, async () => {
    await chrome.storage.session.remove(`tab:${tabId}`);
    await chrome.action.setBadgeText({ tabId, text: '' }).catch(() => {});
  });
}
chrome.webNavigation.onBeforeNavigate.addListener(details => { if (details.frameId === 0) reset(details.tabId); });
chrome.tabs.onRemoved.addListener(reset);

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (sender.id !== chrome.runtime.id) return false;
  (async () => {
    if (message.t === 'pair') {
      const key = String(message.key || '').trim();
      if (!/^[a-f0-9]{64}$/.test(key)) throw new Error('Paste the full key copied from PastDow');
      await chrome.storage.local.set({ key });
      await request('ping');
      return { ok: true };
    }
    if (message.t === 'list') {
      await chains.get(message.tabId);
      const data = await state(message.tabId);
      for (const item of data.media) void push(item, data, message.tabId, generations.get(message.tabId) || 0);
      const tab = await chrome.tabs.get(message.tabId);
      return { items: data.media, title: tab.title, subtitles: data.subtitles.length, paired: !!(await storedKey()) };
    }
    if (message.t === 'download') {
      await chains.get(message.tabId);
      const data = await state(message.tabId), item = data.media.find(x => x.id === message.id);
      if (!item) throw new Error('This page changed, play the video again');
      const body = await payload(item, data, message.tabId);
      const key = await storedKey();
      if (!key) throw new Error('Paste your PastDow pairing key first');
      try { await request('queue', body); return { ok: true }; }
      catch (error) {
        // Only connection failures use the protocol; rejected requests need repairing the pairing.
        if (error.message.includes('rejected') || error.message.includes('returned')) throw error;
        const bytes = new TextEncoder().encode(JSON.stringify({ ...body, key }));
        const encoded = btoa(Array.from(bytes, b => String.fromCharCode(b)).join('')).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
        return { protocol: `pastdow://add?d=${encoded}` };
      }
    }
    throw new Error('Unknown request');
  })().then(reply, error => reply({ error: error.message }));
  return true;
});
