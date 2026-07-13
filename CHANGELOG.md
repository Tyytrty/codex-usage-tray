# Changelog

## 2026-07-13

### Fixed

- Added support for newer Codex `rate_limits` events where `primary` is the only available window and represents the weekly allowance.
- Stopped reporting `no local data` when a fresh weekly-only snapshot is present.
- Kept 5-hour usage explicitly unavailable when Codex does not record it, instead of showing an expired or duplicated value.
- In dual-number mode, automatically show a single weekly icon when only weekly data is available.
- Updated tooltips, menu text, freshness checks, diagnostics, and low-usage notifications for both old and new snapshot formats.

### Validation

- Built the self-contained Windows executable successfully.
- Installed and restarted the tray application.
- Verified a fresh primary-only snapshot was read as 97% weekly remaining with 5-hour usage unavailable.
