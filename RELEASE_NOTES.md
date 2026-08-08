# UWPHook 2.14.3.0

## Highlights

- Sync installed Xbox games with Steam and remove stale UWPHook shortcuts.
- Support Steam's modern cloud-backed collections, including multiple comma-separated collection names.
- Show whether each installed app is already present in Steam and update existing shortcuts instead of duplicating them.
- Improve SteamGridDB matching for game names containing spaces or special characters.

## Packaging and maintenance

- Add a self-contained Windows x64 installer and preserve the existing install directory during upgrades.
- Restore VDF parsing from NuGet and update the embedded PowerShell runtime and security-sensitive dependencies to current .NET 8 servicing releases.
- Refresh fork links, attribution, and release metadata.

Steam is closed briefly while shortcut and collection files are updated, then restarted automatically.
