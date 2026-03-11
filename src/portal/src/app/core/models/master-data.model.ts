// ── Core models ───────────────────────────────────────────────────────────────

export interface LegalEntity {
  id: string;
  code: string;
  name: string;
  currencyCode: string;
  country: string | null;
  isActive: boolean;
  updatedAt: string | null;
}

// ── Sync status ───────────────────────────────────────────────────────────────

export interface MasterDataSyncResponse {
  upsertedCount: number;
  unchangedCount: number;
  deactivatedCount: number;
  errorCount: number;
  errors: import('./common.model').ErrorResponse[] | null;
}

/**
 * Aggregated view of the last master-data sync operation per entity type.
 * Displayed on the Master Data Status feature page.
 */
export interface MasterDataSyncStatus {
  entityType: MasterDataEntityType;
  lastSyncAtUtc: string | null;
  upsertedCount: number;
  deactivatedCount: number;
  errorCount: number;
  isStale: boolean;
  /** Staleness threshold in hours configured for this entity type. */
  staleThresholdHours: number;
}

export type MasterDataEntityType =
  | 'legal_entities'
  | 'sites'
  | 'pumps'
  | 'products'
  | 'operators';
