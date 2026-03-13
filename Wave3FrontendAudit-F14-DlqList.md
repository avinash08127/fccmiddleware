# Wave 3 — F-14 DlqListComponent Audit

**Route:** `/dlq/list`
**Component:** `DlqListComponent` (`src/portal/src/app/features/dlq/dlq-list.component.ts`)
**Service:** `DlqService` (`src/portal/src/app/core/services/dlq.service.ts`)
**Backend:** `DlqController` (`src/cloud/FccMiddleware.Api/Controllers/DlqController.cs`)
**Route guard:** `roleGuard(['SystemAdmin', 'OperationsManager'])`
**Date:** 2026-03-13

---

## Summary

The DLQ list page allows operations users to view, filter, retry, and discard dead-letter items scoped by legal entity. It uses cursor-based pagination, batch actions with role gating, and a confirmation dialog for batch discard. Overall structure is solid, but there are enum drift issues between frontend and backend, pagination inconsistencies, and missing backend validations.

---

## Issues Found

### F14-01 · Missing `NORMALIZATION_FAILURE` in frontend `DeadLetterReason` enum [MEDIUM]

**File:** `src/portal/src/app/core/models/dlq.model.ts:17-23`
**Backend:** `src/cloud/FccMiddleware.Domain/Enums/DeadLetterReason.cs`

Backend defines 6 values: `VALIDATION_FAILURE`, `NORMALIZATION_FAILURE`, `DEDUPLICATION_ERROR`, `ADAPTER_ERROR`, `PERSISTENCE_ERROR`, `UNKNOWN`.
Frontend enum only has 5 — missing `NORMALIZATION_FAILURE`.

**Impact:** Items with reason `NORMALIZATION_FAILURE` display the raw string. The filter dropdown has no option for this reason, so users cannot filter for these items. The `reasonOptions` array is built from `Object.values(DeadLetterReason)`, so it silently omits this category.

---

### F14-02 · Missing `REPLAY_QUEUED` and `REPLAY_FAILED` in frontend `DeadLetterStatus` enum [MEDIUM]

**File:** `src/portal/src/app/core/models/dlq.model.ts:10-15`
**Backend:** `src/cloud/FccMiddleware.Domain/Enums/DeadLetterStatus.cs`

Backend defines 6 statuses: `PENDING`, `REPLAY_QUEUED`, `RETRYING`, `RESOLVED`, `REPLAY_FAILED`, `DISCARDED`.
Frontend only defines 4: `PENDING`, `RETRYING`, `RESOLVED`, `DISCARDED`.

**Impact:** Items in `REPLAY_QUEUED` or `REPLAY_FAILED` state render with the default `contrast` severity badge (the unknown case in `statusSeverity()`). The status filter dropdown omits these values. Users cannot filter for items that are queued for replay or have failed replay.

---

### F14-03 · Cursor pagination breaks when page size changes [MEDIUM]

**File:** `src/portal/src/app/features/dlq/dlq-list.component.ts:643-649`

The table offers `rowsPerPageOptions: [20, 50, 100]` but the cursor array (`cursors`) stores cursor tokens keyed by page index, which is calculated as `Math.floor(first / rows)`. Cursors were generated for the original page size (20). When the user changes page size (e.g., to 50), the page indices no longer map to valid cursors.

Example: User is on page 2 of 20 items (cursor[1] = token for item 20). Switching to 50 rows per page: PrimeNG fires `onLazyLoad` with `first=0, rows=50`. This resolves to page 0 with cursor[0]=null, which works. But navigating to "page 1" (items 50-100) tries cursor[1], which is the cursor for item 20 — wrong offset.

---

### F14-04 · Potential double-load on search [LOW]

**File:** `src/portal/src/app/features/dlq/dlq-list.component.ts:623-629`

`search()` resets `listState` (setting `tableFirst` to 0) but does not reset `searched` to `false`. If `searched` is already `true` from a prior search, the PrimeNG table may fire `onLazyLoad` in response to the `[first]` binding change, and the `onLazyLoad` guard (`if (!this.searched()) return`) won't block it, causing a duplicate API call. The `switchMap` in `load$` mitigates this (cancels the first) but still fires an unnecessary request.

---

### F14-05 · Batch discard silently skips failures — no feedback to user [MEDIUM]

**File:** `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:371-403`

`DiscardBatch` iterates items and calls `continue` for any that are not found or where access is denied (line 374). It returns `200 OK` with no response body indicating which items were skipped. Contrast with `RetryBatch`, which returns `{ succeeded: [...], failed: [...] }`.

**Impact:** Frontend shows "X item(s) discarded successfully" with the count from `selectedItems.length`, which may be incorrect if some items were silently skipped.

---

### F14-06 · No batch size limit on retry/discard [MEDIUM]

**Files:**
- `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:277-348` (RetryBatch)
- `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:350-407` (DiscardBatch)

Neither `RetryBatch` nor `DiscardBatch` validates a maximum number of IDs. The frontend also doesn't limit selection. A user could select and retry hundreds of items. `RetryBatch` uses a semaphore (concurrency 5) but the request would be long-running. `DiscardBatch` executes all in a single `SaveChangesAsync` which could be a very large transaction.

---

### F14-07 · Discard endpoints don't validate item state [LOW]

**Files:**
- `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:216-272` (Discard single)
- `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:350-407` (DiscardBatch)

`Retry` and `RetryBatch` check `item.Status is RESOLVED or DISCARDED` and reject the operation. Neither `Discard` nor `DiscardBatch` performs this check. An already-RESOLVED item can be overwritten to DISCARDED, and an already-DISCARDED item can have its reason, timestamp, and user overwritten.

---

### F14-08 · No backend validation on discard reason minimum length [LOW]

**File:** `src/cloud/FccMiddleware.Api/Controllers/DlqController.cs:216-272`
**Contract:** `src/cloud/FccMiddleware.Contracts/Portal/PortalDlqContracts.cs:41-44`

Frontend enforces `batchDiscardReason.trim().length < 10` before enabling the discard button. Backend `DiscardRequestDto` has no validation attributes — any non-null string (including empty) is accepted. A direct API call bypasses the frontend minimum.

---

### F14-09 · Table selection checkboxes visible to read-only users [LOW]

**File:** `src/portal/src/app/features/dlq/dlq-list.component.ts:225-234`

The table has `selectionMode="multiple"` and renders checkbox columns for all users. The batch action bar is gated behind `*appRoleVisible="['SystemAdmin', 'OperationsManager']"`, but the route itself already requires these roles via `roleGuard`. So this is technically unreachable. However, if role requirements ever diverge (e.g., adding `Viewer` role to the route guard), users could select items but see no action buttons.

---

## API Trace

| Frontend Action | Service Method | HTTP | Backend Endpoint | Auth |
|----------------|---------------|------|-----------------|------|
| Page load / filter | `dlqService.getDeadLetters()` | GET `/api/v1/dlq` | `DlqController.GetDeadLetters` | PortalUser |
| Row click → detail | `router.navigate(['/dlq/items', id])` | — (route) | — | — |
| Batch retry | `dlqService.retryBatch(ids)` | POST `/api/v1/dlq/retry-batch` | `DlqController.RetryBatch` | PortalAdminWrite |
| Batch discard | `dlqService.discardBatch(items)` | POST `/api/v1/dlq/discard-batch` | `DlqController.DiscardBatch` | PortalAdminWrite |

---

## What Works Well

- Legal entity scoping via `PortalAccessResolver` + `ForPortal<T>()` is consistent and secure
- Cursor-based pagination backend implementation is correct (keyset paging with tie-breaking on Id)
- Batch discard requires confirmation dialog with minimum reason length
- `switchMap` on `load$` properly cancels stale requests
- Feedback banner with auto-dismiss for success, persistent for errors
- `takeUntilDestroyed` prevents subscription leaks
- Role-gated batch actions match `PortalAdminWrite` backend policy
