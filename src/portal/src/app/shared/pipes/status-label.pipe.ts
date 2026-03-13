import { Pipe, PipeTransform } from '@angular/core';

const STATUS_LABELS: Record<string, string> = {
  // Transaction statuses
  RECEIVED: 'Received',
  VALIDATED: 'Validated',
  NORMALIZED: 'Normalized',
  DEDUP_PASSED: 'Dedup Passed',
  PENDING: 'Pending',
  SYNCED_TO_ODOO: 'Synced to Odoo',
  DUPLICATE: 'Duplicate',
  ARCHIVED: 'Archived',
  RECONCILED: 'Reconciled',
  FAILED_VALIDATION: 'Failed Validation',
  FAILED_NORMALIZATION: 'Failed Normalization',
  FAILED_SYNC: 'Failed Sync',
  DEAD_LETTERED: 'Dead Lettered',

  // Reconciliation statuses
  MATCHED: 'Matched',
  VARIANCE_WITHIN_TOLERANCE: 'Within Tolerance',
  VARIANCE_FLAGGED: 'Variance Flagged',
  UNMATCHED: 'Unmatched',
  EXCEPTION: 'Exception',
  APPROVED: 'Approved',
  REJECTED: 'Rejected',
  REVIEW_FUZZY_MATCH: 'Fuzzy Match Review',
  VOID: 'Void',

  // Ingestion sources
  FCC_PUSH: 'FCC Push',
  EDGE_UPLOAD: 'Edge Upload',
  CLOUD_PULL: 'Cloud Pull',
  CLOUD_DIRECT: 'Cloud Direct',
  WEBHOOK: 'Webhook',

  // FCC Vendors
  DOMS: 'DOMS',
  RADIX: 'RADIX',
  ADVATEC: 'ADVATEC',
  PETRONITE: 'PETRONITE',

  // Connectivity states
  FULLY_ONLINE: 'Online',
  INTERNET_DOWN: 'Internet Down',
  FCC_UNREACHABLE: 'FCC Unreachable',
  FULLY_OFFLINE: 'Offline',

  // Pre-auth statuses
  AUTHORIZED: 'Authorized',
  AUTHORISED: 'Authorised',
  DISPENSING: 'Dispensing',
  COMPLETED: 'Completed',
  CANCELLED: 'Cancelled',
  EXPIRED: 'Expired',
  LINKED: 'Linked',
};

/**
 * Maps API enum values (SCREAMING_SNAKE_CASE) to human-readable display labels.
 * Example: statusLabel('SYNCED_TO_ODOO') → 'Synced to Odoo'
 */
@Pipe({ name: 'statusLabel', standalone: true })
export class StatusLabelPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    if (!value) return '';
    return STATUS_LABELS[value] ?? value.replace(/_/g, ' ').toLowerCase().replace(/\b\w/g, (c) => c.toUpperCase());
  }
}
