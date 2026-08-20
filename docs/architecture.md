# Game Night Architecture

Game Night should avoid synchronizing an entire emulator configuration blindly. Different players have different controllers, GPUs, audio devices, displays, and performance needs.

Instead, split configuration into three layers.

## 1. Shared Compatibility Profile

Managed by Game Night and expected to match across players.

- Emulator name and version
- Core version, if using RetroArch
- Expected game file name
- Expected game SHA-256 hash
- Required game-specific compatibility settings
- Netplay mode and default port
- Known-good launch arguments

Example location:

```text
shared/compatibility/
config/games.json
```

## 2. Local Hardware Profile

Specific to each player's computer.

- Graphics backend
- Internal resolution
- Anti-aliasing
- Display/fullscreen preferences
- Audio device
- Performance preset

Example location:

```text
user/hardware/
```

These settings should not be forced to match across players unless a specific game requires it.

## 3. Local Controller Profile

Specific to each physical controller.

- Controller identity
- Button mapping
- Stick calibration
- Deadzones
- Rumble
- Hotkeys

Example location:

```text
user/controllers/
```

Dolphin and RetroArch already have controller profile systems, so Game Night should prefer generating or selecting native emulator profiles over inventing a new input layer.

## Networking Shape

Preferred order for Dolphin:

1. Dolphin traversal netplay
2. Private VPN such as Tailscale/WireGuard
3. Direct connect with DNS-only DDNS and a narrow router port forward

Cloudflare DDNS can keep a hostname such as `games.example.com` pointed at the current home IP, but it should be DNS-only for emulator traffic. Normal Cloudflare proxying is for supported HTTP/HTTPS-style traffic, not arbitrary Dolphin or RetroArch netplay.

## Launcher Responsibilities

The launcher can eventually own this flow:

1. Load `config/games.json`.
2. Detect emulator installations.
3. Verify emulator versions.
4. Verify local game file hashes.
5. Detect controller status.
6. Select local hardware/controller profiles.
7. Launch the selected emulator with the selected game.
8. Later: report ready status to a small lobby service.
