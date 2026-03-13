import { FccVendor } from './transaction.model';

// ── Enums ─────────────────────────────────────────────────────────────────────

export enum ConnectivityState {
  FULLY_ONLINE = 'FULLY_ONLINE',
  INTERNET_DOWN = 'INTERNET_DOWN',
  FCC_UNREACHABLE = 'FCC_UNREACHABLE',
  FULLY_OFFLINE = 'FULLY_OFFLINE',
}

export enum AgentRegistrationStatus {
  ACTIVE = 'ACTIVE',
  DEACTIVATED = 'DEACTIVATED',
}

// ── Registration ──────────────────────────────────────────────────────────────

export interface AgentRegistration {
  id: string;
  deviceId: string;
  siteCode: string;
  legalEntityId: string;
  deviceSerialNumber: string;
  deviceModel: string;
  osVersion: string;
  agentVersion: string;
  status: AgentRegistrationStatus;
  registeredAt: string;
  lastSeenAt: string | null;
}

export interface DeviceRegistrationRequest {
  provisioningToken: string;
  siteCode: string;
  deviceSerialNumber: string;
  deviceModel: string;
  osVersion: string;
  agentVersion: string;
  replacePreviousAgent?: boolean;
}

export interface DeviceRegistrationResponse {
  deviceId: string;
  deviceToken: string;
  tokenExpiresAt: string;
  siteCode: string;
  legalEntityId: string;
  siteConfig: import('./site.model').SiteConfig;
  registeredAt: string;
}

// ── Telemetry sub-types ───────────────────────────────────────────────────────

export interface DeviceStatus {
  batteryPercent: number;
  isCharging: boolean;
  storageFreeMb: number;
  storageTotalMb: number;
  memoryFreeMb: number;
  memoryTotalMb: number;
  appVersion: string;
  appUptimeSeconds: number;
  osVersion: string;
  deviceModel: string;
}

export interface FccHealthStatus {
  isReachable: boolean;
  lastHeartbeatAtUtc: string | null;
  heartbeatAgeSeconds: number | null;
  fccVendor: FccVendor;
  fccHost: string;
  fccPort: number;
  consecutiveHeartbeatFailures: number;
}

export interface BufferStatus {
  totalRecords: number;
  pendingUploadCount: number;
  syncedCount: number;
  syncedToOdooCount: number;
  failedCount: number;
  oldestPendingAtUtc: string | null;
  bufferSizeMb: number;
}

export interface SyncStatus {
  lastSyncAttemptUtc: string | null;
  lastSuccessfulSyncUtc: string | null;
  syncLagSeconds: number | null;
  lastStatusPollUtc: string | null;
  lastConfigPullUtc: string | null;
  configVersion: string | null;
  uploadBatchSize: number;
}

export interface ErrorCounts {
  fccConnectionErrors: number;
  cloudUploadErrors: number;
  cloudAuthErrors: number;
  localApiErrors: number;
  bufferWriteErrors: number;
  adapterNormalizationErrors: number;
  preAuthErrors: number;
}

// ── Telemetry payload ─────────────────────────────────────────────────────────

export interface AgentTelemetry {
  schemaVersion: '1.0';
  deviceId: string;
  siteCode: string;
  legalEntityId: string;
  reportedAtUtc: string;
  sequenceNumber: number;
  connectivityState: ConnectivityState;
  device: DeviceStatus;
  fccHealth: FccHealthStatus;
  buffer: BufferStatus;
  sync: SyncStatus;
  errorCounts: ErrorCounts;
}

// ── Aggregate health summary (portal dashboard / agent monitoring view) ───────

export interface AgentHealthSummary {
  deviceId: string;
  siteCode: string;
  siteName: string | null;
  legalEntityId: string;
  agentVersion: string;
  status: AgentRegistrationStatus;
  hasTelemetry: boolean;
  connectivityState: ConnectivityState | null;
  batteryPercent: number | null;
  isCharging: boolean | null;
  bufferDepth: number | null;
  syncLagSeconds: number | null;
  lastTelemetryAt: string | null;
  lastSeenAt: string | null;
}

// ── Audit events ──────────────────────────────────────────────────────────────

export interface AgentAuditEvent {
  id: string;
  deviceId: string;
  eventType: string;
  description: string;
  previousState: string | null;
  newState: string | null;
  occurredAtUtc: string;
  metadata: Record<string, unknown> | null;
}

// ── Version check ─────────────────────────────────────────────────────────────

export interface VersionCheckResponse {
  compatible: boolean;
  minimumVersion: string;
  updateUrl: string | null;
  agentVersion: string;
  latestVersion: string;
  updateRequired: boolean;
  updateAvailable: boolean;
  releaseNotes: string | null;
}
