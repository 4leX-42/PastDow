# PastDow browser extension

Sends the videos you play in Edge or Chrome to PastDow, the way download helper extensions do. It works on pages
that only play inside your signed-in browser, such as Blackboard Collaborate class recordings, and it brings
the page's subtitles along.

## Install

1. Open `edge://extensions` (Edge) or `chrome://extensions` (Chrome) and turn on **Developer mode**.
2. **Load unpacked** and choose this `extension` folder.
3. In PastDow open **MENU**, find **BROWSER EXTENSION** and press **COPY**.
4. Click the extension icon, paste the key and press **PAIR**. You only do this once per browser profile.

## Use

Play the video. The icon shows how many videos it found in that tab, and each one also appears at once in
PastDow's **BROWSER** tab. Click it there to download it with the quality and format picked on screen, or press
**DOWNLOAD** in the popup to use your saved defaults. If the video was already playing when you installed the
extension, reload the tab.

If PastDow is closed, **DOWNLOAD** opens it through its `pastdow:` link, which the app registers for your user
on first start (no admin rights).

## What it catches

MP4, WebM and MOV files, HLS (`.m3u8`) and DASH (`.mpd`) streams, and `.vtt` / `.srt` subtitles. Subtitles go
inside the video, or next to it if the format cannot hold them. Files under 200 KB (previews, ads) and stream
segments are skipped. DRM-protected video (Netflix and the like) cannot be downloaded.

## Privacy

The extension needs access to all sites to see what a page loads and to read the cookies that the video needs.
It sends them only to PastDow on this PC (`http://127.0.0.1:47821`). PastDow first has to sign a random challenge
with the pairing key, so another program on that port gets nothing,
and keeps nothing after the tab closes. A `pastdow:` link carries the same cookies and the key: do not share it.
Download only what you have the right to.
