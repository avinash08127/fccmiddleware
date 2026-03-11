import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  FccConfig,
  Site,
  SiteDetail,
  SiteConfig,
  Product,
  Pump,
  Nozzle,
  UpdateSiteRequest,
  AddPumpRequest,
  UpdateNozzleRequest,
} from '../models';
import { PagedResult } from '../models';
import { SiteOperatingModel, ConnectivityMode, IngestionMode } from '../models';
import { FccVendor } from '../models';

export interface SiteQueryParams {
  legalEntityId: string;
  cursor?: string;
  pageSize?: number;
  isActive?: boolean;
  operatingModel?: SiteOperatingModel;
  connectivityMode?: ConnectivityMode;
  ingestionMode?: IngestionMode;
  fccVendor?: FccVendor;
}

@Injectable({ providedIn: 'root' })
export class SiteService {
  private readonly http = inject(HttpClient);

  getSites(params: SiteQueryParams): Observable<PagedResult<Site>> {
    return this.http.get<PagedResult<Site>>('/api/v1/sites', {
      params: params as unknown as Record<string, string>,
    });
  }

  getSiteById(id: string): Observable<Site> {
    return this.http.get<Site>(`/api/v1/sites/${id}`);
  }

  getSiteDetail(id: string): Observable<SiteDetail> {
    return this.http.get<SiteDetail>(`/api/v1/sites/${id}`);
  }

  updateSite(id: string, req: UpdateSiteRequest): Observable<SiteDetail> {
    return this.http.patch<SiteDetail>(`/api/v1/sites/${id}`, req);
  }

  updateFccConfig(siteId: string, config: Partial<FccConfig>): Observable<SiteConfig> {
    return this.http.put<SiteConfig>(`/api/v1/sites/${siteId}/fcc-config`, config);
  }

  getPumps(siteId: string): Observable<Pump[]> {
    return this.http.get<Pump[]>(`/api/v1/sites/${siteId}/pumps`);
  }

  addPump(siteId: string, req: AddPumpRequest): Observable<Pump> {
    return this.http.post<Pump>(`/api/v1/sites/${siteId}/pumps`, req);
  }

  removePump(siteId: string, pumpId: string): Observable<void> {
    return this.http.delete<void>(`/api/v1/sites/${siteId}/pumps/${pumpId}`);
  }

  updateNozzle(
    siteId: string,
    pumpId: string,
    nozzleNumber: number,
    req: UpdateNozzleRequest,
  ): Observable<Nozzle> {
    return this.http.patch<Nozzle>(
      `/api/v1/sites/${siteId}/pumps/${pumpId}/nozzles/${nozzleNumber}`,
      req,
    );
  }

  getProducts(legalEntityId: string): Observable<Product[]> {
    return this.http.get<Product[]>('/api/v1/master-data/products', {
      params: { legalEntityId },
    });
  }
}
