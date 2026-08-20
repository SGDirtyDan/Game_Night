# Game Night

Game Night is a Windows-friendly launcher/setup project for making emulator netplay less fragile for a small friend group.

The core idea:

- Use portable emulator builds so everyone runs known versions of Dolphin and RetroArch.
- Keep shared compatibility settings separate from each player's local hardware and controller settings.
- Verify each user's locally supplied game files by hash before launch.
- Support Dolphin/RetroArch netplay through traversal, direct connect, a private VPN, or DNS-only DDNS where appropriate.

## Project Layout

```text
Game-Night/
├── config/
│   └── games.example.json
├── docs/
│   ├── architecture.md
│   └── setup-checklist.md
├── emulators/
│   ├── dolphin/
│   └── retroarch/
├── games/
├── scripts/
│   └── Get-GameHash.ps1
├── shared/
│   ├── compatibility/
│   └── profiles/
└── user/
    ├── controllers/
    └── hardware/
```

## Important Boundary

This project can support bundled public-domain/homebrew games and user-supplied commercial game dumps, but it should not distribute copyrighted ROMs or ISOs to friends.

For commercial titles, each player should provide their own lawful copy locally under `games/`. Game Night can verify the expected hash and warn when a file does not match the group profile.

## First Milestone

The first useful milestone is intentionally small:

1. Put a portable Dolphin build in `emulators/dolphin/`.
2. Add `portable.txt` beside `Dolphin.exe`.
3. Add one local game file under `games/`.
4. Record its SHA-256 hash in `config/games.json`.
5. Launch Dolphin manually using the verified local file.

After that, the launcher can automate validation and launch.

## Launcher

The first native launcher lives at:

```text
src/GameNight.Launcher/
```

Run it with:

```powershell
dotnet run --project src\GameNight.Launcher\GameNight.Launcher.csproj
```

The launcher reads `config/games.json`, verifies Dolphin portable mode, verifies the local game hash, and launches the selected game.

If a game is missing, use `Locate Game` in the launcher to select your local file. Game Night verifies the SHA-256 hash before copying it into the expected `games/` path.

Game Night has a global Settings view with dedicated NetPlay and Controller tabs for Dolphin nickname/mode/port, built-in controller profiles, DirectInput button capture, and manual GameCube Port 1 mapping fields.

## Current Library

- Mario Party 4
- Mario Party 5
- Mario Party 6
- Mario Party 7
