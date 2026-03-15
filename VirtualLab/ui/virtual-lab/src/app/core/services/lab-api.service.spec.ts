import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { LabApiService } from './lab-api.service';

describe('LabApiService', () => {
  let service: LabApiService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [LabApiService, provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(LabApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('creates products through the management API', () => {
    const request = {
      labEnvironmentId: 'env-1',
      productCode: 'ULP',
      name: 'Unleaded Petrol',
      grade: '91',
      colorHex: '#cf5f2d',
      unitPrice: 1.53,
      currencyCode: 'USD',
      isActive: true,
    };

    service.createProduct(request).subscribe();

    const httpRequest = httpMock.expectOne('/api/products');
    expect(httpRequest.request.method).toBe('POST');
    expect(httpRequest.request.body).toEqual(request);
    httpRequest.flush({});
  });

  it('updates lab environment settings through the lifecycle endpoint', () => {
    const request = {
      name: 'Default Virtual Lab',
      description: 'Managed from the settings screen.',
      settings: {
        retention: {
          logRetentionDays: 14,
          callbackHistoryRetentionDays: 21,
          transactionRetentionDays: 45,
          preserveTimelineIntegrity: true,
        },
        backup: {
          includeRuntimeDataByDefault: true,
          includeScenarioRunsByDefault: true,
        },
        telemetry: {
          emitMetrics: true,
          emitActivities: true,
        },
      },
    };

    service.updateLabEnvironment(request).subscribe();

    const httpRequest = httpMock.expectOne('/api/lab-environment');
    expect(httpRequest.request.method).toBe('PUT');
    expect(httpRequest.request.body).toEqual(request);
    httpRequest.flush({});
  });

  it('imports lab environment packages through the restore endpoint', () => {
    const request = {
      replaceExisting: true,
      package: {
        formatVersion: 1,
        exportedAtUtc: '2026-03-15T00:00:00Z',
        includesRuntimeData: true,
        environment: {
          id: 'env-1',
          key: 'default-lab',
          name: 'Default Virtual Lab',
          description: 'Demo',
          settingsJson: '{}',
          seedVersion: 1,
          deterministicSeed: 424242,
          createdAtUtc: '2026-03-15T00:00:00Z',
          updatedAtUtc: '2026-03-15T00:00:00Z',
          lastSeededAtUtc: '2026-03-15T00:00:00Z',
        },
        profiles: [],
        products: [],
        sites: [],
        callbackTargets: [],
        pumps: [],
        nozzles: [],
        scenarioDefinitions: [],
        scenarioRuns: [],
        preAuthSessions: [],
        transactions: [],
        callbackAttempts: [],
        logs: [],
      },
    };

    service.importLabEnvironment(request).subscribe();

    const httpRequest = httpMock.expectOne('/api/lab-environment/import');
    expect(httpRequest.request.method).toBe('POST');
    expect(httpRequest.request.body).toEqual(request);
    httpRequest.flush({});
  });
});
