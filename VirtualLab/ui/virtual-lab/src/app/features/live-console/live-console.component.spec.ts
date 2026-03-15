import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { LabApiService } from '../../core/services/lab-api.service';
import { LiveUpdatesService } from '../../core/services/live-updates.service';
import { LiveConsoleComponent } from './live-console.component';

describe('LiveConsoleComponent', () => {
  it('loads the first site and records relevant live updates', async () => {
    const events = new Subject<any>();
    const api = jasmine.createSpyObj<LabApiService>('LabApiService', [
      'getSites',
      'getForecourt',
      'getTransactions',
      'getLogs',
      'liftNozzle',
      'dispense',
      'hangNozzle',
    ]);
    const liveUpdates = {
      connectionState: signal<'idle' | 'connecting' | 'connected' | 'error'>('connected'),
      events$: events.asObservable(),
    };

    api.getSites.and.returnValue(
      of([
        {
          id: 'site-1',
          labEnvironmentId: 'env-1',
          siteCode: 'VL-MW-BT001',
          name: 'Demo Site',
          timeZone: 'UTC',
          currencyCode: 'USD',
          externalReference: 'demo',
          isActive: true,
          inboundAuthMode: 'None',
          apiKeyHeaderName: '',
          apiKeyValue: '',
          basicAuthUsername: '',
          basicAuthPassword: '',
          deliveryMode: 'Hybrid',
          preAuthMode: 'CreateThenAuthorize',
          settings: {
            isTemplate: false,
            defaultCallbackTargetKey: '',
            pullPageSize: 100,
            fiscalization: {
              mode: 'NONE',
              requireCustomerTaxId: false,
              fiscalReceiptRequired: false,
              taxAuthorityName: '',
              taxAuthorityEndpoint: '',
            },
          },
          activeProfile: {
            id: 'profile-1',
            profileKey: 'doms-like',
            name: 'DOMS',
            vendorFamily: 'DOMS',
            authMode: 'ApiKey',
            deliveryMode: 'Hybrid',
            preAuthMode: 'CreateThenAuthorize',
            isActive: true,
            isDefault: true,
          },
          forecourt: {
            pumpCount: 1,
            nozzleCount: 1,
            activePumpCount: 1,
            activeNozzleCount: 1,
          },
          compatibility: {
            isValid: true,
            messages: [],
          },
        },
      ]),
    );
    api.getForecourt.and.returnValues(
      of({
        siteId: 'site-1',
        siteCode: 'VL-MW-BT001',
        siteName: 'Demo Site',
        pumps: [
          {
            id: 'pump-1',
            pumpNumber: 1,
            fccPumpNumber: 1,
            layoutX: 0,
            layoutY: 0,
            label: 'Pump 1',
            isActive: true,
            nozzles: [
              {
                id: 'nozzle-1',
                productId: 'product-1',
                productCode: 'ULP',
                productName: 'Unleaded Petrol',
                nozzleNumber: 1,
                fccNozzleNumber: 1,
                label: 'Nozzle 1',
                state: 'Idle',
                isActive: true,
              },
            ],
          },
        ],
      }),
      of({
        siteId: 'site-1',
        siteCode: 'VL-MW-BT001',
        siteName: 'Demo Site',
        pumps: [
          {
            id: 'pump-1',
            pumpNumber: 1,
            fccPumpNumber: 1,
            layoutX: 0,
            layoutY: 0,
            label: 'Pump 1',
            isActive: true,
            nozzles: [
              {
                id: 'nozzle-1',
                productId: 'product-1',
                productCode: 'ULP',
                productName: 'Unleaded Petrol',
                nozzleNumber: 1,
                fccNozzleNumber: 1,
                label: 'Nozzle 1',
                state: 'Lifted',
                isActive: true,
              },
            ],
          },
        ],
      }),
    );
    api.getTransactions.and.returnValues(of([]), of([]));
    api.getLogs.and.returnValues(of([]), of([]));

    await TestBed.configureTestingModule({
      imports: [LiveConsoleComponent],
      providers: [
        { provide: LabApiService, useValue: api },
        { provide: LiveUpdatesService, useValue: liveUpdates },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(LiveConsoleComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    events.next({
      eventType: 'forecourt-action',
      action: 'lift',
      occurredAtUtc: '2026-03-15T00:00:00Z',
      correlationId: 'corr-1',
      message: 'Nozzle lifted.',
      transactionGenerated: false,
      faulted: false,
      nozzle: {
        siteId: 'site-1',
        pumpId: 'pump-1',
        nozzleId: 'nozzle-1',
        siteCode: 'VL-MW-BT001',
        pumpNumber: 1,
        nozzleNumber: 1,
        label: 'Nozzle 1',
        state: 'Lifted',
        productCode: 'ULP',
        productName: 'Unleaded Petrol',
        unitPrice: 1.53,
        currencyCode: 'USD',
        correlationId: 'corr-1',
        preAuthSessionId: null,
        simulationStateJson: '{}',
        updatedAtUtc: '2026-03-15T00:00:00Z',
      },
      transaction: null,
    });

    await fixture.whenStable();

    expect(fixture.componentInstance.selectedSite()?.siteCode).toBe('VL-MW-BT001');
    expect(fixture.componentInstance.liveFeed().length).toBe(1);
    expect(fixture.componentInstance.liveFeed()[0].correlationId).toBe('corr-1');
  });
});
