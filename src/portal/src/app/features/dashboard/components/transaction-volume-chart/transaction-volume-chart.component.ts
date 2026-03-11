import { Component, Input, OnChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CardModule } from 'primeng/card';
import { ChartModule } from 'primeng/chart';
import { LoadingSpinnerComponent } from '../../../../shared/components/loading-spinner/loading-spinner.component';
import { TransactionVolumeData } from '../../dashboard.model';

@Component({
  selector: 'app-transaction-volume-chart',
  standalone: true,
  imports: [CommonModule, CardModule, ChartModule, LoadingSpinnerComponent],
  template: `
    <p-card styleClass="h-full">
      <ng-template pTemplate="title">
        <span class="widget-title">
          <i class="pi pi-chart-line"></i> Transaction Volume (Last 24h)
        </span>
      </ng-template>
      <div class="chart-wrapper">
        <app-loading-spinner [visible]="loading" />
        @if (!loading && error) {
          <div class="widget-error">
            <i class="pi pi-exclamation-triangle"></i>
            <span>{{ error }}</span>
          </div>
        }
        @if (!loading && !error && chartData) {
          <p-chart type="line" [data]="chartData" [options]="chartOptions" height="220px" />
        }
        @if (!loading && !error && !chartData) {
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
      .chart-wrapper {
        position: relative;
        min-height: 220px;
      }
      .widget-error,
      .widget-empty {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 0.5rem;
        height: 220px;
        color: var(--p-text-muted-color, #64748b);
        font-size: 0.875rem;
      }
      .widget-error {
        color: var(--p-red-500, #ef4444);
      }
    `,
  ],
})
export class TransactionVolumeChartComponent implements OnChanges {
  @Input() data: TransactionVolumeData | null = null;
  @Input() loading = false;
  @Input() error: string | null = null;

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  chartData: any = null;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  chartOptions: any = {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { position: 'bottom' },
      tooltip: { mode: 'index', intersect: false },
    },
    scales: {
      x: { grid: { display: false } },
      y: { beginAtZero: true, ticks: { precision: 0 } },
    },
    interaction: { mode: 'nearest', axis: 'x', intersect: false },
  };

  ngOnChanges(): void {
    if (this.data?.hourlyBuckets?.length) {
      this.buildChart();
    } else {
      this.chartData = null;
    }
  }

  private buildChart(): void {
    const buckets = this.data!.hourlyBuckets;
    const labels = buckets.map((b) => {
      const date = new Date(b.hour);
      return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    });

    this.chartData = {
      labels,
      datasets: [
        {
          label: 'FCC Push',
          data: buckets.map((b) => b.bySource.FCC_PUSH),
          borderColor: '#3B82F6',
          backgroundColor: 'rgba(59,130,246,0.1)',
          tension: 0.3,
          fill: true,
          pointRadius: 3,
        },
        {
          label: 'Edge Upload',
          data: buckets.map((b) => b.bySource.EDGE_UPLOAD),
          borderColor: '#10B981',
          backgroundColor: 'rgba(16,185,129,0.1)',
          tension: 0.3,
          fill: true,
          pointRadius: 3,
        },
        {
          label: 'Cloud Pull',
          data: buckets.map((b) => b.bySource.CLOUD_PULL),
          borderColor: '#F59E0B',
          backgroundColor: 'rgba(245,158,11,0.1)',
          tension: 0.3,
          fill: true,
          pointRadius: 3,
        },
      ],
    };
  }
}
