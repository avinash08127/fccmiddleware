import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { CardModule } from 'primeng/card';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';
import { StaleTransactionsData } from '../../dashboard.model';

@Component({
  selector: 'app-stale-transactions',
  standalone: true,
  imports: [CommonModule, RouterLink, CardModule, LoadingSpinnerComponent],
  template: `
    <p-card styleClass="h-full">
      <ng-template pTemplate="title">
        <span class="widget-title">
          <i class="pi pi-clock"></i> Stale Transactions
        </span>
      </ng-template>
      <div class="stale-wrapper">
        <app-loading-spinner [visible]="loading" />
        @if (!loading && error) {
          <div class="widget-error">
            <i class="pi pi-exclamation-triangle"></i>
            <span>{{ error }}</span>
          </div>
        }
        @if (!loading && !error && data) {
          <div class="stale-body">
            <div class="stale-count" [class.stale-count--alert]="data.count > 0">
              <span class="stale-count__number">{{ data.count }}</span>
              <div class="stale-count__trend">
                @if (data.trend === 'up') {
                  <i class="pi pi-arrow-up trend-up"></i>
                } @else if (data.trend === 'down') {
                  <i class="pi pi-arrow-down trend-down"></i>
                } @else {
                  <i class="pi pi-minus trend-stable"></i>
                }
              </div>
            </div>
            <p class="stale-desc">
              PENDING transactions older than {{ data.thresholdMinutes }} minutes
            </p>
            @if (data.count > 0) {
              <a
                routerLink="/transactions"
                [queryParams]="{ status: 'PENDING', stale: 'true' }"
                class="view-stale-link"
              >
                <i class="pi pi-search"></i> View stale transactions
              </a>
            } @else {
              <p class="all-clear">
                <i class="pi pi-check-circle"></i> All transactions are current
              </p>
            }
          </div>
        }
        @if (!loading && !error && !data) {
          <div class="widget-empty">No data available</div>
        }
      </div>
    </p-card>
  `,
  styles: [
    `
      .widget-title {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        font-size: 1rem;
        font-weight: 600;
      }
      .stale-wrapper {
        position: relative;
        min-height: 180px;
      }
      .stale-body {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 0.75rem;
        padding: 0.5rem 0;
      }
      .stale-count {
        display: flex;
        align-items: center;
        gap: 0.75rem;
      }
      .stale-count__number {
        font-size: 3rem;
        font-weight: 700;
        color: var(--p-text-color, #1e293b);
      }
      .stale-count--alert .stale-count__number {
        color: #EF4444;
      }
      .trend-up {
        font-size: 1.25rem;
        color: #EF4444;
      }
      .trend-down {
        font-size: 1.25rem;
        color: #10B981;
      }
      .trend-stable {
        font-size: 1.25rem;
        color: var(--p-text-muted-color, #64748b);
      }
      .stale-desc {
        font-size: 0.85rem;
        color: var(--p-text-muted-color, #64748b);
        text-align: center;
        margin: 0;
      }
      .view-stale-link {
        display: flex;
        align-items: center;
        gap: 0.375rem;
        color: var(--p-primary-500, #3B82F6);
        text-decoration: none;
        font-size: 0.875rem;
        font-weight: 500;
      }
      .view-stale-link:hover {
        text-decoration: underline;
      }
      .all-clear {
        display: flex;
        align-items: center;
        gap: 0.375rem;
        color: #10B981;
        font-size: 0.875rem;
        margin: 0;
      }
      .widget-error,
      .widget-empty {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 0.5rem;
        height: 180px;
        color: var(--p-text-muted-color, #64748b);
        font-size: 0.875rem;
      }
      .widget-error {
        color: #EF4444;
      }
    `,
  ],
})
export class StaleTransactionsComponent {
  @Input() data: StaleTransactionsData | null = null;
  @Input() loading = false;
  @Input() error: string | null = null;
}
