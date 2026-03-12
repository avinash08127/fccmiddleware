# Mobile Pre-Deployment Checklist — Edge Agent (Android)

**Purpose:** Gate checklist that must be completed before any Edge Agent APK is promoted to production. Every item must be signed off by the responsible role.

---

## 1. Security & Credentials

- [ ] **Replace bootstrap certificate pin hashes** in `AppModule.kt` (lines ~63-70) with real SHA-256 hashes of the production cloud endpoint intermediate CA public keys.
  - Primary intermediate CA pin: replace `sha256/YLh1dUR9y6Kja30RrAn7JKnbQG/uEtLMkBgFF2Fuihg=`
  - Backup intermediate CA pin: replace `sha256/Vjs8r4z+80wjNcr1YKepWQboSIRi63WsWXhIMN+eWys=`
  - Generate with:
    ```bash
    openssl s_client -connect your-api:443 | openssl x509 -pubkey \
      | openssl pkey -pubin -outform der \
      | openssl dgst -sha256 -binary \
      | openssl enc -base64
    ```
  - Include both primary and backup intermediate CA pins so certificate rotation doesn't brick deployed devices.
- [ ] Verify HTTPS enforcement is active — `ProvisioningActivity.parseQrPayload()` rejects any `cu` field that does not start with `https://`.
- [ ] Confirm no sensitive fields (FCC credentials, tokens, customer TIN) appear in Logcat at any log level.
- [ ] Confirm Android Keystore is used for device JWT and refresh token storage (not SharedPreferences).
- [ ] Confirm EncryptedSharedPreferences is used for identity fields (deviceId, siteCode, cloudBaseUrl).
- [ ] Verify LAN API key authentication is enforced when `enableLanApi = true`.

## 2. Configuration & Adapters

- [ ] Confirm `FccAdapterFactory.IMPLEMENTED_VENDORS` includes only vendors with fully working adapter implementations.
- [ ] Verify that selecting an unimplemented vendor (e.g., DOMS) at runtime produces a clear `AdapterNotImplementedException` — not a crash with `NotImplementedError`.
- [ ] Confirm `ConfigPollWorker` applies exponential backoff on rejected configs (not just transport failures).
- [ ] Verify `ConfigManager` rejects configs with incompatible schema versions and provisioning-only field changes.

## 3. Build & Signing

- [ ] APK is signed with the production release keystore (not debug).
- [ ] `minSdkVersion` is 31 (Android 12+).
- [ ] `targetSdkVersion` matches the latest required by Google Play (or internal distribution policy).
- [ ] ProGuard / R8 rules preserve Ktor, Room, kotlinx-serialization, and Koin classes.
- [ ] BuildConfig fields (e.g., version name/code) are set correctly for this release.
- [ ] No `debuggable = true` in the release build variant.

## 4. Connectivity & Resilience

- [ ] Tested in all four connectivity states: `FULLY_ONLINE`, `INTERNET_DOWN`, `FCC_UNREACHABLE`, `FULLY_OFFLINE`.
- [ ] Verified offline buffering: transactions polled while internet is down are uploaded in chronological order when connectivity returns.
- [ ] Verified pre-auth works over LAN-only (no cloud dependency on the request path).
- [ ] Verified `GET /api/transactions` serves from local buffer (no live FCC dependency).
- [ ] Verified `GET /api/pump-status` uses stale fallback when FCC is unreachable.

## 5. Performance

- [ ] `POST /api/preauth` p95 local overhead ≤ 150 ms (before FCC call time).
- [ ] `GET /api/transactions` p95 for first page (limit ≤ 50) with 30k buffered records ≤ 150 ms.
- [ ] `GET /api/status` p95 ≤ 100 ms.
- [ ] Steady-state RSS ≤ 180 MB.
- [ ] Replay throughput ≥ 600 txn/min on stable internet.
- [ ] Battery drain over 8-hour shift ≤ 8% (CLOUD_DIRECT) / ≤ 12% (RELAY / BUFFER_ALWAYS).

## 6. Device & Environment

- [ ] Tested on Urovo i9100 HHT hardware (not just emulator).
- [ ] QR provisioning flow tested end-to-end on a factory-reset device.
- [ ] Foreground service notification displays correctly and persists across device sleep/wake.
- [ ] Boot receiver starts the service after device reboot.
- [ ] Verified behavior when Odoo POS is not running (local API returns appropriate status).

## 7. Observability

- [ ] Telemetry reporting delivers battery, storage, buffer depth, FCC heartbeat, and sync status to cloud.
- [ ] Audit log captures key state transitions (connectivity changes, config applies, decommission events).
- [ ] Version name is included in telemetry and registration requests for fleet tracking.

## 8. Rollback Plan

- [ ] Previous APK version is archived and available for immediate rollback.
- [ ] Cloud config is backward-compatible with the previous agent version (check `compatibility.minAgentVersion`).
- [ ] Rollback procedure is documented and tested.

---

**Sign-off:**

| Role | Name | Date | Status |
|------|------|------|--------|
| Mobile Engineer | | | |
| Security Lead | | | |
| QA Lead | | | |
| DevOps / Release Mgr | | | |
