# Urovo FCM Viability Spike

## 1. Output Location
- Target file path: `docs/specs/testing/tier-1-6-urovo-fcm-viability-spike.md`
- Related spike package: `src/edge-agent/spikes/urovo-fcm-viability`
- Result worksheet: `src/edge-agent/spikes/urovo-fcm-viability/RESULTS.md`

## 2. Scope
- Plan item addressed: `AC-0.1: Urovo FCM Viability Spike`
- Goal: determine whether the production Urovo i9100 image used with Odoo POS can reliably issue Firebase registration tokens and receive data-only FCM wake-up hints.

## 3. Current Repo Status
- Assessment date: `2026-03-14`
- Current Android edge-agent app does not yet include Firebase Messaging dependencies or an `FirebaseMessagingService`.
- No repository evidence currently proves that the production Urovo i9100 image has:
  - Google Play Services installed
  - Play Services in a compatible version/state
  - reliable background data-message delivery after process death or reboot

## 4. Rollout Posture
- Current decision on `2026-03-14`: `best-effort acceleration with polling fallback`
- Rationale:
  - physical-device validation is still pending
  - polling already exists and must remain correct even if FCM is unavailable or unreliable
  - no correctness path may depend on FCM delivery

## 5. Spike Deliverables
- Standalone Android spike project in `src/edge-agent/spikes/urovo-fcm-viability`
- Device worksheet in `src/edge-agent/spikes/urovo-fcm-viability/RESULTS.md`
- Test coverage target:
  - token issuance
  - foreground data-only delivery
  - background data-only delivery
  - delivery after process death
  - behavior after reboot
  - wake-up latency
  - battery impact

## 6. Execution Checklist

### 6.1 Device Inventory
- Record:
  - device serial number
  - Android version
  - build fingerprint
  - Urovo image version
  - Odoo POS build in field
- Confirm package presence and versions:
  - `com.google.android.gms`
  - `com.android.vending`

### 6.2 Token Issuance
- Launch the spike app.
- Record whether `FirebaseMessaging.getToken()` succeeds.
- If it fails, capture:
  - exception class
  - message
  - whether Google Play Services is missing, disabled, or outdated

### 6.3 Delivery Matrix
- Run each case with data-only messages and record `sentAtEpochMs` in the payload:
  - app in foreground
  - app in background
  - app force-stopped / process killed
  - device rebooted
- Measure:
  - message receipt success/failure
  - observed latency in milliseconds
  - whether the app/service process was started for delivery

### 6.4 Battery / Wake Cost
- Run an 8-hour idle shift with normal agent services disabled and the spike installed.
- Compare:
  - baseline device battery drain without spike traffic
  - drain with periodic high-priority data-only hints
- Record whether Doze/App Standby materially suppresses delivery.

## 7. Acceptance / Decision Rule
- Mark FCM as supported only if all of the following are true on the production image:
  - token issuance works repeatedly
  - foreground and background data-only messages are delivered reliably enough to improve operator latency
  - delivery after process death/reboot is understood and acceptable
  - battery impact is within the Android pre-deployment budget
- If any item fails or remains inconclusive, keep the rollout posture as `best-effort acceleration with polling fallback`.

## 8. External Setup References
- Firebase Android setup: `https://firebase.google.com/docs/android/setup`
- Firebase Cloud Messaging for Android: `https://firebase.google.com/docs/cloud-messaging/android/client`

## 9. Open Questions
- Physical-device execution is still required before this document can claim `required + supported`.
- Until `RESULTS.md` is filled from a real Urovo i9100 run, this is a prepared spike plan and not a completed hardware validation.
