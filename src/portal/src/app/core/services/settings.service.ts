import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  SystemSettings,
  UpdateGlobalDefaultsRequest,
  UpsertLegalEntityOverrideRequest,
  UpdateAlertConfigurationRequest,
} from '../models';

@Injectable({ providedIn: 'root' })
export class SettingsService {
  private readonly http = inject(HttpClient);

  getSettings(): Observable<SystemSettings> {
    return this.http.get<SystemSettings>('/api/v1/admin/settings');
  }

  updateGlobalDefaults(req: UpdateGlobalDefaultsRequest): Observable<SystemSettings> {
    return this.http.put<SystemSettings>('/api/v1/admin/settings/global-defaults', req);
  }

  upsertLegalEntityOverride(req: UpsertLegalEntityOverrideRequest): Observable<SystemSettings> {
    return this.http.put<SystemSettings>(
      `/api/v1/admin/settings/overrides/${req.legalEntityId}`,
      req,
    );
  }

  deleteLegalEntityOverride(legalEntityId: string): Observable<SystemSettings> {
    return this.http.delete<SystemSettings>(
      `/api/v1/admin/settings/overrides/${legalEntityId}`,
    );
  }

  updateAlertConfiguration(req: UpdateAlertConfigurationRequest): Observable<SystemSettings> {
    return this.http.put<SystemSettings>('/api/v1/admin/settings/alerts', req);
  }
}
