import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  GenerateBootstrapTokenRequest,
  GenerateBootstrapTokenResponse,
  RevokeBootstrapTokenResponse,
  BootstrapTokenHistoryRow,
  BootstrapTokenHistoryParams,
} from '../models/bootstrap-token.model';
import { PagedResult } from '../models/common.model';
import { buildHttpParams } from './http-params.util';

@Injectable({ providedIn: 'root' })
export class BootstrapTokenService {
  private readonly http = inject(HttpClient);

  generate(req: GenerateBootstrapTokenRequest): Observable<GenerateBootstrapTokenResponse> {
    return this.http.post<GenerateBootstrapTokenResponse>(
      '/api/v1/admin/bootstrap-tokens',
      req,
    );
  }

  revoke(tokenId: string): Observable<RevokeBootstrapTokenResponse> {
    return this.http.delete<RevokeBootstrapTokenResponse>(
      `/api/v1/admin/bootstrap-tokens/${tokenId}`,
    );
  }

  getHistory(params: BootstrapTokenHistoryParams): Observable<PagedResult<BootstrapTokenHistoryRow>> {
    return this.http.get<PagedResult<BootstrapTokenHistoryRow>>(
      '/api/v1/admin/bootstrap-tokens',
      { params: buildHttpParams(params) },
    );
  }
}
