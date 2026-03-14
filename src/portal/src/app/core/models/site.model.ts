import { FccVendor } from './transaction.model';

// ── Tolerance & request types ──────────────────────────────────────────────────

export interface ToleranceConfig {
  amountTolerancePct: number;
  amountToleranceAbsoluteMinorUnits: number;
  timeWindowMinutes: number;
}

export interface UpdateSiteRequest {
  connectivityMode?: ConnectivityMode;
  operatingModel?: SiteOperatingModel;
  siteUsesPreAuth?: boolean;
  tolerance?: ToleranceConfig;
  fiscalization?: {
    mode?: FiscalizationMode;
    taxAuthorityEndpoint?: string | null;
    requireCustomerTaxId?: boolean;
    fiscalReceiptRequired?: boolean;
  };
}

export interface AddNozzleRequest {
  nozzleNumber: number;
  fccNozzleNumber: number;
  canonicalProductCode: string;
}

export interface AddPumpRequest {
  pumpNumber: number;
  fccPumpNumber: number;
  nozzles: AddNozzleRequest[];
}

export interface UpdateNozzleRequest {
  canonicalProductCode: string;
}

// ── Enums ─────────────────────────────────────────────────────────────────────

export enum SiteOperatingModel {
  COCO = 'COCO',
  CODO = 'CODO',
  DODO = 'DODO',
  DOCO = 'DOCO',
}

export enum ConnectivityMode {
  CONNECTED = 'CONNECTED',
  DISCONNECTED = 'DISCONNECTED',
}

export enum IngestionMode {
  CLOUD_DIRECT = 'CLOUD_DIRECT',
  RELAY = 'RELAY',
  BUFFER_ALWAYS = 'BUFFER_ALWAYS',
}

export enum TransactionMode {
  PULL = 'PULL',
  PUSH = 'PUSH',
  HYBRID = 'HYBRID',
}

export enum FccConnectionProtocol {
  REST = 'REST',
  TCP = 'TCP',
  SOAP = 'SOAP',
}

export enum FiscalizationMode {
  FCC_DIRECT = 'FCC_DIRECT',
  EXTERNAL_INTEGRATION = 'EXTERNAL_INTEGRATION',
  NONE = 'NONE',
}

export enum CursorStrategy {
  FCC_TRANSACTION_ID = 'FCC_TRANSACTION_ID',
  END_TIMESTAMP = 'END_TIMESTAMP',
  VENDOR_OFFSET = 'VENDOR_OFFSET',
}

export enum LogLevel {
  TRACE = 'TRACE',
  DEBUG = 'DEBUG',
  INFO = 'INFO',
  WARN = 'WARN',
  ERROR = 'ERROR',
}

export enum VolumeUnit {
  LITRES = 'LITRES',
  MILLILITRES = 'MILLILITRES',
}

export enum SecretEnvelopeFormat {
  NONE = 'NONE',
  JWE_BASE64 = 'JWE_BASE64',
}

export enum PeerDiscoveryMode {
  CLOUD_DIRECTORY = 'CLOUD_DIRECTORY',
  LAN_BROADCAST = 'LAN_BROADCAST',
  HYBRID = 'HYBRID',
}

// ── Site (from master-data sync schema) ──────────────────────────────────────

export interface Site {
  id: string;
  siteCode: string;
  legalEntityId: string;
  siteName: string;
  operatingModel: SiteOperatingModel;
  connectivityMode: ConnectivityMode | null;
  ingestionMode: IngestionMode | null;
  fccVendor: FccVendor | null;
  timezone: string | null;
  isActive: boolean;
  siteUsesPreAuth: boolean;
  updatedAt: string | null;
}

// ── SiteDetail — richer type returned by GET /api/v1/sites/{id} ──────────────

export interface SiteDetail extends Site {
  operatorName: string | null;
  fcc: FccConfig | null;
  fiscalization: SiteConfigFiscalization | null;
  tolerance: ToleranceConfig | null;
  pumps: Pump[];
}

// ── Pump / nozzle ─────────────────────────────────────────────────────────────

export interface Nozzle {
  nozzleNumber: number;
  canonicalProductCode: string;
  odooPumpId: string | null;
}

export interface Pump {
  id: string;
  siteCode: string;
  pumpNumber: number;
  nozzles: Nozzle[];
  isActive: boolean;
  updatedAt: string | null;
}

// ── Product ───────────────────────────────────────────────────────────────────

export interface Product {
  id: string;
  canonicalCode: string;
  displayName: string;
  isActive: boolean;
  updatedAt: string | null;
}

// ── Operator ──────────────────────────────────────────────────────────────────

export interface Operator {
  id: string;
  legalEntityId: string;
  name: string;
  taxPayerId: string | null;
  isActive: boolean;
  updatedAt: string | null;
}

// ── FCC configuration (part of SiteConfig) ───────────────────────────────────

export interface SecretEnvelope {
  format: SecretEnvelopeFormat;
  payload: string | null;
}

export interface FccConfig {
  enabled: boolean;
  fccId: string | null;
  vendor: FccVendor | null;
  model: string | null;
  version: string | null;
  connectionProtocol: FccConnectionProtocol | null;
  hostAddress: string | null;
  port: number | null;
  credentialRef: string | null;
  credentialRevision: number | null;
  secretEnvelope: SecretEnvelope;
  transactionMode: TransactionMode | null;
  ingestionMode: IngestionMode | null;
  pullIntervalSeconds: number | null;
  catchUpPullIntervalSeconds: number | null;
  hybridCatchUpIntervalSeconds: number | null;
  heartbeatIntervalSeconds: number;
  heartbeatTimeoutSeconds: number;
  pushSourceIpAllowList: string[];

  // ── DOMS TCP/JPL fields ──────────────────────────────────────────────────
  jplPort: number | null;
  fcAccessCode: string | null;
  domsCountryCode: string | null;
  posVersionId: string | null;
  configuredPumps: string | null;

  // ── Radix fields ─────────────────────────────────────────────────────────
  sharedSecret: string | null;
  usnCode: number | null;
  authPort: number | null;
  fccPumpAddressMap: string | null;

  // ── Petronite OAuth2 fields ──────────────────────────────────────────────
  clientId: string | null;
  clientSecret: string | null;
  webhookSecret: string | null;
  oauthTokenEndpoint: string | null;

  // ── Advatec TRA fiscal device fields ──────────────────────────────────
  advatecDevicePort: number | null;
  advatecWebhookListenerPort: number | null;
  advatecWebhookToken: string | null;
  advatecEfdSerialNumber: string | null;
  advatecCustIdType: number | null;
  advatecPumpMap: string | null;
}

// ── SiteConfig sub-types ──────────────────────────────────────────────────────

export interface SiteConfigIdentity {
  legalEntityId: string;
  legalEntityCode: string;
  siteId: string;
  siteCode: string;
  siteName: string;
  timezone: string;
  currencyCode: string;
  deviceId: string;
  deviceClass: import('./agent.model').AgentDeviceClass;
  isPrimaryAgent: boolean;
}

export interface SiteConfigSite {
  isActive: boolean;
  operatingModel: SiteOperatingModel;
  connectivityMode: ConnectivityMode;
  odooSiteId: string;
  companyTaxPayerId: string;
  operatorName: string | null;
  operatorTaxPayerId: string | null;
}

export interface SiteConfigSync {
  cloudBaseUrl: string;
  uploadBatchSize: number;
  uploadIntervalSeconds: number;
  syncedStatusPollIntervalSeconds: number;
  configPollIntervalSeconds: number;
  cursorStrategy: CursorStrategy;
  maxReplayBackoffSeconds: number;
  initialReplayBackoffSeconds: number;
  maxRecordsPerUploadWindow: number;
  environment?: string;
}

export interface SiteConfigBuffer {
  retentionDays: number;
  stalePendingDays: number;
  maxRecords: number;
  cleanupIntervalHours: number;
  persistRawPayloads: boolean;
}

export interface SiteConfigLocalApi {
  localhostPort: number;
  enableLanApi: boolean;
  lanBindAddress: string | null;
  lanAllowCidrs: string[];
  lanApiKeyRef: string | null;
  rateLimitPerMinute: number;
}

export interface SiteConfigPeerDirectoryEntry {
  agentId: string;
  deviceClass: import('./agent.model').AgentDeviceClass;
  status: import('./agent.model').AgentRegistrationStatus;
  roleCapability: import('./agent.model').AgentRoleCapability;
  priority: number;
  currentRole: import('./agent.model').SiteHaRuntimeRole;
  peerApiBaseUrl: string | null;
  peerApiAdvertisedHost: string | null;
  peerApiPort: number | null;
  peerApiTlsEnabled: boolean;
  capabilities: string[];
  appVersion: string | null;
  lastHeartbeatUtc: string | null;
  leaderEpochSeen: number | null;
  lastReplicationLagSeconds: number | null;
}

export interface SiteConfigSiteHa {
  enabled: boolean;
  autoFailoverEnabled: boolean;
  priority: number;
  roleCapability: import('./agent.model').AgentRoleCapability;
  currentRole: import('./agent.model').SiteHaRuntimeRole;
  heartbeatIntervalSeconds: number;
  failoverTimeoutSeconds: number;
  maxReplicationLagSeconds: number;
  peerDiscoveryMode: PeerDiscoveryMode;
  allowFailback: boolean;
  leaderAgentId: string | null;
  leaderEpoch: number;
  leaderSinceUtc: string | null;
  peerDirectory: SiteConfigPeerDirectoryEntry[];
}

export interface SiteConfigTelemetry {
  telemetryIntervalSeconds: number;
  logLevel: LogLevel;
  includeDiagnosticsLogs: boolean;
  metricsWindowSeconds: number;
}

export interface SiteConfigFiscalization {
  mode: FiscalizationMode;
  taxAuthorityEndpoint: string | null;
  requireCustomerTaxId: boolean;
  fiscalReceiptRequired: boolean;
}

export interface ProductMapping {
  fccProductCode: string;
  canonicalProductCode: string;
  displayName: string;
  active: boolean;
}

export interface NozzleMapping {
  pumpNozzleId: string;
  pumpNumber: number;
  nozzleNumber: number;
  canonicalProductCode: string;
  odooPumpId: string | null;
  active: boolean;
}

export interface SiteConfigMappings {
  pumpNumberOffset: number;
  priceDecimalPlaces: number;
  volumeUnit: VolumeUnit;
  products: ProductMapping[];
  nozzles: NozzleMapping[];
}

export interface SiteConfigRollout {
  minAgentVersion: string;
  maxAgentVersion: string | null;
  requiresRestartSections: string[];
  configTtlHours: number;
}

export interface SiteConfigSourceRevision {
  databricksSyncAtUtc: string | null;
  siteMasterRevision: string | null;
  fccConfigRevision: string | null;
  portalChangeId: string | null;
}

// ── Full SiteConfig ───────────────────────────────────────────────────────────

export interface SiteConfig {
  schemaVersion: '1.0';
  configVersion: number;
  configId: string;
  issuedAtUtc: string;
  effectiveAtUtc: string;
  sourceRevision: SiteConfigSourceRevision;
  identity: SiteConfigIdentity;
  site: SiteConfigSite;
  fcc: FccConfig;
  sync: SiteConfigSync;
  buffer: SiteConfigBuffer;
  localApi: SiteConfigLocalApi;
  siteHa: SiteConfigSiteHa;
  telemetry: SiteConfigTelemetry;
  fiscalization: SiteConfigFiscalization;
  mappings: SiteConfigMappings;
  rollout: SiteConfigRollout;
}
