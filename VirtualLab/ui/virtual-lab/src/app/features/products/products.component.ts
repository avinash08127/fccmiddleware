import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import {
  type LabEnvironmentDetail,
  type ManagementErrorResponse,
  type ProductUpsertRequest,
  type ProductView,
  LabApiService,
} from '../../core/services/lab-api.service';

@Component({
  selector: 'vl-products',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  template: `
    <section class="page-header">
      <div>
        <p class="eyebrow">VL-2.3 Products</p>
        <h2>Manage the fuel catalog used across seeded sites and forecourt layouts.</h2>
        <p class="copy">
          Product code, label, grade, color, price, and activation state now stay editable from
          the lab UI instead of requiring direct API calls.
        </p>
      </div>

      <div class="header-actions">
        <button type="button" class="secondary" (click)="reload()" [disabled]="loading() || saving()">
          Refresh
        </button>
        <button type="button" class="secondary" (click)="prepareNewProduct()" [disabled]="saving()">
          New product
        </button>
        <button type="button" (click)="saveProduct()" [disabled]="saving() || loading()">
          {{ selectedProductId() ? 'Save changes' : 'Create product' }}
        </button>
      </div>
    </section>

    <section *ngIf="actionMessage()" class="banner success">{{ actionMessage() }}</section>
    <section *ngIf="errorMessage()" class="banner error">{{ errorMessage() }}</section>

    <div class="workspace">
      <aside class="product-list">
        <article
          *ngFor="let product of products()"
          class="product-tile"
          [class.active]="product.id === selectedProductId()"
          (click)="selectProduct(product)"
          (keydown.enter)="selectProduct(product)"
          (keydown.space)="selectProduct(product)"
          tabindex="0"
        >
          <div class="tile-header">
            <div class="tile-title">
              <span class="swatch" [style.background]="product.colorHex"></span>
              <div>
                <strong>{{ product.name }}</strong>
                <p>{{ product.productCode }} · {{ product.grade }}</p>
              </div>
            </div>
            <span class="status-chip" [class.inactive]="!product.isActive">
              {{ product.isActive ? 'Active' : 'Archived' }}
            </span>
          </div>

          <div class="tile-meta">
            <span>{{ product.currencyCode }} {{ product.unitPrice | number: '1.2-2' }}</span>
            <span>{{ product.assignedNozzleCount }} nozzle{{ product.assignedNozzleCount === 1 ? '' : 's' }}</span>
          </div>
        </article>
      </aside>

      <section class="editor">
        <article class="panel summary" *ngIf="environment() as environment">
          <div>
            <h3>{{ selectedProductId() ? 'Edit product' : 'Create product' }}</h3>
            <p>{{ environment.name }} · seed {{ environment.seedVersion }} · deterministic seed {{ environment.deterministicSeed }}</p>
          </div>
          <span class="pill">{{ products().length }} products</span>
        </article>

        <form class="editor-grid" [formGroup]="productForm">
          <article class="panel">
            <h3>Identity</h3>
            <label>
              Product code
              <input formControlName="productCode" />
            </label>
            <label>
              Display name
              <input formControlName="name" />
            </label>
            <div class="split">
              <label>
                Grade
                <input formControlName="grade" />
              </label>
              <label>
                Currency
                <input formControlName="currencyCode" maxlength="8" />
              </label>
            </div>
          </article>

          <article class="panel">
            <h3>Pricing</h3>
            <div class="split">
              <label>
                Unit price
                <input type="number" min="0" step="0.01" formControlName="unitPrice" />
              </label>
              <label>
                Color
                <div class="color-field">
                  <input type="color" formControlName="colorHex" />
                  <input formControlName="colorHex" />
                </div>
              </label>
            </div>
            <label class="checkbox">
              <input type="checkbox" formControlName="isActive" />
              Active in dropdowns and new assignments
            </label>
          </article>
        </form>

        <article class="panel actions" *ngIf="selectedProduct() as product">
          <div>
            <h3>Usage</h3>
            <p>
              {{ product.assignedNozzleCount }} nozzle{{ product.assignedNozzleCount === 1 ? '' : 's' }}
              currently reference this product.
            </p>
          </div>
          <button type="button" class="danger" (click)="archiveProduct()" [disabled]="saving()">
            Archive product
          </button>
        </article>
      </section>
    </div>
  `,
  styles: `
    .page-header,
    .summary,
    .actions {
      align-items: start;
      display: flex;
      gap: 1rem;
      justify-content: space-between;
    }

    .workspace {
      display: grid;
      gap: 1.5rem;
      grid-template-columns: minmax(260px, 320px) minmax(0, 1fr);
      margin-top: 1.5rem;
    }

    .product-list {
      display: grid;
      gap: 0.9rem;
    }

    .product-tile,
    .panel {
      background: var(--vl-panel);
      border: 1px solid var(--vl-line);
      border-radius: 24px;
      box-shadow: var(--vl-shadow);
      padding: 1.2rem;
    }

    .product-tile {
      cursor: pointer;
      transition:
        border-color 160ms ease,
        transform 160ms ease,
        background-color 160ms ease;
    }

    .product-tile.active,
    .product-tile:hover {
      border-color: rgba(207, 95, 45, 0.35);
      transform: translateY(-2px);
    }

    .tile-header,
    .tile-meta {
      align-items: center;
      display: flex;
      gap: 0.75rem;
      justify-content: space-between;
    }

    .tile-title {
      align-items: center;
      display: flex;
      gap: 0.8rem;
    }

    .tile-title p,
    .tile-meta,
    .summary p,
    .actions p {
      color: var(--vl-muted);
      margin: 0.2rem 0 0;
    }

    .swatch {
      border: 1px solid rgba(29, 28, 26, 0.1);
      border-radius: 12px;
      display: inline-flex;
      height: 2.1rem;
      width: 2.1rem;
    }

    .status-chip,
    .pill {
      align-items: center;
      border-radius: 999px;
      display: inline-flex;
      font-size: 0.8rem;
      padding: 0.3rem 0.7rem;
      text-transform: uppercase;
    }

    .status-chip,
    .pill {
      background: rgba(29, 122, 90, 0.12);
      color: var(--vl-emerald);
    }

    .status-chip.inactive {
      background: rgba(111, 106, 98, 0.14);
      color: var(--vl-muted);
    }

    .editor {
      display: grid;
      gap: 1rem;
    }

    .editor-grid {
      display: grid;
      gap: 1rem;
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }

    h3,
    p {
      margin-top: 0;
    }

    label {
      color: var(--vl-muted);
      display: grid;
      gap: 0.45rem;
      margin-bottom: 1rem;
    }

    input {
      background: rgba(255, 255, 255, 0.88);
      border: 1px solid rgba(51, 44, 33, 0.12);
      border-radius: 14px;
      color: var(--vl-text);
      padding: 0.75rem 0.9rem;
    }

    .split {
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

    .color-field {
      align-items: center;
      display: grid;
      gap: 0.75rem;
      grid-template-columns: auto 1fr;
    }

    .color-field input[type='color'] {
      min-height: 3rem;
      padding: 0.25rem;
      width: 4rem;
    }

    button {
      background: var(--vl-accent);
      border: none;
      border-radius: 999px;
      color: #fff;
      cursor: pointer;
      padding: 0.8rem 1.1rem;
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
      .editor-grid,
      .split {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class ProductsComponent implements OnInit {
  private readonly api = inject(LabApiService);
  private readonly fb = inject(FormBuilder);

  readonly products = signal<ProductView[]>([]);
  readonly environment = signal<LabEnvironmentDetail | null>(null);
  readonly selectedProductId = signal<string | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal('');
  readonly actionMessage = signal('');
  readonly selectedProduct = computed(
    () => this.products().find((product) => product.id === this.selectedProductId()) ?? null,
  );

  readonly productForm = this.fb.nonNullable.group({
    labEnvironmentId: ['', Validators.required],
    productCode: ['', Validators.required],
    name: ['', Validators.required],
    grade: ['', Validators.required],
    colorHex: ['#cf5f2d', Validators.required],
    unitPrice: [0, [Validators.required, Validators.min(0)]],
    currencyCode: ['USD', Validators.required],
    isActive: [true],
  });

  ngOnInit(): void {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set('');

    try {
      const [environment, products] = await Promise.all([
        firstValueFrom(this.api.getLabEnvironment()),
        firstValueFrom(this.api.getProducts(true)),
      ]);

      this.environment.set(environment);
      this.products.set(products);

      const selected = this.selectedProductId();
      const nextSelected = products.find((product) => product.id === selected) ?? products[0] ?? null;
      if (nextSelected) {
        this.selectProduct(nextSelected);
      } else {
        this.prepareNewProduct();
      }
    } catch (error) {
      this.errorMessage.set(this.describeError(error, 'Unable to load products.'));
    } finally {
      this.loading.set(false);
    }
  }

  prepareNewProduct(): void {
    this.actionMessage.set('');
    this.selectedProductId.set(null);
    this.productForm.reset({
      labEnvironmentId: this.environment()?.id ?? '',
      productCode: '',
      name: '',
      grade: '',
      colorHex: '#cf5f2d',
      unitPrice: 0,
      currencyCode: this.products()[0]?.currencyCode ?? 'USD',
      isActive: true,
    });
  }

  selectProduct(product: ProductView): void {
    this.actionMessage.set('');
    this.selectedProductId.set(product.id);
    this.productForm.reset({
      labEnvironmentId: product.labEnvironmentId,
      productCode: product.productCode,
      name: product.name,
      grade: product.grade,
      colorHex: product.colorHex,
      unitPrice: product.unitPrice,
      currencyCode: product.currencyCode,
      isActive: product.isActive,
    });
  }

  async saveProduct(): Promise<void> {
    if (this.productForm.invalid) {
      this.productForm.markAllAsTouched();
      this.errorMessage.set('Complete the required product fields before saving.');
      return;
    }

    this.saving.set(true);
    this.errorMessage.set('');
    this.actionMessage.set('');

    const request = this.productForm.getRawValue() as ProductUpsertRequest;

    try {
      const saved = this.selectedProductId()
        ? await firstValueFrom(this.api.updateProduct(this.selectedProductId()!, request))
        : await firstValueFrom(this.api.createProduct(request));

      this.actionMessage.set(
        this.selectedProductId() ? `Updated ${saved.productCode}.` : `Created ${saved.productCode}.`,
      );

      await this.reload();
      this.selectProduct(saved);
    } catch (error) {
      this.errorMessage.set(this.describeError(error, 'Unable to save product.'));
    } finally {
      this.saving.set(false);
    }
  }

  async archiveProduct(): Promise<void> {
    const product = this.selectedProduct();
    if (!product) {
      return;
    }

    this.saving.set(true);
    this.errorMessage.set('');
    this.actionMessage.set('');

    try {
      await firstValueFrom(this.api.archiveProduct(product.id));
      this.actionMessage.set(`Archived ${product.productCode}.`);
      await this.reload();
    } catch (error) {
      this.errorMessage.set(this.describeError(error, 'Unable to archive product.'));
    } finally {
      this.saving.set(false);
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
