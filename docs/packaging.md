# Packaging

Build a double-clickable Windows package with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Publish-GameNight.ps1
```

The default package mode is `Host`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Publish-GameNight.ps1 -PackageMode Host
```

Build a clean friend-test package with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Publish-GameNight.ps1 -PackageMode Friend
```

The package is written to:

```text
dist/GameNight/
```

Friend packages are written to:

```text
dist/GameNight-Friend/
```

Run the packaged launcher:

```text
dist/GameNight/app/GameNight.exe
```

## Package Contents

- `app/GameNight.exe`
- `config/`
- `shared/`
- `docs/`
- `emulators/dolphin/Dolphin-x64/`
- `games/README.txt`
- `README-FIRST.txt`

Local game files are not copied into `dist/GameNight/games/` by the publish script.

Players can either place matching game files in `games/` manually or use `Locate Game` in the launcher. The launcher verifies SHA-256 before copying a selected file into the expected path.

## Friend Mode

Friend mode copies the portable Dolphin build, then removes local personal setup:

- GameCube controller mappings
- Wii Remote mappings
- keyboard controller mappings
- NetPlay nickname
- previous host code
- Dolphin cache/log/screenshot/state folders

It keeps the emulator, manifest, compatibility profiles, and direct-connect NetPlay defaults.
