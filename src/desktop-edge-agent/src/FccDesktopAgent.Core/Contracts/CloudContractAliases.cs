global using SiteConfig = FccMiddleware.Contracts.Config.SiteConfigResponse;
global using SiteConfigBuffer = FccMiddleware.Contracts.Config.BufferDto;
global using SiteConfigFcc = FccMiddleware.Contracts.Config.FccDto;
global using SiteConfigFiscalization = FccMiddleware.Contracts.Config.FiscalizationDto;
global using SiteConfigIdentity = FccMiddleware.Contracts.Config.IdentityDto;
global using SiteConfigLocalApi = FccMiddleware.Contracts.Config.LocalApiDto;
global using SiteConfigMappings = FccMiddleware.Contracts.Config.MappingsDto;
global using SiteConfigNozzleMapping = FccMiddleware.Contracts.Config.NozzleMappingDto;
global using SiteConfigPeerDirectoryEntry = FccMiddleware.Contracts.Config.PeerDirectoryEntryDto;
global using SiteConfigProductMapping = FccMiddleware.Contracts.Config.ProductMappingDto;
global using SiteConfigRollout = FccMiddleware.Contracts.Config.RolloutDto;
global using SiteConfigSecretEnvelope = FccMiddleware.Contracts.Config.SecretEnvelopeDto;
global using SiteConfigSite = FccMiddleware.Contracts.Config.SiteDto;
global using SiteConfigSiteHa = FccMiddleware.Contracts.Config.SiteHaDto;
global using SiteConfigSourceRevision = FccMiddleware.Contracts.Config.SourceRevisionDto;
global using SiteConfigSync = FccMiddleware.Contracts.Config.SyncDto;
global using SiteConfigTelemetry = FccMiddleware.Contracts.Config.TelemetryDto;

global using DeviceRegistrationRequest = FccMiddleware.Contracts.Registration.DeviceRegistrationApiRequest;
global using DeviceRegistrationResponse = FccMiddleware.Contracts.Registration.DeviceRegistrationApiResponse;
global using PeerApiRegistrationMetadata = FccMiddleware.Contracts.Registration.PeerApiRegistrationMetadata;
global using TokenRefreshResponse = FccMiddleware.Contracts.Registration.RefreshTokenResponse;
global using ErrorResponse = FccMiddleware.Contracts.Common.ErrorResponse;

global using UploadRequest = FccMiddleware.Contracts.Ingestion.UploadRequest;
global using UploadResponse = FccMiddleware.Contracts.Ingestion.UploadResponse;
global using UploadResultItem = FccMiddleware.Contracts.Ingestion.UploadRecordResult;
global using UploadTransactionRecord = FccMiddleware.Contracts.Ingestion.UploadTransactionRecord;

global using SyncedStatusResponse = FccMiddleware.Contracts.Transactions.SyncedStatusResponse;
global using VersionCheckApiResponse = FccMiddleware.Contracts.Agent.VersionCheckResponse;

global using EdgeCommandPollResponse = FccMiddleware.Contracts.AgentControl.EdgeCommandPollResponse;
global using EdgeCommandItem = FccMiddleware.Contracts.AgentControl.EdgeCommandPollItem;
global using CommandAckRequest = FccMiddleware.Contracts.AgentControl.CommandAckRequest;
global using CommandAckResponse = FccMiddleware.Contracts.AgentControl.CommandAckResponse;

global using TelemetryPayload = FccMiddleware.Contracts.Telemetry.SubmitTelemetryRequest;
global using TelemetryDeviceStatus = FccMiddleware.Contracts.Telemetry.SubmitTelemetryDeviceStatusRequest;
global using TelemetryFccHealth = FccMiddleware.Contracts.Telemetry.SubmitTelemetryFccHealthRequest;
global using TelemetryBufferStatus = FccMiddleware.Contracts.Telemetry.SubmitTelemetryBufferStatusRequest;
global using TelemetrySyncStatus = FccMiddleware.Contracts.Telemetry.SubmitTelemetrySyncStatusRequest;
global using TelemetryErrorCounts = FccMiddleware.Contracts.Telemetry.SubmitTelemetryErrorCountsRequest;

global using BnaReportBatchUpload = FccMiddleware.Contracts.SiteData.BnaReportBatchRequest;
global using BnaReportUploadItem = FccMiddleware.Contracts.SiteData.BnaReportItem;
global using PumpTotalsBatchUpload = FccMiddleware.Contracts.SiteData.PumpTotalsBatchRequest;
global using PumpTotalsUploadItem = FccMiddleware.Contracts.SiteData.PumpTotalsUploadItem;
global using PumpControlHistoryBatchUpload = FccMiddleware.Contracts.SiteData.PumpControlHistoryBatchRequest;
global using PumpControlHistoryUploadItem = FccMiddleware.Contracts.SiteData.PumpControlHistoryUploadItem;
global using PriceSnapshotBatchUpload = FccMiddleware.Contracts.SiteData.PriceSnapshotBatchRequest;
global using PriceSnapshotUploadItem = FccMiddleware.Contracts.SiteData.PriceSnapshotUploadItem;
global using SiteDataAcceptedResponse = FccMiddleware.Contracts.SiteData.SiteDataAcceptedResponse;
global using DiagnosticLogUploadRequest = FccMiddleware.Contracts.DiagnosticLogs.DiagnosticLogUploadRequest;

global using AgentCommandType = FccMiddleware.Domain.Enums.AgentCommandType;
global using AgentCommandStatus = FccMiddleware.Domain.Enums.AgentCommandStatus;
global using AgentCommandCompletionStatus = FccMiddleware.Domain.Enums.AgentCommandCompletionStatus;
