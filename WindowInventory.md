# Window Inventory — FCC Desktop Edge Agent

> Structural inventory of all UI surfaces. Covers the Avalonia desktop agent (`FccDesktopAgent.App`).

---

## Desktop Agent — Avalonia Windows & Controls

| Window / Control | Type | Location |
|------------------|------|----------|
| `App` | Application Root | `src/desktop-edge-agent/src/FccDesktopAgent.App/App.axaml` |
| `MainWindow` | Window | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/MainWindow.axaml` |
| `SplashWindow` | Window | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/SplashWindow.axaml` |
| `ProvisioningWindow` | Window | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/ProvisioningWindow.axaml` |
| `DecommissionedWindow` | Window | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/DecommissionedWindow.axaml` |
| `DashboardPage` | UserControl (Page) | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/Pages/DashboardPage.axaml` |
| `ConfigurationPage` | UserControl (Page) | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/Pages/ConfigurationPage.axaml` |
| `TransactionsPage` | UserControl (Page) | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/Pages/TransactionsPage.axaml` |
| `PreAuthPage` | UserControl (Page) | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/Pages/PreAuthPage.axaml` |
| `LogsPage` | UserControl (Page) | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/Pages/LogsPage.axaml` |
| `SettingsPanel` | UserControl (Panel) | `src/desktop-edge-agent/src/FccDesktopAgent.App/Views/SettingsPanel.axaml` |
| `TrayIconManager` | Component (no AXAML) | `src/desktop-edge-agent/src/FccDesktopAgent.App/TrayIconManager.cs` |

---

## View Models

| ViewModel | Base Class | Location |
|-----------|------------|----------|
| `ViewModelBase` | `INotifyPropertyChanged` | `src/desktop-edge-agent/src/FccDesktopAgent.App/ViewModels/ViewModelBase.cs` |
| `MainWindowViewModel` | `ViewModelBase`, `IDisposable` | `src/desktop-edge-agent/src/FccDesktopAgent.App/ViewModels/MainWindowViewModel.cs` |
| `SettingsViewModel` | `ViewModelBase` | `src/desktop-edge-agent/src/FccDesktopAgent.App/ViewModels/SettingsViewModel.cs` |

---

## Startup Modes (Window Routing)

| Mode | Condition | Window Shown |
|------|-----------|-------------|
| `Normal` | Device registered, config valid | `SplashWindow` → `MainWindow` (with page navigation) |
| `Provisioning` | Device not registered | `ProvisioningWindow` |
| `Decommissioned` | Device decommissioned by cloud | `DecommissionedWindow` |

---

## Page Navigation (inside MainWindow)

| Page | Purpose |
|------|---------|
| `DashboardPage` | Agent status overview, connectivity, sync summary |
| `ConfigurationPage` | View/edit agent configuration, FCC settings |
| `TransactionsPage` | Browse buffered fuel transactions |
| `PreAuthPage` | View pre-authorization sessions |
| `LogsPage` | View application logs |
| `SettingsPanel` | Application-level settings |

---

## Legacy DOMS Implementation — WinForms UI

| Window / Control | Type | Location |
|------------------|------|----------|
| `AttendantMonitorWindow` | WinForms Form | `DOMSRealImplementation/DppMiddleWareService/AttendantMonitorWindow.cs` |
| `PopupService` | BackgroundService + NotifyIcon | `DOMSRealImplementation/DppMiddleWareService/PopupService.cs` |
| `NativePopup` | P/Invoke MessageBox | `DOMSRealImplementation/DppMiddleWareService/ForecourtTcpWorker/Worker.cs` |

---

## VirtualLab — Angular UI Components

| Component | Type | Location |
|-----------|------|----------|
| `AppComponent` | Root | `VirtualLab/ui/virtual-lab/src/app/app.component.ts` |
| `AppShellComponent` | Layout Shell | `VirtualLab/ui/virtual-lab/src/app/core/layout/app-shell.component.ts` |
| `DashboardComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/dashboard/dashboard.component.ts` |
| `SitesComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/sites/sites.component.ts` |
| `FccProfilesComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/fcc-profiles/fcc-profiles.component.ts` |
| `ForecourtDesignerComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/forecourt-designer/forecourt-designer.component.ts` |
| `LiveConsoleComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/live-console/live-console.component.ts` |
| `PreauthConsoleComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/preauth-console/preauth-console.component.ts` |
| `TransactionsComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/transactions/transactions.component.ts` |
| `LogsComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/logs/logs.component.ts` |
| `ScenariosComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/scenarios/scenarios.component.ts` |
| `SettingsComponent` | Feature Page | `VirtualLab/ui/virtual-lab/src/app/features/settings/settings.component.ts` |
