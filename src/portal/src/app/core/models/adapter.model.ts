export interface AdapterSummary {
  adapterKey: string;
  displayName: string;
  vendor: string;
  adapterVersion: string;
  supportedProtocols: string[];
  supportedIngestionMethods: string[];
  supportsPreAuth: boolean;
  supportsPumpStatus: boolean;
  activeSiteCount: number;
  defaultConfigVersion: number;
  defaultUpdatedAt: string | null;
  defaultUpdatedBy: string | null;
}

export interface AdapterFieldOption {
  label: string;
  value: string;
}

export interface AdapterFieldDefinition {
  key: string;
  label: string;
  type: 'text' | 'secret' | 'number' | 'boolean' | 'select' | 'json';
  group: string;
  required: boolean;
  sensitive: boolean;
  defaultable: boolean;
  siteConfigurable: boolean;
  description: string | null;
  min: number | null;
  max: number | null;
  visibleWhenKey: string | null;
  visibleWhenValue: string | null;
  options: AdapterFieldOption[] | null;
}

export interface AdapterSchema {
  adapterKey: string;
  displayName: string;
  vendor: string;
  adapterVersion: string;
  supportedProtocols: string[];
  supportedIngestionMethods: string[];
  supportsPreAuth: boolean;
  supportsPumpStatus: boolean;
  fields: AdapterFieldDefinition[];
}

export interface AdapterConfigDocument {
  adapterKey: string;
  legalEntityId: string;
  configVersion: number;
  updatedAt: string | null;
  updatedBy: string | null;
  values: Record<string, unknown>;
  secretState: Record<string, boolean>;
}

export interface AdapterSiteUsage {
  siteId: string;
  siteCode: string;
  siteName: string;
  hasOverride: boolean;
  overrideVersion: number | null;
  overrideUpdatedAt: string | null;
  overrideUpdatedBy: string | null;
}

export interface AdapterDetail {
  schema: AdapterSchema;
  defaultConfig: AdapterConfigDocument;
  sites: AdapterSiteUsage[];
}

export interface SiteAdapterConfig {
  siteId: string;
  legalEntityId: string;
  siteCode: string;
  siteName: string;
  adapterKey: string;
  vendor: string;
  defaultConfigVersion: number;
  overrideVersion: number | null;
  overrideUpdatedAt: string | null;
  overrideUpdatedBy: string | null;
  defaultValues: Record<string, unknown>;
  overrideValues: Record<string, unknown>;
  effectiveValues: Record<string, unknown>;
  secretState: Record<string, boolean>;
  fieldSources: Record<string, string>;
  schema: AdapterSchema;
}

export interface UpdateAdapterDefaultConfigRequest {
  legalEntityId: string;
  reason: string;
  values: Record<string, unknown>;
}

export interface UpdateSiteAdapterConfigRequest {
  reason: string;
  effectiveValues: Record<string, unknown>;
}

export interface ResetSiteAdapterConfigRequest {
  reason: string;
}
