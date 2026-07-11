# Codex Usage Tray

A local Windows notification-area companion for Codex usage snapshots.

It reads recent `%USERPROFILE%\.codex\sessions\**\*.jsonl` events that contain `payload.rate_limits`. It does not read `auth.json`, call private APIs, or upload data.

## Features

- Dual large-number tray mode: one tray icon for 5-hour remaining usage and one tray icon for 7-day remaining usage.
- More icon styles: ring, single numeric icon, dual numeric icons, battery bar, and custom PNG assets.
- Low-usage notifications when either usage window drops below the configured threshold.
- Enhanced right-click menu: startup toggle, config file, diagnostics log, assets folder, sessions folder, refresh, reload config, and reset settings.
- Compact tooltip modes for clearer hover text within Windows tray limits.
- Configurable thresholds, colors, font, tooltip mode, notification threshold, and custom asset directory.

## Build and install

```powershell
.\scripts\build.ps1 -SelfContained
.\scripts\install.ps1 -Startup
```

The app installs to:

```text
%LOCALAPPDATA%\CodexUsageTray
```

It uses the startup entry:

```text
CodexUsageTray
```

## Configuration

Right-click the tray icon and choose `打开配置文件`, or edit:

```text
%LOCALAPPDATA%\CodexUsageTray\settings.json
```

Useful settings:

```json
{
  "IconMode": 2,
  "NumericContent": 2,
  "TooltipMode": 1,
  "EnableLowUsageNotifications": true,
  "LowThreshold": 10,
  "MediumThreshold": 30,
  "NotificationThreshold": 20,
  "FontName": "Arial",
  "LowColor": "#AA0000",
  "MediumColor": "#B45C00",
  "GoodColor": "#00692D",
  "UnknownColor": "#696969"
}
```

Enum values:

- `IconMode`: `0` ring, `1` single numbers, `2` dual large numbers, `3` battery, `4` custom assets.
- `NumericContent`: `0` 5-hour, `1` 7-day, `2` both.
- `TooltipMode`: `0` short, `1` detailed.

## Custom assets

Right-click and choose `打开自定义图标目录`. You can place PNG files named `0.png` through `100.png` in:

```text
%LOCALAPPDATA%\CodexUsageTray\assets
%LOCALAPPDATA%\CodexUsageTray\assets\5h
%LOCALAPPDATA%\CodexUsageTray\assets\7d
%LOCALAPPDATA%\CodexUsageTray\assets\both
```

The app falls back to generated icons when a matching PNG is not found.
