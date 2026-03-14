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
  PENDING_APPROVAL = 'PENDING_APPROVAL',
  QUARANTINED = 'QUARANTINED',
  DEACTIVATED = 'DEACTIVATED',
}

export enum AgentDeviceClass {
  ANDROID = 'ANDROID',
  DESKTOP = 'DESKTOP',
}

export enum AgentRoleCapability {
  PRIMARY_ELIGIBLE = 'PRIMARY_ELIGIBLE',
  STANDBY_ONLY = 'STANDBY_ONLY',
  READ_ONLY = 'READ_ONLY',
}

export enum SiteHaRuntimeRole {
  PRIMARY = 'PRIMARY',
  STANDBY_HOT = 'STANDBY_HOT',
  RECOVERING = 'RECOVERING',
  READ_ONLY = 'READ_ONLY',
  OFFLINE = 'OFFLINE',
}

// ── Registration ──────────────────────────────────────────────────────────────

export interface AgentRegistration {
  id: string;
  deviceId: string;
  siteCode: string;
  legalEntityId: string;
  deviceClass: AgentDeviceClass;
  deviceSerialNumber: string;
  deviceModel: string;
  osVersion: string;
  agentVersion: string;
  roleCapability: AgentRoleCapability;
  priority: number;
  currentRole: SiteHaRuntimeRole | null;
  capabilities: string[];
  peerApiBaseUrl: string | null;
  peerApiAdvertisedHost: string | null;
  peerApiPort: number | null;
  peerApiTlsEnabled: boolean;
  leaderEpochSeen: number | null;
  lastReplicationLagSeconds: number | null;
  status: AgentRegistrationStatus;
  registeredAt: string;
  lastSeenAt: string | null;
  suspensionReasonCode: string | null;
  suspensionReason: string | null;
  replacementForDeviceId: string | null;
  approvalGrantedAt: string | null;
  approvalGrantedByActorDisplay: string | null;
}

export interface DeviceRegistrationRequest {
  provisioningToken: string;
  siteCode: string;
  deviceSerialNumber: string;
  deviceModel: string;
  osVersion: string;
  agentVersion: string;
  deviceClass?: AgentDeviceClass;
  roleCapability?: AgentRoleCapability | null;
  siteHaPriority?: number | null;
  capabilities?: string[];
  peerApi?: {
    baseUrl?: string | null;
    advertisedHost?: string | null;
    port?: number | null;
    tlsEnabled?: boolean;
  } | null;
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
  deviceClass: AgentDeviceClass;
  agentVersion: string;
  roleCapability: AgentRoleCapability;
  priority: number;
  currentRole: SiteHaRuntimeRole | null;
  isCurrentLeader: boolean;
  leaderEpoch: number;
  capabilities: string[];
  peerApiBaseUrl: string | null;
  status: AgentRegistrationStatus;
  hasTelemetry: boolean;
  connectivityState: ConnectivityState | null;
  batteryPercent: number | null;
  isCharging: boolean | null;
  bufferDepth: number | null;
  syncLagSeconds: number | null;
  lastReplicationLagSeconds: number | null;
  lastTelemetryAt: string | null;
  lastSeenAt: string | null;
  suspensionReasonCode: string | null;
  approvalGrantedAt: string | null;
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
