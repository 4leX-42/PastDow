const ENDPOINT = 'http://127.0.0.1:47821';
const chains = new Map(), generations = new Map(), pushes = new Map();
const previews = new Map();
const previewRetries = new Map();
// Quality 3 marks a frame captured after readiness checks; older captures can be refreshed.
const goodThumbnail = item => !!item.thumb && item.thumbQuality === 3;
function stopPreviewRetries(tabId) {
  const pending = previewRetries.get(tabId);
  if (pending) pending.timers.forEach(clearTimeout);
  previewRetries.delete(tabId);
}
function schedulePreviewRetries(tabId) {
  if (previewRetries.has(tabId)) return;
  const generation = generations.get(tabId) || 0;
  const pending = { timers: [] };
  previewRetries.set(tabId, pending);
  pending.timers = [1500, 4000, 9000, 20000].map((delay, index) => setTimeout(async () => {
    if (previewRetries.get(tabId) !== pending || (generations.get(tabId) || 0) !== generation) return;
    await enrich(tabId);
    if (index === 3 && previewRetries.get(tabId) === pending) previewRetries.delete(tabId);
  }, delay));
}
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
// Runs in each frame; only coordinates safely mapped to the top viewport may be captured.
function inspectVideo() {
  const og = document.querySelector('meta[property="og:image"]')?.content;
  const videos = [...document.querySelectorAll('video')].map(video => {
    const r = video.getBoundingClientRect(), style = getComputedStyle(video);
    let x = Math.max(0, r.left), y = Math.max(0, r.top);
    let width = Math.min(innerWidth, r.right) - x, height = Math.min(innerHeight, r.bottom) - y;
    let win = window;
    try {
      while (win !== win.top) {
        const frame = win.frameElement, f = frame.getBoundingClientRect();
        if (getComputedStyle(frame).visibility === 'hidden') throw new Error('Hidden frame');
        x += f.left + frame.clientLeft; y += f.top + frame.clientTop;
        win = win.parent;
        width = Math.min(width, f.right - x, win.innerWidth - x);
        height = Math.min(height, f.bottom - y, win.innerHeight - y);
      }
    } catch { width = height = 0; }
    const visible = style.display !== 'none' && style.visibility === 'visible' && Number(style.opacity) > 0 && x >= 0 && y >= 0 && width > 1 && height > 1;
    // Fit the intrinsic picture inside the element rather than capturing letterboxing.
    const inset = name => parseFloat(style[name]) || 0;
    const left = inset('borderLeftWidth') + inset('paddingLeft'), top = inset('borderTopWidth') + inset('paddingTop');
    let pictureWidth = r.width - left - inset('borderRightWidth') - inset('paddingRight');
    let pictureHeight = r.height - top - inset('borderBottomWidth') - inset('paddingBottom');
    let pictureX = r.left + left, pictureY = r.top + top;
    if (video.videoWidth > 0 && video.videoHeight > 0 && !['fill', 'cover', 'none'].includes(style.objectFit)) {
      const scale = Math.min(pictureWidth / video.videoWidth, pictureHeight / video.videoHeight);
      const w = video.videoWidth * scale, h = video.videoHeight * scale;
      pictureX += (pictureWidth - w) / 2; pictureY += (pictureHeight - h) / 2;
      pictureWidth = w; pictureHeight = h;
    }
    const contentX = x + Math.max(0, pictureX - r.left), contentY = y + Math.max(0, pictureY - r.top);
    return { rect: visible ? { x, y, width, height } : null, area: visible ? width * height : 0,
      contentRect: visible ? { x: contentX, y: contentY, width: Math.min(pictureWidth, x + width - contentX), height: Math.min(pictureHeight, y + height - contentY) } : null,
      readyState: video.readyState, videoWidth: video.videoWidth, videoHeight: video.videoHeight,
      currentTime: video.currentTime, paused: video.paused, controls: video.hasAttribute('controls'),
      devicePixelRatio, duration: Number.isFinite(video.duration) && video.duration <= 86400 ? video.duration : 0,
      poster: video.poster, og };
  }).sort((a, b) => b.area - a.area);
  return videos[0] || { og, area: 0 };
}
function readyForThumbnail(video) {
  return video?.readyState >= 2 && video.videoWidth > 0 && video.rect?.width >= 120 && video.rect.height >= 68 &&
    (video.currentTime > .3 || video.paused === false);
}
function frameRect(video) {
  const r = { ...(video.contentRect || video.rect) };
  if (video.controls) {
    r.height = Math.min(r.height, video.rect.y + video.rect.height * .82 - r.y);
    // Center a 16:9 crop in the remaining picture, above the native controls.
    const width = Math.min(r.width, r.height * 16 / 9), height = width * 9 / 16;
    r.x += (r.width - width) / 2; r.y += (r.height - height) / 2;
    r.width = width; r.height = height;
  }
  return r;
}
async function smallJpeg(blob, rect) {
  const bitmap = await createImageBitmap(blob);
  try {
    const r = rect || { x: 0, y: 0, width: bitmap.width, height: bitmap.height };
    if (r.width <= 0 || r.height <= 0 || r.x < 0 || r.y < 0 || r.x + r.width > bitmap.width + 1 || r.y + r.height > bitmap.height + 1) return null;
    let width = Math.min(320, r.width, 1800 * r.width / r.height);
    for (let attempt = 0; attempt < 4; attempt++, width = Math.round(width * .7)) {
      const canvas = new OffscreenCanvas(Math.max(1, Math.round(width)), Math.max(1, Math.round(width * r.height / r.width)));
      canvas.getContext('2d').drawImage(bitmap, r.x, r.y, r.width, r.height, 0, 0, canvas.width, canvas.height);
      const jpeg = await canvas.convertToBlob({ type: 'image/jpeg', quality: .7 });
      const url = 'data:image/jpeg;base64,' + b64(await jpeg.arrayBuffer());
      if (url.length < 60000) return url;
    }
    return null;
  } finally { bitmap.close(); }
}
function enrich(tabId) {
  if (previews.has(tabId)) return previews.get(tabId);
  const generation = generations.get(tabId) || 0;
  const task = (async () => {
    try {
      const existing = await state(tabId);
      if (!existing.media.length || existing.media.every(goodThumbnail)) { stopPreviewRetries(tabId); return; }
      const results = await chrome.scripting.executeScript({ target: { tabId, allFrames: true }, func: inspectVideo });
      const candidates = results.map(x => x.result).filter(Boolean).sort((a, b) => (b.area || 0) - (a.area || 0));
      const video = candidates[0];
      if (!video) return;
      let thumb = null, quality = 0;
      if (readyForThumbnail(video)) {
        try {
          const tab = await chrome.tabs.get(tabId);
          const [active] = await chrome.tabs.query({ active: true, windowId: tab.windowId });
          if (active?.id === tabId) {
            const capture = await chrome.tabs.captureVisibleTab(tab.windowId, { format: 'jpeg', quality: 70 });
            const [stillActive] = await chrome.tabs.query({ active: true, windowId: tab.windowId });
            if (stillActive?.id === tabId) {
              const dpr = video.devicePixelRatio || 1, r = frameRect(video);
              thumb = await smallJpeg(await (await fetch(capture)).blob(), { x: r.x * dpr, y: r.y * dpr, width: r.width * dpr, height: r.height * dpr });
              if (thumb) quality = 3;
            }
          }
        } catch { /* Posters can still work when capture is unavailable. */ }
      }
      for (const url of [video.poster, ...candidates.map(x => x.og)]) {
        if (thumb) break;
        if (!url || !http(url)) continue;
        try {
          const response = await fetch(url, { signal: AbortSignal.timeout(2000), credentials: 'omit' });
          if (!response.ok || !/^image\//i.test(response.headers.get('content-type') || '')) continue;
          const blob = await response.blob();
          if (blob.size > 5000000) continue;
          thumb = await smallJpeg(blob); if (thumb) quality = 1;
        } catch { /* Optional metadata never prevents detection. */ }
      }
      await serial(tabId, async () => {
        if ((generations.get(tabId) || 0) !== generation) return;
        const data = await state(tabId);
        let changed = false;
        for (const item of data.media) {
          if (thumb && !goodThumbnail(item) && quality > (item.thumbQuality || 0)) { item.thumb = thumb; item.thumbQuality = quality; changed = true; }
          if (video.duration > 0 && item.duration !== video.duration) { item.duration = video.duration; changed = true; }
        }
        if (data.media.every(goodThumbnail)) stopPreviewRetries(tabId);
        if (!changed) return;
        await chrome.storage.session.set({ [`tab:${tabId}`]: data });
        for (const item of data.media) void push(item, data, tabId, generation);
      });
    } catch { /* Restricted pages and frames do not expose video metadata. */ }
  })().finally(() => { if (previews.get(tabId) === task) previews.delete(tabId); });
  previews.set(tabId, task);
  return task;
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
    thumb: item.thumb || null, duration: item.duration || 0,
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
    const item = { ...previous, ...detection, id, url: details.url, size: Math.max(detection.size || 0, previous?.size || 0) };
    if (previous) list.splice(list.indexOf(previous), 1);
    list.unshift(item);
    list.splice(detection.subtitle ? 100 : 200);
    if ((generations.get(tabId) || 0) !== generation) return;
    await chrome.storage.session.set({ [`tab:${tabId}`]: data });
    await chrome.action.setBadgeText({ tabId, text: data.media.length ? String(data.media.length) : '' });
    await chrome.action.setBadgeBackgroundColor({ tabId, color: '#f5f5f3' });
    await chrome.action.setBadgeTextColor?.({ tabId, color: '#000000' });
    // A later subtitle response refreshes videos already sent to the app.
    for (const media of detection.subtitle ? data.media : [item]) void push(media, data, tabId, generation);
    if (!detection.subtitle && !goodThumbnail(item)) { schedulePreviewRetries(tabId); void enrich(tabId); }
  });
}, { urls: ['<all_urls>'] }, ['responseHeaders']);

function reset(tabId) {
  generations.set(tabId, (generations.get(tabId) || 0) + 1);
  previews.delete(tabId);
  stopPreviewRetries(tabId);
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
      if (message.refresh !== false) { schedulePreviewRetries(message.tabId); await enrich(message.tabId); }
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
