# Rho Viewer Portable

Rho Viewer Portable is a Windows x64 WinForms desktop tool for reading, searching, analyzing, previewing, and exporting KartRider `.rho`, `.rho5`, and Nexon LPK v4 game resources, with a separate game-file updater.

This project is a functional reconstruction based on the behavior of an older user-provided tool and publicly available Rho format research. It is not a restoration of the original source code of `黑黑de臉蛋 (RhoDump)`, and it does not redistribute the original executable, archive, or game data.

Rho Viewer Portable is not affiliated with, endorsed by, sponsored by, or operated by Nexon, any game developer, publisher, distributor, local agent, platform, or other rights holder.

## Current version

`0.3.9` — framework-dependent `win-x64` portable release targeting .NET 10. The release ZIP is published on the [GitHub Releases page](https://github.com/popuchoco/Rho-Viewer/releases).

The Portable ZIP intentionally does not include the .NET Runtime. Install Microsoft .NET 10 Desktop Runtime 10.0.14 or a later .NET 10 servicing release for Windows x64 before running the application. This keeps the release package small and leaves runtime installation and updates to the user and Microsoft's official installer.

## Legal, EULA, and data-use notice

Use of game data may be subject to applicable local laws and regulations, as well as user agreements, terms of service, licenses, or EULA restrictions imposed by the game developer, publisher, distributor, local agent, platform, or other rights holder. You must comply with all applicable legal and contractual restrictions in your jurisdiction.

This tool does not grant permission to access, extract, analyze, modify, reproduce, redistribute, publish, update, or otherwise use any game data. You are solely responsible for confirming that your use of the tool and the data is authorized and lawful. To the maximum extent permitted by applicable law, the authors and contributors assume no legal responsibility or liability for your use of this tool, any breach of law or contract, or any resulting claim, loss, damage, or dispute. This notice is not legal advice.

## Features

### Rho, Rho5, and LPK reading and browsing

- Recursively scan a directory for `.rho` archives, `.rho5` data-pack sets, and `.lpk` packages.
- Select `.rho` files directly; opening any `.rho5` part loads its complete same-prefix data-pack set.
- Open each `.lpk` package independently; LPK decryption keys depend on the original package filename.
- Read Nexon KartRider LPK v4 resource tables and decrypt/extract entries with flags 6/7 and zero sections; verify decoded resource size and MD5 before use.
- Validate legacy Rho decoded block checksums and return only each resource's declared byte range; Rho5 verifies exact decompressed length and its per-entry MD5. Legacy Adler-32 values detect corruption and do not authenticate an archive's source.
- Treat repeated file paths in numbered Rho5 parts as ordered overlays, so a later part replaces an earlier duplicate instead of making the whole set unreadable.
- Drag a directory or supported archive files into the window.
- Decrypt legacy Rho5 resources using the vendored KartRider-File-Reader implementation; use the saved TW/KR/CN region setting or detect the region from a nearby `KartRiderTW.ini`, `KartRiderKR.ini`, or `KartRiderCN.ini`.
- Display archive name, resource path, type, size, and protection method.
- Show loaded archives and the internal folder tree on the left, with a breadcrumb and resource list on the right.
- Preserve the original archive order for the resource list and folder tree; manual sorting is intentionally not provided.
- Filter by file name, path, archive name, resource category, or extension. Category and extension filters can be combined.
- Render the resource list in virtual mode so only visible grid cells are requested instead of materializing a DataGridView row for every resource; the grid is double-buffered and suppresses redraw while its virtual row count changes.
- Support Select All, Invert Selection, and the `Ctrl+F`, `Ctrl+A`, `Ctrl+I`, `Ctrl+E`, and `Esc` shortcuts.

### Resource preview and analysis

- Preview PNG, JPG/JPEG, BMP, and GIF images.
- Debounce rapid selection changes, cancel obsolete queued previews, and serialize preview work to avoid repeatedly decoding entries during fast navigation. A recent-resource LRU cache retains at most 64 MiB across 128 entries; resources larger than the cache budget bypass it.
- Preview the top mip of common 2D DDS textures, including BC1/DXT1, BC2/DXT3, BC3/DXT5, BC4/ATI1, BC5/ATI2, common DX10 RGBA/BGRA formats, and legacy RGB/luminance masks.
- DDS previews are bounded to 128 MiB input and 128 MiB decoded pixels. Cubemaps, volume textures, texture arrays, BC6H, BC7, and other unsupported formats remain available through raw export/HEX preview.
- DDS format behavior was cross-checked against the public [spnda/dds_image](https://github.com/spnda/dds_image) and [paroj/nv_dds](https://github.com/paroj/nv_dds) references; the application uses its own decoder and does not bundle their code.
- Parse KartRider `.bml` UI resources as a bounded binary XML-like tree and show an escaped, generated XML preview; the source resource is binary, not literal XML text.
- Provide text and HEX fallback views for text and unknown binary resources.
- Attempt to parse KSV recording title, track, event type, recorder, player, and recording data sections.
- Classify KSV recordings separately from audio, and classify `.1s` resources as model/scene resources.
- Probe `.1s` files for public model-format marker candidates and a declared module count when the file header is present. This is a read-only format fingerprint, not a complete decoder or validity check.
- Identify Ogg/Vorbis and Opus streams and report channels, sample rate, estimated duration, and tags.
- Read FLAC STREAMINFO, channels, sample rate, bit depth, estimated duration, and Vorbis comments.
- Apply a 64 MiB per-resource analysis ceiling; OGG and FLAC readers validate page/block lengths and bound packet/comment parsing before presenting metadata.
- Bound single-resource materialization to 256 MiB to reduce memory-exhaustion risk. The current archive backends expose decoded resources as `byte[]`, so larger resources require a dedicated streaming tool rather than in-app preview/export.
- Clearly distinguish a valid empty archive from a load or parsing failure.

### LPK v4 support and limits

- LPK v4 reading is implemented as an independent, read-only decoder; it does not modify or repack source packages.
- The supported table layout covers flags 6 and 7 with zero sections. Other flags or sectioned records are reported as unsupported rather than guessed.
- Resource paths are checked against absolute paths and traversal before they are added to the resource list.
- Two local game packages, `DataPack0_00000.lpk` and `DataPack0_00001.lpk`, were tested: 1,874 and 937 entries respectively. All entry sizes and MD5 hashes passed, and decoded files matched output from the supplied reference utility byte-for-byte.
- The user-provided LPK reference utilities and game packages are not included in this repository or release.

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

The updater follows the public `brownsugar/kart-patcher` discovery flow: it reads `KartRider.pin`, connects to the selected region's patch socket, then uses the socket-provided endpoint (falling back to the pin's `officialPatchUrl` only when the socket response is empty). HTTP requests and redirects are constrained to that discovered endpoint's exact origin; no generic Nexon domain suffixes are assumed. The known TW/KR/CN socket host/port mappings are based on the same project's region presets. NFO200 input is capped at 16 MiB/100,000 records; each compressed/uncompressed file is capped at 16 GiB and a plan at 128 GiB. Downloaded compressed size and bounded decompressed size must match the manifest before apply. Existing reparse points in updater destination paths are rejected. These checks reduce symlink/junction escape risks but do not make updates atomic against another process concurrently changing the selected game directory.

### Runtime security baseline

- The application targets `net10.0-windows` and its framework-dependent runtime configuration requires .NET 10.0.14 or later servicing patches. This includes the .NET Brotli decoder fix for CVE-2026-111497; .NET 8 and 9 reach end of support on November 10, 2026, so this release moves to .NET 10 LTS.
- The updater does not enable `HttpClient` automatic response decompression and explicitly sets it to `None`; application code does not call Brotli decompression APIs. Patch payloads use bounded GZip decompression only. The runtime baseline still matters for future framework code paths and other dependencies.
- The vendored reader's legacy zlib paths now use the .NET `System.IO.Compression.ZLibStream`; the DotNetZip 1.16.0 package reference was removed. This eliminates the current dependency that triggered NU1903 for [CVE-2024-48510](https://nvd.nist.gov/vuln/detail/CVE-2024-48510), whose affected path is `ZipEntry.Extract`. The reader does not implement ZIP archive extraction. See `THIRD_PARTY_NOTICES.txt` for migration scope; real archive corpus validation is required whenever these decompression paths change.
- The NICS August 2026 Microsoft alert includes CVE-2026-62872 for **.NET Framework**, which is distinct from the modern .NET runtime used by this app. The app does not use .NET Framework; Windows and .NET Framework security updates remain the user's OS-maintenance responsibility. See the [NICS alert](https://www.nics.nat.gov.tw/core_business/information_security_information_sharing/Vulnerability_Alert_Announcements/8c0320ef-33cc-4376-955e-81f7750ad160/), [iThome report](https://www.ithome.com.tw/news/179970), and Microsoft's [.NET OOB update](https://devblogs.microsoft.com/dotnet/dotnet-october-2026-oob-security-updates/).

The first implementation intentionally uses full-file updates only. It does not invoke `Patcher.exe` or apply delta patches. The patch service must still be available for the selected region; a historical or discontinued service may be unreachable and is not replaced by this application.

The update workflow is informed by the public [`brownsugar/kart-patcher`](https://github.com/brownsugar/kart-patcher) project. That project provides client-side patcher research and implementation references; it does not redistribute copyrighted game update files.

### Interface and settings

- Use a fixed light theme for predictable native WinForms contrast.
- Offer HanS, HanT, KR, and EN interface packages. The language selection is saved in the existing configuration file and applied after restarting the app.
- Use PerMonitorV2 high-DPI support.
- Resize the left and right layout with the window while preserving the splitter ratio and usable minimum widths.
- Save the normal window size and splitter ratio when the application closes normally.
- Use Settings to specify the default Rho directory and Log directory.
- Select the interface language (`HanS`, `HanT`, `KR`, or `EN`) in Settings. The choice is saved in the existing configuration file and applied after restarting the app.
- Maintain each language as a separate XML package under `languages/`; the Settings window can open that folder. Keep the string `key` attributes unchanged when editing translations. Invalid or missing entries fall back to Traditional Chinese/default text.
- The game update screen remembers the last selected game directory in the local configuration file.

## Quick start

### Use the release package

1. Install Microsoft [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) 10.0.14 or later for Windows x64.
2. Download `RhoViewerPortable_v0.3.9_win-x64.zip` from the [v0.3.9 release](https://github.com/popuchoco/Rho-Viewer/releases/tag/v0.3.9) and extract it.
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
│   ├── DdsImageDecoder.cs
│   ├── LanguageManager.cs
│   ├── languages/
│   │   ├── zh-Hans.xml
│   │   ├── zh-Hant.xml
│   │   ├── ko-KR.xml
│   │   └── en-US.xml
│   ├── MainForm.cs
│   ├── GameUpdateForm.cs
│   ├── KartPatchService.cs
│   ├── Program.cs
│   ├── LpkArchive.cs
│   ├── RhoReaderCore.cs
│   ├── ResourceAnalyzers.cs
│   ├── ResourceClassifier.cs
│   ├── OneSAnalyzer.cs
│   ├── AppSettings.cs
│   ├── AppLogger.cs
│   ├── SettingsForm.cs
│   └── app.manifest
├── RhoViewerPortable.Tests/
│   ├── RhoViewerPortable.Tests.csproj
│   └── Program.cs
└── third_party/
    └── Kartrider-File-Reader/
```

## System requirements

### Running the release package

- Windows x64.
- Microsoft .NET 10 Desktop Runtime 10.0.14 or later for Windows x64.
- To play OGG or FLAC, a compatible player must already be installed or lawfully obtained and placed in the `Players` directory.

### Building the source package

- A Windows x64 development environment.
- .NET 10 SDK and Windows Desktop SDK.
- The first build requires NuGet package restore. After restore is complete, offline parser and updater self-tests do not require network access.

### Run independent tests

The separate test runner covers malformed OGG/FLAC inputs, update-path/manifest/rollback checks, CRC, and UI mutation scopes:

```powershell
dotnet run --project RhoViewerPortable.Tests/RhoViewerPortable.Tests.csproj --configuration Release
```

To decode and read **every entry** from real game archives, pass one or more data roots. The corpus test opens each `.rho` and `.lpk` archive independently, groups numbered `.rho5` parts, reads every resource, and checks the declared decoded size; LPK/Rho5 MD5 and Rho block checksums are also verified by the readers. It is read-only and does not write to the game directories.

```powershell
dotnet run --project RhoViewerPortable.Tests/RhoViewerPortable.Tests.csproj --configuration Release -- --archive-corpus "D:\Game\KartRider\Data" "D:\OldTW\M01\Data"
```

Real game data is not bundled with the repository or CI artifacts. CI runs the deterministic synthetic tests; the real-data corpus command is intended for a local machine that has the relevant installations.

To check only BML entries already extracted from a client archive, pass one or more `.bml` files or directories:

```powershell
dotnet run --project RhoViewerPortable.Tests/RhoViewerPortable.Tests.csproj --configuration Release -- --bml-corpus "D:\Game\KartRider\BmlSamples"
```

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
  "uiLanguage": "zh-Hant.xml",
  "logDirectory": "D:\\RhoViewerLogs",
  "windowWidth": 1120,
  "windowHeight": 720,
  "splitterRatio": 0.2
}
```

- `rhoDirectory`: the default starting directory for Rho browsing and Import Settings Directory.
- `gameDirectory`: the last directory selected by the game update screen. It is only a convenience default; the user must still review the path before checking or applying an update.
- `gameRegion`: `auto`, `tw`, `kr`, or `cn`. `auto` uses the host and port stored in `KartRider.pin`; a selected region uses the corresponding public mapping.
- `uiLanguage`: the selected language package filename (`zh-Hans.xml`, `zh-Hant.xml`, `ko-KR.xml`, or `en-US.xml`). It is persisted with the other settings, so saving folders, updater preferences, or window layout does not reset the language.
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

The GitHub Actions CI workflow builds the Windows x64 portable package, runs offline parser/updater self-tests, and attaches the publish directory to the workflow run as an artifact (retained for 14 days). Version tags publish the ZIP as a GitHub Release asset. Download and extract the complete ZIP; do not copy only `RhoViewerPortable.exe`, because it depends on the adjacent application files and the .NET 10 Desktop Runtime.

## Archive smoke tests

```powershell
RhoViewerPortable.exe --self-test stage_battleReady.rho character_tiera.rho DataPack1_00000.rho5
RhoViewerPortable.exe --smoke-load sound_bgm_desert.rho
```

- `--self-test`: opens specified `.rho`, `.rho5`, or `.lpk` archives and checks representative decoded image/audio resources; for LPK it reads every entry and checks the stored size and MD5.
- `--smoke-load`: creates the WinForms main window, loads the specified archive, and closes automatically to detect startup or loading crashes.

For independent malformed-input (including BML), updater-security, CRC, UI-state, language-persistence, DDS-decoder, and complete archive-corpus tests, use the separate `RhoViewerPortable.Tests` project documented above. This keeps test-only switches out of the released application. Run `RhoViewerPortable.Tests.dll --archive-corpus --sha256 <dataRoot> [otherDataRoot ...]` to print a deterministic aggregate SHA-256 fingerprint over normalized relative archive paths, entry paths, and decoded entry bytes. The fingerprint is useful for repeatability/regression comparison; it is not an authenticity check unless compared with a trusted baseline.

## Architecture overview

```text
MainForm (WinForms UI)
├── AppSettings / AppLogger
├── RhoReaderCore
│   └── local KartLibrary / RhoArchive / Rho5Archive / RhoFolder / RhoFile
├── DdsImageDecoder
│   └── bounded top-mip 2D DDS software preview
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

The `.1s` analyzer reports candidate occurrences of public file/model/vertex markers and a declared module count where the file header permits it. Marker definitions are referenced from the MIT-licensed `VT-Tuzki/kartrider_model_1s_to_obj` project; no parser or exporter code is copied. [`nixliuxin/KartRider-Tools`](https://github.com/nixliuxin/KartRider-Tools) is also acknowledged as a high-level reference for the broader `.1s` analysis and conversion scope, not as a code dependency. Rho Viewer does not copy its source, binaries, or implementation; it still does not decode meshes, materials, animations, or reliably determine whether a model is a track, character, or kart. See `THIRD_PARTY_NOTICES.txt` and `third_party/Kartrider-File-Reader/UPSTREAM.md` for dependency provenance and license details.

KartRider `.bml` resources are not ordinary XML documents. The current preview decodes the length-prefixed UTF-16LE node/text/attribute/child layout described by [`BinaryXmlTag.cs`](https://github.com/xpoi5010/Kartrider-File-Reader/blob/main/src/KartriderLibrary/Xml/BinaryXmlTag.cs) and [`BinaryReaderExt.cs`](https://github.com/xpoi5010/Kartrider-File-Reader/blob/main/src/KartriderLibrary/IO/BinaryReaderExt.cs), then renders an escaped XML-like view. This is an independent parser with byte, depth, node, attribute, and preview limits. It has been verified against seven `.bml` resources from the archived Taiwan client; other client/version variants may differ.

## Security and data boundaries

- Source Rho, Rho5, and LPK archives are opened for reading; the tool does not write back to or repack the source archive.
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
- LPK support is limited to the tested v4 table layout (flags 6/7, zero sections); LPK creation, repacking, and other variants are not supported.
- KSV fields and text encoding may vary by game version and require more fixtures for cross-version validation.
- The archive decoding backend currently materializes each resource as a byte array; resources over 256 MiB are rejected rather than streamed. Incremental archive streaming remains future work.
- Virtual mode reduces UI row-object and repaint costs for large resource lists, but filtering still scans loaded resource metadata; Select All and Invert Selection remain proportional to the number of visible rows.
- `.1s` support is limited to experimental marker fingerprinting; it does not export OBJ/USD or fully parse model geometry.
- Game update availability depends on the region's patch service and the selected installation's `KartRider.pin`. The updater currently supports full-file NFO200 updates only; delta patching is intentionally not enabled.
- Actual audio playback depends on the user's installed player and codecs.
- The true OEP, IAT repair, and inner code-level restoration of the original `黑黑de臉蛋 (RhoDump)` executable still require an isolated VM. This project does not claim to restore its original source code.

## License, sources, and acknowledgements

This source package is released under GPL-3.0. The complete license text is in [`LICENSE`](LICENSE). Third-party source scope, attribution, and redistribution notices are in [`THIRD_PARTY_NOTICES.txt`](THIRD_PARTY_NOTICES.txt).

Acknowledgements:

1. 黑黑de臉蛋's 臉蛋看看 (RhoDump): for the historical program context and research sample source.
2. [`xpoi5010/Kartrider-File-Reader`](https://github.com/xpoi5010/Kartrider-File-Reader): for public Rho/Rho5 format research, the [`Xml` BML implementation](https://github.com/xpoi5010/Kartrider-File-Reader/tree/main/src/KartriderLibrary/Xml), and [`Record` KSV structures](https://github.com/xpoi5010/Kartrider-File-Reader/tree/main/src/KartriderLibrary/Record). The application uses the vendored KartLibrary KSV reader and an independent bounded BML parser. Its GPL-3.0 license text is retained in `third_party/Kartrider-File-Reader/LICENSE`.
3. [`brownsugar/kart-patcher`](https://github.com/brownsugar/kart-patcher): for public KartRider patcher workflow, `KartRider.pin`/patch socket research, NFO200 manifest handling, and update safety reference. The project is published under MIT; this application does not redistribute its Electron application or copyrighted game files.
4. 百度韓服跑跑卡丁車貼吧 澜轶: for the Microsoft LpkToolkit v1.x adaptation framework supporting Nexon LPK v4.
5. 不到壹个字 (budaoyigezi): for the LPK tool implementation reference.
6. Royal: for the batch-tool adaptation reference.
7. [`nixliuxin/KartRider-Tools`](https://github.com/nixliuxin/KartRider-Tools): as a high-level reference for `.1s` resource analysis and conversion scope. No code or binaries from this project are included or copied; Rho Viewer’s `.1s` support remains experimental marker fingerprinting only.
8. [`VT-Tuzki/kartrider_model_1s_to_obj`](https://github.com/VT-Tuzki/kartrider_model_1s_to_obj): for its MIT-licensed public `.1s` marker/format research, referenced for the read-only marker probe. No parser or OBJ exporter code is copied or bundled; the project’s MIT notice is recorded in `THIRD_PARTY_NOTICES.txt`.
9. [`spnda/dds_image`](https://github.com/spnda/dds_image): for public DDS header/mipmap and format reference. The project is MIT-licensed; Rho Viewer uses an independent C# decoder and does not include its code.
10. [`paroj/nv_dds`](https://github.com/paroj/nv_dds): for public legacy DDS supported-format notes. No source code or binaries are copied; the upstream source identifies NVIDIA-provided code and links its General License.

The LPK attributions refer to user-provided reference materials. The reference utilities' source code, binaries, and game sample packages are not redistributed in this project.
The original RhoDump executable, archive, and user-provided game samples are not redistributed in this package.
