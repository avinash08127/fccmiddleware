import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { runtimeConfig } from '../../core/config/runtime-config';
import {
  type LabEnvironmentDetail,
  type LabEnvironmentExportPackage,
  type LabEnvironmentImportResult,
  type LabEnvironmentPruneRequest,
  type LabEnvironmentPruneResult,
  type LabEnvironmentUpsertRequest,
  type ManagementErrorResponse,
  LabApiService,
} from '../../core/services/lab-api.service';

@Component({
  selector: 'vl-settings',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  template: `
    <section class="page-header">
      <div>
        <p class="eyebrow">VL-3.2 Environment Controls</p>
        <h2>Retention, backup portability, and lab lifecycle actions now stay in the browser.</h2>
        <p class="copy">
          The same management endpoints already exposed by the backend are available here for
          environment updates, dry-run pruning, export packages, and restore imports.
        </p>
      </div>

      <div class="header-actions">
        <button type="button" class="secondary" (click)="reload()" [disabled]="loading() || saving()">
          Refresh
        </button>
        <button type="button" (click)="saveEnvironment()" [disabled]="loading() || saving()">
          Save settings
        </button>
      </div>
    </section>

    <section *ngIf="actionMessage()" class="banner success">{{ actionMessage() }}</section>
    <section *ngIf="errorMessage()" class="banner error">{{ errorMessage() }}</section>

    <div class="workspace">
      <section class="column">
        <article class="panel">
          <h3>Environment</h3>
          <form class="stack" [formGroup]="settingsForm">
            <label>
              Name
              <input formControlName="name" />
            </label>

            <label>
              Description
              <textarea formControlName="description" rows="4"></textarea>
            </label>

            <div formGroupName="settings" class="stack">
              <fieldset formGroupName="retention">
                <legend>Retention</legend>
                <div class="grid-2">
                  <label>
                    Log retention days
                    <input type="number" min="1" formControlName="logRetentionDays" />
                  </label>
                  <label>
                    Callback retention days
                    <input type="number" min="1" formControlName="callbackHistoryRetentionDays" />
                  </label>
                  <label>
                    Transaction retention days
                    <input type="number" min="1" formControlName="transactionRetentionDays" />
                  </label>
                  <label class="checkbox">
                    <input type="checkbox" formControlName="preserveTimelineIntegrity" />
                    Preserve linked scenario timelines
                  </label>
                </div>
              </fieldset>

              <fieldset formGroupName="backup">
                <legend>Backup defaults</legend>
                <label class="checkbox">
                  <input type="checkbox" formControlName="includeRuntimeDataByDefault" />
                  Include runtime data in exports by default
                </label>
                <label class="checkbox">
                  <input type="checkbox" formControlName="includeScenarioRunsByDefault" />
                  Include scenario runs by default
                </label>
              </fieldset>

              <fieldset formGroupName="telemetry">
                <legend>Telemetry</legend>
                <label class="checkbox">
                  <input type="checkbox" formControlName="emitMetrics" />
                  Emit metrics
                </label>
                <label class="checkbox">
                  <input type="checkbox" formControlName="emitActivities" />
                  Emit distributed activities
                </label>
              </fieldset>
            </div>
          </form>
        </article>

        <article class="panel" *ngIf="environment() as environment">
          <h3>Metadata</h3>
          <div class="facts">
            <div>
              <span>Environment key</span>
              <strong>{{ environment.key }}</strong>
            </div>
            <div>
              <span>Seed version</span>
              <strong>{{ environment.seedVersion }}</strong>
            </div>
            <div>
              <span>Deterministic seed</span>
              <strong>{{ environment.deterministicSeed }}</strong>
            </div>
            <div>
              <span>Last seeded</span>
              <strong>{{ environment.lastSeededAtUtc || 'Never' }}</strong>
            </div>
          </div>

          <h4>Log categories</h4>
          <div class="category-list">
            <article *ngFor="let category of environment.logCategories" class="category-card">
              <strong>{{ category.category }}</strong>
              <small>{{ category.defaultSeverity }}</small>
              <p>{{ category.description }}</p>
            </article>
          </div>
        </article>
      </section>

      <section class="column">
        <article class="panel">
          <h3>Prune runtime data</h3>
          <form class="stack" [formGroup]="pruneForm">
            <div class="grid-2">
              <label>
                Log retention days
                <input type="number" min="1" formControlName="logRetentionDays" />
              </label>
              <label>
                Callback retention days
                <input type="number" min="1" formControlName="callbackHistoryRetentionDays" />
              </label>
              <label>
                Transaction retention days
                <input type="number" min="1" formControlName="transactionRetentionDays" />
              </label>
              <label class="checkbox">
                <input type="checkbox" formControlName="preserveTimelineIntegrity" />
                Preserve scenario-linked artifacts
              </label>
            </div>

            <div class="action-row">
              <button type="button" class="secondary" (click)="runPrune(true)" [disabled]="saving()">
                Dry run
              </button>
              <button type="button" class="danger" (click)="runPrune(false)" [disabled]="saving()">
                Prune now
              </button>
            </div>
          </form>

          <section *ngIf="pruneResult() as result" class="result-card">
            <h4>{{ result.dryRun ? 'Dry-run result' : 'Prune result' }}</h4>
            <div class="facts">
              <div>
                <span>Transactions removed</span>
                <strong>{{ result.transactionsRemoved }}</strong>
              </div>
              <div>
                <span>Pre-auth removed</span>
                <strong>{{ result.preAuthSessionsRemoved }}</strong>
              </div>
              <div>
                <span>Callbacks removed</span>
                <strong>{{ result.callbackAttemptsRemoved }}</strong>
              </div>
              <div>
                <span>Logs removed</span>
                <strong>{{ result.logsRemoved }}</strong>
              </div>
            </div>
          </section>
        </article>

        <article class="panel">
          <h3>Export and import</h3>
          <div class="action-row">
            <button type="button" class="secondary" (click)="exportEnvironment(false)" [disabled]="saving()">
              Export config only
            </button>
            <button type="button" (click)="exportEnvironment(true)" [disabled]="saving()">
              Export with runtime
            </button>
          </div>

          <label class="import-button">
            Import package JSON
            <input type="file" accept="application/json" (change)="handleImportFile($event)" />
          </label>

          <label class="checkbox">
            <input type="checkbox" [formControl]="importReplaceControl" />
            Replace existing environment contents during import
          </label>

          <button type="button" class="danger" (click)="importEnvironment()" [disabled]="!importPackage() || saving()">
            Import selected package
          </button>

          <section *ngIf="importSummary()" class="result-card">
            <h4>Selected import package</h4>
            <p>{{ importSummary() }}</p>
          </section>

          <section *ngIf="importResult() as result" class="result-card">
            <h4>Import result</h4>
            <div class="facts">
              <div>
                <span>Sites</span>
                <strong>{{ result.siteCount }}</strong>
              </div>
              <div>
                <span>Profiles</span>
                <strong>{{ result.profileCount }}</strong>
              </div>
              <div>
                <span>Products</span>
                <strong>{{ result.productCount }}</strong>
              </div>
              <div>
                <span>Transactions</span>
                <strong>{{ result.transactionCount }}</strong>
              </div>
            </div>
          </section>

          <label>
            Export preview
            <textarea [value]="exportJson()" rows="12" readonly></textarea>
          </label>
        </article>

        <article class="panel runtime-panel">
          <h3>Runtime wiring</h3>
          <p><strong>Environment:</strong> {{ runtime.environmentName }}</p>
          <p><strong>API base URL:</strong> <code>{{ runtime.apiBaseUrl || '(proxied locally)' }}</code></p>
          <p><strong>SignalR hub:</strong> <code>{{ runtime.signalRHubUrl }}</code></p>
        </article>
      </section>
    </div>
  `,
  styles: `
    .page-header,
    .action-row,
    .facts {
      align-items: start;
      display: flex;
      gap: 1rem;
      justify-content: space-between;
    }

    .workspace {
      display: grid;
      gap: 1.5rem;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      margin-top: 1.5rem;
    }

    .column {
      display: grid;
      gap: 1rem;
    }

    .panel {
      background: var(--vl-panel);
      border: 1px solid var(--vl-line);
      border-radius: 24px;
      box-shadow: var(--vl-shadow);
      padding: 1.25rem;
    }

    .stack {
      display: grid;
      gap: 1rem;
    }

    fieldset {
      border: 1px solid rgba(51, 44, 33, 0.12);
      border-radius: 18px;
      margin: 0;
      padding: 1rem;
    }

    legend {
      color: var(--vl-accent);
      font-weight: 600;
      padding: 0 0.35rem;
    }

    label {
      color: var(--vl-muted);
      display: grid;
      gap: 0.45rem;
    }

    input,
    textarea {
      background: rgba(255, 255, 255, 0.88);
      border: 1px solid rgba(51, 44, 33, 0.12);
      border-radius: 14px;
      color: var(--vl-text);
      padding: 0.75rem 0.9rem;
      width: 100%;
    }

    textarea {
      resize: vertical;
    }

    .grid-2 {
      display: grid;
      gap: 1rem;
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }

    .checkbox {
      align-items: center;
      display: flex;
      gap: 0.6rem;
    }

    .checkbox input {
      width: auto;
    }

    button,
    .import-button {
      background: var(--vl-accent);
      border: none;
      border-radius: 999px;
      color: #fff;
      cursor: pointer;
      display: inline-flex;
      font: inherit;
      justify-content: center;
      padding: 0.8rem 1.1rem;
      text-decoration: none;
    }

    button.secondary {
      background: rgba(255, 255, 255, 0.7);
      color: var(--vl-text);
    }

    button.danger {
      background: #9f2c2c;
    }

    button:disabled {
      cursor: wait;
      opacity: 0.7;
    }

    .import-button {
      align-items: center;
      max-width: fit-content;
      position: relative;
    }

    .import-button input {
      inset: 0;
      opacity: 0;
      position: absolute;
    }

    .facts {
      flex-wrap: wrap;
    }

    .facts div,
    .category-card,
    .result-card {
      background: rgba(255, 255, 255, 0.58);
      border: 1px solid rgba(51, 44, 33, 0.08);
      border-radius: 18px;
      padding: 0.9rem 1rem;
    }

    .facts div {
      min-width: 10rem;
    }

    .facts span,
    .category-card small,
    .result-card p,
    .runtime-panel p {
      color: var(--vl-muted);
      display: block;
    }

    .category-list {
      display: grid;
      gap: 0.75rem;
      margin-top: 1rem;
    }

    .category-card p,
    .result-card p,
    .runtime-panel p {
      margin-bottom: 0;
    }

    .banner {
      border-radius: 18px;
      margin-top: 1rem;
      padding: 0.9rem 1rem;
    }

    .banner.success {
      background: rgba(29, 122, 90, 0.12);
      color: var(--vl-emerald);
    }

    .banner.error {
      background: rgba(159, 44, 44, 0.12);
      color: #9f2c2c;
    }

    @media (max-width: 960px) {
      .workspace,
      .grid-2 {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class SettingsComponent implements OnInit {
  private readonly api = inject(LabApiService);
  private readonly fb = inject(FormBuilder);

  readonly runtime = runtimeConfig;
  readonly environment = signal<LabEnvironmentDetail | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal('');
  readonly actionMessage = signal('');
  readonly pruneResult = signal<LabEnvironmentPruneResult | null>(null);
  readonly exportJson = signal('');
  readonly importPackage = signal<LabEnvironmentExportPackage | null>(null);
  readonly importSummary = signal('');
  readonly importResult = signal<LabEnvironmentImportResult | null>(null);

  readonly settingsForm = this.fb.nonNullable.group({
    name: ['', Validators.required],
    description: [''],
    settings: this.fb.nonNullable.group({
      retention: this.fb.nonNullable.group({
        logRetentionDays: [30, [Validators.required, Validators.min(1)]],
        callbackHistoryRetentionDays: [30, [Validators.required, Validators.min(1)]],
        transactionRetentionDays: [90, [Validators.required, Validators.min(1)]],
        preserveTimelineIntegrity: [true],
      }),
      backup: this.fb.nonNullable.group({
        includeRuntimeDataByDefault: [true],
        includeScenarioRunsByDefault: [true],
      }),
      telemetry: this.fb.nonNullable.group({
        emitMetrics: [true],
        emitActivities: [true],
      }),
    }),
  });

  readonly pruneForm = this.fb.nonNullable.group({
    logRetentionDays: [30, [Validators.required, Validators.min(1)]],
    callbackHistoryRetentionDays: [30, [Validators.required, Validators.min(1)]],
    transactionRetentionDays: [90, [Validators.required, Validators.min(1)]],
    preserveTimelineIntegrity: [true],
  });

  readonly importReplaceControl = this.fb.nonNullable.control(true);

  ngOnInit(): void {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set('');

    try {
      const environment = await firstValueFrom(this.api.getLabEnvironment());
      this.environment.set(environment);
      this.settingsForm.reset({
        name: environment.name,
        description: environment.description,
        settings: environment.settings,
      });
      this.pruneForm.reset({
        logRetentionDays: environment.settings.retention.logRetentionDays,
        callbackHistoryRetentionDays: environment.settings.retention.callbackHistoryRetentionDays,
        transactionRetentionDays: environment.settings.retention.transactionRetentionDays,
        preserveTimelineIntegrity: environment.settings.retention.preserveTimelineIntegrity,
      });
    } catch (error) {
      this.errorMessage.set(this.describeError(error, 'Unable to load environment settings.'));
    } finally {
      this.loading.set(false);
    }
  }

  async saveEnvironment(): Promise<void> {
    if (this.settingsForm.invalid) {
      this.settingsForm.markAllAsTouched();
      this.errorMessage.set('Review the environment settings before saving.');
      return;
    }

    this.saving.set(true);
    this.errorMessage.set('');
    this.actionMessage.set('');

    try {
      const request = this.settingsForm.getRawValue() as LabEnvironmentUpsertRequest;
      const environment = await firstValueFrom(this.api.updateLabEnvironment(request));
      this.environment.set(environment);
      this.actionMessage.set('Environment settings saved.');
    } catch (error) {
      this.errorMessage.set(this.describeError(error, 'Unable to save environment settings.'));
    } finally {
      this.saving.set(false);
    }
  }

  async runPrune(dryRun: boolean): Promise<void> {
    if (this.pruneForm.invalid) {
      this.pruneForm.markAllAsTouched();
      this.errorMessage.set('Review the prune retention values before continuing.');
      return;
    }

    this.saving.set(true);
    this.errorMessage.set('');
    this.actionMessage.set('');

    try {
      const request = {
        dryRun,
        ...this.pruneForm.getRawValue(),
      } as LabEnvironmentPruneRequest;

      const result = await firstValueFrom(this.api.pruneLabEnvironment(request));
      this.pruneResult.set(result);
      this.actionMessage.set(dryRun ? 'Dry-run prune completed.' : 'Runtime data prune completed.');
    } catch (error) {
      this.errorMessage.set(this.describeError(error, 'Unable to prune environment data.'));
    } finally {
      this.saving.set(false);
    }
  }

  async exportEnvironment(includeRuntimeData: boolean): Promise<void> {
    this.saving.set(true);
    this.errorMessage.set('');
    this.actionMessage.set('');

    try {
      const packageJson = await firstValueFrom(this.api.exportLabEnvironment(includeRuntimeData));
      this.exportJson.set(JSON.stringify(packageJson, null, 2));
      this.actionMessage.set(
        includeRuntimeData ? 'Exported environment with runtime data.' : 'Exported configuration package.',
      );
    } catch (error) {
      this.errorMessage.set(this.describeError(error, 'Unable to export environment package.'));
    } finally {
      this.saving.set(false);
    }
  }

  async importEnvironment(): Promise<void> {
    const packageJson = this.importPackage();
    if (!packageJson) {
      this.errorMessage.set('Select an export package before importing.');
      return;
    }

    this.saving.set(true);
    this.errorMessage.set('');
    this.actionMessage.set('');

    try {
      const result = await firstValueFrom(
        this.api.importLabEnvironment({
          replaceExisting: this.importReplaceControl.getRawValue(),
          package: packageJson,
        }),
      );
      this.importResult.set(result);
      this.actionMessage.set('Environment import completed.');
      await this.reload();
    } catch (error) {
      this.errorMessage.set(this.describeError(error, 'Unable to import environment package.'));
    } finally {
      this.saving.set(false);
    }
  }

  async handleImportFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }

    try {
      const text = await file.text();
      const packageJson = JSON.parse(text) as LabEnvironmentExportPackage;
      this.importPackage.set(packageJson);
      this.importSummary.set(
        `${packageJson.environment.name} · format ${packageJson.formatVersion} · ${packageJson.sites.length} sites · ${packageJson.products.length} products · ${packageJson.transactions.length} transactions`,
      );
      this.errorMessage.set('');
      this.actionMessage.set(`Loaded import package ${file.name}.`);
    } catch {
      this.importPackage.set(null);
      this.importSummary.set('');
      this.errorMessage.set('The selected file is not a valid environment export package.');
    } finally {
      input.value = '';
    }
  }

  private describeError(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      const payload = error.error as ManagementErrorResponse | undefined;
      if (payload?.message) {
        return payload.message;
      }

      if (typeof error.error === 'string' && error.error.length > 0) {
        return error.error;
      }
    }

    return fallback;
  }
}
