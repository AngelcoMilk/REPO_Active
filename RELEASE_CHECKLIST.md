# Release Checklist

Use this checklist before publishing a new mod package.

## Version
- `manifest.json` `version_number` is updated.
- `src/REPO_Active/Plugin.cs` `PluginVersion` matches `manifest.json`.
- README version notes (if any) are updated.

## Build
- Run: `dotnet build .\src\REPO_Active\REPO_Active.csproj -c Release`
- Build result is `0` errors.

## Package Content
- Package includes:
  - `BepInEx/plugins/REPO_Active/REPO_Active.dll`
  - `manifest.json`
  - `README.md`
  - `icon.png`
- `README.md` inside package must be UTF-8 **without BOM**.

## Behavior Smoke Test
- Singleplayer:
  - F3 manual activation works.
  - Auto mode works if enabled.
- Multiplayer host:
  - Discovery and activation chain works.
- Multiplayer non-host:
  - Behavior matches `EnforceHostAuthority` setting.
- Event chain:
  - Activation/state events keep queue in sync.

## Logs
- Log file is generated under:
  - `BepInEx/config/REPO_Active/logs`
- Snapshot logs exist for key transitions:
  - Queue rebuilt
  - Activation event
  - Completion state event
  - Reconcile path

## Output
- Final package created at repo root: `REPO_Active_r2modman.zip`
- Optional: test install with r2modman profile before upload.
