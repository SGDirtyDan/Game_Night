# Launcher

The first Game Night launcher is a small WPF desktop app.

## Run

```powershell
dotnet run --project src\GameNight.Launcher\GameNight.Launcher.csproj
```

## Current Behavior

- Reads curated entries from `config/games.json`.
- Auto-detects supported games dropped into the `games/` folder.
- Automatically refreshes when `config`, `games`, or `artwork` files change.
- Shows a readiness summary for the selected game.
- Provides a global Settings view with dedicated NetPlay, Graphics, and Controller tabs.
- Shows installed package version information in Settings > About.
- Checks the Dolphin executable.
- Checks Dolphin portable mode.
- Verifies the selected game file's SHA-256 hash when the game has a pinned manifest hash.
- Allows auto-detected games to launch without a pinned hash, and labels them as detected rather than verified.
- Checks the compatibility profile file.
- Locates and imports a user-supplied game file after verifying its SHA-256 hash.
- Shows Dolphin GameCube Port 1 controller status.
- Checks whether Windows currently reports a matching connected controller.
- Shows whether the core GameCube mappings are present.
- Shows Dolphin NetPlay mode, nickname, host port, and UPnP status.
- Opens Dolphin from the launcher for manual NetPlay hosting/joining.
- Provides a Netplay Lobby button that starts Dolphin, attempts to open Dolphin's NetPlay menu automatically, and closes that Dolphin instance after the NetPlay window closes.
- Launches Dolphin in batch mode with the selected game, hiding the main Dolphin UI during play.

## Readiness Summary

The summary at the top of the selected game panel rolls detailed checks into one state:

- `Ready to Play`: setup, controller, and netplay checks are OK.
- `Needs Attention`: the game can launch, but controller or netplay checks need review.
- `Cannot Play Yet`: setup checks failed, such as a missing game file or hash mismatch.

The Mario Party library no longer has a per-game preferred NetPlay mode. Use the global Netplay settings to switch between Direct and Traversal while troubleshooting each setup.

The installed Dolphin build does not expose a command-line flag for opening or hosting NetPlay directly. Game Night's Netplay Lobby button uses Windows UI Automation to open Dolphin's NetPlay menu item after Dolphin starts. If a NetPlay window is found, Game Night monitors the NetPlay window chain for that Dolphin process so the setup dialog can hand off to the lobby without closing Dolphin. Once no NetPlay window reappears for a short grace period, Game Night closes that helper Dolphin instance. If Dolphin changes its menu or window text, the launcher falls back with a message and Dolphin remains open.

## Global Settings

The Settings view applies player-level configuration across the whole library. Game Night can currently write these Dolphin settings directly:

- NetPlay nickname
- NetPlay mode
- NetPlay host/listen/connect port
- UPnP off
- Graphics backend, adapter, aspect ratio, V-Sync, and fullscreen launch
- Internal resolution, anti-aliasing, texture filtering, output resampling, color correction, and post-processing effect
- GameCube Port 1 mapping for known controller profiles
- DirectInput capture for GameCube buttons, stick axes, trigger axes/sliders, and D-pad hats
- A visual GameCube Port 1 mapping panel with capture buttons positioned around a controller image
- Dolphin-native binding strings displayed on each mapping button

The first built-in controller profile targets PlayStation-style `Wireless Controller` devices. More controller templates can be added as friends test their hardware.

The Controller tab saves Dolphin binding strings directly. Capture covers A, B, X, Y, Z, Start, digital L/R, analog L/R, L-Stick, C-Stick, and D-pad fields. Click the displayed mapping value, then press or move the matching controller input.

Like Dolphin, Game Night separates `L`/`R` trigger press mappings from `L Analog`/`R Analog` trigger axis mappings. Players without analog triggers can map only the digital trigger press fields; players with analog triggers can map both.

Game Night does not embed Dolphin's exact Qt settings UI. Instead, it recreates the core setup workflow in WPF and writes the same portable Dolphin configuration files.

## Updates

Each package includes `version.json` at the package root. The launcher reads this file and shows the installed version, channel, package date, update feed URL, and release notes in Settings > About.

Remote update checks are not enabled yet. The current manifest is intentionally simple so a future updater can compare the installed `version.json` against a hosted version feed, download a newer package, and preserve local player-owned folders such as `games/`.

## Artwork

Optional local artwork can be dropped into the package without changing `config/games.json`:

- `artwork/banners/<game-id>.png`
- `artwork/covers/<game-id>.png`

The launcher also accepts `.jpg`, `.jpeg`, and `.bmp`. Artwork changes are picked up automatically while the app is running.

Game Night also recognizes Dolphin-style custom artwork placed next to a game file:

- Banner: `icon.png` or `<game filename>.png`
- Cover: `cover.png` or `<game filename>.cover.png`

## Game Discovery

Game Night treats `config/games.json` as curated metadata, not the whole library. Any supported file found under `games/` is added to the launcher automatically unless it already has a matching manifest entry.

Current auto-detected Dolphin extensions:

- `.rvz`
- `.iso`
- `.gcm`
- `.wbfs`
- `.ciso`
- `.gcz`
- `.nkit.iso`

Curated manifest entries are still useful for friendly names, pinned hashes, and compatibility profiles. Newly detected files use a cleaned-up version of the filename for the display name and can be played immediately.

## Curated Games

- Mario Party 4
- Mario Party 5
- Mario Party 6
- Mario Party 7

## Next UI Milestones

- Add a controller profile export/import workflow.
- Replace the Netplay Lobby automation with a true host/join workflow if Dolphin exposes a supported command-line or API path.
- Add friendly missing-file instructions.
