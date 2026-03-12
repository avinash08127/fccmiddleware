# FCC Vendor Support Matrix

This is the published runtime support matrix for FCC adapters after the DBIP-03 and DBIP-04 fixes.

| Runtime | DOMS | RADIX | PETRONITE | ADVATEC |
| --- | --- | --- | --- | --- |
| Cloud API | Supported | Supported | Supported | Rejected |
| Cloud Worker | Supported | Supported | Supported | Rejected |
| Desktop Edge Agent | Supported | Supported | Supported | Rejected |
| Android Edge Agent | Supported for `TCP` only | Supported | Supported | Rejected |

## Enforcement points

- Cloud API and Cloud Worker share the same adapter registration in `FccMiddleware.Infrastructure/Adapters/CloudFccAdapterFactoryRegistration.cs`.
- Portal FCC config updates reject unsupported cloud vendors in `FccMiddleware.Api/Controllers/SitesController.cs`.
- Desktop rejects unsupported site configs in `FccDesktopAgent.Core/Config/ConfigManager.cs` and resolves runtime adapter settings through `DesktopFccRuntimeConfiguration`.
- Android rejects unsupported vendor/protocol combinations in `edge/config/ConfigManager.kt` and `edge/adapter/common/FccAdapterFactory.kt`.

## Notes

- Android `DOMS` support is explicitly limited to the TCP/JPL adapter. `DOMS` over REST is rejected at config time and adapter-resolution time.
- Desktop DOMS TCP adapter creation now requires real `siteCode`, `legalEntityId`, `currency`, and `timezone` from site config. Placeholder defaults are no longer injected at runtime.
