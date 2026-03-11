import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { SystemSettings, UpdateSettingsRequest } from '../models';

@Injectable({ providedIn: 'root' })
export class SettingsService {
  private readonly http = inject(HttpClient);

  getSettings(): Observable<SystemSettings> {
    return this.http.get<SystemSettings>('/api/v1/settings');
  }

  updateSettings(settings: UpdateSettingsRequest): Observable<SystemSettings> {
    return this.http.patch<SystemSettings>('/api/v1/settings', settings);
  }
}
