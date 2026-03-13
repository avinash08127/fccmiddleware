# F-16 — Settings Page Audit

**Route:** `/settings`
**Component:** `SettingsComponent` (`src/portal/src/app/features/settings/settings.component.ts`)
**Route config:** `src/portal/src/app/features/settings/settings.routes.ts`
**Service:** `SettingsService` (`src/portal/src/app/core/services/settings.service.ts`)
**Model:** `src/portal/src/app/core/models/settings.model.ts`
**Backend controller:** `AdminSettingsController` (`src/cloud/FccMiddleware.Api/Controllers/AdminSettingsController.cs`)
**Backend contracts:** `PortalSettingsContracts.cs` (`src/cloud/FccMiddleware.Contracts/Portal/PortalSettingsContracts.cs`)
**Backend entity:** `PortalSettings` (`src/cloud/FccMiddleware.Domain/Entities/PortalSettings.cs`)

---

## API Endpoints Used

| Frontend call | Backend endpoint | Backend auth |
|---|---|---|
| `GET /api/v1/admin/settings` | `AdminSettingsController.GetSettings` | PortalAdminWrite |
| `PUT /api/v1/admin/settings/global-defaults` | `AdminSettingsController.UpdateGlobalDefaults` | PortalAdminWrite |
| `PUT /api/v1/admin/settings/overrides/{lei}` | `AdminSettingsController.UpsertOverride` | PortalAdminWrite |
| `DELETE /api/v1/admin/settings/overrides/{lei}` | `AdminSettingsController.DeleteOverride` | PortalAdminWrite |
| `PUT /api/v1/admin/settings/alerts` | `AdminSettingsController.UpdateAlerts` | PortalAdminWrite |
| `GET /api/v1/master-data/legal-entities` | MasterDataController | PortalUser |

---

## Findings

### F16-01 · Role guard / backend policy authorization mismatch (M)

**Frontend:** `roleGuard(['SystemAdmin'])` — only `SystemAdmin` role can navigate to `/settings`.
**Backend:** `[Authorize(Policy = "PortalAdminWrite")]` which accepts `OperationsManager`, `SystemAdmin`, and `SystemAdministrator` (see `Program.cs:218-225`).

**Impact:** `OperationsManager` users can access all settings API endpoints directly (Postman, scripts, etc.) but are blocked from the UI page. Either the frontend guard should include `OperationsManager` or the backend should restrict to `SystemAdmin` only — depends on business intent.

**File:** `settings.routes.ts:9` vs `Program.cs:218-225`

---

### F16-02 · No confirmation dialog before deleting override (M)

`deleteOverride()` at line 770 immediately calls the API with no confirmation prompt. Accidental clicks on the trash icon delete a legal-entity override instantly. Other pages in the portal (DLQ discard, agent decommission) use confirmation dialogs for destructive operations.

**File:** `settings.component.ts:770`

---

### F16-03 · No server-side email validation for alert recipients (M)

The frontend validates email format via regex (`EMAIL_PATTERN` at line 783), but `UpdateAlerts` on the backend stores `EmailRecipientsHigh` and `EmailRecipientsCritical` directly into the JSON blob with no validation. A direct API call can inject arbitrary strings (empty, scripts, malformed) into the notification recipient lists.

**File:** `AdminSettingsController.cs:189-207` — no validation on `request.EmailRecipientsHigh` / `request.EmailRecipientsCritical`

---

### F16-04 · No server-side numeric range validation (M)

The frontend enforces bounds via PrimeNG `[min]`/`[max]` attributes (e.g., `amountTolerancePercent` 0–100, `timeWindowMinutes` 1–60, `stalePendingThresholdDays` 1–90). The backend `UpdateGlobalDefaults` and `UpsertOverride` accept any numeric values without validation. A direct API call could set:
- `amountTolerancePercent` to 1000% (auto-match everything)
- `timeWindowMinutes` to 0 or negative (break reconciliation matching)
- `auditEventRetentionDays` to 0 (purge all audit records)

**File:** `AdminSettingsController.cs:44-80` (UpdateGlobalDefaults), `AdminSettingsController.cs:82-140` (UpsertOverride)

---

### F16-05 · GET settings requires write-level authorization (M)

The entire `AdminSettingsController` is decorated with `[Authorize(Policy = "PortalAdminWrite")]` (line 16). The `GET /api/v1/admin/settings` read endpoint inherits this write-level policy. Non-admin portal users (e.g., `PortalUser` role observers) cannot view system settings even in read-only mode.

If future requirements add read-only settings visibility, the GET method should use a separate `PortalUser` or `PortalAdminRead` policy with `[Authorize]` override on the method.

**File:** `AdminSettingsController.cs:16,28-40`

---

### F16-06 · Race condition in EnsureSettingsAsync on first request (L)

If two concurrent requests hit `EnsureSettingsAsync` before any row exists, both will attempt to insert the singleton row. The second insert will throw a unique constraint violation from the database. In practice this is unlikely since the EF migration seeds the singleton row, but the code path exists.

**File:** `AdminSettingsController.cs:215-243`

---

### F16-07 · Delete override updates audit trail even when nothing is deleted (L)

`DeleteOverride` at line 160 checks if the override exists; if not (`overrideRow is null`), it skips the remove but still calls `UpdateAuditFields(settings)` and `SaveChangesAsync`. This updates `UpdatedAt`/`UpdatedBy` on the settings row without actually changing anything, creating a misleading audit trail.

**File:** `AdminSettingsController.cs:157-171`

---

### F16-08 · `availableLegalEntities` is a regular function, not a computed signal (L)

`availableLegalEntities` at line 644 is defined as a regular arrow function that reads `this.overrides` (a plain array, not a signal). It is recalculated on every Angular change detection cycle rather than only when inputs change. Should be a `computed()` signal reading from signal-based overrides for efficiency.

**File:** `settings.component.ts:644-649`

---

### F16-09 · No cross-list duplicate prevention for email recipients (L)

`addEmailHigh()` checks for duplicates only within `emailRecipientsHigh`, and `addEmailCritical()` checks only within `emailRecipientsCritical`. The same email address can appear in both lists. Whether this is intentional (person receives both severity levels) is unclear — no UI hint exists. If a user meant to move an email from High to Critical, they might end up with it in both.

**File:** `settings.component.ts:789-823`

---

### F16-10 · Backend always writes even when values are unchanged (L)

`UpdateGlobalDefaults`, `UpdateAlerts`, and `saveRetentionPolicies` always serialize and save the settings blob plus update `UpdatedAt`/`UpdatedBy`, even when the submitted values are identical to the current ones. This creates noise in the audit trail (settings appear to change on every save click).

**File:** `AdminSettingsController.cs:54-80,185-213`

---

## Summary

| ID | Severity | Category | Description |
|---|---|---|---|
| F16-01 | **M** | Auth | Frontend roleGuard(['SystemAdmin']) blocks OperationsManager users who backend allows |
| F16-02 | **M** | UX | No confirmation dialog for delete override |
| F16-03 | **M** | Validation | No server-side email validation for alert recipients |
| F16-04 | **M** | Validation | No server-side numeric range validation for settings values |
| F16-05 | **M** | Auth | GET settings endpoint requires write-level (PortalAdminWrite) authorization |
| F16-06 | **L** | Concurrency | Race condition in EnsureSettingsAsync on first request |
| F16-07 | **L** | Logic | Delete override updates audit trail even when nothing deleted |
| F16-08 | **L** | Performance | availableLegalEntities is a plain function, not a computed signal |
| F16-09 | **L** | UX | No cross-list duplicate prevention for email recipients |
| F16-10 | **L** | Logic | Backend always writes/updates audit even when values unchanged |

**Total: 5 Medium, 5 Low = 10 issues**
