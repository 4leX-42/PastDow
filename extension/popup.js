const $ = id => document.getElementById(id);
let tabId;
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

$('pair').onsubmit = async event => {
  event.preventDefault();
  try { await message({ t: 'pair', key: $('key').value }); $('key').value = ''; $('status').textContent = 'Paired with PastDow'; await refresh(); }
  catch (error) { $('status').textContent = error.message; }
};
$('repair').onclick = () => { paired(false); $('repair').hidden = true; $('key').focus(); };

(async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  tabId = tab.id;
  await refresh();
})().catch(error => { $('status').textContent = error.message; });

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'session' && changes[`tab:${tabId}`]) void refresh(false).catch(() => {});
});
