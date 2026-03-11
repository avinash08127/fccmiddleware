import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface DashboardSummary {
  profileName: string;
  sites: number;
  pumps: number;
  nozzles: number;
  transactions: number;
}

export interface SiteSummary {
  id: number;
  siteCode: string;
  pumps: number;
  nozzles: number;
}

export interface LatencySummary {
  profileName: string;
  replaySignature: string;
  measurements: {
    dashboardQueryP95Ms: number;
    siteLoadP95Ms: number;
    signalRBroadcastP95Ms: number;
    fccHealthP95Ms: number;
    transactionPullP95Ms: number;
    sampleCount: number;
  };
}

@Injectable({ providedIn: 'root' })
export class LabApiService {
  private readonly http = inject(HttpClient);

  getDashboard(): Observable<DashboardSummary> {
    return this.http.get<DashboardSummary>(this.url('/api/dashboard'));
  }

  getSites(): Observable<SiteSummary[]> {
    return this.http.get<SiteSummary[]>(this.url('/api/sites'));
  }

  getLatency(): Observable<LatencySummary> {
    return this.http.get<LatencySummary>(this.url('/api/diagnostics/latency'));
  }

  private url(path: string): string {
    return `${environment.apiBaseUrl}${path}`;
  }
}
