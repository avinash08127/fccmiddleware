// Pagination

export interface PageMeta {
  pageSize: number;
  hasMore: boolean;
  nextCursor: string | null;
  totalCount: number | null;
}

export interface PagedResult<T> {
  data: T[];
  meta: PageMeta;
}

// Error envelope

export interface ErrorResponse {
  errorCode: string;
  message: string;
  details: Record<string, unknown> | null;
  traceId: string;
  timestamp: string;
}

// Status badge helper — maps domain status strings to severity levels used by PrimeNG Tag

export type StatusSeverity = 'success' | 'info' | 'warning' | 'danger' | 'secondary';

export interface StatusBadge {
  label: string;
  severity: StatusSeverity;
}
