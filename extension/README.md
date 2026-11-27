# PastDow browser extension

Sends the videos you play in Edge or Chrome to PastDow, the way download helper extensions do. It works on pages
that only play inside your signed-in browser, such as Blackboard Collaborate class recordings, and it brings
the page's subtitles along.

## Install

In PastDow open **MENU**, go to **BROWSER EXTENSION** and press **ADD TO CHROME**, **ADD TO BRAVE** or **ADD TO EDGE**
(only the browsers you have are shown). PastDow opens the browser and clicks through its steps for you: extensions
page, Developer mode, Load unpacked, the folder. Leave the mouse alone for those few seconds. The extension then
pairs with PastDow by itself.

Browsers do not let a program add an extension silently, so PastDow drives the same screens you would. If one of
them changes and a step fails, PastDow leaves the extensions page open, copies the folder path and tells you the two
clicks left: **Developer mode**, then **Load unpacked** and paste the path with Ctrl+V.

By hand: load this `extension` folder the same way, then paste the key from **MENU > BROWSER EXTENSION > COPY KEY**
into the extension popup and press **PAIR**.

## Use

**FORCE DETECT** searches the page for media and adds **Try with yt-dlp** as a fallback.

Pairing repairs itself from PastDow's key file after a rejected key. Pending videos retry automatically
when you open the popup, detect another video, or once a minute while PastDow is unavailable.

**Download options** (the gear beside **DOWNLOAD**) picks video quality and format, or audio format, for that item.
Available stream/page qualities load when you open it; the initial choices come from PastDow.

**HISTORY** shows recent downloads; **FOLDER** selects the file in Windows Explorer.

Detected videos show a thumbnail and duration when the page makes them available.

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
It sends them only to PastDow on this PC (`http://127.0.0.1:47821`), and never in clear: each message is encrypted
and signed with keys derived from the pairing key, which itself never travels. Another program on that port sees
only ciphertext, and PastDow has to sign its answer, so the extension notices and stops.
Detections are cleared when the tab closes; download history stays locally in PastDow. A `pastdow:` link carries the same cookies and the key: do not share it.
Download only what you have the right to.
