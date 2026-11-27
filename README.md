<p align="center">
  <img src="docs/logo.png" width="120" alt="Grab Editor logo">
</p>

<h1 align="center">Grab Editor</h1>

<p align="center">
  A simple, fast video editor for Windows. Cut, trim, speed up, add text, export.<br>
  Portable. No installer. No account.
</p>

<p align="center">
  <a href="https://github.com/aroxwex/Grab-Editor/releases"><img alt="Release" src="https://img.shields.io/github/v/release/aroxwex/Grab-Editor"></a>
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey">
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/aroxwex/Grab-Editor"></a>
</p>

<!-- Add a screenshot after your first run:  ![Grab Editor](docs/screenshot.png) -->

> **Status: early preview (0.1).** The basics work and are meant to feel good. More tools are on the way, see [What's next](#whats-next).
> Looking for a downloader instead? That is [Grab](https://github.com/aroxwex/Grab), a separate app.

## What you can do

- **Edit on a timeline.** Put clips in a row, drag them to reorder, drag their edges to trim, split them anywhere.
- **Per-clip control.** Speed (0.25× to 4×), volume (0 to 200%), fade in / fade out, and *Fit* or *Fill* the frame.
- **Text and captions.** Add text at any moment: size, position, colour and an optional dark box. Move it or stretch it on the timeline.
- **Pick the shape.** 16:9, 9:16 (Shorts, TikTok, Reels), 1:1 or 4:5.
- **Undo and redo** for everything, and your work is **auto-saved** in case something goes wrong.
- **Smooth preview.** Videos that Windows can't play directly get a small preview copy automatically, so editing stays fast.
- **Export** in 1080p, 720p or 480p, in three quality levels, or **under 25 MB** for Discord.
- **Projects.** Save, reopen and keep editing. Recent projects are on the start screen.
- **Download a video** from a link (YouTube and hundreds of other sites) with **Import > From a link**, and it lands straight in your project.

## Download

1. Open the [Releases](https://github.com/aroxwex/Grab-Editor/releases) page and download **Grab-Editor-portable.zip**.
2. Extract it anywhere. You get one folder:

   ```
   GrabEditor/
   ├─ GrabEditor.exe
   ├─ bin/     ffmpeg.exe, ffprobe.exe, yt-dlp.exe, qjs.exe
   └─ data/    settings, preview copies and the last export log (created on first launch)
   ```
3. Run `GrabEditor.exe`. Nothing to install, and you don't need .NET.

> Windows may say *"Windows protected your PC"* the first time, because the app isn't code-signed.
> Click **More info > Run anyway**. You can always build it yourself from this repository.

## Quick start

1. **Add videos:** drop files onto the window, or press **Import**. The first video goes onto the timeline by itself.
2. **Watch:** press `Space`. Click the ruler to move around.
3. **Cut:** move the playhead and press `S` to split. Select the part you don't want and press `Delete`.
4. **Trim:** drag the edges of a clip. Edges snap to each other and to the playhead.
5. **Change a clip:** click it, then use the panel on the right: speed, volume, fades, fit or fill.
6. **Add text:** press `T`, type your words, choose size and position.
7. **Export:** press **Export**, choose a size and press **Export** again.

## Shortcuts

| Key | What it does |
| --- | --- |
| `Space` | Play / pause |
| `S` or `B` | Split the clip at the playhead |
| `Delete` | Delete the selected clip or text |
| `Ctrl` + `D` | Duplicate the selected clip |
| `T` | Add a text at the playhead |
| `[` and `]` | Trim the clip's start / end to the playhead |
| `←` `→` | One frame back / forward (hold `Shift` for one second) |
| `Home` / `End` | Jump to the start / end |
| `+` / `-` | Zoom the timeline (or `Ctrl` + mouse wheel) |
| `Ctrl` + `Z` / `Ctrl` + `Y` | Undo / redo |
| `Ctrl` + `I` | Import videos |
| `Ctrl` + `S` / `Ctrl` + `O` / `Ctrl` + `N` | Save / open / new project |
| `Ctrl` + `E` | Export |
| `F1` | Show the shortcuts |
| `Alt` (while dragging) | Turn snapping off for that drag |

## How it works (and why exports look better than the preview)

- Grab Editor never changes your original files. A project only remembers *which part of which file* goes where, so if you move or rename a video the project will say it's missing. Put it back and everything works again.
- For playback, videos Windows can't play smoothly are copied once into a small 540p version in `data/cache`. That is **only for the preview**. Exports always read your originals at full quality.
- **Export** turns the whole timeline into one [FFmpeg](https://ffmpeg.org/) command. Pictures are encoded with H.264 (CRF 18 to 23 depending on the quality you pick) and sound with AAC 192 kbps. Clips with different shapes are fitted into the frame, so everything joins cleanly.
- The preview plays at the speed and volume you set, but it can't play volume above 100%. The export does.
- Old preview copies are deleted after 30 days, and you can clear them any time from the shortcuts window (`F1`).

## Troubleshooting

| Problem | What to try |
| --- | --- |
| "Missing in the bin folder" at the top | Put `ffmpeg.exe` and `ffprobe.exe` into the `bin` folder next to `GrabEditor.exe`. |
| A video shows "Getting the preview ready…" | A preview copy is being made. Longer videos take a minute. You can keep editing. |
| "This file is missing" | The video was moved or renamed. Move it back, or remove the clip and import it again. |
| Export says "Not enough free space" | Free some room on the drive in the message, or choose another save folder. |
| Export fails | Open `data/last-export.log` and share it when you report the problem on GitHub. |
| Downloading from a link fails | Put a newer `yt-dlp.exe` into `bin`. For videos that need a login, download them with [Grab](https://github.com/aroxwex/Grab), then import the file. |
| Windows SmartScreen warning | See the note under **Download**. |

## What's next

Planned, in roughly this order: a music / sound track, transitions between clips, filters and colour adjustment, rotate and crop, GIF export, more text styles and animations, and captions made from speech.
Ideas and bug reports are welcome in [Issues](https://github.com/aroxwex/Grab-Editor/issues).

## Build from source

Requires the .NET 8 SDK on Windows.

```
dotnet publish src/GrabEditor/GrabEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/GrabEditor
```

Then put `ffmpeg.exe`, `ffprobe.exe`, `yt-dlp.exe` and `qjs.exe` into `dist/GrabEditor/bin`.
For development you can also run `dotnet run --project src/GrabEditor` with those programs on your `PATH`.

The **Build Grab Editor** workflow in `.github/workflows` does all of this on GitHub: run it manually, or push a tag like `v0.1.0` to publish a release.

```
src/GrabEditor/
├─ Models/       the project: media, clips, texts
├─ Services/     ffmpeg and yt-dlp helpers, exporting, preview copies, undo, settings
├─ Controls/     the timeline
├─ Themes/       colours and styles
├─ Assets/       logo and icon
└─ MainWindow    the app window, split into Playback / Edit / Media / Project / Export / Download
```

## Credits and licenses

Grab Editor does its heavy lifting with other people's great work. These programs ship unmodified in `bin` and run as separate processes:

| Program | Used for | License |
| --- | --- | --- |
| [FFmpeg](https://ffmpeg.org/) | Reading videos, preview copies, thumbnails, exporting | GPL v3 (the "essentials" build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), source available there) |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | Only for Import > From a link | Unlicense |
| [QuickJS-NG](https://github.com/quickjs-ng/quickjs) | Helper scripts YouTube needs | MIT |

The .NET runtime is bundled too (MIT). Check each project for its current license terms.

## Disclaimer

Only download and edit content you have the right to use. The terms of service of the sites you use and the copyright rules that apply to you are your own responsibility. Grab Editor is not affiliated with YouTube or any other site.

## License

[MIT](LICENSE) © 2026 aroxwex
