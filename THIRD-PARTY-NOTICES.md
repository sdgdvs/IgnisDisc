# Third-party notices

IgnisDisc is MIT-licensed (see `LICENSE`). Release builds include the following third-party components.

| Component | Used for | License |
| --- | --- | --- |
| .NET runtime and Windows Desktop runtime (self-contained builds) | Runs the app | MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| CommunityToolkit.Mvvm | MVVM helpers in the desktop app | MIT — https://github.com/CommunityToolkit/dotnet/blob/main/License.md |
| Microsoft.Extensions.DependencyInjection | Service wiring in the desktop app | MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |

Windows components used at runtime but **not** redistributed: IMAPIv2 (`imapi2.dll`, `imapi2fs.dll`) and Media Foundation.

## ffmpeg (optional, 1-click install available)

DVD-Video transcoding calls an external `ffmpeg.exe` as a separate program. It is **not** bundled by default.
You can download and install the official LGPL Windows build with 1 click from the **DVD-Video** or **Settings** workspace (installed per-user into `%LOCALAPPDATA%\Programs\ffmpeg\ffmpeg.exe` alongside `ffmpeg-LICENSE.txt`), or point IgnisDisc to an existing `ffmpeg.exe`.
If a release bundles it (via `scripts/Build-Installer.ps1 -FfmpegPath ... -FfmpegLicensePath ...`), that build's
license text ships as `ffmpeg-LICENSE.txt` next to it. ffmpeg is licensed under the LGPL v2.1+ (or GPL v2+ for
GPL-enabled builds); see https://ffmpeg.org/legal.html. Prefer an LGPL build, record exactly which build you shipped
in the release notes, and link to its source.

## par2cmdline (optional, 1-click install available)

Archive recovery data is created and repaired by calling an external `par2.exe`
([par2cmdline](https://github.com/Parchive/par2cmdline), GPL v2+) as a separate program. It is **not** bundled by default;
you can download and install it with 1 click from the **Archive** or **Settings** workspace (installed per-user into
`%LOCALAPPDATA%\Programs\par2cmdline\par2.exe` alongside `par2-LICENSE.txt`) or install it yourself on `PATH`.

## Online services (optional, 1-click lookup & configurable defaults)

Audio ripping can look up track names from [MusicBrainz](https://musicbrainz.org) (automatically during ripping or on demand via the 1-click **Look Up Disc (MusicBrainz)** button) and check rips against the
[AccurateRip](http://www.accuraterip.com) database. Both lookups can be toggled in the **Audio CD Ripper** and **Settings** workspaces, and only send the disc's table-of-contents
identifiers, never audio or personal data.

## Test-only dependencies (not shipped)

xUnit, Microsoft.NET.Test.Sdk, coverlet, and LTRData.DiscUtils (used as an independent ISO 9660 / Joliet / UDF
reader to validate generated images).
