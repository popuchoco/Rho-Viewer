# Portable logic tests

This no-package runner exercises the platform-independent UI and L1–L3 helper policies without loading WinForms. It includes filename validation, playback-temp cleanup decisions, unique updater backup names, directory reparse checks, and M5 selection/debounce helpers.

Run it with:

```sh
dotnet run --project RhoViewerPortable.Logic.Tests/RhoViewerPortable.Logic.Tests.csproj --configuration Release
```

The directory lease blocks ordinary rename/reparse replacement on Windows while held. On other platforms the runner checks existing paths for reparse points, but cannot provide the Windows handle-sharing guarantee. This runner does not validate Windows shell associations or the full WinForms UI.
