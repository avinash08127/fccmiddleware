import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { catchError, forkJoin, of } from 'rxjs';
import {
  FccAuthConfiguration,
  FccDeliveryCapabilities,
  FccExtensionPointDefinition,
  FccFailureSimulationDefinition,
  FccProfilePreviewResult,
  FccProfileRecord,
  FccProfileSummary,
  FccProfileValidationMessage,
  FccProfileValidationResult,
  LabApiService,
  LabEnvironmentSummary,
  PreAuthFlowMode,
  SimulatedAuthMode,
  TransactionDeliveryMode,
} from '../../core/services/lab-api.service';

@Component({
  selector: 'vl-fcc-profiles',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  template: `
    <section class="page-header">
      <div>
        <p class="eyebrow">VL-2.2 FCC Profiles</p>
        <h2>Profile contracts stay data-driven and inspectable.</h2>
        <p class="copy">
          Auth mode, pre-auth mode, endpoints, templates, validation rules, and mappings are all
          editable here. JSON parse errors and backend validation failures stay visible.
        </p>
      </div>

      <div class="header-actions">
        <button type="button" class="secondary" (click)="prepareNewProfile()">New profile</button>
        <button type="button" class="secondary" (click)="validateProfile()">Validate</button>
        <button type="button" (click)="saveProfile()" [disabled]="saving()">
          {{ selectedProfileId() ? 'Save changes' : 'Create profile' }}
        </button>
      </div>
    </section>

    <section *ngIf="actionMessage()" class="banner success">{{ actionMessage() }}</section>
    <section *ngIf="errorMessage()" class="banner error">{{ errorMessage() }}</section>

    <div class="workspace">
      <aside class="profile-list">
        <article
          *ngFor="let profile of profiles()"
          class="profile-tile"
          [class.active]="profile.id === selectedProfileId()"
          (click)="selectProfile(profile.id)"
          (keydown.enter)="selectProfile(profile.id)"
          (keydown.space)="selectProfile(profile.id)"
          tabindex="0"
        >
          <div class="tile-header">
            <div>
              <strong>{{ profile.name }}</strong>
              <p>{{ profile.profileKey }}</p>
            </div>
            <span class="status-chip" [class.invalid]="!profile.isActive">
              {{ profile.isActive ? 'Active' : 'Archived' }}
            </span>
          </div>
          <p>{{ profile.authMode }} · {{ profile.deliveryMode }} · {{ profile.preAuthMode }}</p>
          <small>{{ profile.vendorFamily || 'No vendor family' }}</small>
        </article>
      </aside>

      <section class="editor">
        <div class="panel-toolbar">
          <div>
            <h3>{{ selectedProfileId() ? 'Edit profile' : 'Create profile' }}</h3>
            <p *ngIf="selectedProfileSummary(); else newProfileHint">
              {{ selectedProfileSummary()!.profileKey }} · {{ selectedProfileSummary()!.vendorFamily }}
            </p>
            <ng-template #newProfileHint>
              <p>Import an existing profile JSON or build one from scratch.</p>
            </ng-template>
          </div>

          <div class="toolbar-actions">
            <label class="import-button secondary">
              Import JSON
              <input type="file" accept="application/json" (change)="handleImport($event)" />
            </label>
            <button type="button" class="secondary" (click)="exportProfile()">Export JSON</button>
            <button type="button" class="danger" *ngIf="selectedProfileId()" (click)="archiveProfile()">
              Archive
            </button>
          </div>
        </div>

        <section *ngIf="validationMessages().length" class="validation-panel">
          <h4>Validation</h4>
          <ul>
            <li *ngFor="let message of validationMessages()">
              <strong>{{ message.path }}</strong>: {{ message.message }}
            </li>
          </ul>
        </section>

        <form class="editor-grid" [formGroup]="profileForm">
          <article class="panel">
            <h4>Identity</h4>
            <label>
              Profile key
              <input formControlName="profileKey" />
            </label>
            <label>
              Name
              <input formControlName="name" />
            </label>
            <label>
              Vendor family
              <input formControlName="vendorFamily" />
            </label>
            <div class="split">
              <label>
                Delivery mode
                <select formControlName="deliveryMode">
                  <option *ngFor="let mode of deliveryModes" [value]="mode">{{ mode }}</option>
                </select>
              </label>
              <label>
                Pre-auth mode
                <select formControlName="preAuthMode">
                  <option *ngFor="let mode of preAuthModes" [value]="mode">{{ mode }}</option>
                </select>
              </label>
            </div>
            <div class="split">
              <label class="checkbox">
                <input type="checkbox" formControlName="isActive" />
                Active
              </label>
              <label class="checkbox">
                <input type="checkbox" formControlName="isDefault" />
                Default
              </label>
            </div>
          </article>

          <article class="panel">
            <h4>Auth and capabilities</h4>
            <label>
              Auth mode
              <select formControlName="authMode">
                <option *ngFor="let mode of authModes" [value]="mode">{{ mode }}</option>
              </select>
            </label>

            <div class="stack" *ngIf="profileForm.controls.authMode.value === 'ApiKey'">
              <label>
                API key header
                <input formControlName="apiKeyHeaderName" />
              </label>
              <label>
                API key value
                <input formControlName="apiKeyValue" />
              </label>
            </div>

            <div class="stack" *ngIf="profileForm.controls.authMode.value === 'BasicAuth'">
              <label>
                Basic auth username
                <input formControlName="basicAuthUsername" />
              </label>
              <label>
                Basic auth password
                <input type="password" formControlName="basicAuthPassword" />
              </label>
            </div>

            <div class="split">
              <label class="checkbox">
                <input type="checkbox" formControlName="supportsPush" />
                Supports push
              </label>
              <label class="checkbox">
                <input type="checkbox" formControlName="supportsPull" />
                Supports pull
              </label>
            </div>
            <div class="split">
              <label class="checkbox">
                <input type="checkbox" formControlName="supportsHybrid" />
                Supports hybrid
              </label>
              <label class="checkbox">
                <input type="checkbox" formControlName="supportsPreAuthCancellation" />
                Supports pre-auth cancellation
              </label>
            </div>
          </article>

          <article class="panel span-2">
            <h4>Endpoint surface JSON</h4>
            <textarea formControlName="endpointSurfaceJson" rows="12"></textarea>
          </article>

          <article class="panel span-2">
            <h4>Request templates JSON</h4>
            <textarea formControlName="requestTemplatesJson" rows="14"></textarea>
          </article>

          <article class="panel span-2">
            <h4>Response templates JSON</h4>
            <textarea formControlName="responseTemplatesJson" rows="14"></textarea>
          </article>

          <article class="panel">
            <h4>Validation rules JSON</h4>
            <textarea formControlName="validationRulesJson" rows="10"></textarea>
          </article>

          <article class="panel">
            <h4>Field mappings JSON</h4>
            <textarea formControlName="fieldMappingsJson" rows="10"></textarea>
          </article>

          <article class="panel">
            <h4>Failure simulation JSON</h4>
            <textarea formControlName="failureSimulationJson" rows="9"></textarea>
          </article>

          <article class="panel">
            <h4>Extensions JSON</h4>
            <textarea formControlName="extensionsJson" rows="9"></textarea>
          </article>

          <article class="panel span-2">
            <div class="section-header">
              <div>
                <h4>Preview</h4>
                <p>Render the current draft without saving it.</p>
              </div>
              <button type="button" class="secondary" (click)="previewProfile()">Render preview</button>
            </div>

            <div class="split preview-controls">
              <label>
                Operation
                <input formControlName="previewOperation" />
              </label>
            </div>

            <div *ngIf="previewResult()" class="preview-grid">
              <section>
                <h5>Request headers</h5>
                <pre>{{ previewResult()!.requestHeaders | json }}</pre>
              </section>
              <section>
                <h5>Response headers</h5>
                <pre>{{ previewResult()!.responseHeaders | json }}</pre>
              </section>
              <section>
                <h5>Request body</h5>
                <pre>{{ previewResult()!.requestBody }}</pre>
              </section>
              <section>
                <h5>Response body</h5>
                <pre>{{ previewResult()!.responseBody }}</pre>
              </section>
            </div>
          </article>
        </form>
      </section>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }

    h2,
    h3,
    h4,
    h5,
    p,
    pre {
      margin: 0;
    }

    .eyebrow {
      color: var(--vl-accent);
      font-size: 0.8rem;
      letter-spacing: 0.16em;
      margin-bottom: 0.75rem;
      text-transform: uppercase;
    }

    .copy,
    .profile-tile p,
    .profile-tile small,
    .section-header p,
    .banner {
      color: var(--vl-muted);
    }

    .page-header,
    .workspace,
    .editor-grid,
    .split,
    .preview-grid {
      display: grid;
      gap: 1rem;
    }

    .page-header {
      align-items: start;
      grid-template-columns: minmax(0, 1.8fr) auto;
      margin-bottom: 1rem;
    }

    .header-actions,
    .toolbar-actions,
    .tile-header,
    .panel-toolbar,
    .section-header {
      display: flex;
      gap: 0.75rem;
      justify-content: space-between;
    }

    .workspace {
      grid-template-columns: 320px minmax(0, 1fr);
    }

    .profile-list,
    .editor,
    .profile-tile,
    .panel,
    .banner,
    .validation-panel {
      background: var(--vl-panel-strong);
      border: 1px solid var(--vl-line);
      border-radius: 20px;
      box-shadow: var(--vl-shadow);
    }

    .profile-list,
    .editor {
      padding: 1rem;
    }

    .profile-list {
      display: grid;
      gap: 0.75rem;
      align-content: start;
      max-height: calc(100vh - 8rem);
      overflow: auto;
    }

    .profile-tile {
      cursor: pointer;
      padding: 1rem;
    }

    .profile-tile.active {
      border-color: rgba(207, 95, 45, 0.4);
      transform: translateY(-1px);
    }

    .status-chip {
      align-items: center;
      background: rgba(29, 122, 90, 0.12);
      border-radius: 999px;
      color: var(--vl-emerald);
      display: inline-flex;
      font-size: 0.8rem;
      font-weight: 600;
      padding: 0.25rem 0.6rem;
    }

    .status-chip.invalid {
      background: rgba(207, 95, 45, 0.14);
      color: var(--vl-accent);
    }

    .editor {
      display: grid;
      gap: 1rem;
    }

    .validation-panel,
    .panel {
      padding: 1rem;
    }

    .validation-panel ul {
      margin: 0.75rem 0 0;
      padding-left: 1rem;
    }

    .editor-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }

    .span-2 {
      grid-column: span 2;
    }

    .panel {
      display: grid;
      gap: 0.9rem;
    }

    .split {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }

    label {
      color: var(--vl-muted);
      display: grid;
      gap: 0.35rem;
      font-size: 0.95rem;
    }

    input,
    select,
    textarea {
      background: rgba(255, 255, 255, 0.75);
      border: 1px solid var(--vl-line);
      border-radius: 12px;
      color: var(--vl-text);
      min-width: 0;
      padding: 0.75rem 0.9rem;
    }

    textarea {
      font-family: 'IBM Plex Mono', 'Consolas', monospace;
      resize: vertical;
    }

    button,
    .import-button {
      background: var(--vl-accent);
      border: none;
      border-radius: 999px;
      color: white;
      cursor: pointer;
      display: inline-flex;
      justify-content: center;
      padding: 0.75rem 1rem;
      text-align: center;
    }

    button.secondary,
    .import-button.secondary {
      background: rgba(207, 95, 45, 0.12);
      color: var(--vl-accent);
    }

    button.danger {
      background: #8b1e1e;
    }

    .import-button input {
      display: none;
    }

    .banner {
      margin-bottom: 1rem;
      padding: 1rem 1.25rem;
    }

    .banner.success {
      color: var(--vl-emerald);
    }

    .banner.error {
      color: #8b1e1e;
    }

    .checkbox {
      align-items: center;
      display: flex;
      gap: 0.6rem;
    }

    .checkbox input {
      min-width: auto;
      width: auto;
    }

    .preview-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }

    pre {
      background: rgba(22, 26, 29, 0.94);
      border-radius: 16px;
      color: #f9efe1;
      font-family: 'IBM Plex Mono', 'Consolas', monospace;
      overflow: auto;
      padding: 1rem;
      white-space: pre-wrap;
      word-break: break-word;
    }

    @media (max-width: 1200px) {
      .workspace,
      .page-header,
      .editor-grid,
      .preview-grid {
        grid-template-columns: 1fr;
      }

      .span-2 {
        grid-column: span 1;
      }
    }

    @media (max-width: 720px) {
      .header-actions,
      .toolbar-actions,
      .tile-header,
      .panel-toolbar,
      .section-header {
        flex-direction: column;
      }

      .split {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class FccProfilesComponent {
  private readonly api = inject(LabApiService);
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  readonly authModes: SimulatedAuthMode[] = ['None', 'ApiKey', 'BasicAuth'];
  readonly deliveryModes: TransactionDeliveryMode[] = ['Push', 'Pull', 'Hybrid'];
  readonly preAuthModes: PreAuthFlowMode[] = ['CreateOnly', 'CreateThenAuthorize'];

  readonly profiles = signal<FccProfileSummary[]>([]);
  readonly environment = signal<LabEnvironmentSummary | null>(null);
  readonly selectedProfileSummary = signal<FccProfileSummary | null>(null);
  readonly selectedProfileId = signal<string | null>(null);
  readonly validationMessages = signal<FccProfileValidationMessage[]>([]);
  readonly previewResult = signal<FccProfilePreviewResult | null>(null);
  readonly actionMessage = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly saving = signal(false);

  readonly profileForm = this.fb.group({
    profileKey: [''],
    name: [''],
    vendorFamily: [''],
    deliveryMode: ['Pull' as TransactionDeliveryMode],
    preAuthMode: ['CreateOnly' as PreAuthFlowMode],
    authMode: ['None' as SimulatedAuthMode],
    apiKeyHeaderName: [''],
    apiKeyValue: [''],
    basicAuthUsername: [''],
    basicAuthPassword: [''],
    isActive: [true],
    isDefault: [false],
    supportsPush: [false],
    supportsPull: [true],
    supportsHybrid: [false],
    supportsPreAuthCancellation: [true],
    endpointSurfaceJson: ['[]'],
    requestTemplatesJson: ['[]'],
    responseTemplatesJson: ['[]'],
    validationRulesJson: ['[]'],
    fieldMappingsJson: ['[]'],
    failureSimulationJson: ['{\n  "simulatedDelayMs": 0,\n  "enabled": false,\n  "failureRatePercent": 0,\n  "httpStatusCode": 500,\n  "errorCode": "",\n  "messageTemplate": ""\n}'],
    extensionsJson: ['{\n  "resolverKey": "",\n  "configuration": {}\n}'],
    previewOperation: ['health'],
  });

  constructor() {
    forkJoin({
      environment: this.api.getLabEnvironment().pipe(catchError(() => of(null))),
      profiles: this.api.getProfiles().pipe(catchError(() => of([] as FccProfileSummary[]))),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(({ environment, profiles }) => {
        this.environment.set(environment);
        this.profiles.set(profiles);

        if (profiles.length > 0) {
          this.selectProfile(profiles[0].id);
          return;
        }

        this.prepareNewProfile();
      });
  }

  prepareNewProfile(): void {
    this.selectedProfileId.set(null);
    this.selectedProfileSummary.set(null);
    this.validationMessages.set([]);
    this.previewResult.set(null);
    this.errorMessage.set(null);
    this.actionMessage.set(null);

    this.profileForm.reset({
      profileKey: '',
      name: '',
      vendorFamily: '',
      deliveryMode: 'Pull',
      preAuthMode: 'CreateOnly',
      authMode: 'None',
      apiKeyHeaderName: '',
      apiKeyValue: '',
      basicAuthUsername: '',
      basicAuthPassword: '',
      isActive: true,
      isDefault: false,
      supportsPush: false,
      supportsPull: true,
      supportsHybrid: false,
      supportsPreAuthCancellation: true,
      endpointSurfaceJson: '[]',
      requestTemplatesJson: '[]',
      responseTemplatesJson: '[]',
      validationRulesJson: '[]',
      fieldMappingsJson: '[]',
      failureSimulationJson: this.stringifyJson({
        simulatedDelayMs: 0,
        enabled: false,
        failureRatePercent: 0,
        httpStatusCode: 500,
        errorCode: '',
        messageTemplate: '',
      }),
      extensionsJson: this.stringifyJson({
        resolverKey: '',
        configuration: {},
      }),
      previewOperation: 'health',
    });
  }

  selectProfile(id: string): void {
    this.api
      .getProfile(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: profile => {
          const summary = this.profiles().find(item => item.id === id) ?? null;
          this.selectedProfileId.set(id);
          this.selectedProfileSummary.set(summary);
          this.validationMessages.set([]);
          this.previewResult.set(null);
          this.patchProfileForm(profile);
        },
        error: () => {
          this.errorMessage.set('Profile details could not be loaded.');
        },
      });
  }

  validateProfile(): void {
    const draft = this.buildDraft();
    if (!draft) {
      return;
    }

    this.api
      .validateProfile(draft)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.validationMessages.set(result.messages);
          this.errorMessage.set(result.isValid ? null : 'Profile validation reported errors.');
          this.actionMessage.set(result.isValid ? 'Profile validation passed.' : null);
        },
        error: () => {
          this.errorMessage.set('Profile validation request failed.');
        },
      });
  }

  previewProfile(): void {
    const draft = this.buildDraft();
    if (!draft) {
      return;
    }

    this.api
      .previewProfile({
        profileId: this.selectedProfileId(),
        draft,
        operation: this.profileForm.controls.previewOperation.value ?? 'health',
        sampleValues: null,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: preview => {
          this.previewResult.set(preview);
          this.errorMessage.set(null);
        },
        error: (error: HttpErrorResponse) => {
          this.errorMessage.set(error.error?.message ?? 'Profile preview failed.');
        },
      });
  }

  saveProfile(): void {
    const draft = this.buildDraft();
    if (!draft) {
      return;
    }

    this.saving.set(true);
    this.errorMessage.set(null);
    this.actionMessage.set(null);

    const request$ = this.selectedProfileId()
      ? this.api.updateProfile(this.selectedProfileId()!, draft)
      : this.api.createProfile(draft);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: profile => {
        this.saving.set(false);
        this.actionMessage.set(`Profile '${profile.profileKey}' saved.`);
        this.refreshProfiles(profile.id ?? null);
      },
      error: (error: HttpErrorResponse) => {
        this.saving.set(false);
        this.handleValidationError(error, 'Profile could not be saved.');
      },
    });
  }

  archiveProfile(): void {
    if (!this.selectedProfileId()) {
      return;
    }

    this.api
      .archiveProfile(this.selectedProfileId()!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: profile => {
          this.actionMessage.set(`Profile '${profile.profileKey}' archived.`);
          this.refreshProfiles(null);
        },
        error: (error: HttpErrorResponse) => {
          this.errorMessage.set(error.error?.message ?? 'Profile could not be archived.');
        },
      });
  }

  exportProfile(): void {
    const draft = this.buildDraft();
    if (!draft) {
      return;
    }

    const blob = new Blob([this.stringifyJson(draft)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `${draft.profileKey || 'fcc-profile'}.json`;
    anchor.click();
    URL.revokeObjectURL(url);
  }

  handleImport(event: Event): void {
    const input = event.target as HTMLInputElement | null;
    const file = input?.files?.[0];

    if (!file) {
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      try {
        const imported = JSON.parse(String(reader.result ?? '{}')) as FccProfileRecord;
        this.selectedProfileId.set(imported.id ?? null);
        this.selectedProfileSummary.set(null);
        this.patchProfileForm(imported);
        this.validationMessages.set([]);
        this.previewResult.set(null);
        this.actionMessage.set(`Imported profile draft '${imported.profileKey}'.`);
        this.errorMessage.set(null);
      } catch {
        this.errorMessage.set('Imported file is not valid profile JSON.');
      }
    };
    reader.readAsText(file);

    if (input) {
      input.value = '';
    }
  }

  private refreshProfiles(selectId: string | null): void {
    this.api
      .getProfiles()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: profiles => {
          this.profiles.set(profiles);

          if (selectId) {
            const match = profiles.find(profile => profile.id === selectId);
            if (match) {
              this.selectProfile(selectId);
              return;
            }
          }

          if (profiles.length > 0) {
            this.selectProfile(profiles[0].id);
            return;
          }

          this.prepareNewProfile();
        },
        error: () => {
          this.errorMessage.set('Profile list could not be refreshed.');
        },
      });
  }

  private patchProfileForm(profile: FccProfileRecord): void {
    const auth = profile.contract.auth;
    const capabilities = profile.contract.capabilities;

    this.profileForm.reset({
      profileKey: profile.profileKey,
      name: profile.name,
      vendorFamily: profile.vendorFamily,
      deliveryMode: profile.deliveryMode,
      preAuthMode: profile.contract.preAuthMode,
      authMode: auth.mode,
      apiKeyHeaderName: auth.apiKeyHeaderName,
      apiKeyValue: auth.apiKeyValue,
      basicAuthUsername: auth.basicAuthUsername,
      basicAuthPassword: auth.basicAuthPassword,
      isActive: profile.isActive,
      isDefault: profile.isDefault,
      supportsPush: capabilities.supportsPush,
      supportsPull: capabilities.supportsPull,
      supportsHybrid: capabilities.supportsHybrid,
      supportsPreAuthCancellation: capabilities.supportsPreAuthCancellation,
      endpointSurfaceJson: this.stringifyJson(profile.contract.endpointSurface),
      requestTemplatesJson: this.stringifyJson(profile.contract.requestTemplates),
      responseTemplatesJson: this.stringifyJson(profile.contract.responseTemplates),
      validationRulesJson: this.stringifyJson(profile.contract.validationRules),
      fieldMappingsJson: this.stringifyJson(profile.contract.fieldMappings),
      failureSimulationJson: this.stringifyJson(profile.contract.failureSimulation),
      extensionsJson: this.stringifyJson(profile.contract.extensions),
      previewOperation: profile.contract.endpointSurface[0]?.operation ?? 'health',
    });
  }

  private buildDraft(): FccProfileRecord | null {
    const environmentId = this.environment()?.id;
    if (!environmentId) {
      this.errorMessage.set('No lab environment is available for profile management.');
      return null;
    }

    const parseErrors: FccProfileValidationMessage[] = [];
    const parseJson = <T>(path: string, value: string, fallback: T): T => {
      try {
        return JSON.parse(value) as T;
      } catch {
        parseErrors.push({
          path,
          message: `Invalid JSON in ${path}.`,
          severity: 'Error',
        });
        return fallback;
      }
    };

    const endpointSurface = parseJson('contract.endpointSurface', this.profileForm.controls.endpointSurfaceJson.value ?? '[]', []);
    const requestTemplates = parseJson('contract.requestTemplates', this.profileForm.controls.requestTemplatesJson.value ?? '[]', []);
    const responseTemplates = parseJson('contract.responseTemplates', this.profileForm.controls.responseTemplatesJson.value ?? '[]', []);
    const validationRules = parseJson('contract.validationRules', this.profileForm.controls.validationRulesJson.value ?? '[]', []);
    const fieldMappings = parseJson('contract.fieldMappings', this.profileForm.controls.fieldMappingsJson.value ?? '[]', []);
    const failureSimulation = parseJson<FccFailureSimulationDefinition>(
      'contract.failureSimulation',
      this.profileForm.controls.failureSimulationJson.value ?? '{}',
      {
        simulatedDelayMs: 0,
        enabled: false,
        failureRatePercent: 0,
        httpStatusCode: 500,
        errorCode: '',
        messageTemplate: '',
      },
    );
    const extensions = parseJson<FccExtensionPointDefinition>(
      'contract.extensions',
      this.profileForm.controls.extensionsJson.value ?? '{}',
      { resolverKey: '', configuration: {} },
    );

    if (parseErrors.length > 0) {
      this.validationMessages.set(parseErrors);
      this.errorMessage.set('Profile draft contains JSON parse errors.');
      return null;
    }

    const auth: FccAuthConfiguration = {
      mode: this.profileForm.controls.authMode.value ?? 'None',
      apiKeyHeaderName: this.profileForm.controls.apiKeyHeaderName.value ?? '',
      apiKeyValue: this.profileForm.controls.apiKeyValue.value ?? '',
      basicAuthUsername: this.profileForm.controls.basicAuthUsername.value ?? '',
      basicAuthPassword: this.profileForm.controls.basicAuthPassword.value ?? '',
    };

    const capabilities: FccDeliveryCapabilities = {
      supportsPush: this.profileForm.controls.supportsPush.value ?? false,
      supportsPull: this.profileForm.controls.supportsPull.value ?? false,
      supportsHybrid: this.profileForm.controls.supportsHybrid.value ?? false,
      supportsPreAuthCancellation:
        this.profileForm.controls.supportsPreAuthCancellation.value ?? true,
    };

    return {
      id: this.selectedProfileId(),
      labEnvironmentId: environmentId,
      profileKey: this.profileForm.controls.profileKey.value ?? '',
      name: this.profileForm.controls.name.value ?? '',
      vendorFamily: this.profileForm.controls.vendorFamily.value ?? '',
      deliveryMode: this.profileForm.controls.deliveryMode.value ?? 'Pull',
      isActive: this.profileForm.controls.isActive.value ?? true,
      isDefault: this.profileForm.controls.isDefault.value ?? false,
      contract: {
        endpointSurface,
        auth,
        capabilities,
        preAuthMode: this.profileForm.controls.preAuthMode.value ?? 'CreateOnly',
        requestTemplates,
        responseTemplates,
        validationRules,
        fieldMappings,
        failureSimulation,
        extensions,
      },
    };
  }

  private handleValidationError(error: HttpErrorResponse, fallbackMessage: string): void {
    const response = error.error as FccProfileValidationResult | { message?: string } | undefined;
    if (response && typeof response === 'object' && 'messages' in response && Array.isArray(response.messages)) {
      this.validationMessages.set(response.messages);
      this.errorMessage.set(fallbackMessage);
      return;
    }

    if (response && typeof response === 'object' && 'message' in response) {
      this.errorMessage.set(response.message ?? fallbackMessage);
      return;
    }

    this.errorMessage.set(fallbackMessage);
  }

  private stringifyJson(value: unknown): string {
    return JSON.stringify(value, null, 2);
  }
}
