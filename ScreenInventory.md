# Screen Inventory — FCC Edge Agent (Android)

**Project**: `fcc-edge-agent`
**Last scanned**: 2026-03-13

---

## Activities

| Screen | Type | Location | Purpose |
|--------|------|----------|---------|
| SplashActivity | Activity | `ui/SplashActivity.kt` | App entry point — Puma Energy splash branding, routes to LauncherActivity |
| LauncherActivity | Activity | `ui/LauncherActivity.kt` | Decision router — checks registration/decommission state, routes to Provisioning, Diagnostics, or Decommissioned |
| ProvisioningActivity | Activity | `ui/ProvisioningActivity.kt` | QR code scanning for device provisioning — first-launch bootstrap flow, calls register API |
| DiagnosticsActivity | Activity | `ui/DiagnosticsActivity.kt` | Post-registration operational dashboard — starts foreground service, shows diagnostics |
| SettingsActivity | Activity | `ui/SettingsActivity.kt` | Local FCC connection override management (host/port overrides) |
| DecommissionedActivity | Activity | `ui/DecommissionedActivity.kt` | Terminal dead-end screen — shown after 403 DEVICE_DECOMMISSIONED |

## Fragments

None. The project does not use Fragments.

## Jetpack Compose Screens

None. The project uses traditional Android Views exclusively.

## Navigation Destinations

All navigation is Intent-based (no Jetpack Navigation component). See `NavigationGraph.md` for the full routing map.

---

## Summary

| Metric | Count |
|--------|-------|
| Activities | 6 |
| Fragments | 0 |
| Compose screens | 0 |
| Services (UI-adjacent) | 1 (EdgeAgentForegroundService — notification-only) |
| Total screens | 6 |

This is a **headless agent** application. The UI is minimal — provisioning, diagnostics, settings, and terminal states only. The core value is in the background foreground service, not in the UI layer.
