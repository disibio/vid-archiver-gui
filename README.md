<p align="center"><img src="src/VidArchiverGui.App/Assets/icon.png" width="128" alt="Vid Archiver GUI icon"></p>

<h1 align="center">Vid Archiver GUI</h1>

___

A graphical front end for yt-dlp, inspired by the yt-dlg oleksis fork. It's designed to be dead simple to use and fix the minor things that annoyed me with other GUI's. This is a completely custom rewrite in C#, primarily AI written but well tested. 

___

A cross-platform (Windows / macOS / Linux) desktop front-end for [yt-dlp](https://github.com/yt-dlp/yt-dlp),
built with .NET 10 and Avalonia, with one big addition: **folder rules** that pick the destination folder
automatically based on where a video comes from.

![Downloads tab: four videos, each filed into a folder by a rule](docs/screenshots/downloads-light.png)

> **Written by AI.** Vid Archiver GUI is written with **Claude Opus 5.5**.

Inspired by yt-dlg ([youtube-dl-gui, the oleksis fork](https://github.com/oleksis/youtube-dl-gui)). Vid Archiver GUI
is an independent implementation; no code from that project is included.

## Download

Get the latest build from [Releases](https://github.com/disibio/vid-archiver-gui/releases): a standalone `.exe` for
Windows (in a `.zip`) and `.tar.gz` packages for Linux x64/arm64, each with an `install.sh`. The Windows build isn't
code-signed, so SmartScreen may warn on first launch: choose **More info → Run anyway**. On first start the app offers
to install yt-dlp, FFmpeg and deno for you.

## Screenshots

| Folder rules | Presets |
|---|---|
| ![Folder rules tab](docs/screenshots/folder-rules.png) | ![Presets tab](docs/screenshots/presets.png) |
| **Settings** | **Dark theme** |
| ![Settings tab with the setup check](docs/screenshots/settings.png) | ![Downloads tab in the dark theme](docs/screenshots/downloads.png) |

## License

Apache License 2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE). Copyright 2026 The Vid Archiver GUI contributors.
Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and on the
app's **About** tab. yt-dlp, youtube-dl and FFmpeg are not included; the app downloads them from their official
releases (verified against published checksums) and they remain under their own licenses. When dependencies change,
regenerate THIRD-PARTY-NOTICES.txt from the packages' license files.

## How it works

1. Paste one or more URLs on the **Downloads** tab and click **Add** (or Ctrl+Enter).
2. The app runs `yt-dlp -J --flat-playlist` to learn the site, channel and playlist.
3. **Folder rules** are checked top to bottom; the first match sets the folder (and optionally the preset).
   If nothing matches, the fallback folder is used. You can still change the folder before starting.
4. Click **Start** / **Start all**. yt-dlp runs with your preset's arguments plus `-P <folder>`.

Tip: set a folder by hand, then click **Remember** to turn it into a rule for that channel.

### Folder rules

Each rule has one or more conditions (All/Any) on these fields:

| Field      | Example value                                  |
|------------|------------------------------------------------|
| Site       | `ArchiveOrg`, `Wikimedia` (yt-dlp's extractor name; channels and playlists use the same name) |
| Domain     | `archive.org`, `commons.wikimedia.org`         |
| Channel    | `My Favourite Creator`                         |
| ChannelId  | the site's ID for the channel                  |
| Playlist   | `Lo-fi Beats`                                  |
| Title, Url | anything                                       |

Operators: Equals, Contains, StartsWith, Regex (all case-insensitive).
Folder paths may use tokens:

- `{site} {domain} {playlist} {yyyy} {mm}` — built in.
- Any field from yt-dlp's info JSON by its yt-dlp name: `{channel} {channel_id} {uploader} {uploader_id}
  {upload_date} {title} {id} {extractor_key}` …
- Fallback chains: `{channel|channel_id|uploader|uploader_id}` uses the first field that has a value
  (`{(a|b)}` also works). A quoted alternative is literal text: `{playlist|"Singles"}`.
- If nothing in a token has a value, the folder is named `Unknown`.

Example: `E:\ARCHIVE\{site}\{channel|channel_id|uploader|uploader_id}`.
Use **Test a URL** on the rules tab to see a link's field values and the folder it would get.

### Presets

A preset is just yt-dlp arguments ([options reference](https://github.com/yt-dlp/yt-dlp#usage-and-options)) — paste
your usual command. Quotes group text, backslashes are literal (Windows
paths work as-is), new lines are fine and `#` starts a comment line. The app adds `-P`, `--ffmpeg-location`,
`--newline` and a progress template; everything else is yours. Login options (`--cookies-from-browser`, `--cookies`,
`--proxy`, ...) are also used for the metadata lookup.

### When downloads fail

The app reads the downloader's error and reacts to what kind of error it is, for both the info lookup and the download:

| Error | What happens |
|---|---|
| Network (timeouts, dropped connections, 5xx) | Retried with the same downloader after 15 s, then 60 s |
| Site changed ("Unable to extract", nsig/signature, missing formats, other extractor errors) | The other downloaders of the same kind are tried in list order (e.g. stable → nightly → master → your forks); app-managed ones that aren't installed yet are installed first. A downloader that works is remembered for that item. Turn off under Settings → Downloaders. |
| Needs a login (bot check, age gate, members-only) | Stops and suggests picking cookies; cookies are never added automatically |
| Browser cookies unreadable | Stops and suggests closing the browser, using Firefox, or a cookies.txt |
| Rate-limited (429, "try again later") | Stops and suggests waiting and turning on Download gently |
| Unavailable (private, removed, geo-blocked, unsupported URL) | Stops; nothing else would help |

### Download gently

**Settings → Downloads → Download gently** makes every download pause before each request (1 s) and between videos
(5–15 s), and back off exponentially (up to a minute) before retries. It is slower, but sites are much less likely
to throttle you or start asking you to sign in. Use it for archiving whole channels, ideally with 1 simultaneous
download. A preset's own `--sleep-*` / `--retry-sleep` options take precedence.

### Cookies

Some videos need a login: members-only, age-restricted, or a site asking you to sign in to confirm you're not a bot. Pick
cookies in the **Cookies** list on the Downloads tab (for new URLs) or on an individual item, then Start it again.

- The list offers **No cookies** (the default), every browser found on this computer (Firefox, Chrome, Edge, Brave,
  Chromium, Vivaldi, Opera, Whale, Safari) and any cookies.txt files or browser profiles added under
  **Settings → Cookies**.
- A folder rule can switch cookies too (e.g. a members-only channel always uses Firefox).
- The choice replaces any `--cookies` / `--cookies-from-browser` in the preset; with **No cookies** the preset's own
  options still apply.
- Firefox is the most reliable. On Windows, Chromium-based browsers usually can't be read while open and newer versions
  encrypt their cookies, so export a cookies.txt instead. youtube-dl only supports cookies.txt files.
- Cookies tie downloads to your account; heavy archiving with them can get the account rate-limited, so use them only
  where needed.

### Downloaders (yt-dlp, channels, forks)

The Settings tab lists the downloaders; one is the default and each preset can pick a different one
(so a folder rule can route a site through e.g. nightly by choosing a preset).

| Downloader | Source |
|---|---|
| yt-dlp (stable / nightly / master) | installed and updated by the app from `yt-dlp/yt-dlp`, `yt-dlp-nightly-builds`, `yt-dlp-master-builds` |
| youtube-dl (nightly) | installed and updated by the app from `ytdl-org/ytdl-nightly` |
| Custom | any executable (a fork, a pip install, …), marked *yt-dlp compatible* or *youtube-dl compatible* |

The default downloader is installed on first run, and app-installed downloaders are checked for updates once a day.
For youtube-dl-compatible downloaders the destination is applied through `-o` (a relative `-o` in the preset is
placed inside the routed folder) and progress is read from the normal output. Options differ between downloaders,
so write each preset for the downloader it uses.

When the app updates a downloader it keeps the version it replaced. If a new release breaks something, select the
downloader and click **Roll back to …**; the daily update check then skips the release you rolled back from until a
newer one appears (clicking **Check for update** installs it anyway, and **Roll back** again undoes the rollback).

Each download shows its original URL; **Copy URL** (or right-click → copy URL / title / folder, add again) copies it.

### Setup check (first run)

The **Setup check** at the top of the Settings tab asks yt-dlp what it can actually use (`yt-dlp -v`) and checks your
folders. A banner appears when something is missing; **Fix now** installs it. Everything the app installs goes into
its own data folder and is verified against the publisher's SHA-256 checksums.

| Needed | Why | Windows | macOS | Linux |
|---|---|---|---|---|
| yt-dlp | the downloader | automatic | automatic | automatic |
| deno | solving some sites' JavaScript challenges (yt-dlp only enables deno by default) | one click | one click | one click |
| ffmpeg | merging video+audio, embedding | one click | `brew install ffmpeg` (shown with a Copy button) | one click (needs `tar`), else the apt/dnf/pacman/zypper command |
| archive folder | `--download-archive` needs its folder | Create folder | Create folder | Create folder |

Copies you installed yourself (on PATH, Homebrew, `~/.local/bin`) are used when present.

### Platforms

Windows 10/11, macOS 14+ (Apple Silicon and Intel) and Linux (x64/arm64, glibc) — the requirements of .NET 10.
Linux has been tested on Ubuntu 22.04 (WSLg), including first-run setup and a full download. macOS has not been
tested on real hardware yet.

Settings live in `%APPDATA%\VidArchiverGui\settings.json` (Windows), `~/Library/Application Support/VidArchiverGui` (macOS) or
`~/.config/VidArchiverGui` (Linux); the Microsoft Store version keeps them in its own package folder (Settings → App data
shows where). Put an empty `portable.txt` next to the executable to keep data beside it.

## Building

```
dotnet build VidArchiverGui.slnx
dotnet test
dotnet run --project src/VidArchiverGui.App
```

Packages (all self-contained; users don't need .NET):

| Target | Command | Output |
|---|---|---|
| Windows | `run.bat publish` | `publish/win-x64/VidArchiverGui.exe` (+ license files) |
| Linux | `packaging/package-linux.sh` (on Linux, or in WSL using the Windows `dotnet.exe`) | `publish/linux/*.tar.gz` for x64 and arm64; unpack and run `./install.sh` (adds a menu entry; `--remove` uninstalls) |
| Microsoft Store | `powershell -ExecutionPolicy Bypass -File packaging\package-msix.ps1` (needs the Windows SDK; fill in `packaging/msix/store-identity.json` from Partner Center first, `-Sign` for a local test install) | `publish/msix/*.msixbundle` (x64 + arm64); listing text in `packaging/msix/store-listing.md` |
| macOS | `packaging/package-macos.sh` **on a Mac** (Apple Silicon needs the ad-hoc code signature it applies) | `publish/macos/*.zip` containing `Vid Archiver GUI.app` for arm64 and x64 |

The macOS app isn't notarized: on first launch, right-click it and choose **Open**.

## Layout

- `src/VidArchiverGui.Core` — UI-free logic: models, routing engine, path tokens, argument parsing, yt-dlp process + output
  parsing, tool download/update.
- `src/VidArchiverGui.App` — Avalonia UI (MVVM with CommunityToolkit.Mvvm).
- `tests/VidArchiverGui.Core.Tests` — unit tests for the core.

Debug builds: set `VIDARCHIVERGUI_SNAPSHOT_DIR=<folder>` (and optionally `VIDARCHIVERGUI_SNAPSHOT_URLS=url1;url2`) to render every tab
to PNG offscreen and exit — handy for checking UI changes. Add `VIDARCHIVERGUI_SNAPSHOT_SETUP=1` (with a `portable.txt` next
to the app for a clean data folder) to simulate a first run: it captures the setup banner, presses **Fix now**, and with
`VIDARCHIVERGUI_SNAPSHOT_DOWNLOAD=<url>` (optionally `VIDARCHIVERGUI_SNAPSHOT_PRESET=<name prefix>`) downloads for real.
