---
name: codex-usage-tray
description: Build, install, run, or troubleshoot the Windows Codex Usage Tray companion that shows locally recorded Codex rate-limit usage in the notification area.
---

# Codex Usage Tray

Use this plugin only for the Windows tray companion contained at the plugin root. The application reads local Codex session JSONL files and does not use credentials or a network API.

## Commands

From the plugin root, build a portable executable with:

```powershell
.\scripts\build.ps1 -SelfContained
```

Install, start, and register it for the current user's Windows sign-in with:

```powershell
.\scripts\install.ps1 -Startup
```

For a manual refresh, tell the user to double-click either tray icon. The app defaults to dual large-number tray icons: one icon for 5-hour remaining usage and one icon for 7-day remaining usage. If no data appears, verify that `%USERPROFILE%\.codex\sessions` contains a recent JSONL event with `payload.rate_limits` after a Codex response. Do not inspect or disclose `%USERPROFILE%\.codex\auth.json`.
