# Remote Updates

Game Night supports a manual update check from Settings > About.

The installed package reads `version.json` from the package root. If `updateFeedUrl` is set, the launcher downloads that JSON feed, compares `latestVersion` with the installed `version`, and shows the package download URL and release notes.

## Feed Format

Use `update.example.json` as the template:

```json
{
  "latestVersion": "0.3.9",
  "channel": "friend",
  "packageUrl": "https://github.com/YOUR-GITHUB-USER/YOUR-REPO/releases/download/v0.3.9/GameNight-Friend-0.3.9.zip",
  "sha256": "replace-with-package-sha256",
  "releaseNotes": [
    "Short note for players."
  ]
}
```

## GitHub Release Flow

1. Prepare the release locally:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\New-GameNightRelease.ps1 -PackageMode Friend -GitHubOwner YOUR-GITHUB-USER -GitHubRepository YOUR-REPO
```

2. Create a GitHub release tagged `vx.y.z`.
3. Upload `dist/releases/GameNight-Friend-x.y.z.zip` to the release.
4. Host `dist/releases/update.json` somewhere stable. A GitHub repo file with a raw URL is fine for the first pass.
5. Set `updateFeedUrl` in packaged `version.json` to the raw hosted feed URL.
6. Publish another package so testers receive the configured feed URL.

This first updater phase does not replace files automatically. Players use the shown download link and update manually, which keeps their local `games`, controller profile, NetPlay settings, and Dolphin user data out of the blast radius.
