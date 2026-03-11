import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  DashboardAlertsResponse,
  DashboardQueryParams,
  DashboardSummary,
} from './dashboard.model';

@Injectable({ providedIn: 'root' })
export class DashboardService {
  private readonly http = inject(HttpClient);

  getSummary(params?: DashboardQueryParams): Observable<DashboardSummary> {
    const httpParams = params?.legalEntityId
      ? new HttpParams().set('legalEntityId', params.legalEntityId)
      : undefined;
    return this.http.get<DashboardSummary>('/api/v1/admin/dashboard/summary', {
      params: httpParams,
    });
  }

  getAlerts(params?: DashboardQueryParams): Observable<DashboardAlertsResponse> {
    const httpParams = params?.legalEntityId
      ? new HttpParams().set('legalEntityId', params.legalEntityId)
      : undefined;
    return this.http.get<DashboardAlertsResponse>('/api/v1/admin/dashboard/alerts', {
      params: httpParams,
    });
  }
}
