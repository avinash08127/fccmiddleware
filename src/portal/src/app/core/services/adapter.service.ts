import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  AdapterConfigDocument,
  AdapterDetail,
  AdapterSummary,
  ResetSiteAdapterConfigRequest,
  SiteAdapterConfig,
  UpdateAdapterDefaultConfigRequest,
  UpdateSiteAdapterConfigRequest,
} from '../models';

@Injectable({ providedIn: 'root' })
export class AdapterService {
  private readonly http = inject(HttpClient);

  getAdapters(legalEntityId: string): Observable<AdapterSummary[]> {
    return this.http.get<AdapterSummary[]>('/api/v1/adapters', {
      params: { legalEntityId },
    });
  }

  getAdapterDetail(adapterKey: string, legalEntityId: string): Observable<AdapterDetail> {
    return this.http.get<AdapterDetail>(`/api/v1/adapters/${adapterKey}`, {
      params: { legalEntityId },
    });
  }

  getAdapterDefaults(
    adapterKey: string,
    legalEntityId: string,
  ): Observable<AdapterConfigDocument> {
    return this.http.get<AdapterConfigDocument>(`/api/v1/adapters/${adapterKey}/defaults`, {
      params: { legalEntityId },
    });
  }

  updateAdapterDefaults(
    adapterKey: string,
    request: UpdateAdapterDefaultConfigRequest,
  ): Observable<AdapterConfigDocument> {
    return this.http.put<AdapterConfigDocument>(
      `/api/v1/adapters/${adapterKey}/defaults`,
      request,
    );
  }

  getSiteAdapterConfig(siteId: string): Observable<SiteAdapterConfig> {
    return this.http.get<SiteAdapterConfig>(`/api/v1/sites/${siteId}/adapter-config`);
  }

  updateSiteAdapterConfig(
    siteId: string,
    request: UpdateSiteAdapterConfigRequest,
  ): Observable<SiteAdapterConfig> {
    return this.http.put<SiteAdapterConfig>(
      `/api/v1/sites/${siteId}/adapter-config/overrides`,
      request,
    );
  }

  resetSiteAdapterConfig(
    siteId: string,
    request: ResetSiteAdapterConfigRequest,
  ): Observable<SiteAdapterConfig> {
    return this.http.post<SiteAdapterConfig>(
      `/api/v1/sites/${siteId}/adapter-config/reset`,
      request,
    );
  }
}
