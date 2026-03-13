import { ReconciliationStatus } from './transaction.model';
import { PreAuthStatus } from './pre-auth.model';

// ── Enums ─────────────────────────────────────────────────────────────────────

export enum ReconciliationDecision {
  APPROVED = 'APPROVED',
  REJECTED = 'REJECTED',
}

// ── Core models ───────────────────────────────────────────────────────────────

/** Embedded pre-auth summary included in the detail endpoint response. */
export interface ReconciliationPreAuthSummary {
  requestedAt: string | null;
  vehicleNumber: string | null;
  customerBusinessName: string | null;
  attendantId: string | null;
  fccCorrelationId: string | null;
  fccAuthorizationCode: string | null;
}

/** Embedded transaction summary included in the detail endpoint response. */
export interface ReconciliationTransactionSummary {
  fccTransactionId: string;
  volumeMicrolitres: number | null;
  startedAt: string | null;
  completedAt: string | null;
}

/**
 * A reconciliation record links a PreAuthRecord to its matched CanonicalTransaction
 * and exposes the variance outcome. Queried from the Reconciliation Workbench.
 */
export interface ReconciliationRecord {
  id: string;
  preAuthId: string | null;
  transactionId: string | null;
  siteCode: string;
  legalEntityId: string;
  odooOrderId: string | null;
  pumpNumber: number;
  nozzleNumber: number;
  productCode: string | null;
  currencyCode: string | null;
  /** Authorised amount in minor currency units. */
  requestedAmount: number | null;
  /** Actual dispensed amount in minor currency units. Null when unmatched. */
  actualAmount: number | null;
  /** Variance in minor units (actualAmount − requestedAmount). Null when unmatched. */
  amountVariance: number | null;
  /** Legacy compatibility field in basis points. Prefer variancePercent for display. */
  varianceBps: number | null;
  /** Variance as a percentage value (e.g. 2.5 means 2.5%). */
  variancePercent: number | null;
  /** Which matching step resolved this record. */
  matchMethod: string | null;
  /** True when multiple candidates were found and a tie-break was applied. */
  ambiguityFlag: boolean;
  preAuthStatus: PreAuthStatus | null;
  reconciliationStatus: ReconciliationStatus | null;
  decision: ReconciliationDecision | null;
  decisionReason: string | null;
  decidedBy: string | null;
  decidedAt: string | null;
  createdAt: string;
  updatedAt: string;
  /** Embedded pre-auth details (populated by GET /reconciliation/:id). */
  preAuthSummary: ReconciliationPreAuthSummary | null;
  /** Embedded transaction details (populated by GET /reconciliation/:id). */
  transactionSummary: ReconciliationTransactionSummary | null;
}

/**
 * A variance exception represents a reconciliation record where the variance
 * exceeded tolerance and requires operator review (approve or reject).
 */
export interface ReconciliationException {
  id: string;
  preAuthId: string | null;
  transactionId: string | null;
  legalEntityId: string;
  siteCode: string;
  pumpNumber: number;
  nozzleNumber: number;
  odooOrderId: string | null;
  currencyCode: string | null;
  requestedAmount: number | null;
  actualAmount: number | null;
  amountVariance: number | null;
  /** Legacy compatibility field in basis points (1 bps = 0.01%). Prefer variancePercent for display. */
  varianceBps: number | null;
  /** Variance as a percentage value (e.g. 2.5 means 2.5%). */
  variancePercent: number | null;
  /** Which matching step resolved this record (e.g. EXACT_CORRELATION_ID). */
  matchMethod: string | null;
  /** True when multiple candidates were found and a tie-break was applied. */
  ambiguityFlag: boolean;
  reconciliationStatus: ReconciliationStatus;
  decision: ReconciliationDecision | null;
  decisionReason: string | null;
  decidedBy: string | null;
  decidedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

// ── Request / response ────────────────────────────────────────────────────────

export interface ApproveRejectRequest {
  decision: ReconciliationDecision;
  /** Mandatory reason text required for both APPROVED and REJECTED decisions. */
  reason: string;
}

/** Response from POST /reconciliation/:id/approve or /reject. */
export interface ReconciliationReviewResponse {
  reconciliationId: string;
  status: string;
  legalEntityId: string;
  siteCode: string;
  reviewedByUserId: string;
  reviewedAtUtc: string;
  reviewReason: string;
}

// ── Query params ──────────────────────────────────────────────────────────────

export interface ReconciliationQueryParams {
  legalEntityId: string;
  cursor?: string;
  pageSize?: number;
  siteCode?: string;
  reconciliationStatus?: ReconciliationStatus;
  decision?: ReconciliationDecision | 'PENDING_DECISION';
  from?: string;
  to?: string;
}
