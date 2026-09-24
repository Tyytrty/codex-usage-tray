# Codex Usage Tray

A local Windows notification-area companion for Codex usage snapshots and task activity.

It reads local `%USERPROFILE%\.codex\sessions` logs. It does not read `auth.json`, call private APIs, or upload data.

## Features

- Two side-by-side tray icons show the 5-hour and 7-day remaining usage.
- A short status dash above the 5-hour number blinks orange while Codex tasks are active (1 task: 2-second cycle; 2: 1-second; 3 or more: 0.5-second), stays green when connected and idle, and turns gray when usage data is unavailable. It leaves room for the original large, aligned usage numbers.
- Activity comes from local `task_started` / `task_complete` events, independently of rate-limit snapshot updates.
- Supports newer primary-only Codex snapshots: the single value is treated as the weekly limit, while 5-hour usage is shown as unavailable instead of reusing stale data.
- More icon styles: ring, single numeric icon, stacked numbers in one icon, battery bar, and custom PNG assets.
- Low-usage notifications when either usage window drops below the configured threshold.
- Enhanced right-click menu: startup toggle, config file, diagnostics log, assets folder, sessions folder, refresh, reload config, and reset settings.
- Compact tooltip modes for clearer hover text within Windows tray limits.
- Configurable thresholds, colors, font, tooltip mode, notification threshold, and custom asset directory.

## Codex data format compatibility

Codex Usage Tray reads local `payload.rate_limits` events. Older Codex builds usually recorded both `primary` (5-hour) and `secondary` (weekly) windows. Newer builds may record only `primary`, with a reset time that matches the weekly limit shown in the official Usage page.

When only one window is available, the tray treats it as the weekly limit, displays one weekly percentage, and marks 5-hour usage as unavailable. This prevents an expired 5-hour snapshot from being shown as current data. The official Usage page remains the source of truth because this companion does not use account credentials or private APIs.

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

- `IconMode`: `0` ring, `1` single numbers, `2` separate 5-hour and 7-day numbers, `3` battery, `4` custom assets.
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
