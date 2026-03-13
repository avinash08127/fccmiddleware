// F12-07: Shared audit utilities — extracted to avoid duplication between
// AuditLogComponent and AuditDetailComponent.

export type PrimeSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

// F12-02: UUID regex for correlation ID validation.
export const UUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function eventTypeSeverity(eventType: string): PrimeSeverity {
  if (eventType.startsWith('Transaction')) return 'info';
  if (eventType.startsWith('PreAuth')) return 'secondary';
  if (eventType.startsWith('Reconciliation')) return 'warn';
  if (eventType.startsWith('Agent')) return 'success';
  if (eventType === 'ConnectivityChanged' || eventType === 'BufferThresholdExceeded') return 'danger';
  return 'contrast';
}
