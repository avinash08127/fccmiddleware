import { ConnectivityState } from '../../core/models';

// ── Transaction Volume ────────────────────────────────────────────────────────

export interface TransactionVolumeHourlyBucket {
  hour: string; // ISO 8601 UTC — start of the hour
  total: number;
  bySource: {
    fccPush: number;
    edgeUpload: number;
    cloudPull: number;
  };
}

export interface TransactionVolumeData {
  hourlyBuckets: TransactionVolumeHourlyBucket[];
}

// ── Ingestion Health ──────────────────────────────────────────────────────────

export interface IngestionHealthData {
  transactionsPerMinute: number;
  successRate: number; // 0–1
  errorRate: number; // 0–1
  latencyP95Ms: number | null;
  dlqDepth: number;
  periodMinutes: number;
}

// ── Agent Status Summary ──────────────────────────────────────────────────────

export interface OfflineAgentItem {
  deviceId: string;
  siteCode: string;
  lastSeenAt: string | null;
  connectivityState: ConnectivityState;
}

export interface AgentStatusSummaryData {
  totalAgents: number;
  online: number;
  degraded: number;
  offline: number;
  offlineAgents: OfflineAgentItem[];
}

// ── Reconciliation Summary ────────────────────────────────────────────────────

export interface ReconciliationSummaryData {
  pendingExceptions: number;
  autoApproved: number;
  flagged: number;
  lastUpdatedAt: string;
}

// ── Stale Transactions ────────────────────────────────────────────────────────

export type StaleTransactionTrend = 'up' | 'down' | 'stable';

export interface StaleTransactionsData {
  count: number;
  trend: StaleTransactionTrend;
  thresholdMinutes: number;
}

// ── Combined Summary ──────────────────────────────────────────────────────────

export interface DashboardSummary {
  transactionVolume: TransactionVolumeData;
  ingestionHealth: IngestionHealthData;
  agentStatus: AgentStatusSummaryData;
  reconciliation: ReconciliationSummaryData;
  staleTransactions: StaleTransactionsData;
  generatedAt: string;
}

// ── Alerts ────────────────────────────────────────────────────────────────────

export type AlertType = 'connectivity' | 'dlq' | 'stale_data' | 'reconciliation' | 'system';
export type AlertSeverity = 'critical' | 'warning' | 'info';

export interface DashboardAlert {
  id: string;
  type: AlertType;
  severity: AlertSeverity;
  message: string;
  siteCode?: string;
  legalEntityId?: string;
  createdAt: string;
}

export interface DashboardAlertsResponse {
  alerts: DashboardAlert[];
  totalCount: number;
}

// ── Query Params ──────────────────────────────────────────────────────────────

export interface DashboardQueryParams {
  legalEntityId?: string;
}
