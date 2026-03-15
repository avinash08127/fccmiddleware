global using SiteConfig = FccMiddleware.Contracts.Config.SiteConfigResponse;
global using SiteConfigBuffer = FccMiddleware.Contracts.Config.BufferDto;
global using SiteConfigFcc = FccMiddleware.Contracts.Config.FccDto;
global using SiteConfigFiscalization = FccMiddleware.Contracts.Config.FiscalizationDto;
global using SiteConfigIdentity = FccMiddleware.Contracts.Config.IdentityDto;
global using SiteConfigSourceRevision = FccMiddleware.Contracts.Config.SourceRevisionDto;
global using SiteConfigLocalApi = FccMiddleware.Contracts.Config.LocalApiDto;
global using SiteConfigMappings = FccMiddleware.Contracts.Config.MappingsDto;
global using SiteConfigNozzleMapping = FccMiddleware.Contracts.Config.NozzleMappingDto;
global using SiteConfigProductMapping = FccMiddleware.Contracts.Config.ProductMappingDto;
global using SiteConfigRollout = FccMiddleware.Contracts.Config.RolloutDto;
global using SiteConfigSecretEnvelope = FccMiddleware.Contracts.Config.SecretEnvelopeDto;
global using SiteConfigSite = FccMiddleware.Contracts.Config.SiteDto;
global using SiteConfigSiteHa = FccMiddleware.Contracts.Config.SiteHaDto;
global using SiteConfigSync = FccMiddleware.Contracts.Config.SyncDto;
global using SiteConfigTelemetry = FccMiddleware.Contracts.Config.TelemetryDto;

global using DeviceRegistrationRequest = FccMiddleware.Contracts.Registration.DeviceRegistrationApiRequest;
global using DeviceRegistrationResponse = FccMiddleware.Contracts.Registration.DeviceRegistrationApiResponse;
global using TokenRefreshResponse = FccMiddleware.Contracts.Registration.RefreshTokenResponse;

global using UploadRequest = FccMiddleware.Contracts.Ingestion.UploadRequest;
global using UploadResponse = FccMiddleware.Contracts.Ingestion.UploadResponse;
global using UploadResultItem = FccMiddleware.Contracts.Ingestion.UploadRecordResult;
global using UploadTransactionRecord = FccMiddleware.Contracts.Ingestion.UploadTransactionRecord;

global using SyncedStatusResponse = FccMiddleware.Contracts.Transactions.SyncedStatusResponse;

global using EdgeCommandPollResponse = FccMiddleware.Contracts.AgentControl.EdgeCommandPollResponse;
global using EdgeCommandItem = FccMiddleware.Contracts.AgentControl.EdgeCommandPollItem;
global using CommandAckRequest = FccMiddleware.Contracts.AgentControl.CommandAckRequest;
global using CommandAckResponse = FccMiddleware.Contracts.AgentControl.CommandAckResponse;

global using TelemetryPayload = FccMiddleware.Contracts.Telemetry.SubmitTelemetryRequest;

global using ErrorResponse = FccMiddleware.Contracts.Common.ErrorResponse;
global using AgentCommandType = FccMiddleware.Domain.Enums.AgentCommandType;
global using AgentCommandStatus = FccMiddleware.Domain.Enums.AgentCommandStatus;
global using CloudConnectivityState = FccMiddleware.Domain.Enums.ConnectivityState;
global using CloudFccVendor = FccMiddleware.Domain.Enums.FccVendor;
global using FccDesktopAgent.Core.Tests;
