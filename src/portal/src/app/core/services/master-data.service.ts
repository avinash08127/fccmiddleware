import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { LegalEntity, MasterDataSyncStatus } from '../models';

@Injectable({ providedIn: 'root' })
export class MasterDataService {
  private readonly http = inject(HttpClient);

  getSyncStatus(): Observable<MasterDataSyncStatus[]> {
    return this.http.get<MasterDataSyncStatus[]>('/api/v1/master-data/sync-status');
  }

  getLegalEntities(): Observable<LegalEntity[]> {
    return this.http.get<LegalEntity[]>('/api/v1/master-data/legal-entities');
  }
}
