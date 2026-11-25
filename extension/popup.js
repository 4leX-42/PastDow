const $ = id => document.getElementById(id);
let tabId;

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

async function refresh() {
  const result = await message({ t: 'list', tabId });
  paired(result.paired);
  $('items').replaceChildren();
  if (!result.items.length) {
    const empty = document.createElement('div'); empty.id = 'empty';
    empty.textContent = 'Nothing found in this tab yet. Press play; if the video was already playing, reload the tab.';
    $('items').append(empty);
    return;
  }
  for (const item of result.items) {
    const row = document.createElement('article'), text = document.createElement('div');
    const title = document.createElement('b'), small = document.createElement('small'), button = document.createElement('button');
    title.textContent = result.title || new URL(item.url).pathname.split('/').pop() || 'Video';
    title.title = title.textContent;
    small.textContent = detail(item, result.subtitles);
    button.textContent = 'DOWNLOAD';
    button.onclick = async () => {
      button.disabled = true;
      try {
        const sent = await message({ t: 'download', tabId, id: item.id });
        if (sent.protocol) {
          // PastDow is closed: its pastdow: handler opens it with this video
          const link = document.createElement('a'); link.href = sent.protocol; link.textContent = 'Open PastDow';
          $('status').textContent = 'PastDow is closed, opening it. If the browser asks, allow it. ';
          $('status').append(link); link.click();
        } else { $('status').textContent = 'Added to PastDow'; button.textContent = 'ADDED'; }
      } catch (error) { $('status').textContent = error.message; button.disabled = false; }
    };
    text.append(title, small); row.append(text, button); $('items').append(row);
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
  if (area === 'session' && changes[`tab:${tabId}`]) void refresh().catch(() => {});
});
