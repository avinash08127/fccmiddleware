import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

export type SimulatedAuthMode = 'None' | 'ApiKey' | 'BasicAuth';
export type TransactionDeliveryMode = 'Push' | 'Pull' | 'Hybrid';
export type PreAuthFlowMode = 'CreateOnly' | 'CreateThenAuthorize';
export type NozzleState = 'Idle' | 'Lifted' | 'Authorized' | 'Dispensing' | 'Hung' | 'Faulted';
export type CallbackAttemptStatus = 'Pending' | 'Succeeded' | 'Failed' | 'Cancelled' | 'InProgress';
export type SimulatedTransactionStatus =
  | 'Created'
  | 'ReadyForDelivery'
  | 'Delivered'
  | 'Acknowledged'
  | 'Failed';

export interface ManagementValidationMessage {
  path: string;
  message: string;
  severity: string;
  code: string;
}

export interface ManagementErrorResponse {
  message: string;
  errors: ManagementValidationMessage[];
}

export interface LabEnvironmentSummary {
  id: string;
  key: string;
  name: string;
  description: string;
  lastSeededAtUtc: string | null;
}

export interface SiteFiscalizationSettings {
  mode: string;
  requireCustomerTaxId: boolean;
  fiscalReceiptRequired: boolean;
  taxAuthorityName: string;
  taxAuthorityEndpoint: string;
}

export interface SiteSettings {
  isTemplate: boolean;
  defaultCallbackTargetKey: string;
  pullPageSize: number;
  fiscalization: SiteFiscalizationSettings;
}

export interface FccProfileSummary {
  id: string;
  profileKey: string;
  name: string;
  vendorFamily: string;
  authMode: SimulatedAuthMode;
  deliveryMode: TransactionDeliveryMode;
  preAuthMode: PreAuthFlowMode;
  isActive: boolean;
  isDefault: boolean;
}

export interface SiteForecourtSummary {
  pumpCount: number;
  nozzleCount: number;
  activePumpCount: number;
  activeNozzleCount: number;
}

export interface SiteCompatibility {
  isValid: boolean;
  messages: ManagementValidationMessage[];
}

export interface CallbackTargetRecord {
  id: string;
  targetKey: string;
  name: string;
  callbackUrl: string;
  authMode: SimulatedAuthMode;
  apiKeyHeaderName: string;
  apiKeyValue: string;
  basicAuthUsername: string;
  basicAuthPassword: string;
  isActive: boolean;
}

export interface SiteListItem {
  id: string;
  labEnvironmentId: string;
  siteCode: string;
  name: string;
  timeZone: string;
  currencyCode: string;
  externalReference: string;
  isActive: boolean;
  inboundAuthMode: SimulatedAuthMode;
  apiKeyHeaderName: string;
  apiKeyValue: string;
  basicAuthUsername: string;
  basicAuthPassword: string;
  deliveryMode: TransactionDeliveryMode;
  preAuthMode: PreAuthFlowMode;
  settings: SiteSettings;
  activeProfile: FccProfileSummary;
  forecourt: SiteForecourtSummary;
  compatibility: SiteCompatibility;
}

export interface ForecourtNozzleView {
  id: string;
  productId: string;
  productCode: string;
  productName: string;
  nozzleNumber: number;
  fccNozzleNumber: number;
  label: string;
  state: NozzleState;
  isActive: boolean;
}

export interface ForecourtPumpView {
  id: string;
  pumpNumber: number;
  fccPumpNumber: number;
  layoutX: number;
  layoutY: number;
  label: string;
  isActive: boolean;
  nozzles: ForecourtNozzleView[];
}

export interface SiteForecourtView {
  siteId: string;
  siteCode: string;
  siteName: string;
  pumps: ForecourtPumpView[];
}

export interface ForecourtNozzleUpsertRequest {
  id?: string | null;
  productId: string;
  nozzleNumber: number;
  fccNozzleNumber: number;
  label: string;
  isActive: boolean;
}

export interface ForecourtPumpUpsertRequest {
  id?: string | null;
  pumpNumber: number;
  fccPumpNumber: number;
  layoutX?: number | null;
  layoutY?: number | null;
  label: string;
  isActive: boolean;
  nozzles: ForecourtNozzleUpsertRequest[];
}

export interface SaveForecourtRequest {
  pumps: ForecourtPumpUpsertRequest[];
}

export interface ProductView {
  id: string;
  labEnvironmentId: string;
  productCode: string;
  name: string;
  grade: string;
  colorHex: string;
  unitPrice: number;
  currencyCode: string;
  isActive: boolean;
  assignedNozzleCount: number;
}

export interface SiteDetail extends SiteListItem {
  forecourtConfiguration: SiteForecourtView;
  callbackTargets: CallbackTargetRecord[];
  availableProfiles: FccProfileSummary[];
}

export interface CallbackTargetUpsertRequest {
  id?: string | null;
  targetKey: string;
  name: string;
  callbackUrl: string;
  authMode: SimulatedAuthMode;
  apiKeyHeaderName: string;
  apiKeyValue: string;
  basicAuthUsername: string;
  basicAuthPassword: string;
  isActive: boolean;
}

export interface SiteUpsertRequest {
  labEnvironmentId: string;
  activeFccSimulatorProfileId: string;
  siteCode: string;
  name: string;
  timeZone: string;
  currencyCode: string;
  externalReference: string;
  inboundAuthMode: SimulatedAuthMode;
  apiKeyHeaderName: string;
  apiKeyValue: string;
  basicAuthUsername: string;
  basicAuthPassword: string;
  deliveryMode: TransactionDeliveryMode;
  preAuthMode: PreAuthFlowMode;
  isActive: boolean;
  settings: SiteSettings;
  callbackTargets?: CallbackTargetUpsertRequest[] | null;
}

export interface DuplicateSiteRequest {
  siteCode: string;
  name: string;
  externalReference: string;
  activeFccSimulatorProfileId?: string | null;
  copyForecourt: boolean;
  copyCallbackTargets: boolean;
  markAsTemplate: boolean;
  activate: boolean;
}

export interface SiteSeedResult {
  siteId: string;
  siteCode: string;
  resetApplied: boolean;
  transactionsRemoved: number;
  preAuthSessionsRemoved: number;
  callbackAttemptsRemoved: number;
  logsRemoved: number;
  transactionsCreated: number;
  preAuthSessionsCreated: number;
  callbackAttemptsCreated: number;
  nozzlesReset: number;
}

export interface FccEndpointDefinition {
  operation: string;
  method: string;
  pathTemplate: string;
  enabled: boolean;
  description: string;
}

export interface FccAuthConfiguration {
  mode: SimulatedAuthMode;
  apiKeyHeaderName: string;
  apiKeyValue: string;
  basicAuthUsername: string;
  basicAuthPassword: string;
}

export interface FccDeliveryCapabilities {
  supportsPush: boolean;
  supportsPull: boolean;
  supportsHybrid: boolean;
  supportsPreAuthCancellation: boolean;
}

export interface FccTemplateDefinition {
  operation: string;
  name: string;
  contentType: string;
  headers: Record<string, string>;
  bodyTemplate: string;
}

export interface FccValidationRuleDefinition {
  ruleKey: string;
  scope: string;
  expression: string;
  message: string;
  required: boolean;
  expectedType: string;
  minimum?: number | null;
  maximum?: number | null;
  pattern: string;
  allowedValues: string[];
}

export interface FccFieldMappingDefinition {
  scope: string;
  sourceField: string;
  targetField: string;
  direction: string;
  transform: string;
}

export interface FccFailureSimulationDefinition {
  simulatedDelayMs: number;
  enabled: boolean;
  failureRatePercent: number;
  httpStatusCode: number;
  errorCode: string;
  messageTemplate: string;
}

export interface FccExtensionPointDefinition {
  resolverKey: string;
  configuration: Record<string, string>;
}

export interface FccProfileContract {
  endpointSurface: FccEndpointDefinition[];
  auth: FccAuthConfiguration;
  capabilities: FccDeliveryCapabilities;
  preAuthMode: PreAuthFlowMode;
  requestTemplates: FccTemplateDefinition[];
  responseTemplates: FccTemplateDefinition[];
  validationRules: FccValidationRuleDefinition[];
  fieldMappings: FccFieldMappingDefinition[];
  failureSimulation: FccFailureSimulationDefinition;
  extensions: FccExtensionPointDefinition;
}

export interface FccProfileRecord {
  id?: string | null;
  labEnvironmentId: string;
  profileKey: string;
  name: string;
  vendorFamily: string;
  deliveryMode: TransactionDeliveryMode;
  isActive: boolean;
  isDefault: boolean;
  contract: FccProfileContract;
}

export interface FccProfileValidationMessage {
  path: string;
  message: string;
  severity: string;
}

export interface FccProfileValidationResult {
  isValid: boolean;
  messages: FccProfileValidationMessage[];
}

export interface FccProfilePreviewRequest {
  profileId?: string | null;
  draft?: FccProfileRecord | null;
  operation: string;
  sampleValues?: Record<string, string> | null;
}

export interface FccProfilePreviewResult {
  operation: string;
  requestBody: string;
  requestHeaders: Record<string, string>;
  responseBody: string;
  responseHeaders: Record<string, string>;
  sampleValues: Record<string, string>;
}

export interface DashboardTransactionItem {
  id: string;
  siteCode: string;
  correlationId: string;
  externalTransactionId: string;
  status: SimulatedTransactionStatus;
  deliveryMode: TransactionDeliveryMode;
  pumpNumber: number;
  nozzleNumber: number;
  productCode: string;
  volume: number;
  totalAmount: number;
  occurredAtUtc: string;
}

export interface DashboardAuthFailureItem {
  id: string;
  siteCode: string | null;
  eventType: string;
  message: string;
  correlationId: string;
  occurredAtUtc: string;
}

export interface DashboardCallbackAttemptItem {
  id: string;
  siteCode: string;
  targetKey: string;
  correlationId: string;
  attemptNumber: number;
  status: CallbackAttemptStatus;
  responseStatusCode: number;
  errorMessage: string;
  attemptedAtUtc: string;
  nextRetryAtUtc: string | null;
}

export interface DashboardAlertItem {
  id: string;
  siteCode: string | null;
  category: string;
  eventType: string;
  severity: string;
  message: string;
  correlationId: string;
  occurredAtUtc: string;
}

export interface DashboardSummary {
  refreshedAtUtc: string;
  profileName: string;
  seedTargets: {
    sites: number;
    pumps: number;
    nozzles: number;
    transactions: number;
  };
  sites: SiteListItem[];
  activeTransactions: {
    total: number;
    items: DashboardTransactionItem[];
  };
  authFailures: {
    last24Hours: number;
    items: DashboardAuthFailureItem[];
  };
  callbackDelivery: {
    succeededLast24Hours: number;
    failedLast24Hours: number;
    pending: number;
    successRatePercent: number;
    items: DashboardCallbackAttemptItem[];
  };
  recentAlerts: DashboardAlertItem[];
}

export interface LatencySummary {
  profileName: string;
  replaySignature: string;
  thresholds: {
    startupReadyMinutes: number;
    dashboardLoadP95Ms: number;
    signalRUpdateP95Ms: number;
    fccEmulatorP95Ms: number;
    transactionPullP95Ms: number;
  };
  measurements: {
    dashboardQueryP95Ms: number;
    siteLoadP95Ms: number;
    signalRBroadcastP95Ms: number;
    fccHealthP95Ms: number;
    transactionPullP95Ms: number;
    sampleCount: number;
    apiP95ByRoute: Record<string, number>;
  };
}

export interface LogRecord {
  id: string;
  siteId: string | null;
  siteCode: string | null;
  profileId: string | null;
  profileKey: string | null;
  simulatedTransactionId: string | null;
  preAuthSessionId: string | null;
  category: string;
  eventType: string;
  severity: string;
  message: string;
  correlationId: string;
  occurredAtUtc: string;
}

export interface LogDetailRecord extends LogRecord {
  profileName: string | null;
  externalTransactionId: string | null;
  rawPayloadJson: string;
  canonicalPayloadJson: string;
  metadataJson: string;
  requestHeadersJson: string | null;
  requestPayloadJson: string | null;
  responseHeadersJson: string | null;
  responsePayloadJson: string | null;
}

export interface PayloadContractValidationIssue {
  code: string;
  severity: string;
  payloadKind: string;
  path: string;
  message: string;
}

export interface PayloadFieldComparison {
  scope: string;
  sourceField: string;
  targetField: string;
  status: string;
  sourceValue: string | null;
  targetValue: string | null;
  transform: string;
  message: string;
}

export interface PayloadContractValidationReport {
  scope: string;
  enabled: boolean;
  outcome: string;
  errorCount: number;
  warningCount: number;
  matchedCount: number;
  missingCount: number;
  mismatchCount: number;
  issues: PayloadContractValidationIssue[];
  comparisons: PayloadFieldComparison[];
}

export interface TransactionRecord {
  id: string;
  siteId: string;
  externalTransactionId: string;
  correlationId: string;
  siteCode: string;
  siteName: string;
  profileId: string;
  profileKey: string;
  pumpNumber: number;
  nozzleNumber: number;
  productCode: string;
  productName: string;
  deliveryMode: TransactionDeliveryMode;
  status: SimulatedTransactionStatus;
  volume: number;
  totalAmount: number;
  occurredAtUtc: string;
  deliveredAtUtc: string | null;
  preAuthSessionId: string | null;
  callbackAttemptCount: number;
  lastCallbackStatus: string | null;
  rawPayloadJson: string;
  canonicalPayloadJson: string;
  contractValidation: PayloadContractValidationReport;
  metadataJson: string;
  timelineJson: string;
}

export interface TransactionCallbackAttemptRecord {
  id: string;
  callbackTargetId: string;
  targetKey: string;
  targetName: string;
  correlationId: string;
  attemptNumber: number;
  status: string;
  responseStatusCode: number;
  requestUrl: string;
  requestHeadersJson: string;
  requestPayloadJson: string;
  responseHeadersJson: string;
  responsePayloadJson: string;
  errorMessage: string;
  retryCount: number;
  maxRetryCount: number;
  attemptedAtUtc: string;
  completedAtUtc: string | null;
  nextRetryAtUtc: string | null;
  acknowledgedAtUtc: string | null;
}

export interface TransactionTimelineEntryRecord {
  source: string;
  occurredAtUtc: string;
  category: string;
  eventType: string;
  severity: string;
  state: string;
  message: string;
  metadataJson: string;
}

export interface TransactionDetailRecord extends TransactionRecord {
  profileName: string;
  unitPrice: number;
  currencyCode: string;
  createdAtUtc: string;
  rawHeadersJson: string;
  rawPayloadJson: string;
  canonicalPayloadJson: string;
  contractValidation: PayloadContractValidationReport;
  metadataJson: string;
  callbackAttempts: TransactionCallbackAttemptRecord[];
  timeline: TransactionTimelineEntryRecord[];
}

export interface TransactionReplayResult {
  transactionId: string;
  externalTransactionId: string;
  correlationId: string;
  message: string;
}

export type ScenarioRunStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';

export interface CallbackHistoryRecord {
  id: string;
  callbackTargetId: string;
  siteId: string | null;
  profileId: string | null;
  simulatedTransactionId: string | null;
  targetKey: string;
  targetName: string;
  correlationId: string;
  externalTransactionId: string | null;
  authOutcome: string;
  authMode: string;
  httpMethod: string;
  requestUrl: string;
  requestHeadersJson: string;
  requestPayloadJson: string;
  responseStatusCode: number;
  responseHeadersJson: string;
  responsePayloadJson: string;
  correlationMetadataJson: string;
  isReplay: boolean;
  replayedFromId: string | null;
  capturedAtUtc: string;
}

export interface CallbackReplayRecord {
  captureId: string;
  targetKey: string;
  correlationId: string;
  message: string;
}

export interface ScenarioScriptSetupRecord {
  resetNozzles: boolean;
  clearActivePreAuth: boolean;
  profileKey: string | null;
  deliveryMode: TransactionDeliveryMode | null;
  preAuthMode: PreAuthFlowMode | null;
}

export interface ScenarioActionRecord {
  kind: string;
  name: string;
  correlationAlias: string | null;
  correlationId: string | null;
  pumpNumber: number | null;
  nozzleNumber: number | null;
  action: string | null;
  amount: number | null;
  targetAmount: number | null;
  targetVolume: number | null;
  flowRateLitresPerMinute: number | null;
  elapsedSeconds: number | null;
  expiresInSeconds: number | null;
  delayMs: number | null;
  limit: number | null;
  injectDuplicate: boolean;
  simulateFailure: boolean;
  clearFault: boolean;
  failureMessage: string | null;
  failureCode: string | null;
  failureStatusCode: number | null;
  targetKey: string | null;
  customerName: string | null;
  customerTaxId: string | null;
  customerTaxOffice: string | null;
}

export interface ScenarioAssertionRecord {
  kind: string;
  name: string;
  correlationAlias: string | null;
  targetKey: string | null;
  expectedStatus: string | null;
  category: string | null;
  eventType: string | null;
  expectedCount: number | null;
  minimumCount: number | null;
  isReplay: boolean | null;
}

export interface ScenarioScriptRecord {
  version: number;
  siteCode: string;
  setup: ScenarioScriptSetupRecord;
  actions: ScenarioActionRecord[];
  assertions: ScenarioAssertionRecord[];
}

export interface ScenarioRunSummaryRecord {
  id: string;
  siteId: string;
  scenarioDefinitionId: string;
  scenarioKey: string;
  scenarioName: string;
  siteCode: string;
  correlationId: string;
  replaySeed: number;
  replaySignature: string;
  status: ScenarioRunStatus;
  startedAtUtc: string;
  completedAtUtc: string | null;
  stepCount: number;
  assertionCount: number;
  errorCount: number;
}

export interface ScenarioDefinitionRecord {
  id: string;
  labEnvironmentId: string;
  scenarioKey: string;
  name: string;
  description: string;
  deterministicSeed: number;
  replaySignature: string;
  isActive: boolean;
  script: ScenarioScriptRecord;
  latestRun: ScenarioRunSummaryRecord | null;
}

export interface ScenarioStepResultRecord {
  order: number;
  kind: string;
  name: string;
  status: string;
  correlationId: string;
  message: string;
  outputJson: string;
  startedAtUtc: string;
  completedAtUtc: string;
}

export interface ScenarioAssertionResultRecord {
  order: number;
  kind: string;
  name: string;
  passed: boolean;
  message: string;
  outputJson: string;
}

export interface ScenarioRunDetailRecord {
  id: string;
  siteId: string;
  scenarioDefinitionId: string;
  scenarioKey: string;
  scenarioName: string;
  siteCode: string;
  correlationId: string;
  replaySeed: number;
  replaySignature: string;
  status: ScenarioRunStatus;
  inputSnapshotJson: string;
  resultSummaryJson: string;
  startedAtUtc: string;
  completedAtUtc: string | null;
  steps: ScenarioStepResultRecord[];
  assertions: ScenarioAssertionResultRecord[];
}

export interface ScenarioListResponse {
  definitions: ScenarioDefinitionRecord[];
  runs: ScenarioRunSummaryRecord[];
}

export interface ScenarioImportRecord {
  scenarioKey: string;
  name: string;
  description: string;
  deterministicSeed: number;
  isActive: boolean;
  script: ScenarioScriptRecord;
}

export interface ScenarioImportResult {
  importedCount: number;
  updatedCount: number;
  createdCount: number;
  skippedCount: number;
  definitions: ScenarioDefinitionRecord[];
}

export interface PushTransactionAttemptSummary {
  externalTransactionId: string;
  correlationId: string;
  targetKey: string;
  status: string;
  duplicateInjected: boolean;
  attemptNumber: number;
  retryCount: number;
  responseStatusCode: number;
  acknowledged: boolean;
  nextRetryAtUtc: string | null;
}

export interface PushTransactionsResult {
  statusCode: number;
  message: string;
  pushedCount: number;
  attempts: PushTransactionAttemptSummary[];
}

export interface PreAuthSessionRecord {
  id: string;
  siteCode: string;
  profileKey: string;
  correlationId: string;
  externalReference: string;
  mode: string;
  status: string;
  reservedAmount: number;
  authorizedAmount: number | null;
  finalAmount: number | null;
  finalVolume: number | null;
  createdAtUtc: string;
  authorizedAtUtc: string | null;
  completedAtUtc: string | null;
  expiresAtUtc: string | null;
  rawRequestJson: string;
  canonicalRequestJson: string;
  requestValidation: PayloadContractValidationReport;
  rawResponseJson: string;
  canonicalResponseJson: string;
  responseValidation: PayloadContractValidationReport;
  timelineJson: string;
}

export interface NozzleSimulationSnapshot {
  siteId: string;
  pumpId: string;
  nozzleId: string;
  siteCode: string;
  pumpNumber: number;
  nozzleNumber: number;
  label: string;
  state: NozzleState;
  productCode: string;
  productName: string;
  unitPrice: number;
  currencyCode: string;
  correlationId: string;
  preAuthSessionId: string | null;
  simulationStateJson: string;
  updatedAtUtc: string;
}

export interface TransactionSimulationSummary {
  id: string;
  externalTransactionId: string;
  correlationId: string;
  deliveryMode: TransactionDeliveryMode;
  status: SimulatedTransactionStatus;
  volume: number;
  totalAmount: number;
  unitPrice: number;
  occurredAtUtc: string;
  rawPayloadJson: string;
  canonicalPayloadJson: string;
  contractValidation: PayloadContractValidationReport;
  metadataJson: string;
  timelineJson: string;
}

export interface NozzleLiftRequest {
  correlationId?: string | null;
  forceFault?: boolean;
  faultMessage?: string | null;
}

export interface NozzleHangRequest {
  correlationId?: string | null;
  elapsedSeconds?: number | null;
  clearFault?: boolean;
}

export interface DispenseSimulationRequest {
  action: 'start' | 'stop';
  correlationId?: string | null;
  flowRateLitresPerMinute?: number | null;
  targetAmount?: number | null;
  targetVolume?: number | null;
  elapsedSeconds?: number | null;
  injectDuplicate?: boolean;
  simulateFailure?: boolean;
  failureMessage?: string | null;
  forceFault?: boolean;
}

export interface NozzleActionResult {
  statusCode: number;
  message: string;
  nozzle: NozzleSimulationSnapshot | null;
  transaction: TransactionSimulationSummary | null;
  transactionGenerated: boolean;
  faulted: boolean;
  correlationId: string;
}

export interface LabPreAuthActionRequest {
  action: 'create' | 'authorize' | 'cancel' | 'expire';
  preAuthId?: string | null;
  correlationId?: string | null;
  pumpNumber?: number | null;
  nozzleNumber?: number | null;
  amount?: number | null;
  expiresInSeconds?: number | null;
  simulateFailure?: boolean;
  failureStatusCode?: number | null;
  failureMessage?: string | null;
  failureCode?: string | null;
  customerName?: string | null;
  customerTaxId?: string | null;
  customerTaxOffice?: string | null;
}

export interface LabPreAuthActionResult {
  statusCode: number;
  action: string;
  message: string;
  siteCode: string;
  correlationId: string;
  responseBody: string;
  session: PreAuthSessionRecord | null;
}

@Injectable({ providedIn: 'root' })
export class LabApiService {
  private readonly http = inject(HttpClient);

  getLabEnvironment(): Observable<LabEnvironmentSummary> {
    return this.http.get<LabEnvironmentSummary>(this.url('/api/lab-environment'));
  }

  getDashboard(): Observable<DashboardSummary> {
    return this.http.get<DashboardSummary>(this.url('/api/dashboard'));
  }

  getSites(includeInactive = true): Observable<SiteListItem[]> {
    return this.http.get<SiteListItem[]>(this.url('/api/sites'), {
      params: { includeInactive },
    });
  }

  getSite(id: string): Observable<SiteDetail> {
    return this.http.get<SiteDetail>(this.url(`/api/sites/${id}`));
  }

  createSite(request: SiteUpsertRequest): Observable<SiteDetail> {
    return this.http.post<SiteDetail>(this.url('/api/sites'), request);
  }

  updateSite(id: string, request: SiteUpsertRequest): Observable<SiteDetail> {
    return this.http.put<SiteDetail>(this.url(`/api/sites/${id}`), request);
  }

  archiveSite(id: string): Observable<SiteDetail> {
    return this.http.delete<SiteDetail>(this.url(`/api/sites/${id}`));
  }

  duplicateSite(id: string, request: DuplicateSiteRequest): Observable<SiteDetail> {
    return this.http.post<SiteDetail>(this.url(`/api/sites/${id}/duplicate`), request);
  }

  getForecourt(id: string): Observable<SiteForecourtView> {
    return this.http.get<SiteForecourtView>(this.url(`/api/sites/${id}/forecourt`));
  }

  saveForecourt(id: string, request: SaveForecourtRequest): Observable<SiteForecourtView> {
    return this.http.put<SiteForecourtView>(this.url(`/api/sites/${id}/forecourt`), request);
  }

  seedSite(
    id: string,
    request: { resetBeforeSeed: boolean; includeCompletedPreAuth: boolean },
  ): Observable<SiteSeedResult> {
    return this.http.post<SiteSeedResult>(this.url(`/api/sites/${id}/seed`), request);
  }

  resetSite(id: string): Observable<SiteSeedResult> {
    return this.http.post<SiteSeedResult>(this.url(`/api/sites/${id}/reset`), {});
  }

  getProducts(includeInactive = true): Observable<ProductView[]> {
    return this.http.get<ProductView[]>(this.url('/api/products'), {
      params: { includeInactive },
    });
  }

  getProfiles(): Observable<FccProfileSummary[]> {
    return this.http.get<FccProfileSummary[]>(this.url('/api/fcc-profiles'));
  }

  getProfile(id: string): Observable<FccProfileRecord> {
    return this.http.get<FccProfileRecord>(this.url(`/api/fcc-profiles/${id}`));
  }

  validateProfile(record: FccProfileRecord): Observable<FccProfileValidationResult> {
    return this.http.post<FccProfileValidationResult>(
      this.url('/api/fcc-profiles/validate'),
      record,
    );
  }

  previewProfile(request: FccProfilePreviewRequest): Observable<FccProfilePreviewResult> {
    return this.http.post<FccProfilePreviewResult>(this.url('/api/fcc-profiles/preview'), request);
  }

  createProfile(record: FccProfileRecord): Observable<FccProfileRecord> {
    return this.http.post<FccProfileRecord>(this.url('/api/fcc-profiles'), record);
  }

  updateProfile(id: string, record: FccProfileRecord): Observable<FccProfileRecord> {
    return this.http.put<FccProfileRecord>(this.url(`/api/fcc-profiles/${id}`), record);
  }

  archiveProfile(id: string): Observable<FccProfileRecord> {
    return this.http.delete<FccProfileRecord>(this.url(`/api/fcc-profiles/${id}`));
  }

  getLatency(): Observable<LatencySummary> {
    return this.http.get<LatencySummary>(this.url('/api/diagnostics/latency'));
  }

  getLogs(
    filters: {
      siteId?: string;
      profileId?: string;
      category?: string;
      severity?: string;
      siteCode?: string;
      correlationId?: string;
      search?: string;
      limit?: number;
    } = {},
  ): Observable<LogRecord[]> {
    const params: Record<string, string | number> = {};

    if (filters.siteId) {
      params['siteId'] = filters.siteId;
    }

    if (filters.profileId) {
      params['profileId'] = filters.profileId;
    }

    if (filters.category) {
      params['category'] = filters.category;
    }

    if (filters.severity) {
      params['severity'] = filters.severity;
    }

    if (filters.siteCode) {
      params['siteCode'] = filters.siteCode;
    }

    if (filters.correlationId) {
      params['correlationId'] = filters.correlationId;
    }

    if (filters.search) {
      params['search'] = filters.search;
    }

    if (filters.limit) {
      params['limit'] = filters.limit;
    }

    return this.http.get<LogRecord[]>(this.url('/api/logs'), { params });
  }

  getLog(id: string): Observable<LogDetailRecord> {
    return this.http.get<LogDetailRecord>(this.url(`/api/logs/${id}`));
  }

  getTransactions(
    filters: {
      siteId?: string;
      siteCode?: string;
      correlationId?: string;
      search?: string;
      deliveryMode?: TransactionDeliveryMode;
      status?: SimulatedTransactionStatus;
      limit?: number;
    } = {},
  ): Observable<TransactionRecord[]> {
    const params: Record<string, string | number> = {};

    if (filters.siteId) {
      params['siteId'] = filters.siteId;
    }

    if (filters.siteCode) {
      params['siteCode'] = filters.siteCode;
    }

    if (filters.correlationId) {
      params['correlationId'] = filters.correlationId;
    }

    if (filters.search) {
      params['search'] = filters.search;
    }

    if (filters.deliveryMode) {
      params['deliveryMode'] = filters.deliveryMode;
    }

    if (filters.status) {
      params['status'] = filters.status;
    }

    if (filters.limit) {
      params['limit'] = filters.limit;
    }

    return this.http.get<TransactionRecord[]>(this.url('/api/transactions'), { params });
  }

  getTransaction(id: string): Observable<TransactionDetailRecord> {
    return this.http.get<TransactionDetailRecord>(this.url(`/api/transactions/${id}`));
  }

  replayTransaction(
    id: string,
    request: { correlationId?: string | null } = {},
  ): Observable<TransactionReplayResult> {
    return this.http.post<TransactionReplayResult>(this.url(`/api/transactions/${id}/replay`), request);
  }

  repushTransaction(
    id: string,
    request: { targetKey?: string | null } = {},
  ): Observable<PushTransactionsResult> {
    return this.http.post<PushTransactionsResult>(this.url(`/api/transactions/${id}/re-push`), request);
  }

  getPreAuthSessions(
    filters: {
      siteCode?: string;
      correlationId?: string;
      limit?: number;
    } = {},
  ): Observable<PreAuthSessionRecord[]> {
    const params: Record<string, string | number> = {};

    if (filters.siteCode) {
      params['siteCode'] = filters.siteCode;
    }

    if (filters.correlationId) {
      params['correlationId'] = filters.correlationId;
    }

    if (filters.limit) {
      params['limit'] = filters.limit;
    }

    return this.http.get<PreAuthSessionRecord[]>(this.url('/api/preauth-sessions'), { params });
  }

  liftNozzle(
    siteId: string,
    pumpId: string,
    nozzleId: string,
    request: NozzleLiftRequest,
  ): Observable<NozzleActionResult> {
    return this.http.post<NozzleActionResult>(
      this.url(`/api/sites/${siteId}/pumps/${pumpId}/nozzles/${nozzleId}/lift`),
      request,
    );
  }

  hangNozzle(
    siteId: string,
    pumpId: string,
    nozzleId: string,
    request: NozzleHangRequest,
  ): Observable<NozzleActionResult> {
    return this.http.post<NozzleActionResult>(
      this.url(`/api/sites/${siteId}/pumps/${pumpId}/nozzles/${nozzleId}/hang`),
      request,
    );
  }

  dispense(
    siteId: string,
    pumpId: string,
    nozzleId: string,
    request: DispenseSimulationRequest,
  ): Observable<NozzleActionResult> {
    return this.http.post<NozzleActionResult>(
      this.url(`/api/sites/${siteId}/pumps/${pumpId}/nozzles/${nozzleId}/dispense`),
      request,
    );
  }

  simulatePreAuth(siteId: string, request: LabPreAuthActionRequest): Observable<LabPreAuthActionResult> {
    return this.http.post<LabPreAuthActionResult>(
      this.url(`/api/sites/${siteId}/preauth/simulate`),
      request,
    );
  }

  getScenarioLibrary(): Observable<ScenarioListResponse> {
    return this.http.get<ScenarioListResponse>(this.url('/api/scenarios'));
  }

  runScenario(request: { scenarioId?: string | null; scenarioKey?: string | null; replaySeed?: number | null }): Observable<ScenarioRunDetailRecord> {
    return this.http.post<ScenarioRunDetailRecord>(this.url('/api/scenarios/run'), request);
  }

  getScenarioRun(id: string): Observable<ScenarioRunDetailRecord> {
    return this.http.get<ScenarioRunDetailRecord>(this.url(`/api/scenarios/runs/${id}`));
  }

  exportScenarios(): Observable<ScenarioImportRecord[]> {
    return this.http.get<ScenarioImportRecord[]>(this.url('/api/scenarios/export'));
  }

  importScenarios(request: { replaceExisting: boolean; definitions: ScenarioImportRecord[] }): Observable<ScenarioImportResult> {
    return this.http.post<ScenarioImportResult>(this.url('/api/scenarios/import'), request);
  }

  getCallbackHistory(targetKey: string, limit = 100): Observable<CallbackHistoryRecord[]> {
    return this.http.get<CallbackHistoryRecord[]>(this.url(`/api/callbacks/${targetKey}/history`), {
      params: { limit },
    });
  }

  replayCallback(targetKey: string, id: string): Observable<CallbackReplayRecord> {
    return this.http.post<CallbackReplayRecord>(this.url(`/api/callbacks/${targetKey}/history/${id}/replay`), {});
  }

  private url(path: string): string {
    return `${environment.apiBaseUrl}${path}`;
  }
}
