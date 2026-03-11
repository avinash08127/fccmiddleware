import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { CardModule } from 'primeng/card';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';
import { ReconciliationSummaryData } from '../../dashboard.model';

@Component({
  selector: 'app-reconciliation-summary',
  standalone: true,
  imports: [CommonModule, RouterLink, CardModule, LoadingSpinnerComponent],
  template: `
    <p-card styleClass="h-full">
      <ng-template pTemplate="title">
        <span class="widget-title">
          <i class="pi pi-check-square"></i> Reconciliation
        </span>
      </ng-template>
      <div class="recon-wrapper">
        <app-loading-spinner [visible]="loading" />
        @if (!loading && error) {
          <div class="widget-error">
            <i class="pi pi-exclamation-triangle"></i>
            <span>{{ error }}</span>
          </div>
        }
        @if (!loading && !error && data) {
          <div class="recon-metrics">
            <div class="recon-metric recon-metric--flagged">
              <span class="recon-metric__count">{{ data.flagged }}</span>
              <span class="recon-metric__label">Flagged</span>
            </div>
            <div class="recon-metric recon-metric--pending">
              <span class="recon-metric__count">{{ data.pendingExceptions }}</span>
              <span class="recon-metric__label">Pending Review</span>
            </div>
            <div class="recon-metric recon-metric--approved">
              <span class="recon-metric__count">{{ data.autoApproved }}</span>
              <span class="recon-metric__label">Auto-Approved</span>
            </div>
          </div>
          <div class="recon-footer">
            <span class="last-updated">Updated {{ data.lastUpdatedAt | date: 'shortTime' }}</span>
            @if (data.flagged > 0 || data.pendingExceptions > 0) {
              <a routerLink="/reconciliation" class="action-link">
                Review exceptions <i class="pi pi-arrow-right"></i>
              </a>
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
      .recon-wrapper {
        position: relative;
        min-height: 180px;
      }
      .recon-metrics {
        display: flex;
        flex-direction: column;
        gap: 0.75rem;
        margin-bottom: 1rem;
      }
      .recon-metric {
        display: flex;
        justify-content: space-between;
        align-items: center;
        padding: 0.625rem 0.875rem;
        border-radius: 8px;
        border-left: 4px solid transparent;
      }
      .recon-metric--flagged {
        background: #FEE2E2;
        border-left-color: #EF4444;
      }
      .recon-metric--pending {
        background: #FEF3C7;
        border-left-color: #F59E0B;
      }
      .recon-metric--approved {
        background: #D1FAE5;
        border-left-color: #10B981;
      }
      .recon-metric__count {
        font-size: 1.5rem;
        font-weight: 700;
      }
      .recon-metric__label {
        font-size: 0.8rem;
        font-weight: 500;
        color: inherit;
        opacity: 0.8;
      }
      .recon-footer {
        display: flex;
        justify-content: space-between;
        align-items: center;
        font-size: 0.8rem;
      }
      .last-updated {
        color: var(--p-text-muted-color, #64748b);
      }
      .action-link {
        color: var(--p-primary-500, #3B82F6);
        text-decoration: none;
        display: flex;
        align-items: center;
        gap: 0.25rem;
        font-weight: 500;
      }
      .action-link:hover {
        text-decoration: underline;
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
export class ReconciliationSummaryComponent {
  @Input() data: ReconciliationSummaryData | null = null;
  @Input() loading = false;
  @Input() error: string | null = null;
}
