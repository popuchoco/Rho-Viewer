# Rho Viewer Portable

Rho Viewer Portable is a Windows x64 WinForms desktop tool for reading, searching, analyzing, previewing, and exporting KartRider `.rho` archives.

This project is a functional reconstruction based on the behavior of an older user-provided tool and publicly available Rho format research. It is not a restoration of the original source code of `臉蛋看看(RhoDump)`, and it does not redistribute the original EXE, RAR archive, or game data.

Rho Viewer Portable is not affiliated with, endorsed by, sponsored by, or operated by Nexon, any game developer, publisher, distributor, local agent, platform, or other rights holder.

## Current version

`0.1.4` — framework-dependent `win-x64` portable release.

The Portable ZIP intentionally does not include the .NET Runtime. Install Microsoft .NET 8 Desktop Runtime for Windows x64 before running the application. This keeps the release package small and leaves runtime installation and updates to the user and Microsoft's official installer.

## Legal, EULA, and data-use notice

Use of game data may be subject to applicable local laws and regulations, as well as user agreements, terms of service, licenses, or EULA restrictions imposed by the game developer, publisher, distributor, local agent, platform, or other rights holder. You must comply with all applicable legal and contractual restrictions in your jurisdiction.

This tool does not grant permission to access, extract, analyze, modify, reproduce, redistribute, publish, or otherwise use any game data. You are solely responsible for confirming that your use of the tool and the data is authorized and lawful. To the maximum extent permitted by applicable law, the authors and contributors assume no legal responsibility or liability for your use of this tool, any breach of law or contract, or any resulting claim, loss, damage, or dispute. This notice is not legal advice.

## Features

### Rho reading and browsing

- Recursively scan a directory for `.rho` files.
- Select one or more `.rho` files directly.
- Drag a directory or `.rho` files into the window.
- Display archive name, resource path, type, size, and protection method.
- Show loaded archives and the internal folder tree on the left, with a breadcrumb and resource list on the right.
- Preserve the original archive order for the resource list and folder tree; manual sorting is intentionally not provided so the original data order remains traceable.
- Filter by file name, path, or extension.
- Support Select All, Invert Selection, and the `Ctrl+F`, `Ctrl+A`, `Ctrl+I`, `Ctrl+E`, and `Esc` shortcuts.

### Resource preview and analysis

- Preview PNG, JPG/JPEG, BMP, and GIF images.
- Provide text and HEX fallback views for text and unknown binary resources.
- Attempt to parse KSV recording title, track, event type, recorder, player, and recording data sections.
- Identify Ogg/Vorbis and Opus streams and report channels, sample rate, estimated duration, and tags.
- Read FLAC STREAMINFO, channels, sample rate, bit depth, estimated duration, and Vorbis comments.
- Clearly distinguish a valid empty archive from a load or parsing failure.

### Export and audio playback

- Export selected resources or all resources into subdirectories named after the source Rho archive.
- Provide export progress, cancellation, and failed-item reporting.
- Extract OGG, FLAC, WAV, and MP3 resources into a temporary directory before handing them to an external player.
- Prefer `Players\vlc.exe`, `Players\mpv.exe`, or `Players\ffplay.exe` in the Portable directory; if none is found, use the Windows default file association.
- Allow playback to be stopped and attempt to clean up temporary files created by the application.
- Do not bundle a third-party audio codec or player. Users must obtain players from lawful sources and comply with their licenses.

### Interface and settings

- Use a fixed light theme. A dark-mode switch is intentionally not provided because native WinForms controls can produce inconsistent contrast, selection colors, drop-downs, and scrollbars across themes.
- Use PerMonitorV2 high-DPI support.
- Resize the left and right layout with the window while preserving the splitter ratio and usable minimum widths.
- Save the normal window size and splitter ratio when the application closes normally, then restore them at the next launch.
- Use Settings to specify the default Rho directory and Log directory. Import Settings Directory can directly load the configured Rho directory.

## Quick start

### Use the release package

1. Install Microsoft .NET 8 Desktop Runtime for Windows x64.
2. Extract `RhoViewerPortable_v0.1.4_win-x64.zip`.
3. Run `RhoViewerPortable.exe`.
4. Click Select Directory, Select Rho Files, or drag files into the window.
5. On first use, open Settings to specify the frequently used Rho and Log directories.

The release package is framework-dependent. The application does not download the Runtime, RhoKey, players, or sample data from GitHub, another website, or a remote service.

### Use the source package

The source package has this root structure:

```text
RhoViewerPortable-Source/
├── README.md
├── LICENSE
├── THIRD_PARTY_NOTICES.txt
├── rho_probe.py
├── RhoViewerPortable/
│   ├── RhoViewerPortable.csproj
│   ├── MainForm.cs
│   ├── Program.cs
│   ├── RhoReaderCore.cs
│   ├── ResourceAnalyzers.cs
│   ├── AppSettings.cs
│   ├── AppLogger.cs
│   ├── SettingsForm.cs
│   └── app.manifest
└── third_party/
    └── Kartrider-File-Reader/
```

## System requirements

### Running the release package

- Windows x64.
- Microsoft .NET 8 Desktop Runtime for Windows x64.
- To play OGG or FLAC, a compatible player must already be installed or a lawfully obtained player must be placed in the `Players` directory.

### Building the source package

- A Windows x64 development environment.
- .NET 8 SDK and Windows Desktop SDK.
- The first build requires NuGet package restore. After restore is complete, application execution and offline self-tests do not require network access.

## Configuration and logs

Configuration file:

```text
Config\RhoViewerPortable.json
```

The configuration file is stored below the application directory. Main fields are:

```json
{
  "rhoDirectory": "D:\\Game\\Rho",
  "logDirectory": "D:\\RhoViewerLogs",
  "windowWidth": 1120,
  "windowHeight": 720,
  "splitterRatio": 0.2
}
```

- `rhoDirectory`: the default starting directory for Select Directory and the source used by Import Settings Directory.
- `logDirectory`: the directory for daily logs.
- `windowWidth` and `windowHeight`: the normal window size used at the next launch.
- `splitterRatio`: the width ratio of the left archive/folder area relative to the main content area.

Log files use the `RhoViewerPortable_yyyyMMdd.log` naming format and UTF-8 encoding. They record startup, loading, filtering, exporting, playback, cleanup, settings, and errors. If the configured Log directory is not writable, the application falls back to a system temporary directory and does not prevent startup because of a logging failure.

## Build and publish

Run the following commands from the source package root:

```powershell
dotnet restore RhoViewerPortable\RhoViewerPortable.csproj
dotnet build RhoViewerPortable\RhoViewerPortable.csproj -c Release --no-restore -p:NoWarn=NU1903
dotnet publish RhoViewerPortable\RhoViewerPortable.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=false `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o publish\win-x64
```

`--self-contained false` is intentional: the release package does not carry the .NET Runtime. `PublishSingleFile=false` keeps the application DLL, dependency files, and runtimeconfig visible for troubleshooting and for confirming a framework-dependent deployment.

## Offline self-tests

```powershell
RhoViewerPortable.exe --format-self-test
RhoViewerPortable.exe --self-test stage_battleReady.rho character_tiera.rho
RhoViewerPortable.exe --smoke-load sound_bgm_desert.rho
```

- `--format-self-test`: tests synthetic FLAC, an invalid OGG header, and the KSV fallback path.
- `--self-test`: reads the specified Rho files and verifies the archive layer, image signatures, audio extraction, and audio analyzers.
- `--smoke-load`: creates the WinForms main window, loads the specified Rho, and closes automatically to detect startup or loading crashes.

Baseline sample results for this release:

| Sample | Result |
|---|---|
| `sound_bgm_desert.rho` | Rho 1.1; 5 OGG resources; the first audio resource extracts and passes the `OggS` check |
| `stage_battleReady.rho` | Rho 1.1; 94 resources; image signature check passes |
| `character_tiera.rho` | Rho 1.1; 116 resources; image signature check passes |
| `track_park_R01.rho` | Rho 1.1; 6 resources; image signature check passes |
| `sound_bgm_.rho` | Rho 1.1; 0 resources; displayed as a valid empty archive |

## Architecture overview

```text
MainForm (WinForms UI)
├── AppSettings / AppLogger
├── RhoReaderCore
│   └── local KartLibrary / RhoArchive / RhoFolder / RhoFile
├── ResourceAnalyzers
│   ├── KSV summary
│   ├── OGG Vorbis / Opus metadata
│   └── FLAC metadata
└── External playback bridge
    ├── portable Players folder
    └── Windows file association fallback
```

The RhoKey table is stored locally at `third_party/Kartrider-File-Reader/src/KartriderLibrary/Encrypt/RhoKey.cs`. The application does not read a GitHub raw URL at runtime and does not require a remote key service.

## Security and data boundaries

- Source Rho archives are opened for reading; the tool does not write back to or repack the source archive.
- Resources inside a Rho archive are not executed as programs. An external player only receives an extracted temporary audio file after the user starts playback.
- Export paths are restricted to the output directory selected by the user to prevent resource path traversal.
- The source and Portable packages do not include user Rho files, images, audio, original EXEs, RAR archives, or user configuration data.
- No API key, private token, or remote download URL is written into the execution flow.

## Known limitations

- The current implementation provides metadata analysis and an external-player bridge; it is not an embedded OGG or FLAC codec.
- Playback progress and volume controls are not provided. Stop only controls an external player for which the application can obtain a process handle.
- Rho archives are not modified or repacked.
- KSV fields and text encoding may vary by game version and require more fixtures for cross-version validation.
- The true OEP, IAT repair, and inner code-level restoration of the original `臉蛋看看(RhoDump)` executable still require an isolated VM. This project does not claim to restore its original source code.
- Actual audio playback depends on the user's installed player and codecs. The release package does not include a specific player or guarantee a particular listening result.

## License, sources, and acknowledgements

This source package is released under GPL-3.0. The complete license text is in [`LICENSE`](LICENSE). Third-party source scope, attribution, and redistribution notices are in [`THIRD_PARTY_NOTICES.txt`](THIRD_PARTY_NOTICES.txt).

Acknowledgements:

1. 黑黑de臉蛋's `臉蛋看看(RhoDump)`: for the historical program context and research sample source.
2. [xpoi5010/Kartrider-File-Reader](https://github.com/xpoi5010/Kartrider-File-Reader): for public Rho/Rho5 format research and C# decoding reference code. Its GPL-3.0 license text is retained in `third_party/Kartrider-File-Reader/LICENSE`.

This project does not redistribute the original `臉蛋看看(RhoDump)` EXE, RAR archive, private source code, or user-provided game resources.

## Related file

- [`rho_probe.py`](rho_probe.py): an offline Rho inspection helper that uses the local source-package RhoKey and does not download a key over the network.
