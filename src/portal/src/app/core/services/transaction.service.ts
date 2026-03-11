import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  Transaction,
  TransactionDetail,
  TransactionQueryParams,
  AcknowledgeItem,
  AcknowledgeResult,
} from '../models';
import { PagedResult } from '../models';

@Injectable({ providedIn: 'root' })
export class TransactionService {
  private readonly http = inject(HttpClient);

  getTransactions(params: TransactionQueryParams): Observable<PagedResult<Transaction>> {
    return this.http.get<PagedResult<Transaction>>('/api/v1/transactions', {
      params: params as unknown as Record<string, string>,
    });
  }

  getTransactionById(id: string): Observable<TransactionDetail> {
    return this.http.get<TransactionDetail>(`/api/v1/transactions/${id}`);
  }

  acknowledgeTransactions(
    acknowledgements: AcknowledgeItem[]
  ): Observable<AcknowledgeResult[]> {
    return this.http.post<AcknowledgeResult[]>('/api/v1/transactions/acknowledge', {
      acknowledgements,
    });
  }
}
