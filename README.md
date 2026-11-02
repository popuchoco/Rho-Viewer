# Rho Viewer Portable

Rho Viewer Portable is a Windows x64 WinForms desktop tool for reading, searching, analyzing, previewing, and exporting KartRider `.rho` and `.rho5` game resources, with a separate game-file updater.

This project is a functional reconstruction based on the behavior of an older user-provided tool and publicly available Rho format research. It is not a restoration of the original source code of `黑黑de臉蛋 (RhoDump)`, and it does not redistribute the original executable, archive, or game data.

Rho Viewer Portable is not affiliated with, endorsed by, sponsored by, or operated by Nexon, any game developer, publisher, distributor, local agent, platform, or other rights holder.

## Current version

`0.2.0` — framework-dependent `win-x64` portable release.

The Portable ZIP intentionally does not include the .NET Runtime. Install Microsoft .NET 8 Desktop Runtime for Windows x64 before running the application. This keeps the release package small and leaves runtime installation and updates to the user and Microsoft's official installer.

## Legal, EULA, and data-use notice

Use of game data may be subject to applicable local laws and regulations, as well as user agreements, terms of service, licenses, or EULA restrictions imposed by the game developer, publisher, distributor, local agent, platform, or other rights holder. You must comply with all applicable legal and contractual restrictions in your jurisdiction.

This tool does not grant permission to access, extract, analyze, modify, reproduce, redistribute, publish, update, or otherwise use any game data. You are solely responsible for confirming that your use of the tool and the data is authorized and lawful. To the maximum extent permitted by applicable law, the authors and contributors assume no legal responsibility or liability for your use of this tool, any breach of law or contract, or any resulting claim, loss, damage, or dispute. This notice is not legal advice.

## Features

### Rho and Rho5 reading and browsing

- Recursively scan a directory for `.rho` archives and `.rho5` data-pack sets.
- Select `.rho` files directly; opening any `.rho5` part loads its complete same-prefix data-pack set.
- Drag a directory or supported archive files into the window.
- Decrypt legacy Rho5 resources using the vendored KartRider-File-Reader implementation; use the saved TW/KR/CN region setting or detect the region from a nearby `KartRiderTW.ini`, `KartRiderKR.ini`, or `KartRiderCN.ini`.
- Display archive name, resource path, type, size, and protection method.
- Show loaded archives and the internal folder tree on the left, with a breadcrumb and resource list on the right.
- Preserve the original archive order for the resource list and folder tree; manual sorting is intentionally not provided.
- Filter by file name, path, archive name, resource category, or extension. Category and extension filters can be combined.
- Support Select All, Invert Selection, and the `Ctrl+F`, `Ctrl+A`, `Ctrl+I`, `Ctrl+E`, and `Esc` shortcuts.

### Resource preview and analysis

- Preview PNG, JPG/JPEG, BMP, and GIF images.
- Provide text and HEX fallback views for text and unknown binary resources.
- Attempt to parse KSV recording title, track, event type, recorder, player, and recording data sections.
- Classify KSV recordings separately from audio, and classify `.1s` resources as model/scene resources.
- Probe `.1s` files for public model-format marker candidates and a declared module count when the file header is present. This is a read-only format fingerprint, not a complete decoder or validity check.
- Identify Ogg/Vorbis and Opus streams and report channels, sample rate, estimated duration, and tags.
- Read FLAC STREAMINFO, channels, sample rate, bit depth, estimated duration, and Vorbis comments.
- Clearly distinguish a valid empty archive from a load or parsing failure.

### LPK format status

- `.lpk` files are recognized and produce an explicit unsupported-format diagnostic; they are not treated as `.rho` or `.rho5` files.
- The supplied [`ihopenot/LpkUnpacker`](https://github.com/ihopenot/LpkUnpacker) project targets Live2DViewerEx LPK archives (ZIP containers with `config.mlve`), not the supplied KartRider `DataPack*.lpk` files. The two local DataPack samples are not ZIP archives and the vendored Rho5 reader rejects them, so the project does not guess a decryption algorithm or copy code from the unrelated tool.
- A compatible Nexon DataPack LPK specification or a correctly scoped open-source implementation is still required before LPK decryption can be implemented.

### Export and audio playback

- Export selected resources or all resources into subdirectories named after the source Rho archive.
- Provide export progress, cancellation, and failed-item reporting.
- Extract OGG, FLAC, WAV, and MP3 resources into a temporary directory before handing them to an external player.
- Prefer `Players\\vlc.exe`, `Players\\mpv.exe`, or `Players\\ffplay.exe` in the Portable directory; if none is found, use the Windows default file association.
- Do not bundle a third-party audio codec or player. Users must obtain players from lawful sources and comply with their licenses.

### Game file update

The **Update Game Files** screen is designed for a user who only wants to select the game directory:

1. Select the directory containing `KartRider.pin`.
2. Select `Auto (from KartRider.pin)`, `Taiwan (TW)`, `Korea (KR)`, or `China (CN)`. The selected region maps to the patch server host and port used for discovery.
3. The application reads the local pin information and contacts the mapped patch service.
4. It reads the KartRider `NFO200` file list and compares local files using the Kart-specific CRC.
5. It shows a preview of missing or mismatched files and the estimated download size.
6. After explicit confirmation, full compressed files are downloaded to a staging directory, decompressed, CRC-checked, and then applied.

Before replacing an existing file, the updater moves the original into this relative backup path:

```text
<selected game directory>\\.RhoViewerUpdate\\backups\\<yyyyMMdd_HHmmss>\\<original relative path>
```

For example, a file originally located at `Data\\aaa.pk` is backed up under the timestamped backup directory as `Data\\aaa.pk`. The completed backup path is shown in the update status area. The updater attempts rollback if applying a batch fails, rejects absolute paths and parent traversal, and writes only below the selected game directory. It does not update the Rho Viewer executable, alter the registry, or execute a downloaded program.

The first implementation intentionally uses full-file updates only. It does not invoke `Patcher.exe` or apply delta patches. The patch service must still be available for the selected region; a historical or discontinued service may be unreachable and is not replaced by this application.

The update workflow is informed by the public [`brownsugar/kart-patcher`](https://github.com/brownsugar/kart-patcher) project. That project provides client-side patcher research and implementation references; it does not redistribute copyrighted game update files.

### Interface and settings

- Use a fixed light theme for predictable native WinForms contrast.
- Present the current interface in Traditional Chinese (zh-TW); selectable additional locales are not implemented yet.
- Use PerMonitorV2 high-DPI support.
- Resize the left and right layout with the window while preserving the splitter ratio and usable minimum widths.
- Save the normal window size and splitter ratio when the application closes normally.
- Use Settings to specify the default Rho directory and Log directory.
- The game update screen remembers the last selected game directory in the local configuration file.

## Quick start

### Use the release package

1. Install Microsoft .NET 8 Desktop Runtime for Windows x64.
2. Extract the portable ZIP from the GitHub Release page.
3. Run `RhoViewerPortable.exe`.
4. Click Select Directory, Select Rho Files, or drag files into the window.
5. To update game files, click **Update Game Files**, select the game directory containing `KartRider.pin`, click **Check for Updates**, review the preview, and confirm the update.

The release package is framework-dependent. The application does not download the Runtime, RhoKey, players, sample data, or a copy of the game from GitHub or another website.

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
│   ├── GameUpdateForm.cs
│   ├── KartPatchService.cs
│   ├── Program.cs
│   ├── RhoReaderCore.cs
│   ├── ResourceAnalyzers.cs
│   ├── ResourceClassifier.cs
│   ├── OneSAnalyzer.cs
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
- To play OGG or FLAC, a compatible player must already be installed or lawfully obtained and placed in the `Players` directory.

### Building the source package

- A Windows x64 development environment.
- .NET 8 SDK and Windows Desktop SDK.
- The first build requires NuGet package restore. After restore is complete, offline Rho parsing self-tests do not require network access.

## Configuration and logs

Configuration file:

```text
Config\\RhoViewerPortable.json
```

Main fields are:

```json
{
  "rhoDirectory": "D:\\Game\\Rho",
  "gameDirectory": "D:\\Game\\KartRider",
  "gameRegion": "auto",
  "logDirectory": "D:\\RhoViewerLogs",
  "windowWidth": 1120,
  "windowHeight": 720,
  "splitterRatio": 0.2
}
```

- `rhoDirectory`: the default starting directory for Rho browsing and Import Settings Directory.
- `gameDirectory`: the last directory selected by the game update screen. It is only a convenience default; the user must still review the path before checking or applying an update.
- `gameRegion`: `auto`, `tw`, `kr`, or `cn`. `auto` uses the host and port stored in `KartRider.pin`; a selected region uses the corresponding public mapping.
- `logDirectory`: the directory for daily logs.
- `windowWidth` and `windowHeight`: the normal window size used at the next launch.
- `splitterRatio`: the width ratio of the left archive/folder area relative to the main content area.

Log files use the `RhoViewerPortable_yyyyMMdd.log` naming format and UTF-8 encoding. They record startup, loading, filtering, exporting, playback, update checks, update failures, settings, and cleanup. If the configured Log directory is not writable, the application falls back to a system temporary directory and does not prevent startup because of a logging failure.

## Build and publish

Run the following commands from the source package root:

```powershell
dotnet restore RhoViewerPortable\\RhoViewerPortable.csproj
dotnet build RhoViewerPortable\\RhoViewerPortable.csproj -c Release --no-restore -p:NoWarn=NU1903
dotnet publish RhoViewerPortable\\RhoViewerPortable.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=false `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o publish\\win-x64
```

`--self-contained false` is intentional: the release package does not carry the .NET Runtime. `PublishSingleFile=false` keeps the application DLL, dependency files, and runtimeconfig visible for troubleshooting and for confirming a framework-dependent deployment.

The GitHub Actions CI workflow also publishes a Windows x64 portable package named `Rho-Viewer-win-x64` and attaches it to the workflow run as an artifact (retained for 14 days). Download and extract the artifact contents together; do not copy only `RhoViewerPortable.exe`, because it depends on the adjacent application files and the .NET 8 Desktop Runtime.

## Offline self-tests

```powershell
RhoViewerPortable.exe --format-self-test
RhoViewerPortable.exe --kart-crc-self-test
RhoViewerPortable.exe --self-test stage_battleReady.rho character_tiera.rho DataPack1_00000.rho5
RhoViewerPortable.exe --smoke-load sound_bgm_desert.rho
```

- `--format-self-test`: tests synthetic FLAC, invalid OGG handling, KSV fallback, `.1s` marker probing, and resource categories.
- `--kart-crc-self-test`: verifies the local Kart-specific CRC implementation against a deterministic test vector.
- `--self-test`: reads specified `.rho` or `.rho5` archive inputs and verifies archive, image, audio extraction, and analyzer behavior.
- `--smoke-load`: creates the WinForms main window, loads the specified Rho, and closes automatically to detect startup or loading crashes.

## Architecture overview

```text
MainForm (WinForms UI)
├── AppSettings / AppLogger
├── RhoReaderCore
│   └── local KartLibrary / RhoArchive / Rho5Archive / RhoFolder / RhoFile
├── ResourceAnalyzers
│   ├── KSV summary
│   ├── OGG Vorbis / Opus metadata
│   ├── FLAC metadata
│   └── `.1s` marker probe
├── ResourceClassifier
│   ├── image / audio / model-scene / KSV / structured-data categories
│   └── independent of the original archive folder hierarchy
├── GameUpdateForm
│   └── KartPatchService
│       ├── KartRider.pin parser
│       ├── patch socket discovery
│       ├── NFO200 manifest parser
│       ├── Kart CRC verification
│       └── staged download, backup, and rollback
└── External playback bridge
    ├── portable Players folder
    └── Windows file association fallback
```

The RhoKey table is stored locally at `third_party/Kartrider-File-Reader/src/KartriderLibrary/Encrypt/RhoKey.cs`. The application does not read a GitHub raw URL at runtime and does not require a remote key service.

The `.1s` analyzer reports candidate occurrences of public file/model/vertex markers and a declared module count where the file header permits it. Marker definitions are referenced from the MIT-licensed `VT-Tuzki/kartrider_model_1s_to_obj` project; no parser or exporter code is copied. It does not decode meshes, materials, animations, or reliably determine whether a model is a track, character, or kart. See `THIRD_PARTY_NOTICES.txt` and `third_party/Kartrider-File-Reader/UPSTREAM.md` for dependency provenance and license details.

## Security and data boundaries

- Source Rho archives are opened for reading; the tool does not write back to or repack the source archive.
- Resources inside a Rho archive are not executed as programs. An external player only receives an extracted temporary audio file after the user starts playback.
- Export paths and game update paths are restricted to their respective user-selected directories.
- Update downloads are staged and CRC-checked before replacement; original files are backed up before they are moved.
- The updater does not execute `Patcher.exe`, install a service, modify the registry, or update the Rho Viewer executable.
- The source and Portable packages do not include user Rho files, images, audio, original executables, RAR archives, or user configuration data.
- No API key or private token is required or written into the execution flow.

## Known limitations

- The current implementation provides metadata analysis and an external-player bridge; it is not an embedded OGG or FLAC codec.
- Playback progress and volume controls are not provided.
- Rho archives are not modified or repacked.
- KSV fields and text encoding may vary by game version and require more fixtures for cross-version validation.
- `.1s` support is limited to experimental marker fingerprinting; it does not export OBJ/USD or fully parse model geometry.
- Game update availability depends on the region's patch service and the selected installation's `KartRider.pin`. The updater currently supports full-file NFO200 updates only; delta patching is intentionally not enabled.
- Actual audio playback depends on the user's installed player and codecs.
- The true OEP, IAT repair, and inner code-level restoration of the original `黑黑de臉蛋 (RhoDump)` executable still require an isolated VM. This project does not claim to restore its original source code.

## License, sources, and acknowledgements

This source package is released under GPL-3.0. The complete license text is in [`LICENSE`](LICENSE). Third-party source scope, attribution, and redistribution notices are in [`THIRD_PARTY_NOTICES.txt`](THIRD_PARTY_NOTICES.txt).

Acknowledgements:

1. 黑黑de臉蛋's 臉蛋看看 (RhoDump): for the historical program context and research sample source.
2. [`xpoi5010/Kartrider-File-Reader`](https://github.com/xpoi5010/Kartrider-File-Reader): for public Rho/Rho5 format research and C# decoding reference code. Its GPL-3.0 license text is retained in `third_party/Kartrider-File-Reader/LICENSE`.
3. [`brownsugar/kart-patcher`](https://github.com/brownsugar/kart-patcher): for public KartRider patcher workflow, `KartRider.pin`/patch socket research, NFO200 manifest handling, and update safety reference. The project is published under MIT; this application does not redistribute its Electron application or copyrighted game files.

The original RhoDump executable, archive, and user-provided game samples are not redistributed in this package.
