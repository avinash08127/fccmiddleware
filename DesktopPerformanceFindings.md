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

---

## Authentication & Security Module

### P-DSK-006
- **Title:** PlatformCredentialStore serializes all credential operations behind a single SemaphoreSlim — token reads block on writes
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Slow data loading
- **Description:** On Windows and the Linux AES fallback path, all credential store operations (get, set, delete) acquire `_fileLock` — a single `SemaphoreSlim(1, 1)`. This means concurrent token reads from different workers (CloudUploadWorker, ConfigPollWorker, StatusPollWorker, TelemetryReporter) block each other and also block behind any in-progress write. During token refresh, `CommitTokenBundleAsync` performs 4-6 sequential `SetSecretAsync` / `DeleteSecretAsync` calls (staging write, active write, legacy cleanup, marker cleanup), holding the lock for the entire chain. Any worker trying to read the device token during refresh is blocked for the duration of all these file I/O operations.
- **Evidence:**
  - `PlatformCredentialStore.cs:26` — `private readonly SemaphoreSlim _fileLock = new(1, 1)` — single global lock
  - `PlatformCredentialStore.cs:85,101,126` — Get, Set, Delete all acquire `_fileLock` on Windows
  - `DeviceTokenProvider.cs:244-253` — `CommitTokenBundleAsync` calls 2+ `SetSecretAsync` + 2+ `DeleteSecretAsync` sequentially
- **Impact:** Token reads are delayed by 50-200ms (4-6 file I/O operations × 10-30ms each) when a refresh is in progress. Multiple workers waiting on the semaphore create a convoy effect on each refresh cycle.
- **Recommended Fix:** Cache the active token bundle in memory (already partially done via the `StoredTokenBundle` pattern). Only acquire the file lock for writes. Read from the in-memory cache for token reads, falling back to file only when the cache is empty (cold start).

---

### P-DSK-007
- **Title:** DeviceTokenProvider.GetTokenAsync always attempts staged bundle recovery on every call
- **Module:** Authentication & Security
- **Severity:** Low
- **Category:** Slow data loading
- **Description:** `GetTokenAsync()` calls `TryRecoverStagedTokenBundleAsync()` first, before attempting to load the active bundle. The recovery path reads the staging key from the credential store on every single token access — even though staging keys only exist during the brief window of an interrupted token refresh (a rare crash scenario). In normal operation, this is a wasted credential store read (and semaphore acquisition on Windows) for every API call that needs a token.
- **Evidence:**
  - `DeviceTokenProvider.cs:52-53` — `var recovered = await TryRecoverStagedTokenBundleAsync(ct)` — called on every `GetTokenAsync`
  - `DeviceTokenProvider.cs:210-229` — `TryRecoverStagedTokenBundleAsync` reads from credential store
  - Normal operation: staging key does not exist, returning null after a full credential store read
- **Impact:** One unnecessary credential store read per token access. On Windows, this includes a file read + DPAPI decrypt + semaphore wait.
- **Recommended Fix:** Add an in-memory flag (`_stagingRecoveryAttempted`) that is set after the first recovery check. Skip the staging check on subsequent calls. The flag is reset when a new refresh starts (before `MarkRefreshPendingAsync`).

---

### P-DSK-008
- **Title:** PBKDF2 with 100,000 iterations runs synchronously on first credential access — blocks startup
- **Module:** Authentication & Security
- **Severity:** Low
- **Category:** Heavy file operations on UI thread
- **Description:** On Linux without `secret-tool`, `DeriveLinuxMachineKey()` runs PBKDF2 with 100,000 iterations of SHA-256 to derive the AES encryption key. This is CPU-intensive (~50-200ms depending on hardware) and runs synchronously within the credential store's async methods. The first credential access (typically loading the device token during startup) triggers this derivation. On low-powered edge devices (Raspberry Pi, industrial mini-PCs), the 100K iterations can take 500ms+, adding noticeable delay to agent startup.
- **Evidence:**
  - `PlatformCredentialStore.cs:338-343` — `Rfc2898DeriveBytes.Pbkdf2(..., iterations: 100_000, ...)` — synchronous CPU-bound work
  - `PlatformCredentialStore.cs:273` — called from `GetSecretEncryptedFileAsync` on first read
  - `PlatformCredentialStore.cs:234` — called from `SetSecretEncryptedFileAsync` on first write
- **Impact:** 50-500ms blocking delay on first credential operation. Only affects Linux without `secret-tool`. The derived key is not cached between calls (re-derived every operation).
- **Recommended Fix:** Cache the derived key in a private field after first derivation. The inputs (machine-id + salt) don't change during process lifetime, so the key is stable. This eliminates the PBKDF2 cost from all operations after the first.

---

## Configuration Module

### P-DSK-009
- **Title:** ConfigManager.CheckSection allocates 20 JSON strings per config apply for change detection
- **Module:** Configuration
- **Severity:** Low
- **Category:** Inefficient database queries
- **Description:** `ConfigManager.CheckSection` compares config sections by serializing both the previous and current objects to JSON via `JsonSerializer.Serialize(previous)` and `JsonSerializer.Serialize(current)`, then doing an ordinal string comparison. This is invoked for all 10 sections (`identity`, `site`, `fcc`, `sync`, `buffer`, `localApi`, `telemetry`, `fiscalization`, `mappings`, `rollout`) on every successful config apply. Each invocation allocates two temporary strings — for sections like `Mappings` that contain arrays of product and nozzle mappings, these can be multi-kilobyte allocations. On each cloud config poll that returns a new version (as opposed to 304 Not Modified), 20 JSON serializations execute before the config is applied.
- **Evidence:**
  - `ConfigManager.cs:317-318` — `var prevJson = JsonSerializer.Serialize(previous); var currJson = JsonSerializer.Serialize(current);`
  - `ConfigManager.cs:300-309` — called for all 10 sections on every `ApplyConfigAsync`
  - `ConfigManager.cs:76` — version check exits early on 304, so this only runs on actual changes
- **Impact:** 20 temporary JSON string allocations per config apply. On low-powered edge devices, this contributes to GC pressure during config transitions. The serializations also have CPU cost proportional to section size.
- **Recommended Fix:** Implement `IEquatable<T>` on section classes with structural comparison, or compute and cache a SHA-256 hash per section for comparison. Alternatively, accept the trade-off since config applies are infrequent (typically minutes to hours apart).

---

### P-DSK-010
- **Title:** RegistrationManager.PostConfigure invalidates cache and forces synchronous file re-read on every Options resolution
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Slow data loading
- **Description:** `RegistrationManager.PostConfigure` (line 176) unconditionally clears `_cached = null` before calling `LoadState()`, forcing a synchronous `File.ReadAllText` of `registration.json` on every `IOptions<AgentConfiguration>` or `IOptionsMonitor<AgentConfiguration>` resolution. The comment (H-08) explains this is to ensure PostConfigure sees the latest persisted state after registration completes. However, after the initial startup, the registration state changes extremely rarely (only during provisioning or decommission). The cache invalidation occurs on EVERY options resolution — including every `IOptionsMonitor.CurrentValue` read by workers, every `IOptions<T>.Value` read, and every hot-reload signal. Combined with `ConfigManager` signalling `IOptionsChangeTokenSource` on each cloud config poll, this triggers repeated unnecessary file reads.
- **Evidence:**
  - `RegistrationManager.cs:176` — `lock (_lock) _cached = null;` — unconditional cache clear
  - `RegistrationManager.cs:178` — `var state = LoadState();` — triggers synchronous file read
  - `RegistrationManager.cs:74` — `File.ReadAllText(path)` — synchronous I/O
  - `ConfigManager.cs:124-125` — `SignalOptionsChange()` triggers IOptionsMonitor re-evaluation → PostConfigure runs again
- **Impact:** Every cloud config poll that signals a change triggers a synchronous `registration.json` read. On devices with slow storage (USB drives, SD cards at edge sites), each read can take 10-50ms, adding latency to config hot-reload.
- **Recommended Fix:** Only invalidate the cache when the registration state actually changes (after `SaveStateAsync`), not on every PostConfigure call. Add a version counter or change flag that PostConfigure checks before invalidating.

---

## FCC Device Integration Module

### P-DSK-011
- **Title:** OdooWsMessageHandler.HandleAllAsync loads 500 transactions with full EF change tracking — heavy memory allocation
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Inefficient database queries
- **Description:** `HandleAllAsync` queries `db.Transactions.OrderByDescending(t => t.CompletedAt).Take(500).ToListAsync()` without `AsNoTracking()`. EF Core's change tracker creates snapshot copies of every loaded entity's property values for dirty checking. For 500 transactions with `RawPayloadJson` (which stores the full FCC protocol payload — potentially kilobytes per record), this allocates significant memory for snapshots that are never used (the handler only reads entities, never modifies them). The `HandleLatestAsync` method has the same issue — it loads up to 200 transactions without `AsNoTracking()`.
- **Evidence:**
  - `OdooWsMessageHandler.cs:79-82` — `db.Transactions.OrderByDescending(t => t.CompletedAt).Take(500).ToListAsync()` — no `AsNoTracking()`
  - `OdooWsMessageHandler.cs:61-64` — `HandleLatestAsync` also missing `AsNoTracking()` — loads up to 200 with tracking
  - `TransactionBufferManager.cs:85` — `GetPendingBatchAsync` correctly uses `.AsNoTracking()` — model to follow
  - `BufferedTransaction.cs` — `RawPayloadJson` field can be kilobytes per record
- **Impact:** 500 entity snapshots with large `RawPayloadJson` fields creates significant GC pressure. On a site with frequent WebSocket `all` queries, this contributes to Gen2 GC pauses on memory-constrained edge devices.
- **Recommended Fix:** Add `.AsNoTracking()` to both `HandleAllAsync` and `HandleLatestAsync` queries. These are read-only operations that never call `SaveChangesAsync`.

---

### P-DSK-012
- **Title:** WebSocket pump status broadcast creates per-connection timer loop — O(N) FCC queries for N clients
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** UI thread blocking
- **Description:** `OdooWebSocketServer.PumpStatusBroadcastLoopAsync` starts a per-connection background loop that calls `PumpStatusService.GetPumpStatusAsync(null, ct)` every 3 seconds for each connected client. With 5 concurrent WebSocket connections (typical for a multi-POS site), this means 5 independent pump status queries every 3 seconds. While `PumpStatusService` has single-flight protection (SemaphoreSlim + stale cache), the overlapping async calls still contend on the semaphore and the 4 blocked callers wait for the single live query to complete. The broadcast payload is identical for all clients since pump status is site-wide, not per-client.
- **Evidence:**
  - `OdooWebSocketServer.cs:189-220` — `PumpStatusBroadcastLoopAsync` — per-connection loop
  - `OdooWebSocketServer.cs:75` — `var pumpStatusTask = PumpStatusBroadcastLoopAsync(webSocket, cts.Token)` — started for every connection
  - `PumpStatusService.cs` — single-flight protection prevents concurrent FCC calls but doesn't eliminate wait contention
- **Impact:** 5× unnecessary semaphore contention and cache lookups every 3 seconds. On slow FCC endpoints, all 5 connections wait in sequence on the semaphore, each adding latency to the broadcast cycle.
- **Recommended Fix:** Replace per-connection broadcast loops with a single site-wide broadcast timer that queries pump status once and sends to all connected clients. The existing `BroadcastToAllAsync` method already supports this pattern.

---

### P-DSK-013
- **Title:** CloudUploadWorker builds upload request by mapping all buffered transactions including full RawPayloadJson
- **Module:** FCC Device Integration
- **Severity:** Low
- **Category:** Inefficient database queries
- **Description:** `CloudUploadWorker.BuildUploadRequest` calls `ToCanonical` for each transaction in the batch, which copies `RawPayloadJson` (line 387) into the upload DTO. The `GetPendingBatchAsync` query already loads the full entity including `RawPayloadJson` with `AsNoTracking()`. The upload request then serializes the entire `CanonicalTransaction` including the raw payload. If the cloud API doesn't need the raw FCC payload (it already received it or can reconstruct from the canonical fields), this is wasted bandwidth and serialization overhead. For Radix XML transactions, `RawPayloadJson` can be 5-10 KB per record. A batch of 50 records sends 250-500 KB of redundant raw payloads.
- **Evidence:**
  - `CloudUploadWorker.cs:387` — `RawPayloadJson = t.RawPayloadJson` — copies full raw payload
  - `CloudUploadWorker.cs:360` — `batch.Select(t => ToCanonical(t, config)).ToList()` — maps all fields
  - `TransactionBufferManager.cs:81-86` — `GetPendingBatchAsync` loads full entities including `RawPayloadJson`
- **Impact:** 250-500 KB of redundant payload data per upload batch. On constrained networks (cellular/satellite at remote fuel stations), this wastes bandwidth and increases upload latency.
- **Recommended Fix:** Confirm with the cloud API whether `RawPayloadJson` is required in upload requests. If not, exclude it from `ToCanonical`. If it is required, consider compressing the upload payload.

---

### P-DSK-014
- **Title:** IngestionOrchestrator creates a new DI scope per poll cycle — scope+DbContext initialization on every cadence tick
- **Module:** FCC Device Integration
- **Severity:** Low
- **Category:** Inefficient database queries
- **Description:** `DoPollAndBufferAsync` creates a new `IServiceScope`, resolves `AgentDbContext` and `TransactionBufferManager`, and disposes the scope on every cadence tick (line 151-153). This is architecturally correct (scoped services should not be captured by singletons), but on a 30-second cadence interval, it means 2,880 scope+context allocations per day. Each allocation involves EF Core context creation, change tracker initialization, and SQLite connection pool checkout. On ticks where the FCC has no new transactions (most ticks), the scope is created just to query `SyncStates` and discover there's nothing to do.
- **Evidence:**
  - `IngestionOrchestrator.cs:151-153` — `using var scope = _scopeFactory.CreateScope()` — per-tick allocation
  - `IngestionOrchestrator.cs:172-173` — `db.SyncStates.FirstOrDefaultAsync(s => s.Id == 1, ct)` — minimal query that still requires full scope
  - `CadenceController.cs:291` — Pre-auth expiry also creates a scope per tick (same pattern)
- **Impact:** Minor GC pressure from ~2,880+ scope/context allocations per day. On low-powered edge devices, contributes to brief CPU spikes during GC.
- **Recommended Fix:** Consider a lightweight "has pending work" check that avoids full scope creation (e.g., cached flag set by webhook listeners or a raw SQL count query). Alternatively, accept the trade-off since the allocations are small and the pattern is architecturally sound.

---

## Site Master Data Module

### P-DSK-015
- **Title:** DashboardPage refresh timer runs GROUP BY aggregation over all transactions every 5 seconds
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Inefficient database queries
- **Description:** `DashboardPage.RefreshAllAsync` fires every 5 seconds via `System.Threading.Timer` and calls `TransactionBufferManager.GetBufferStatsAsync`. This method executes two queries: (1) `GROUP BY SyncStatus` with `Count()` over the entire `buffered_transactions` table, and (2) `MIN(CreatedAt)` with a `WHERE SyncStatus = 'Pending'` filter. On a database approaching the 30,000 record limit, the GROUP BY scans the full table (no covering index on `SyncStatus` alone). The result is immediately posted to the UI thread. With 17,280 executions per day (5s interval), this is the single highest-frequency DB operation in the application.
- **Evidence:**
  - `DashboardPage.axaml.cs:44` — `_refreshTimer = new Timer(_, null, TimeSpan.Zero, TimeSpan.FromSeconds(5))`
  - `DashboardPage.axaml.cs:105` — `var stats = await buffer.GetBufferStatsAsync()`
  - `TransactionBufferManager.cs:190-193` — `GroupBy(t => t.SyncStatus).Select(g => new { Status = g.Key, Count = g.Count() })`
  - `TransactionBufferManager.cs:195-199` — second query for oldest pending timestamp
  - No index on `SyncStatus` alone (indices are compound: `ix_bt_sync_status` = `SyncStatus, CreatedAt`)
- **Impact:** On a buffer with 30K records, two full-table scans every 5 seconds. CPU and I/O overhead is noticeable on low-powered edge devices (Celeron/Atom), contributing to UI sluggishness.
- **Recommended Fix:** Increase the refresh interval to 15-30 seconds for buffer stats. Cache the stats in memory and only re-query when a transaction is buffered or uploaded (event-driven). Alternatively, maintain a summary row in `sync_state` that is updated atomically on each status transition.

---

### P-DSK-016
- **Title:** DashboardPage creates new SolidColorBrush objects on every connectivity state change
- **Module:** Site Master Data
- **Severity:** Low
- **Category:** Inefficient list rendering
- **Description:** `DashboardPage.UpdateConnectivityDisplay` (lines 70-81) creates new `SolidColorBrush(Color.Parse(...))` objects on every call — up to 5 brush allocations per connectivity change (StatusIndicator, InternetStatus x2, FccStatus x2). Connectivity state changes can fire frequently during network instability (e.g., flapping WiFi on a fuel station). Each `Color.Parse` involves string parsing, and each `SolidColorBrush` is a small heap allocation. While individually trivial, during rapid connectivity flapping this generates unnecessary GC pressure.
- **Evidence:**
  - `DashboardPage.axaml.cs:70` — `StatusIndicator.Foreground = new SolidColorBrush(Color.Parse(color))`
  - `DashboardPage.axaml.cs:74-76` — `InternetStatus.Foreground = new SolidColorBrush(Color.Parse(...))`
  - `DashboardPage.axaml.cs:79-81` — `FccStatus.Foreground = new SolidColorBrush(Color.Parse(...))`
  - Called on every `_connectivity.StateChanged` event and every 5-second timer tick (line 171-177)
- **Impact:** Minor GC pressure during network instability. Each connectivity change allocates ~5 SolidColorBrush objects that immediately become eligible for GC.
- **Recommended Fix:** Cache brushes as `static readonly` fields (e.g., `static readonly SolidColorBrush GreenBrush = new(Color.Parse("#22C55E"))`) and reference them by state. Alternatively, define them as XAML StaticResources.

---

### P-DSK-017
- **Title:** SiteDataManager.SyncFromConfig performs synchronous File.WriteAllText on the calling thread
- **Module:** Site Master Data
- **Severity:** Low
- **Category:** Heavy file operations on UI thread
- **Description:** `SiteDataManager.SyncFromConfig` (line 92) calls `File.WriteAllText(path, json)` synchronously. This method is called from `RegistrationManager.SyncSiteData`, which can be invoked from `ProvisioningWindow.axaml.cs` on the UI thread (lines 251, 415). The JSON serialization (line 91) and file write block the calling thread for the duration of disk I/O. For a typical `site-data.json` file (1-5 KB), the write takes <1ms on SSD but can take 10-50ms on slow USB drives or SD cards used in some edge devices.
- **Evidence:**
  - `SiteDataManager.cs:91-92` — `var json = JsonSerializer.Serialize(snapshot, JsonOptions); File.WriteAllText(path, json);`
  - `ProvisioningWindow.axaml.cs:251` — `_registrationManager.SyncSiteData(siteConfig)` — called in async void handler
  - `SiteDataManager.cs:122` — `LoadSiteData` also uses synchronous `File.ReadAllText`
- **Impact:** Brief UI freeze (10-50ms) on slow storage during registration. Minimal impact on SSD-based deployments.
- **Recommended Fix:** Convert to `File.WriteAllTextAsync` / `File.ReadAllTextAsync` and make `SyncFromConfig` and `LoadSiteData` async methods. Update callers accordingly.

---

### P-DSK-018
- **Title:** Pre-auth expiry sweep performs sequential FCC deauthorizations inside the cadence loop
- **Module:** Pre-Authorization
- **Severity:** Medium
- **Category:** Slow data loading
- **Description:** The cadence controller executes `RunExpiryCheckAsync` inline on every tick. That method loads up to `PreAuthExpiryBatchSize` expired records and then awaits FCC deauthorization one record at a time inside a `foreach`. `TryCancelAtFccAsync` creates a vendor adapter and calls `CancelPreAuthAsync` directly with the outer cadence token, without adding a short per-record timeout. With the default batch size of 50 and default pre-auth timeout budget of 30 seconds, a backlog of expired authorized records can hold the cadence loop for minutes.
- **Evidence:**
  - `CadenceController.cs:286-295` — expiry check runs inside the main cadence loop every tick
  - `AgentConfiguration.cs:72-78` — defaults are `PreAuthTimeoutSeconds = 30` and `PreAuthExpiryBatchSize = 50`
  - `PreAuthHandler.cs:316-323` — loads a batch of expired pre-auth records
  - `PreAuthHandler.cs:333-342` — deauthorizes authorized records sequentially in a `foreach`
  - `PreAuthHandler.cs:392-402` — `TryCancelAtFccAsync` calls `adapter.CancelPreAuthAsync(...)` without its own timeout wrapper
- **Impact:** Large expiry backlogs can delay transaction polling, cloud upload, status polling, telemetry, and later expiry cycles because the cadence loop is blocked on slow FCC cancel calls.
- **Recommended Fix:** Move FCC deauthorization retries out of the hot cadence path, or at minimum cap each cancel with a short timeout and a small degree of bounded parallelism.

---

## Transaction Management Module

### P-DSK-019
- **Title:** TransactionsPage 10-second auto-refresh timer runs even when page is not visible or active
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Inefficient database queries
- **Description:** `TransactionsPage` constructor (line 34) starts a `System.Threading.Timer` that fires every 10 seconds, posting `LoadPageAsync()` to the UI thread. This timer runs continuously from the moment the page is constructed, regardless of whether the page is currently visible (e.g., user navigated to Dashboard or Settings). Each tick executes two database queries (COUNT + paged data fetch) against the SQLite database. With 30,000+ buffered transactions, this creates unnecessary I/O and CPU load even when no one is looking at the Transactions tab.
- **Evidence:**
  - `TransactionsPage.axaml.cs:34` — `_refreshTimer = new Timer(... TimeSpan.Zero, TimeSpan.FromSeconds(10))`
  - `TransactionsPage.axaml.cs:40-101` — `LoadPageAsync` runs full EF Core query each tick
  - `TransactionsPage.axaml.cs:64` — `_totalCount = await query.CountAsync()` — separate COUNT query
  - `TransactionsPage.axaml.cs:66-89` — paged data query with projection
- **Impact:** Continuous 10-second database polling when the page is hidden wastes CPU cycles, disk I/O, and battery on edge devices. With multiple pages doing similar polling, the aggregate overhead compounds.
- **Recommended Fix:** Pause the timer when the page loses visibility (override `OnDetachedFromVisualTree` or listen to `IsVisible` changes). Resume on re-attach. Alternatively, use a reactive approach where the page subscribes to a transaction-change event from `TransactionBufferManager`.

### P-DSK-020
- **Title:** WebSocket per-connection pump status loop queries FCC independently every 3 seconds per client
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Slow data loading
- **Description:** `OdooWebSocketServer.PumpStatusBroadcastLoopAsync` (lines 189-219) starts an independent timer loop per connected WebSocket client. Each loop calls `PumpStatusService.GetPumpStatusAsync(null, ct)` every 3 seconds. With 5 concurrent Odoo POS clients connected, this results in 5 independent FCC pump status queries every 3 seconds (100 queries/minute) even though all clients receive identical data. The pump status result is not cached or shared between connections.
- **Evidence:**
  - `OdooWebSocketServer.cs:75` — `var pumpStatusTask = PumpStatusBroadcastLoopAsync(ws, cts.Token)` — per-connection
  - `OdooWebSocketServer.cs:193` — `var interval = TimeSpan.FromSeconds(Math.Max(1, Options.PumpStatusBroadcastIntervalSeconds))`
  - `OdooWebSocketServer.cs:203` — `var result = await svc.GetPumpStatusAsync(null, ct)` — FCC call per client
  - `OdooWebSocketServer.cs:204-208` — sends identical data to each client independently
- **Impact:** Linear scaling of FCC queries with WebSocket client count. With 10 connected clients at 3-second intervals, the FCC receives 200 status requests per minute, potentially overwhelming serial-protocol FCCs (DOMS TCP/JPL).
- **Recommended Fix:** Replace per-connection loops with a single shared pump status polling loop. Cache the latest pump status result and broadcast the cached snapshot to all connected clients from a single timer. Use `BroadcastToAllAsync` to send the same data to all clients at once.

### P-DSK-021
- **Title:** WebSocket "all" and "latest" modes fetch tracked entities without AsNoTracking
- **Module:** Transaction Management
- **Severity:** Low
- **Category:** Inefficient database queries
- **Description:** `OdooWsMessageHandler.HandleAllAsync` (lines 79-82) fetches up to 500 transactions without `.AsNoTracking()`, and `HandleLatestAsync` (lines 52-64) fetches up to 200 transactions also without AsNoTracking. The entities are only read (mapped to DTOs via `ToWsDto()`) and never modified. Tracked entities consume more memory (EF Core maintains change-tracking snapshots) and incur overhead on `SaveChangesAsync` calls within the same scope if other handlers modify entities.
- **Evidence:**
  - `OdooWsMessageHandler.cs:79-82` — `db.Transactions.OrderByDescending(...).Take(500).ToListAsync(ct)` — no AsNoTracking
  - `OdooWsMessageHandler.cs:52-64` — `HandleLatestAsync` query — no AsNoTracking
  - `OdooWsMessageHandler.cs:66` — `txns.Select(t => t.ToWsDto())` — read-only mapping
  - Contrast: `TransactionsPage.axaml.cs:49` — `db.Transactions.AsNoTracking()` — correct usage
- **Impact:** Unnecessary memory overhead for change-tracking snapshots of up to 500 entities per WebSocket "all" request. With frequent requests from multiple clients, GC pressure increases.
- **Recommended Fix:** Add `.AsNoTracking()` to the queries in `HandleAllAsync` and `HandleLatestAsync` since the entities are read-only.

### P-DSK-022
- **Title:** TransactionsPage executes separate COUNT and data queries per page load instead of combined approach
- **Module:** Transaction Management
- **Severity:** Low
- **Category:** Inefficient database queries
- **Description:** `TransactionsPage.LoadPageAsync` executes two separate database round-trips per page load: `query.CountAsync()` (line 64) for total count, then `query.OrderByDescending(...).Skip(...).Take(...).ToListAsync()` (lines 66-89) for the page data. Both queries apply the same filter predicates but are executed independently. On SQLite with 30,000+ records, the COUNT query scans the index to count matching rows, then the data query re-scans for the page. This doubles the I/O for each 10-second refresh cycle.
- **Evidence:**
  - `TransactionsPage.axaml.cs:64` — `_totalCount = await query.CountAsync()` — first round trip
  - `TransactionsPage.axaml.cs:66-89` — `await query.OrderByDescending(...).Skip(...).Take(PageSize).ToListAsync()` — second round trip
  - Both queries share the same filter predicates (_statusFilter, _pumpFilter, _dateFrom, _dateTo)
- **Impact:** Two database round-trips per 10-second refresh. With the timer running even when hidden (P-DSK-019), this doubles the unnecessary I/O overhead.
- **Recommended Fix:** Fetch `PageSize + 1` items in a single query to determine if a next page exists (same pattern as `TransactionBufferManager.GetPagedForLocalApiAsync`). Cache the total count and refresh it less frequently (e.g., every 5th load or on filter change only).

---

### P-DSK-023
- **Title:** Cloud sync status is queried twice every 5 seconds after the dashboard has been opened
- **Module:** Cloud Sync
- **Severity:** Medium
- **Category:** Inefficient database queries
- **Description:** Two different UI surfaces poll the same cloud-sync status data on independent timers. `MainWindowViewModel` refreshes the status bar every 5 seconds, and `DashboardPage` refreshes the dashboard every 5 seconds. Both create a new scope, resolve `TransactionBufferManager`, execute `GetBufferStatsAsync()`, and query `SyncStateRecord.LastUploadAt`. `MainWindow` caches `DashboardPage` for the lifetime of the window and only disposes it on window close, so once the dashboard has been opened its hidden timer keeps running in parallel with the main-window status timer.
- **Evidence:**
  - `MainWindowViewModel.cs:44-46` — starts a 5-second timer for status-bar refresh
  - `MainWindowViewModel.cs:109-149` — each tick queries `TransactionBufferManager` and `AgentDbContext.SyncStates`
  - `DashboardPage.axaml.cs:44` — starts a second 5-second timer
  - `DashboardPage.axaml.cs:95-178` — each tick runs the same buffer-stats and sync-state queries
  - `MainWindow.axaml.cs:24-30` and `MainWindow.axaml.cs:99-110` — dashboard page is cached, not recreated per navigation
  - `MainWindow.axaml.cs:89-92` — dashboard disposal happens only when the window closes
- **Impact:** Once the operator visits the dashboard, the app permanently doubles its highest-frequency cloud-sync status query path for the rest of the session. On large SQLite buffers this means extra table scans, scopes, and allocations with no additional information.
- **Recommended Fix:** Publish one shared status snapshot from a single service and bind both the status bar and dashboard to it, or pause the dashboard timer when the page is not visible/attached.

---

### P-DSK-024
- **Title:** LogsPage keeps polling `audit_log` every 10 seconds after the tab is hidden
- **Module:** Monitoring & Diagnostics
- **Severity:** Medium
- **Category:** Inefficient database queries
- **Description:** `LogsPage` starts a 10-second refresh timer in its constructor, and `MainWindow` caches the page for the entire lifetime of the window. Once the operator opens Logs once, the page keeps creating a scope and querying `audit_log` every 10 seconds even when another tab is active. If the user closes the window to tray, the page is still alive because `OnClosing` hides the window instead of disposing the cached pages.
- **Evidence:**
  - `LogsPage.axaml.cs:21-28` - timer starts in the constructor
  - `LogsPage.axaml.cs:31-68` - each tick creates a DI scope and queries `AgentDbContext`
  - `MainWindow.axaml.cs:24-30` - `LogsPage` is cached in `_logsPage`
  - `MainWindow.axaml.cs:99-108` - navigation reuses the cached instance instead of recreating it when shown
  - `MainWindow.axaml.cs:68-75` - close action hides the window to tray rather than disposing pages
  - `MainWindow.axaml.cs:89-92` and `MainWindow.axaml.cs:200-204` - cached pages are only disposed on a real app shutdown
- **Impact:** A single visit to the Logs tab creates permanent background SQLite activity for the rest of the session, adding needless query load and allocations with no operator-visible benefit while the page is hidden.
- **Recommended Fix:** Stop or pause the timer when `LogsPage` is not visible/attached, or move log refresh into a shared observable service that only polls while the diagnostics view is active.

---

### P-DSK-025
- **Title:** Transaction update broadcasts fan out to WebSocket clients sequentially
- **Module:** Odoo Integration
- **Severity:** Medium
- **Category:** Slow data loading
- **Description:** Real-time Odoo transaction updates are broadcast one client at a time. `HandleManagerUpdateAsync()` and `HandleAttendantUpdateAsync()` delegate to `BroadcastToAllAsync()`, and that method awaits `ws.SendAsync(...)` serially inside a `foreach`. Broadcast latency therefore grows linearly with the number of connected terminals, and one slow socket delays delivery to every client after it.
- **Evidence:**
  - `OdooWsMessageHandler.cs:112` — manager updates use `_broadcastToAll(...)`
  - `OdooWsMessageHandler.cs:131-132` — attendant updates use the same broadcast path
  - `OdooWebSocketServer.cs:244-255` — `BroadcastToAllAsync()` iterates clients and awaits each `SendAsync` sequentially
- **Impact:** Cart-state and transaction-status updates can arrive noticeably late on some Odoo terminals under load or on poor LAN links, even though the underlying database mutation already completed.
- **Recommended Fix:** Snapshot the current clients and send in parallel with bounded concurrency and per-socket timeouts. That keeps one slow connection from stretching the latency budget for every other terminal.
