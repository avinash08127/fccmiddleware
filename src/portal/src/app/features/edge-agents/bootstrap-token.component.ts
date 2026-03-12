import { Component, DestroyRef, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { CardModule } from 'primeng/card';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';
import { MessageModule } from 'primeng/message';
import { InputTextModule } from 'primeng/inputtext';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import * as QRCode from 'qrcode';

import { BootstrapTokenService } from '../../core/services/bootstrap-token.service';
import { MasterDataService } from '../../core/services/master-data.service';
import { SiteService } from '../../core/services/site.service';
import { LegalEntity } from '../../core/models/master-data.model';
import { Site } from '../../core/models/site.model';
import { GenerateBootstrapTokenResponse } from '../../core/models/bootstrap-token.model';
import { EmptyStateComponent } from '../../shared/components/empty-state/empty-state.component';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-bootstrap-token',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    CardModule,
    ButtonModule,
    SelectModule,
    MessageModule,
    InputTextModule,
    TagModule,
    TooltipModule,
    EmptyStateComponent,
  ],
  template: `
    <div class="page-container">
      <div class="page-header">
        <h1 class="page-title"><i class="pi pi-key"></i> Generate Bootstrap Token</h1>
      </div>

      <p-card styleClass="form-card">
        <div class="form-row">
          <div class="form-field">
            <label for="bootstrap-legal-entity">Legal Entity</label>
            <p-select
              id="bootstrap-legal-entity"
              [options]="legalEntityOptions()"
              [ngModel]="selectedLegalEntityId()"
              (ngModelChange)="onLegalEntityChange($event)"
              optionLabel="label"
              optionValue="value"
              placeholder="Select Legal Entity"
              styleClass="full-width"
            />
          </div>
        </div>

        @if (selectedLegalEntityId()) {
          <div class="form-row">
            <div class="form-field">
              <label for="bootstrap-site">Site</label>
              <p-select
                id="bootstrap-site"
                [options]="siteOptions()"
                [(ngModel)]="selectedSiteCode"
                optionLabel="label"
                optionValue="value"
                placeholder="Select Site"
                [filter]="true"
                filterPlaceholder="Search sites..."
                styleClass="full-width"
                [loading]="loadingSites()"
              />
            </div>
          </div>

          <div class="form-row">
            <div class="form-field">
              <label for="bootstrap-environment">Environment (optional)</label>
              <p-select
                id="bootstrap-environment"
                [options]="environmentOptions"
                [(ngModel)]="selectedEnvironment"
                placeholder="Default (from server config)"
                [showClear]="true"
                styleClass="full-width"
              />
            </div>
          </div>

          <div class="form-actions">
            <p-button
              label="Generate Token"
              icon="pi pi-key"
              (onClick)="generateToken()"
              [disabled]="!selectedSiteCode || generating()"
              [loading]="generating()"
            />
          </div>
        }

        @if (error()) {
          <p-message severity="error" styleClass="result-msg">
            {{ error() }}
          </p-message>
        }

        @if (generatedToken()) {
          <div class="token-result">
            <p-message severity="success" styleClass="result-msg">
              Bootstrap token generated successfully for site {{ generatedToken()!.siteCode }}.
            </p-message>

            <div class="token-details">
              <div class="detail-row">
                <span class="detail-label">Token ID</span>
                <code>{{ generatedToken()!.tokenId }}</code>
              </div>
              <div class="detail-row">
                <span class="detail-label">Expires At</span>
                <span>{{ generatedToken()!.expiresAt | date:'medium' }}</span>
              </div>
              <div class="detail-row">
                <span class="detail-label">Provisioning Token</span>
                <div class="token-value-row">
                  <code class="token-code">{{ tokenDisplay() }}</code>
                  <p-button
                    [icon]="copied() ? 'pi pi-check' : 'pi pi-copy'"
                    [severity]="copied() ? 'success' : 'secondary'"
                    size="small"
                    [rounded]="true"
                    [text]="true"
                    pTooltip="Copy to clipboard"
                    (onClick)="copyToken()"
                  />
                  <p-button
                    [icon]="tokenRevealed() ? 'pi pi-eye-slash' : 'pi pi-eye'"
                    severity="secondary"
                    size="small"
                    [rounded]="true"
                    [text]="true"
                    [pTooltip]="tokenRevealed() ? 'Hide' : 'Reveal'"
                    (onClick)="tokenRevealed.set(!tokenRevealed())"
                  />
                </div>
              </div>

              <p-message severity="warn" styleClass="result-msg">
                Copy this token now. It will not be shown again.
              </p-message>

              @if (!tokenRevoked()) {
                <div class="revoke-actions">
                  <p-button
                    label="Revoke Token"
                    icon="pi pi-ban"
                    severity="danger"
                    size="small"
                    [outlined]="true"
                    (onClick)="revokeToken()"
                    [disabled]="revoking()"
                    [loading]="revoking()"
                  />
                </div>
              }

              @if (tokenRevoked()) {
                <p-message severity="info" styleClass="result-msg">
                  This token has been revoked and can no longer be used for provisioning.
                </p-message>
              }
            </div>

            @if (qrDataUrl()) {
              <div class="qr-section">
                <h3 class="qr-title"><i class="pi pi-qrcode"></i> Provisioning QR Code</h3>
                <p class="qr-hint">
                  Scan this QR code with the Android edge agent to provision the device.
                </p>
                <div class="qr-image-wrapper">
                  <img [src]="qrDataUrl()" alt="Provisioning QR Code" class="qr-image" />
                </div>
                <div class="qr-actions">
                  <p-button
                    label="Download QR"
                    icon="pi pi-download"
                    severity="secondary"
                    size="small"
                    (onClick)="downloadQr()"
                  />
                  <p-button
                    label="Print QR"
                    icon="pi pi-print"
                    severity="secondary"
                    size="small"
                    (onClick)="printQr()"
                  />
                </div>
              </div>
            }
          </div>
        }
      </p-card>
    </div>
  `,
  styles: [`
    :host { display: block; padding: 1.5rem; }

    .page-header {
      display: flex;
      align-items: center;
      margin-bottom: 1.25rem;
    }
    .page-title {
      font-size: 1.5rem;
      font-weight: 700;
      margin: 0;
      display: flex;
      align-items: center;
      gap: 0.5rem;
      color: var(--p-text-color, #1e293b);
    }

    .form-card { max-width: 600px; }

    .form-row { margin-bottom: 1rem; }
    .form-field {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
    }
    .form-field label {
      font-size: 0.78rem;
      font-weight: 600;
      color: var(--p-text-muted-color, #64748b);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .full-width { width: 100%; }

    .form-actions {
      margin-top: 1.25rem;
      margin-bottom: 1rem;
    }

    .result-msg { margin-top: 1rem; width: 100%; }

    .token-result { margin-top: 1rem; }
    .token-details {
      margin-top: 0.75rem;
      background: var(--p-surface-50, #f8fafc);
      border: 1px solid var(--p-surface-200, #e2e8f0);
      border-radius: 0.5rem;
      padding: 1rem;
    }

    .detail-row {
      display: flex;
      flex-direction: column;
      gap: 0.15rem;
      margin-bottom: 0.75rem;
    }
    .detail-row:last-child { margin-bottom: 0; }
    .detail-label {
      font-size: 0.72rem;
      font-weight: 600;
      color: var(--p-text-muted-color, #64748b);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .token-value-row {
      display: flex;
      align-items: center;
      gap: 0.25rem;
    }
    .token-code {
      font-family: monospace;
      font-size: 0.85rem;
      word-break: break-all;
      flex: 1;
      background: var(--p-surface-100, #f1f5f9);
      padding: 0.35rem 0.5rem;
      border-radius: 0.25rem;
    }

    code {
      font-family: monospace;
      font-size: 0.85rem;
    }

    .revoke-actions {
      margin-top: 0.75rem;
    }

    .qr-section {
      margin-top: 1.25rem;
      background: var(--p-surface-50, #f8fafc);
      border: 1px solid var(--p-surface-200, #e2e8f0);
      border-radius: 0.5rem;
      padding: 1rem;
      text-align: center;
    }
    .qr-title {
      font-size: 1rem;
      font-weight: 600;
      margin: 0 0 0.25rem;
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 0.4rem;
      color: var(--p-text-color, #1e293b);
    }
    .qr-hint {
      font-size: 0.82rem;
      color: var(--p-text-muted-color, #64748b);
      margin: 0 0 0.75rem;
    }
    .qr-image-wrapper {
      display: flex;
      justify-content: center;
      margin-bottom: 0.75rem;
    }
    .qr-image {
      width: 280px;
      height: 280px;
      border: 4px solid #fff;
      border-radius: 0.5rem;
      box-shadow: 0 1px 4px rgba(0, 0, 0, 0.08);
    }
    .qr-actions {
      display: flex;
      justify-content: center;
      gap: 0.5rem;
    }
  `],
})
export class BootstrapTokenComponent {
  private readonly bootstrapTokenService = inject(BootstrapTokenService);
  private readonly masterDataService = inject(MasterDataService);
  private readonly siteService = inject(SiteService);
  private readonly destroyRef = inject(DestroyRef);

  // Legal entities
  private readonly legalEntities = signal<LegalEntity[]>([]);
  readonly selectedLegalEntityId = signal<string | null>(null);
  readonly legalEntityOptions = computed(() =>
    this.legalEntities().map((e) => ({ label: `${e.name} (${e.code})`, value: e.id })),
  );

  // Sites
  private readonly sites = signal<Site[]>([]);
  readonly loadingSites = signal(false);
  readonly siteOptions = computed(() =>
    this.sites().map((s) => ({ label: `${s.siteCode} — ${s.siteName}`, value: s.siteCode })),
  );
  selectedSiteCode: string | null = null;

  // Environment
  readonly environmentOptions = [
    { label: 'Production', value: 'PRODUCTION' },
    { label: 'Staging', value: 'STAGING' },
    { label: 'Development', value: 'DEVELOPMENT' },
    { label: 'Local', value: 'LOCAL' },
  ];
  selectedEnvironment: string | null = null;

  // Generation state
  readonly generating = signal(false);
  readonly error = signal<string | null>(null);
  readonly generatedToken = signal<GenerateBootstrapTokenResponse | null>(null);
  readonly copied = signal(false);
  readonly tokenRevealed = signal(false);
  readonly qrDataUrl = signal<string | null>(null);

  // Revocation state
  readonly revoking = signal(false);
  readonly tokenRevoked = signal(false);

  readonly tokenDisplay = computed(() => {
    const token = this.generatedToken();
    if (!token) return '';
    if (this.tokenRevealed()) return token.rawToken;
    return token.rawToken.substring(0, 6) + '...' + token.rawToken.substring(token.rawToken.length - 4);
  });

  constructor() {
    this.masterDataService
      .getLegalEntities()
      .pipe(takeUntilDestroyed())
      .subscribe({ next: (entities) => this.legalEntities.set(entities) });
  }

  onLegalEntityChange(entityId: string | null): void {
    this.selectedLegalEntityId.set(entityId);
    this.selectedSiteCode = null;
    this.sites.set([]);
    this.generatedToken.set(null);
    this.error.set(null);
    this.qrDataUrl.set(null);

    if (entityId) {
      this.loadingSites.set(true);
      this.siteService
        .getSites({ legalEntityId: entityId, pageSize: 500, isActive: true })
        .pipe(
          catchError(() => {
            this.loadingSites.set(false);
            return EMPTY;
          }),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe((result) => {
          this.sites.set(result.data);
          this.loadingSites.set(false);
        });
    }
  }

  generateToken(): void {
    const legalEntityId = this.selectedLegalEntityId();
    const siteCode = this.selectedSiteCode;
    if (!legalEntityId || !siteCode) return;

    this.generating.set(true);
    this.error.set(null);
    this.generatedToken.set(null);
    this.copied.set(false);
    this.tokenRevealed.set(false);
    this.qrDataUrl.set(null);
    this.tokenRevoked.set(false);

    const req: import('../../core/models/bootstrap-token.model').GenerateBootstrapTokenRequest = {
      siteCode,
      legalEntityId,
      ...(this.selectedEnvironment ? { environment: this.selectedEnvironment } : {}),
    };

    this.bootstrapTokenService
      .generate(req)
      .pipe(
        catchError((err) => {
          const msg = err?.error?.message ?? 'Failed to generate bootstrap token.';
          this.error.set(msg);
          this.generating.set(false);
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.generatedToken.set(result);
        this.generating.set(false);
        this.generateQrCode(result);
      });
  }

  revokeToken(): void {
    const token = this.generatedToken();
    if (!token) return;

    this.revoking.set(true);
    this.error.set(null);

    this.bootstrapTokenService
      .revoke(token.tokenId)
      .pipe(
        catchError((err) => {
          const msg = err?.error?.message ?? 'Failed to revoke bootstrap token.';
          this.error.set(msg);
          this.revoking.set(false);
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.tokenRevoked.set(true);
        this.revoking.set(false);
        this.qrDataUrl.set(null);
      });
  }

  async copyToken(): Promise<void> {
    const token = this.generatedToken();
    if (!token) return;
    try {
      await navigator.clipboard.writeText(token.rawToken);
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2000);
    } catch {
      // fallback: select text for manual copy
    }
  }

  private async generateQrCode(token: GenerateBootstrapTokenResponse): Promise<void> {
    const qrPayload: Record<string, unknown> = {
      v: this.selectedEnvironment ? 2 : 1,
      sc: token.siteCode,
      cu: environment.apiBaseUrl,
      pt: token.rawToken,
    };
    if (this.selectedEnvironment) {
      qrPayload['env'] = this.selectedEnvironment;
    }
    const payload = JSON.stringify(qrPayload);

    try {
      const dataUrl = await QRCode.toDataURL(payload, {
        errorCorrectionLevel: 'H',
        margin: 2,
        width: 560,
        color: { dark: '#000000', light: '#ffffff' },
      });
      this.qrDataUrl.set(dataUrl);
    } catch {
      // QR generation failed silently — token is still usable via copy
    }
  }

  downloadQr(): void {
    const dataUrl = this.qrDataUrl();
    const token = this.generatedToken();
    if (!dataUrl || !token) return;

    const link = document.createElement('a');
    link.href = dataUrl;
    link.download = `provision-qr-${token.siteCode}.png`;
    link.click();
  }

  printQr(): void {
    const dataUrl = this.qrDataUrl();
    const token = this.generatedToken();
    if (!dataUrl || !token) return;

    const win = window.open('', '_blank');
    if (!win) return;

    win.document.write(`
      <html>
        <head><title>Provisioning QR — ${token.siteCode}</title></head>
        <body style="display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:100vh;margin:0;font-family:sans-serif">
          <h2 style="margin:0 0 0.25rem">Provisioning QR Code</h2>
          <p style="margin:0 0 1rem;color:#64748b">Site: <strong>${token.siteCode}</strong></p>
          <img src="${dataUrl}" style="width:400px;height:400px" />
          <p style="margin:1rem 0 0;font-size:0.8rem;color:#94a3b8">
            Token ID: ${token.tokenId} &mdash; Expires: ${new Date(token.expiresAt).toLocaleString()}
          </p>
        </body>
      </html>
    `);
    win.document.close();
    win.focus();
    win.print();
  }
}
