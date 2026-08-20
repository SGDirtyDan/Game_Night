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
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\New-GameNightRelease.ps1 -PackageMode Friend
```

2. Install and authenticate GitHub CLI:

```powershell
winget install GitHub.cli
gh auth login
```

3. Publish the release:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Publish-GitHubRelease.ps1 -PackageMode Friend
```

4. Commit and push the updated feed:

```powershell
git add version.json update.json docs scripts update.example.json
git commit -m "Prepare Game Night release feed"
git push
```

The current feed URL is:

```text
https://raw.githubusercontent.com/SGDirtyDan/Game_Night/main/update.json
```

This first updater phase does not replace files automatically. Players use the shown download link and update manually, which keeps their local `games`, controller profile, NetPlay settings, and Dolphin user data out of the blast radius.
