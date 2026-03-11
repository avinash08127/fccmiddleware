import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { By } from '@angular/platform-browser';
import { DashboardComponent } from './dashboard.component';
import { DashboardSummary, DashboardAlertsResponse } from './dashboard.model';
import { ConnectivityState } from '../../core/models';

const mockSummary: DashboardSummary = {
  transactionVolume: {
    hourlyBuckets: Array.from({ length: 24 }, (_, i) => ({
      hour: new Date(Date.now() - (23 - i) * 3_600_000).toISOString(),
      total: 100 + i * 10,
      bySource: { FCC_PUSH: 60 + i, EDGE_UPLOAD: 30 + i, CLOUD_PULL: 10 + i },
    })),
  },
  ingestionHealth: {
    transactionsPerMinute: 12.5,
    successRate: 0.987,
    errorRate: 0.013,
    latencyP95Ms: 320,
    dlqDepth: 2,
    periodMinutes: 60,
  },
  agentStatus: {
    totalAgents: 120,
    online: 110,
    degraded: 7,
    offline: 3,
    offlineAgents: [
      {
        deviceId: 'dev-001',
        siteCode: 'MW001',
        lastSeenAt: new Date(Date.now() - 3_600_000).toISOString(),
        connectivityState: ConnectivityState.FULLY_OFFLINE,
      },
    ],
  },
  reconciliation: {
    pendingExceptions: 5,
    autoApproved: 240,
    flagged: 2,
    lastUpdatedAt: new Date().toISOString(),
  },
  staleTransactions: {
    count: 3,
    trend: 'up',
    thresholdMinutes: 30,
  },
  generatedAt: new Date().toISOString(),
};

const mockAlertsResponse: DashboardAlertsResponse = {
  totalCount: 2,
  alerts: [
    {
      id: 'alert-1',
      type: 'connectivity',
      severity: 'critical',
      message: 'Agent MW001 has been offline for 60 minutes',
      siteCode: 'MW001',
      createdAt: new Date().toISOString(),
    },
    {
      id: 'alert-2',
      type: 'dlq',
      severity: 'warning',
      message: '2 transactions in dead-letter queue require attention',
      createdAt: new Date().toISOString(),
    },
  ],
};

describe('DashboardComponent', () => {
  let fixture: ComponentFixture<DashboardComponent>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(DashboardComponent);
  });

  afterEach(() => {
    httpMock.verify();
  });

  function flushDashboardRequests(): void {
    // Flush legal entities
    const entitiesReq = httpMock.expectOne('/api/v1/master-data/legal-entities');
    entitiesReq.flush([]);
    // Flush summary + alerts
    const summaryReq = httpMock.expectOne('/api/v1/admin/dashboard/summary');
    summaryReq.flush(mockSummary);
    const alertsReq = httpMock.expectOne('/api/v1/admin/dashboard/alerts');
    alertsReq.flush(mockAlertsResponse);
    fixture.detectChanges();
  }

  it('should create the dashboard component', () => {
    flushDashboardRequests();
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('should show loading spinners while data is loading', () => {
    fixture.detectChanges();
    // Flush only legal entities; leave summary/alerts pending
    const entitiesReq = httpMock.expectOne('/api/v1/master-data/legal-entities');
    entitiesReq.flush([]);
    fixture.detectChanges();

    expect(fixture.componentInstance.summaryLoading()).toBeTrue();
    expect(fixture.componentInstance.alertsLoading()).toBeTrue();

    // Cleanup pending requests
    httpMock.expectOne('/api/v1/admin/dashboard/summary').flush(mockSummary);
    httpMock.expectOne('/api/v1/admin/dashboard/alerts').flush(mockAlertsResponse);
  });

  it('should render all 6 widgets after data loads', () => {
    fixture.detectChanges();
    flushDashboardRequests();

    const widgets = [
      'app-transaction-volume-chart',
      'app-ingestion-health',
      'app-agent-status-summary',
      'app-reconciliation-summary',
      'app-stale-transactions',
      'app-active-alerts',
    ];

    for (const selector of widgets) {
      const el = fixture.debugElement.query(By.css(selector));
      expect(el).withContext(`Expected widget ${selector} to be present`).toBeTruthy();
    }
  });

  it('should pass summary data to child widgets', () => {
    fixture.detectChanges();
    flushDashboardRequests();

    expect(fixture.componentInstance.summary()).toEqual(mockSummary);
    expect(fixture.componentInstance.summaryLoading()).toBeFalse();
  });

  it('should display alerts after load', () => {
    fixture.detectChanges();
    flushDashboardRequests();

    expect(fixture.componentInstance.alerts()).toHaveSize(2);
    expect(fixture.componentInstance.alertsLoading()).toBeFalse();
  });

  it('should reload data when legal entity filter changes', () => {
    fixture.detectChanges();
    flushDashboardRequests();

    // Trigger legal entity change
    fixture.componentInstance.onLegalEntityChange('le-id-123');
    fixture.detectChanges();

    const summaryReq = httpMock.expectOne(
      '/api/v1/admin/dashboard/summary?legalEntityId=le-id-123',
    );
    summaryReq.flush(mockSummary);

    const alertsReq = httpMock.expectOne(
      '/api/v1/admin/dashboard/alerts?legalEntityId=le-id-123',
    );
    alertsReq.flush(mockAlertsResponse);

    expect(fixture.componentInstance.selectedLegalEntityId()).toBe('le-id-123');
  });

  it('should show error state when summary request fails', () => {
    fixture.detectChanges();

    const entitiesReq = httpMock.expectOne('/api/v1/master-data/legal-entities');
    entitiesReq.flush([]);

    const summaryReq = httpMock.expectOne('/api/v1/admin/dashboard/summary');
    summaryReq.flush('Server error', { status: 500, statusText: 'Internal Server Error' });

    const alertsReq = httpMock.expectOne('/api/v1/admin/dashboard/alerts');
    alertsReq.flush(mockAlertsResponse);

    fixture.detectChanges();

    expect(fixture.componentInstance.summaryError()).toBeTruthy();
    expect(fixture.componentInstance.summaryLoading()).toBeFalse();
  });

  it('should auto-refresh every 60 seconds', fakeAsync(() => {
    fixture.detectChanges();
    flushDashboardRequests();

    // Advance time by 60 seconds
    tick(60_000);
    fixture.detectChanges();

    // Expect a second set of dashboard requests
    const summaryReq = httpMock.expectOne('/api/v1/admin/dashboard/summary');
    summaryReq.flush(mockSummary);
    const alertsReq = httpMock.expectOne('/api/v1/admin/dashboard/alerts');
    alertsReq.flush(mockAlertsResponse);

    fixture.destroy();
  }));
});
