// ── Enums ─────────────────────────────────────────────────────────────────────

export enum FccVendor {
  DOMS = 'DOMS',
  RADIX = 'RADIX',
  ADVATEC = 'ADVATEC',
  PETRONITE = 'PETRONITE',
}

export enum TransactionStatus {
  PENDING = 'PENDING',
  SYNCED = 'SYNCED',
  SYNCED_TO_ODOO = 'SYNCED_TO_ODOO',
  STALE_PENDING = 'STALE_PENDING',
  DUPLICATE = 'DUPLICATE',
  ARCHIVED = 'ARCHIVED',
}

export enum IngestionSource {
  FCC_PUSH = 'FCC_PUSH',
  EDGE_UPLOAD = 'EDGE_UPLOAD',
  CLOUD_PULL = 'CLOUD_PULL',
}

export enum ReconciliationStatus {
  MATCHED = 'MATCHED',
  VARIANCE_WITHIN_TOLERANCE = 'VARIANCE_WITHIN_TOLERANCE',
  VARIANCE_FLAGGED = 'VARIANCE_FLAGGED',
  UNMATCHED = 'UNMATCHED',
  APPROVED = 'APPROVED',
  REJECTED = 'REJECTED',
  REVIEW_FUZZY_MATCH = 'REVIEW_FUZZY_MATCH',
}

// ── Core model ────────────────────────────────────────────────────────────────

export interface Transaction {
  id: string;
  fccTransactionId: string;
  siteCode: string;
  pumpNumber: number;
  nozzleNumber: number;
  productCode: string;
  /** Dispensed volume in microlitres. 1 L = 1,000,000 µL. */
  volumeMicrolitres: number;
  /** Total amount in minor currency units (e.g. cents). */
  amountMinorUnits: number;
  /** Price per litre in minor currency units. */
  unitPriceMinorPerLitre: number;
  startedAt: string;
  completedAt: string;
  fccVendor: FccVendor;
  legalEntityId: string;
  currencyCode: string;
  status: TransactionStatus;
  ingestionSource: IngestionSource;
  ingestedAt: string;
  updatedAt: string;
  schemaVersion: number;
  isDuplicate: boolean;
  correlationId: string;
  fiscalReceiptNumber: string | null;
  attendantId: string | null;
  odooOrderId: string | null;
  preAuthId: string | null;
  reconciliationStatus: ReconciliationStatus | null;
  duplicateOfId: string | null;
  rawPayloadRef: string | null;
  rawPayloadJson: string | null;
}

/** Extended detail view — same shape as Transaction (all fields are returned). */
export type TransactionDetail = Transaction;

// ── Query params ──────────────────────────────────────────────────────────────

export interface TransactionQueryParams {
  legalEntityId: string;
  cursor?: string;
  pageSize?: number;
  siteCode?: string;
  status?: TransactionStatus;
  from?: string;
  to?: string;
  productCode?: string;
  fields?: string;
  // Extended search / filter params
  fccTransactionId?: string;
  odooOrderId?: string;
  fccVendor?: FccVendor;
  ingestionSource?: IngestionSource;
  pumpNumber?: number;
  isStale?: boolean;
  sortField?: string;
  sortOrder?: 'asc' | 'desc';
}

// ── Ingest (internal — used by portal when displaying ingestion results) ──────

export interface IngestResponse {
  id: string;
  fccTransactionId: string;
  siteCode: string;
  status: 'PENDING' | 'DUPLICATE';
  correlationId: string;
  ingestedAt: string;
}

// ── Upload batch results ───────────────────────────────────────────────────────

export type UploadOutcome = 'ACCEPTED' | 'DUPLICATE' | 'REJECTED';

export interface UploadRecordResult {
  fccTransactionId: string;
  siteCode: string;
  outcome: UploadOutcome;
  id: string | null;
  error: import('./common.model').ErrorResponse | null;
}

// ── Acknowledge ────────────────────────────────────────────────────────────────

export interface AcknowledgeItem {
  id: string;
  odooOrderId: string;
}

export type AcknowledgeOutcome =
  | 'ACKNOWLEDGED'
  | 'ALREADY_ACKNOWLEDGED'
  | 'CONFLICT'
  | 'NOT_FOUND'
  | 'FAILED';

export interface AcknowledgeResult {
  id: string;
  outcome: AcknowledgeOutcome;
  error: AcknowledgeError | null;
}

export interface AcknowledgeError {
  code: string;
  message: string;
}

export interface AcknowledgeBatchResponse {
  results: AcknowledgeResult[];
  succeededCount: number;
  failedCount: number;
}

// ── Synced-status poll ─────────────────────────────────────────────────────────

export type TransactionCloudStatus =
  | TransactionStatus
  | 'NOT_FOUND';

export interface TransactionStatusEntry {
  id: string;
  status: TransactionCloudStatus;
}
