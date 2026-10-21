# Making a release

A release is the control panel and the console, self-contained for 64-bit Windows (nobody has to install .NET),
with the chart data and the bridge. People get it two ways from the GitHub release:

- **`DigiTower-win-Setup.exe`**: installs for the Windows user, without admin rights, into
  `%LOCALAPPDATA%\DigiTower`, with a desktop and a Start menu shortcut. The installed control panel looks for new
  releases (Settings → *Look for updates*) and offers them in its sidebar; one click downloads, stops the ATC (held AI
  is started) and restarts with the new version. Uninstalling (Windows *Apps*) removes the program, the downloaded
  models, the logs and the recordings; the settings (`%APPDATA%\DigiTower\settings.json`) and the bridge in DCS stay.
- **`DigiTower-win-Portable.zip`**: the same, unpacked anywhere; it can update itself too.

The builds are not code-signed, so Windows SmartScreen asks once (*More info → Run anyway*).

## Steps

1. Pick the version (semantic: `0.1.0`, `0.2.0`, `0.2.1`; a test build `0.2.0-beta.1`).
2. On the merged `main`, with the tests passing:
   ```powershell
   dotnet tool restore                                            # vpk (Velopack), pinned in dotnet-tools.json
   # Only if out\release\velopack is gone: fetch the latest release so the update is a small delta.
   dotnet vpk download github --repoUrl https://github.com/AtrixZockt/DigiTower --outputDir out\release\velopack
   .\tools\Publish-Release.ps1 -Version 0.1.0 -Installer
   ```
   `out\release\0.1.0\DigiTower\` is the program folder (try it: `DigiTower.exe`), `out\release\velopack\` the
   files to upload.
3. Upload them as a GitHub release (a token with *Contents: read and write* for the repository; `gh auth token`
   gives one):
   ```powershell
   dotnet vpk upload github --repoUrl https://github.com/AtrixZockt/DigiTower --outputDir out\release\velopack `
       --tag v0.1.0 --releaseName "DigiTower 0.1.0" --token <token>         # a draft: check it, then publish on GitHub
   ```
   `--publish` publishes at once, `--pre` marks a test build (installed panels only offer full releases). If the
   release notes already wait in a draft for that tag (`gh release create v0.1.0 --draft`), add `--merge`: the
   files go into that draft.
4. Write what changed into the release text on GitHub (each release lists what is new and how to install).
   When the bridge changed, say so: installed users update it on the Install page and restart DCS.

Never in a release: `data\voice\voices.local.json`, `recognition.local.json`, models, caches, recordings (the
script copies only what the program needs).
