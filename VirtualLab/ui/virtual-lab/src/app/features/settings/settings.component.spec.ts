import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { LabApiService } from '../../core/services/lab-api.service';
import { SettingsComponent } from './settings.component';

describe('SettingsComponent', () => {
  const environment = {
    id: 'env-1',
    key: 'default-lab',
    name: 'Default Virtual Lab',
    description: 'Demo environment',
    lastSeededAtUtc: '2026-03-15T00:00:00Z',
    seedVersion: 1,
    deterministicSeed: 424242,
    createdAtUtc: '2026-03-15T00:00:00Z',
    updatedAtUtc: '2026-03-15T00:00:00Z',
    settings: {
      retention: {
        logRetentionDays: 30,
        callbackHistoryRetentionDays: 30,
        transactionRetentionDays: 90,
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
    logCategories: [
      {
        category: 'AuthFailure',
        defaultSeverity: 'Warning',
        description: 'Inbound and callback authentication failures.',
      },
    ],
  };

  function createApiSpy() {
    return jasmine.createSpyObj<LabApiService>('LabApiService', [
      'getLabEnvironment',
      'updateLabEnvironment',
      'pruneLabEnvironment',
      'exportLabEnvironment',
      'importLabEnvironment',
    ]);
  }

  it('loads environment settings into the form', async () => {
    const api = createApiSpy();
    api.getLabEnvironment.and.returnValue(of(environment));

    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [{ provide: LabApiService, useValue: api }],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(fixture.componentInstance.settingsForm.getRawValue().name).toBe('Default Virtual Lab');
    expect(fixture.nativeElement.textContent).toContain('AuthFailure');
  });

  it('saves updated environment settings', async () => {
    const api = createApiSpy();
    api.getLabEnvironment.and.returnValue(of(environment));
    api.updateLabEnvironment.and.returnValue(of(environment));

    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [{ provide: LabApiService, useValue: api }],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    fixture.componentInstance.settingsForm.controls.name.setValue('Managed Lab');
    await fixture.componentInstance.saveEnvironment();

    expect(api.updateLabEnvironment).toHaveBeenCalled();
    expect(fixture.componentInstance.actionMessage()).toContain('saved');
  });

  it('runs prune dry-runs from the lifecycle controls', async () => {
    const api = createApiSpy();
    api.getLabEnvironment.and.returnValue(of(environment));
    api.pruneLabEnvironment.and.returnValue(
      of({
        labEnvironmentId: 'env-1',
        environmentKey: 'default-lab',
        dryRun: true,
        executedAtUtc: '2026-03-15T00:00:00Z',
        logCutoffUtc: null,
        callbackCutoffUtc: null,
        transactionCutoffUtc: null,
        logsRemoved: 0,
        callbackAttemptsRemoved: 0,
        transactionsRemoved: 0,
        preAuthSessionsRemoved: 0,
        scenarioRunsPreserved: 4,
      }),
    );

    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [{ provide: LabApiService, useValue: api }],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    await fixture.componentInstance.runPrune(true);

    expect(api.pruneLabEnvironment).toHaveBeenCalledWith(
      jasmine.objectContaining({
        dryRun: true,
      }),
    );
    expect(fixture.componentInstance.pruneResult()?.dryRun).toBeTrue();
  });

  it('imports a selected environment package', async () => {
    const api = createApiSpy();
    api.getLabEnvironment.and.returnValues(of(environment), of(environment));
    api.importLabEnvironment.and.returnValue(
      of({
        labEnvironmentId: 'env-1',
        environmentKey: 'default-lab',
        replaceExisting: true,
        siteCount: 1,
        profileCount: 1,
        productCount: 1,
        scenarioDefinitionCount: 1,
        scenarioRunCount: 1,
        transactionCount: 2,
        preAuthSessionCount: 1,
        callbackAttemptCount: 1,
        logCount: 3,
      }),
    );

    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [{ provide: LabApiService, useValue: api }],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    fixture.componentInstance.importPackage.set({
      formatVersion: 1,
      exportedAtUtc: '2026-03-15T00:00:00Z',
      includesRuntimeData: true,
      environment: {
        id: 'env-1',
        key: 'default-lab',
        name: 'Default Virtual Lab',
        description: 'Demo environment',
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
    });

    await fixture.componentInstance.importEnvironment();

    expect(api.importLabEnvironment).toHaveBeenCalledWith(
      jasmine.objectContaining({
        replaceExisting: true,
      }),
    );
    expect(fixture.componentInstance.importResult()?.transactionCount).toBe(2);
  });
});
