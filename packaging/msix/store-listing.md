# Microsoft Store listing (copy into Partner Center)

## Product name

Vid Archiver GUI

## Short description

A graphical front end for yt-dlp that saves every download to the right folder automatically.

## Description

Vid Archiver GUI is a graphical front end for yt-dlp, the open-source command-line media downloader. Everything
yt-dlp can do is available through a simple window: paste links, choose a preset, click Start. There's no command line.

Vid Archiver GUI doesn't download anything itself. It runs yt-dlp for you. yt-dlp is a separate open-source project
(https://github.com/yt-dlp/yt-dlp). On first start the app offers to install it from its official releases, along
with FFmpeg and deno, and checks each download against the publisher's checksums. You can also point the app at a
yt-dlp you already have.

Folder rules
Rules pick the destination folder automatically from the site, channel, playlist, title or any other field yt-dlp
reports. For example, you can file lectures under Learning\{channel} and podcasts under Audio\{playlist}. The first
matching rule decides, and a Test a URL tool shows which folder a link would get.

Presets
A preset is just yt-dlp options. Paste the command you already use, and pick a different preset per rule.

Built for archiving
- Queue many links and run several downloads at once
- Automatic retries for network errors, and a fallback to other yt-dlp release channels when a site changes
- Download gently mode, which paces requests so large archive jobs are polite to servers
- Keeps your yt-dlp up to date, with one-click rollback
- Light and dark themes

Please respect copyright and each site's terms of service. Download only content that you own, that is freely
licensed, or that you have permission to save.

Open source under the Apache 2.0 license: https://github.com/disibio/vid-archiver-gui

## Search terms (max 7)

yt-dlp, yt-dlp gui, media downloader, video archiver, download manager, archive, ffmpeg

## Category

Utilities & tools

## Screenshots

docs/screenshots/*.png (they use Wikimedia Commons open movies)

## Privacy policy URL

https://github.com/disibio/vid-archiver-gui/blob/main/PRIVACY.md

## Notes for certification

Vid Archiver GUI is a graphical front end for yt-dlp, an open-source command-line program. The app itself contains
no downloading or site-specific code; it builds a yt-dlp command line from the user's settings and shows its progress.

- runFullTrust: this is a regular desktop app (.NET/Avalonia). It runs yt-dlp, FFmpeg and deno as separate
  processes and writes to folders the user chooses.
- Helper tools: on first run the user is asked to install yt-dlp, FFmpeg and deno. They're downloaded from the
  projects' official GitHub releases into the app's own data folder, verified against the publishers' SHA-256
  checksums, and updated from the same source. They're separate command-line programs; they don't change the app's
  functionality or code.
- To test: open the Settings tab and click "Fix now" to install the tools. Then paste
  https://commons.wikimedia.org/wiki/File:Big_Buck_Bunny_4K.webm on the Downloads tab, click Add, then Start.
