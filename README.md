# VidGrab

Descarga vídeo y audio en máxima calidad desde YouTube y cualquier web que soporte
[yt-dlp](https://github.com/yt-dlp/yt-dlp). Un solo `.exe` para Windows 10/11, sin instalar nada ni ser
administrador.

## Uso

1. Abre `VidGrab.exe`. Si tienes un enlace copiado, aparece ya en el campo.
2. Elige **vídeo** (y la calidad máxima) o **audio** (MP3).
3. Pulsa Enter o **descargar**. Puedes pegar varios enlaces a la vez o arrastrarlos a la ventana.

Doble clic en una descarga terminada para abrirla; clic derecho para mostrarla en la carpeta, cancelar,
volver a bajarla o copiar el enlace. También se puede lanzar con enlaces: `VidGrab.exe URL [URL ...]`.

Por defecto el vídeo va a `Vídeos\VidGrab` y el audio a `Música\VidGrab`, en carpetas por canal. Clic
en la ruta del pie para cambiarla.

## Cómo funciona

La primera vez descarga el motor en `%LOCALAPPDATA%\VidGrab\bin` (unos 200 MB) desde las releases
oficiales y comprueba el SHA-256 publicado de cada fichero:

| Pieza | Para qué | Origen |
|---|---|---|
| `yt-dlp.exe` | la descarga | `github.com/yt-dlp/yt-dlp` |
| `ffmpeg.exe`, `ffprobe.exe` | unir vídeo y audio, MP3, miniaturas | `github.com/yt-dlp/FFmpeg-Builds` |
| `deno.exe` | el JavaScript que YouTube exige para las calidades altas | `github.com/denoland/deno` |

No van dentro del `.exe` porque yt-dlp deja de funcionar en semanas cuando YouTube cambia algo. La app lo
actualiza sola (`yt-dlp -U`) una vez al día como mucho.

Opciones de descarga: el mejor vídeo y audio por resolución, fps y HDR, en MKV con metadatos, capítulos,
miniatura, subtítulos en español e inglés y marcas de SponsorBlock. Hasta dos descargas a la vez. Lo ya
descargado no se repite (`archive-video.txt` / `archive-audio.txt`).

**Cookies**: si un vídeo pide iniciar sesión o confirmar la edad, clic en «sin cookies» del pie para usar
las de tu navegador (Firefox funciona mejor; Chrome y Edge cifran las suyas y suelen fallar).

## Compilar

Requiere el SDK de .NET (cualquiera reciente) y el targeting pack de .NET Framework 4.8.

```powershell
dotnet build -c Release            # bin\Release\net48\VidGrab.exe
.\tools\make-icon.ps1              # regenera app.ico
```

## Licencia

VidGrab solo lanza yt-dlp (Unlicense), ffmpeg (GPL) y deno (MIT) como programas aparte; no los incluye en
su código.
