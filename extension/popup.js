const $ = id => document.getElementById(id);
let tabId, currentTab = 'found', historyGeneration = 0;
const names = {};   // item id -> name typed with the pencil (kept while the popup is open)

const choices = new Map(), availableFormats = new Map();
let defaults = { q: 0, vfmt: 'mkv', afmt: 'best', mode: 'video' }, lastMode;
const genericHeights = [2160, 1440, 1080, 720];
const videoFormats = ['mkv', 'mp4', 'webm'], audioFormats = ['best', 'mp3', 'm4a', 'flac', 'wav', 'opus'];
const qualityLabel = q => q === 0 ? 'BEST' : q === 2160 ? '4K' : `${q}P`;
function qualityChips(heights) {
  const allowed = [360, 480, 720, 1080, 1440, 2160];
  // Round up to a supported cap so a slightly unusual source height is still included.
  return [...new Set(heights.map(h => allowed.find(q => q >= h)).filter(Boolean))].sort((a, b) => b - a);
}
function choiceFor(item) {
  if (!choices.has(item.id)) choices.set(item.id, {
    audio: (lastMode || defaults.mode) === 'audio', q: defaults.q,
    vfmt: defaults.vfmt, afmt: defaults.afmt, open: false
  });
  return choices.get(item.id);
}
function queueChoice(item) {
  const choice = choiceFor(item);
  return { audio: choice.audio, q: choice.audio || !['PAGE', 'HLS', 'DASH'].includes(item.kind) ? 0 : choice.q, fmt: choice.audio ? choice.afmt : choice.vfmt };
}
function formatPicker(item, button) {
  const choice = choiceFor(item), panel = document.createElement('div'), gear = document.createElement('button');
  panel.className = 'format-panel'; panel.hidden = !choice.open;
  gear.className = 'gear'; gear.textContent = '\u2699'; gear.title = 'Download options';
  gear.setAttribute('aria-label', 'Download options');
  gear.setAttribute('aria-expanded', String(choice.open));
  const updateLabel = () => {
    const selected = queueChoice(item);
    button.textContent = 'DOWNLOAD \u00b7 ' + (selected.audio ? selected.fmt.toUpperCase() :
      (['PAGE', 'HLS', 'DASH'].includes(item.kind) ? qualityLabel(selected.q) + ' ' : '') + selected.fmt.toUpperCase());
  };
  const draw = () => {
    const info = availableFormats.get(item.url);
    const heights = ['HLS', 'PAGE', 'DASH'].includes(item.kind) ? (info?.heights ? qualityChips(info.heights) : [...new Set([...genericHeights, ...([480, 360].includes(choice.q) ? [choice.q] : [])])]) : null;
    if (info?.audio === false && choice.audio) choice.audio = false;
    if (heights && !info?.loading && choice.q && !heights.includes(choice.q)) choice.q = 0;
    updateLabel(); panel.replaceChildren();
    const chips = (label, values, selected, pick) => {
      const group = document.createElement('div'), caption = document.createElement('span');
      group.className = 'chip-row'; caption.className = 'chip-label'; caption.textContent = label; group.append(caption);
      for (const [value, text] of values) {
        const chip = document.createElement('button'); chip.textContent = text; chip.className = 'chip';
        chip.setAttribute('aria-pressed', String(value === selected));
        if (label === 'TYPE' && value === true && info?.audio === false) chip.disabled = true;
        chip.onclick = () => { pick(value); draw(); };
        group.append(chip);
      }
      panel.append(group);
    };
    chips('TYPE', [[false, 'VIDEO'], [true, 'AUDIO']], choice.audio, value => {
      choice.audio = value; lastMode = value ? 'audio' : 'video';
      void chrome.storage.local.set({ lastMode });
    });
    if (!choice.audio && heights && !info?.loading) chips('QUALITY', [0, ...heights].map(q => [q, qualityLabel(q)]), choice.q, value => { choice.q = value; });
    chips('FORMAT', (choice.audio ? audioFormats : videoFormats).map(fmt => [fmt, fmt.toUpperCase()]), choice.audio ? choice.afmt : choice.vfmt, value => {
      if (choice.audio) choice.afmt = value; else choice.vfmt = value;
    });
    if (info?.loading) {
      const loading = document.createElement('small'); loading.className = 'format-loading'; loading.textContent = 'Loading formats...'; panel.append(loading);
    } else if (info?.failed) {
      const fallback = document.createElement('small'); fallback.textContent = 'Formats unavailable: showing standard options'; panel.append(fallback);
    }
  };
  const load = async () => {
    if (!['PAGE', 'HLS'].includes(item.kind)) return;
    let info = availableFormats.get(item.url);
    if (!info) {
      info = { loading: true }; availableFormats.set(item.url, info);
      info.task = message({ t: 'formats', tabId, id: item.id }).then(result => Object.assign(info, result), () => { info.failed = true; }).finally(() => { info.loading = false; });
    }
    draw();
    if (info.task) await info.task;
    draw();
  };
  gear.onclick = async () => {
    choice.open = !choice.open; panel.hidden = !choice.open;
    gear.setAttribute('aria-expanded', String(choice.open));
    if (choice.open) await load();
  };
  draw();
  if (choice.open) void load();
  return { gear, panel };
}

async function message(body) {
  const result = await chrome.runtime.sendMessage(body);
  if (result.error) throw new Error(result.error);
  return result;
}

function paired(on) {
  $('pair').classList.toggle('paired', on);
  $('repair').hidden = !on;
}

function detail(item, subtitles) {
  const size = item.kind === 'HLS' || item.kind === 'DASH' ? 'STREAM' : item.size ? (item.size / 1048576).toFixed(1) + ' MB' : 'SIZE UNKNOWN';
  const subs = subtitles ? subtitles + (subtitles === 1 ? ' subtitle' : ' subtitles') : '';
  return [item.kind, size, new URL(item.url).host, subs].filter(Boolean).join(' · ');
}

function durationLabel(seconds) {
  const s = Math.floor(seconds || 0);
  return s >= 3600 ? `${Math.floor(s / 3600)}:${String(Math.floor(s / 60) % 60).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}
async function refresh(capture = true) {
  if (currentTab !== 'found') return;
  const result = await message({ t: 'list', tabId, refresh: capture });
  paired(result.paired);
  $('items').replaceChildren();
  if (!result.items.length) {
    const empty = document.createElement('div'); empty.id = 'empty';
    empty.textContent = 'Press play to find videos in this tab.';
    $('items').append(empty);
    return;
  }
  for (const item of result.items) {
    const row = document.createElement('article'), text = document.createElement('div');
    const title = document.createElement('b'), small = document.createElement('small'), button = document.createElement('button');
    text.className = 'info';
    const preview = document.createElement('div'); preview.className = 'preview'; preview.textContent = '\u25b6';
    if (item.thumb) {
      const image = document.createElement('img'); image.src = item.thumb; image.alt = '';
      image.onerror = () => image.remove(); preview.append(image);
    }
    if (item.duration > 0) {
      const badge = document.createElement('span'); badge.className = 'duration'; badge.textContent = durationLabel(item.duration); preview.append(badge);
    }
    if (result.subtitles) {
      const subs = document.createElement('span'); subs.className = 'subtitles'; subs.textContent = 'CC'; subs.title = 'Subtitles available'; preview.append(subs);
    }
    const kind = document.createElement('span'); kind.className = 'kind'; kind.textContent = item.kind;
    title.textContent = names[item.id] || (item.kind === 'PAGE' && item.title === 'Try with yt-dlp' ? item.title : '') || result.title || new URL(item.url).pathname.split('/').pop() || 'Video';
    title.title = title.textContent;
    // pencil: edit the file name in place before downloading
    const edit = document.createElement('button'); edit.className = 'pencil'; edit.title = 'Rename before downloading'; edit.textContent = '\u270e';
    edit.onclick = () => {
      const input = document.createElement('input'); input.className = 'rename'; input.value = title.textContent; input.maxLength = 200;
      const done = keep => { if (keep && input.value.trim()) names[item.id] = input.value.trim(); title.textContent = names[item.id] || title.textContent; title.title = title.textContent; input.replaceWith(title); };
      input.onkeydown = e => { if (e.key === 'Enter') done(true); if (e.key === 'Escape') done(false); };
      input.onblur = () => done(true);
      title.replaceWith(input); input.focus(); input.select();
    };
    small.textContent = detail(item, 0).split(' · ').slice(1).join(' · ');
    button.className = 'download';
    const picker = formatPicker(item, button);
    button.onclick = async () => {
      button.disabled = true;
      try {
        const sent = await message({ t: 'download', tabId, id: item.id, name: names[item.id] || '', ...queueChoice(item) });
        lastMode = choiceFor(item).audio ? 'audio' : 'video';
        await chrome.storage.local.set({ lastMode });
        if (sent.protocol) {
          // PastDow is closed: its pastdow: handler opens it with this video
          const link = document.createElement('a'); link.href = sent.protocol; link.textContent = 'Open PastDow';
          $('status').textContent = 'PastDow is closed, opening it. If the browser asks, allow it. ';
          $('status').append(link); link.click();
        } else { $('status').textContent = 'Added to PastDow'; button.textContent = 'ADDED'; }
      } catch (error) { $('status').textContent = error.message; button.disabled = false; }
    };
    const actions = document.createElement('div'); actions.className = 'actions'; actions.append(edit, picker.gear, button);
    text.append(kind, title, small, actions); row.append(preview, text, picker.panel); $('items').append(row);
  }
}

function relativeDate(date) {
  const time = new Date(date), now = new Date(), age = Math.max(0, now - time);
  if (!Number.isFinite(time.getTime())) return '';
  if (age < 60000) return 'just now';
  if (age < 3600000) return Math.floor(age / 60000) + ' min ago';
  const yesterday = new Date(now); yesterday.setDate(now.getDate() - 1);
  if (time.toDateString() === yesterday.toDateString()) return 'yesterday';
  if (age < 86400000) return Math.floor(age / 3600000) + ' h ago';
  return time.toLocaleDateString('en-GB');
}

async function loadHistory() {
  const generation = ++historyGeneration;
  $('history').replaceChildren();
  $('status').textContent = 'Loading downloads...';
  try {
    const result = await message({ t: 'history' });
    if (generation !== historyGeneration || currentTab !== 'history') return;
    paired(true);
    $('status').textContent = '';
    if (!result.items.length) {
      const empty = document.createElement('div'); empty.className = 'history-empty';
      empty.textContent = 'No downloads yet.'; $('history').append(empty);
    }
    for (const item of result.items) {
      const row = document.createElement('article'); row.classList.toggle('missing', !item.exists);
      const preview = document.createElement('div'); preview.className = 'preview'; preview.textContent = item.audio ? '\u266b' : '\ud83c\udf9e\ufe0e';
      if (typeof item.thumb === 'string' && item.thumb.length <= 120000 && /^data:image\/(?:jpeg|png);base64,[A-Za-z0-9+/=]*$/.test(item.thumb)) {
        const image = document.createElement('img'); image.src = item.thumb; image.alt = '';
        image.onerror = () => image.remove(); preview.append(image);
      }
      const text = document.createElement('div'); text.className = 'info';
      const title = document.createElement('b'); title.textContent = item.title || item.name; title.title = title.textContent;
      const name = document.createElement('small'); name.className = 'filename'; name.textContent = item.name;
      const detail = document.createElement('small');
      detail.textContent = (item.size / 1048576).toFixed(1) + ' MB \u00b7 ' + relativeDate(item.date);
      const button = document.createElement('button'), icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      icon.setAttribute('class', 'folder-icon'); icon.setAttribute('viewBox', '0 0 24 24'); icon.setAttribute('aria-hidden', 'true');
      const outline = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      outline.setAttribute('d', 'M2 5h7l2 2h11v13H2z'); icon.append(outline);
      button.append(icon, 'FOLDER'); button.title = 'Open in Explorer';
      button.onclick = async () => {
        button.disabled = true;
        try {
          const result = await message({ t: 'reveal', id: item.id });
          $('status').textContent = result.ok ? 'Opened in Explorer' : 'File and folder are no longer available';
        } catch (error) { $('status').textContent = error.message; }
        finally { button.disabled = false; }
      };
      text.append(title, name, detail);
      if (!item.exists) {
        const missing = document.createElement('small'); missing.textContent = 'File moved or deleted'; text.append(missing);
      }
      text.append(button); row.append(preview, text); $('history').append(row);
    }
  } catch {
    if (generation === historyGeneration && currentTab === 'history') $('status').textContent = 'Open PastDow to see your downloads';
  }
}

async function selectTab(tab) {
  currentTab = tab === 'history' ? 'history' : 'found';
  historyGeneration++;
  $('foundTab').setAttribute('aria-selected', String(currentTab === 'found'));
  $('historyTab').setAttribute('aria-selected', String(currentTab === 'history'));
  $('force').hidden = currentTab !== 'found';
  $('items').hidden = currentTab !== 'found'; $('history').hidden = currentTab !== 'history';
  $('foot').textContent = currentTab === 'history' ? 'Your last downloads from PastDow · FOLDER shows the file in Explorer' : "Videos also appear in PastDow's BROWSER tab, pick quality there";
  $('status').textContent = '';
  await chrome.storage.local.set({ lastTab: currentTab });
  if (currentTab === 'history') await loadHistory(); else await refresh();
}
$('foundTab').onclick = () => void selectTab('found').catch(error => { $('status').textContent = error.message; });
$('historyTab').onclick = () => void selectTab('history').catch(error => { $('status').textContent = error.message; });

async function refreshConnection() {
  const result = await message({ t: 'status' });
  $('connection').textContent = result.status;
  paired(result.paired);
  if ([0, 2160, 1440, 1080, 720, 480, 360].includes(result.q)) defaults.q = result.q;
  if (videoFormats.includes(result.vfmt)) defaults.vfmt = result.vfmt;
  if (audioFormats.includes(result.afmt)) defaults.afmt = result.afmt;
  if (['video', 'audio'].includes(result.mode)) defaults.mode = result.mode;
}
$('force').onclick = async () => {
  $('force').disabled = true;
  try { const result = await message({ t: 'force', tabId }); await refresh(false); $('status').textContent = result.result; }
  catch (error) { $('status').textContent = error.message; }
  finally { $('force').disabled = false; }
};

$('pair').onsubmit = async event => {
  event.preventDefault();
  try { await message({ t: 'pair', key: $('key').value }); $('key').value = ''; $('status').textContent = 'Paired with PastDow'; await refreshConnection(); if (currentTab === 'history') await loadHistory(); else await refresh(); }
  catch (error) { $('status').textContent = error.message; }
};
$('repair').onclick = () => { paired(false); $('repair').hidden = true; $('key').focus(); };

(async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  tabId = tab.id;
  const { lastTab } = await chrome.storage.local.get('lastTab');
  const saved = await chrome.storage.local.get('lastMode');
  if (['video', 'audio'].includes(saved.lastMode)) lastMode = saved.lastMode;
  await refreshConnection();
  await selectTab(lastTab);
})().catch(error => { $('status').textContent = error.message; });

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'session' && changes[`tab:${tabId}`]) void refresh(false).catch(() => {});
});
