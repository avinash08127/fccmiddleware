# Navigation Graph — FCC Edge Agent (Android)

**Project**: `fcc-edge-agent`
**Last scanned**: 2026-03-13

---

## Navigation Framework

**None** — no Jetpack Navigation, no NavGraph XML, no Navigation Compose.
All routing is manual `Intent`-based with `FLAG_ACTIVITY_NEW_TASK` / `FLAG_ACTIVITY_CLEAR_TASK` flags.

---

## Activity Navigation Map

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          APP LAUNCH                                     │
│                              │                                          │
│                              ▼                                          │
│                      ┌──────────────┐                                   │
│                      │SplashActivity│  LAUNCHER (entry point)           │
│                      │  (branded)   │  Theme: Theme.FccEdgeAgent.Splash │
│                      └──────┬───────┘                                   │
│                             │ delayed transition                        │
│                             ▼                                           │
│                    ┌─────────────────┐                                  │
│                    │LauncherActivity │  Decision router                  │
│                    │  (invisible)    │  Theme: Theme.FccEdgeAgent.Launcher│
│                    └────┬───┬───┬───┘                                   │
│                         │   │   │                                       │
│          ┌──────────────┘   │   └──────────────┐                        │
│          │                  │                  │                         │
│          ▼                  ▼                  ▼                         │
│  ┌────────────────┐ ┌──────────────┐ ┌─────────────────────┐           │
│  │ Provisioning   │ │ Diagnostics  │ │  Decommissioned     │           │
│  │   Activity     │ │  Activity    │ │    Activity          │           │
│  │ (QR scan +     │ │ (dashboard + │ │ (terminal dead-end)  │           │
│  │  registration) │ │  service     │ │                      │           │
│  └───────┬────────┘ │  start)      │ └──────────────────────┘           │
│          │          └──────┬───────┘                                     │
│          │                 │                                             │
│          │                 ▼                                             │
│          │          ┌──────────────┐                                     │
│          │          │  Settings    │                                     │
│          │          │  Activity    │                                     │
│          │          │ (FCC host/   │                                     │
│          │          │  port override)│                                   │
│          │          └──────────────┘                                     │
│          │                                                              │
│          │  On successful registration                                  │
│          └──────────► DiagnosticsActivity                               │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Routing Logic

### SplashActivity → LauncherActivity

| Trigger | Destination | Flags |
|---------|-------------|-------|
| Splash timer completes | `LauncherActivity` | `NEW_TASK + CLEAR_TASK` |

### LauncherActivity Decision Tree

```
LauncherActivity.onCreate()
    │
    ├── isDecommissioned? ──────────► DecommissionedActivity
    │                                   flags: NEW_TASK + CLEAR_TASK
    │
    ├── isReprovisioningRequired? ──► ProvisioningActivity
    │                                   flags: NEW_TASK + CLEAR_TASK
    │
    ├── isRegistered? ──────────────► DiagnosticsActivity
    │                                   flags: NEW_TASK + CLEAR_TASK
    │                                   + starts EdgeAgentForegroundService
    │
    └── else (first launch) ────────► ProvisioningActivity
                                        flags: NEW_TASK + CLEAR_TASK
```

### ProvisioningActivity

| Trigger | Destination | Flags |
|---------|-------------|-------|
| Successful QR scan + registration | `DiagnosticsActivity` | `NEW_TASK + CLEAR_TASK` |

### DiagnosticsActivity

| Trigger | Destination | Flags |
|---------|-------------|-------|
| Settings menu action | `SettingsActivity` | Standard |

### SettingsActivity

| Trigger | Destination | Notes |
|---------|-------------|-------|
| Save / Back | Returns to `DiagnosticsActivity` | `finish()` or back press |
| Apply override | Triggers `CadenceController.onFccReconnectRequested` | FCC adapter rebuild |

---

## Service-Initiated Navigation

The `EdgeAgentForegroundService` can trigger navigation from the background:

| Trigger | Destination | Flags |
|---------|-------------|-------|
| Refresh token expired (`isReprovisioningRequired`) | `ProvisioningActivity` | `NEW_TASK + CLEAR_TASK` |
| Device decommissioned (403 response) | `DecommissionedActivity` | `NEW_TASK + CLEAR_TASK` |

Both monitors run on 10-second polling intervals within the service coroutine scope.

---

## Back Stack Behavior

All primary transitions use `FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_CLEAR_TASK`, which means:

- The entire task/back-stack is cleared on every major transition
- Users cannot press Back to return to previous screens
- This is intentional for a kiosk-style headless agent
- Only `SettingsActivity` uses standard back-stack behavior (launched from `DiagnosticsActivity` without clear flags)

---

## Deep Links / External Entry Points

| Entry Point | Source | Destination |
|-------------|--------|-------------|
| App icon tap | Android launcher | `SplashActivity` → `LauncherActivity` → routing |
| `BOOT_COMPLETED` broadcast | Android OS | `BootReceiver` → starts `EdgeAgentForegroundService` (no UI) |
| Foreground service notification | Notification tray | None (no pending intent configured — tap does nothing) |

---

## Screen Lifecycle Summary

| Screen | Can reach from | Terminal? |
|--------|---------------|-----------|
| SplashActivity | App launch only | No — always transitions to Launcher |
| LauncherActivity | SplashActivity only | No — always routes elsewhere |
| ProvisioningActivity | LauncherActivity, EdgeAgentForegroundService | No — transitions to Diagnostics on success |
| DiagnosticsActivity | LauncherActivity, ProvisioningActivity | No — operational home screen |
| SettingsActivity | DiagnosticsActivity | No — returns to Diagnostics |
| DecommissionedActivity | LauncherActivity, EdgeAgentForegroundService | **Yes** — dead-end, no exit |
