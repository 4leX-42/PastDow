# Ulvark

A small Windows app to download video and audio at the best available quality from YouTube and the 1,000+
sites supported by [yt-dlp](https://github.com/yt-dlp/yt-dlp). One portable `.exe` for Windows 10/11: no
installer, no admin rights.

## Download

Get `Ulvark.exe` from the [latest release](https://github.com/4leX-42/Ulvark/releases/latest) and run it.

Or from PowerShell:

```powershell
$f="$env:TEMP\Ulvark.exe"; irm https://github.com/4leX-42/Ulvark/releases/latest/download/Ulvark.exe -OutFile $f; & $f
```

## Use

1. Copy a link and open Ulvark: it is already in the field.
2. Pick **Video** (and a resolution cap) or **Audio** (MP3).
3. Press Enter or **Download**. Paste several links at once or drop them on the window.

Double-click a finished download to open it. Right-click a download to show it in its folder, cancel it,
download it again or copy its link. You can also start it with links: `Ulvark.exe URL [URL ...]`.

Video goes to `Videos\Ulvark`, audio to `Music\Ulvark`, one folder per channel. Change either from the
**Menu**, where you also turn on browser cookies (needed for age-restricted videos), switch off skipping of
links you already downloaded, and force a yt-dlp update.

## How it works

On first run Ulvark downloads its engine into `%LOCALAPPDATA%\Ulvark\bin` from the official releases and
checks each file against its published SHA-256:

| Part | Why | From |
|---|---|---|
| `yt-dlp.exe` | the download itself | `github.com/yt-dlp/yt-dlp` |
| `ffmpeg.exe`, `ffprobe.exe` | merging video and audio, MP3, thumbnails | `github.com/yt-dlp/FFmpeg-Builds` |
| `deno.exe` | the JavaScript YouTube requires for high-quality formats | `github.com/denoland/deno` |

They are not bundled because yt-dlp goes stale within weeks whenever YouTube changes something; Ulvark
updates it (`yt-dlp -U`) at most once a day.

Downloads use the best video and audio by resolution, frame rate and HDR, saved as MKV with metadata,
chapters, thumbnail, English and Spanish subtitles and SponsorBlock chapter marks. Two run at a time.

## Build

Needs a recent .NET SDK and the .NET Framework 4.8 targeting pack.

```powershell
dotnet build -c Release            # bin\Release\net48\Ulvark.exe
.\tools\make-icon.ps1              # regenerates app.ico
```

## Credits and licence

Ulvark is a front end. All the hard work is done by [yt-dlp](https://github.com/yt-dlp/yt-dlp)
(Unlicense), [FFmpeg](https://ffmpeg.org) (GPL build from yt-dlp/FFmpeg-Builds) and
[Deno](https://deno.com) (MIT), which run as separate programs and are not part of this repository.
Ulvark is not affiliated with any of them.

Download only what you have the right to download.
