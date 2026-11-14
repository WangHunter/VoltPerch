# Changelog

Notable changes to VoltPerch are recorded here. Hardware support remains subject to the compatibility limits in [README.md](README.md).

## 0.2.0 - 2026-11-14

### Added

- VoltPerch branding and a portable Windows x64 distribution layout.
- English as the default interface language, with an optional Simplified Chinese interface.
- Device connection status, battery details and a compact tray panel with connected/all-device views.
- Per-device last-known battery caching and explicit not-live labels after disconnects or failed reads.
- Battery queries for VGN V98pro V2 and SteelSeries Rival 650, with connection-mode-specific verification limits.
- Multiple Xbox controller battery reports through Windows.Gaming.Input, labeled as estimates, and coarse XInput fallback levels.
- Classic Bluetooth battery-property association across HFP service nodes, including a validated JBL TUNE760NC path.
- Device removal from details and right-click menus, plus restoration from Settings.
- Tray display preferences, low-battery notification suppression and optional launch at sign-in.
- Protocol, state, wake-refresh and localization checks; English contributor and issue-reporting documentation.

### Changed

- Applied proportional main-window scaling and compact tray layouts.
- Replaced main-window toolbar symbols with consistent vector icons.
- Replaced tray numbers with a transparent battery-only icon that follows the taskbar theme.
- Simplified the Settings toolbar and made disclosure headers follow theme changes.
- Kept unknown readings, cached values, system estimates and coarse battery levels visually distinct.

### Release status

This is the first public release. Hardware support remains limited to the connection modes and verification scope described in the compatibility table.
