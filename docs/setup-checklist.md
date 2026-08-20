# Setup Checklist

## Host Machine

- Reserve a stable LAN IP for the gaming PC in the router.
- Install or extract portable Dolphin into `emulators/dolphin/`.
- Create `emulators/dolphin/portable.txt`.
- Install or extract portable RetroArch into `emulators/retroarch/` if needed.
- Keep Windows Firewall enabled.
- Allow only the required emulator executables/ports.
- Avoid broad port ranges and DMZ mode.

## Dolphin

- Use the same Dolphin version across the group.
- Prefer traversal netplay for the first test.
- Use direct connect only if traversal is unreliable or if you want a stable hostname workflow.
- Keep shared compatibility settings separate from local graphics/controller preferences.

## RetroArch

- Use the same RetroArch version and core versions across the group.
- Record each game's expected core and SHA-256 hash.
- Prefer RetroArch's controller autoconfig where possible.

## Game Files

- Store local game files in `games/`.
- Do not synchronize copyrighted ROM/ISO files through this repository.
- Use `scripts/Get-GameHash.ps1` to calculate SHA-256 hashes.
- Copy `config/games.example.json` to `config/games.json` and fill in real hashes locally.

## Cloudflare DDNS Option

- Create a DNS-only `A` record such as `games.example.com`.
- Use a Cloudflare API token restricted to the relevant zone and DNS edit permissions.
- Run the DDNS updater from an always-on machine, such as a Raspberry Pi.
- Forward only the exact port needed for the emulator.

## Private VPN Option

- Use Tailscale or WireGuard for a small trusted friend group.
- Avoid public port forwarding when VPN connectivity works well.
- Have friends connect to the host's private VPN IP or MagicDNS name.
