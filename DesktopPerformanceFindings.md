# Desktop Performance Findings

> Performance audit of the FCC Desktop Edge Agent.

---

## Application Shell Module

### P-DSK-001
- **Title:** Status bar timer creates a new DI scope and database connection every 5 seconds
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Inefficient database queries
- **Description:** `MainWindow._statusTimer` fires every 5 seconds and calls `RefreshStatusBarAsync()`, which creates a new `IServiceScope`, resolves a scoped `TransactionBufferManager` (which in turn creates a scoped `AgentDbContext`), executes a query, and disposes the scope. This opens and closes a SQLite connection every 5 seconds for the lifetime of the application. While SQLite connection pooling mitigates the cost, the scope creation, EF context initialization, and change tracker allocation are unnecessary overhead for a simple count query.
- **Evidence:**
  - `MainWindow.axaml.cs:48` — `_statusTimer = new Timer(_ => _ = RefreshStatusBarAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5))`
  - `MainWindow.axaml.cs:185-186` — `using var scope = _services.CreateScope(); var buffer = scope.ServiceProvider.GetService<TransactionBufferManager>()`
- **Impact:** ~17,280 unnecessary scope+context allocations per day. Each involves EF Core change tracker initialization. On low-powered edge devices, this contributes to GC pressure and brief CPU spikes.
- **Recommended Fix:** Either (a) expose a lightweight `GetPendingCount()` method that uses a raw SQL query without full EF tracking, (b) cache the count in a singleton service updated by the upload worker, or (c) increase the poll interval to 15-30 seconds.

---

### P-DSK-002
- **Title:** VelopackUpdateService.IsInstalled recreates UpdateManager on every property access
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Slow data loading
- **Description:** The `IsInstalled` property getter creates a new `SimpleWebSource` and `UpdateManager` on every call. `UpdateManager` construction involves filesystem checks to detect the Velopack install directory. If `IsInstalled` is checked frequently (e.g., on each update check), this allocates objects and does I/O unnecessarily since the installed state cannot change while the process is running.
- **Evidence:**
  - `VelopackUpdateService.cs:33-38` — `IsInstalled` getter creates `new SimpleWebSource(...)` and `new UpdateManager(source)` on every access
  - `VelopackUpdateService.cs:68` — `IsInstalled` also called inside `CheckForUpdatesAsync`, doubling the construction
- **Impact:** Minor — two redundant `UpdateManager` allocations per update check cycle. The filesystem probes are fast but unnecessary.
- **Recommended Fix:** Cache the `IsInstalled` result in a `Lazy<bool>` field, since install state cannot change during process lifetime.

---

### P-DSK-003
- **Title:** Synchronous file I/O for window state persistence runs on UI thread
- **Module:** Application Shell
- **Severity:** Low
- **Category:** Heavy file operations on UI thread
- **Description:** `WindowStateService.Load()` uses `File.ReadAllText()` (synchronous I/O) and is called from `MainWindow.RestoreWindowState()` during the constructor, which runs on the UI thread. `WindowStateService.Save()` uses `File.WriteAllText()` and runs from `OnClosing()`, also on the UI thread. While the file is small (~100 bytes), any disk latency (antivirus scan, spinning disk seek, network drive) will block the UI thread.
- **Evidence:**
  - `WindowStateService.cs:22` — `File.ReadAllText(StatePath)` — sync read
  - `WindowStateService.cs:37-38` — `Directory.CreateDirectory(dir)` + `File.WriteAllText(StatePath, json)` — sync write
  - `MainWindow.axaml.cs:55` — `RestoreWindowState()` called from constructor (UI thread)
  - `MainWindow.axaml.cs:86` — `SaveWindowState()` called from `OnClosing` (UI thread)
- **Impact:** Theoretical UI freeze of 10-50ms under adverse disk conditions. Practically negligible on SSDs but worth noting for deployments on older hardware.
- **Recommended Fix:** Use `File.ReadAllTextAsync()` / `File.WriteAllTextAsync()` with `await`, or accept the trade-off given the file size.

---

## Device Provisioning Module

### P-DSK-004
- **Title:** Connection tests run sequentially — 20-second worst case when parallel would halve the wait
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** UI thread blocking
- **Description:** `RunConnectionTestsAsync()` tests cloud connectivity first (up to 10-second timeout), waits for it to complete, then tests FCC connectivity (up to 10-second timeout). These two tests are completely independent — the FCC test has no dependency on the cloud test result. If both endpoints are unreachable, the user waits up to 20+ seconds with the UI frozen (buttons disabled, "Testing..." shown) before seeing the failure result. Running both tests concurrently via `Task.WhenAll` would halve the worst-case wait to ~10 seconds.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:472-494` — cloud test awaited first
  - `ProvisioningWindow.axaml.cs:497-535` — FCC test starts only after cloud test completes
  - `ProvisioningWindow.axaml.cs:459-460` — `NextButton.IsEnabled = false; BackButton.IsEnabled = false;` — UI disabled for entire duration
  - Both tests use independent 10-second timeouts
- **Impact:** Poor user experience during connection testing, especially at sites with network issues. 20-second unresponsive UI may appear hung to field technicians.
- **Recommended Fix:** Run both tests concurrently: `var cloudTask = TestCloudAsync(); var fccTask = TestFccAsync(); await Task.WhenAll(cloudTask, fccTask);` Update indicators as each task completes.

---

### P-DSK-005
- **Title:** RegistrationManager.LoadState performs synchronous file I/O on first call from PostConfigure
- **Module:** Device Provisioning
- **Severity:** Low
- **Category:** Slow data loading
- **Description:** `RegistrationManager.LoadState()` uses `File.ReadAllText(path)` (synchronous I/O) when the in-memory cache is empty. This method is called from `PostConfigure()` which runs during `IOptions<AgentConfiguration>` resolution — typically on the first service that requests `AgentConfiguration`. On first startup or after cache invalidation (line 176), this blocks the calling thread with synchronous disk I/O. In the provisioning flow, `PostConfigure` runs during service resolution in `Program.cs`, blocking the main thread.
- **Evidence:**
  - `RegistrationManager.cs:74` — `File.ReadAllText(path)` — synchronous read
  - `RegistrationManager.cs:176` — `lock (_lock) _cached = null;` — cache cleared in PostConfigure before LoadState
  - `RegistrationManager.cs:178` — `var state = LoadState();` — synchronous call in PostConfigure path
- **Impact:** Minor startup delay (typically <5ms on SSD). On slow storage (USB drives, network mounts at edge sites), could contribute to noticeable startup latency.
- **Recommended Fix:** Accept the trade-off (PostConfigure is inherently synchronous in .NET's Options infrastructure) or pre-warm the cache asynchronously during host build before Options are first resolved.
