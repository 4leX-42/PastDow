const $ = id => document.getElementById(id);
let tabId, currentTab = 'found', historyGeneration = 0;
const names = {};   // item id -> name typed with the pencil (kept while the popup is open)

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
    title.textContent = names[item.id] || result.title || new URL(item.url).pathname.split('/').pop() || 'Video';
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
    button.textContent = 'DOWNLOAD';
    button.onclick = async () => {
      button.disabled = true;
      try {
        const sent = await message({ t: 'download', tabId, id: item.id, name: names[item.id] || '' });
        if (sent.protocol) {
          // PastDow is closed: its pastdow: handler opens it with this video
          const link = document.createElement('a'); link.href = sent.protocol; link.textContent = 'Open PastDow';
          $('status').textContent = 'PastDow is closed, opening it. If the browser asks, allow it. ';
          $('status').append(link); link.click();
        } else { $('status').textContent = 'Added to PastDow'; button.textContent = 'ADDED'; }
      } catch (error) { $('status').textContent = error.message; button.disabled = false; }
    };
    const actions = document.createElement('div'); actions.className = 'actions'; actions.append(edit, button);
    text.append(kind, title, small, actions); row.append(preview, text); $('items').append(row);
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
  $('items').hidden = currentTab !== 'found'; $('history').hidden = currentTab !== 'history';
  $('foot').textContent = currentTab === 'history' ? 'Your last downloads from PastDow · FOLDER shows the file in Explorer' : "Videos also appear in PastDow's BROWSER tab, pick quality there";
  $('status').textContent = '';
  await chrome.storage.local.set({ lastTab: currentTab });
  if (currentTab === 'history') await loadHistory(); else await refresh();
}
$('foundTab').onclick = () => void selectTab('found').catch(error => { $('status').textContent = error.message; });
$('historyTab').onclick = () => void selectTab('history').catch(error => { $('status').textContent = error.message; });

$('pair').onsubmit = async event => {
  event.preventDefault();
  try { await message({ t: 'pair', key: $('key').value }); $('key').value = ''; $('status').textContent = 'Paired with PastDow'; if (currentTab === 'history') await loadHistory(); else await refresh(); }
  catch (error) { $('status').textContent = error.message; }
};
$('repair').onclick = () => { paired(false); $('repair').hidden = true; $('key').focus(); };

(async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  tabId = tab.id;
  const { lastTab } = await chrome.storage.local.get('lastTab');
  await selectTab(lastTab);
})().catch(error => { $('status').textContent = error.message; });

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'session' && changes[`tab:${tabId}`]) void refresh(false).catch(() => {});
});
