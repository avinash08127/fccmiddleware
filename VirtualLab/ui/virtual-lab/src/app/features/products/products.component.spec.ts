import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { TestBed } from '@angular/core/testing';
import { ProductsComponent } from './products.component';
import { LabApiService } from '../../core/services/lab-api.service';

describe('ProductsComponent', () => {
  const environment = {
    id: 'env-1',
    key: 'default-lab',
    name: 'Default Virtual Lab',
    description: 'Demo',
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
    logCategories: [],
  };

  const existingProduct = {
    id: 'product-1',
    labEnvironmentId: 'env-1',
    productCode: 'ULP',
    name: 'Unleaded Petrol',
    grade: '91',
    colorHex: '#cf5f2d',
    unitPrice: 1.53,
    currencyCode: 'USD',
    isActive: true,
    assignedNozzleCount: 0,
  };

  it('loads products on startup', async () => {
    const api = jasmine.createSpyObj<LabApiService>('LabApiService', [
      'getLabEnvironment',
      'getProducts',
      'createProduct',
      'updateProduct',
      'archiveProduct',
    ]);
    api.getLabEnvironment.and.returnValue(of(environment));
    api.getProducts.and.returnValue(of([existingProduct]));

    await TestBed.configureTestingModule({
      imports: [ProductsComponent],
      providers: [{ provide: LabApiService, useValue: api }],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(fixture.componentInstance.products()).toEqual([existingProduct]);
    expect(fixture.nativeElement.textContent).toContain('Unleaded Petrol');
  });

  it('creates a new product from the form', async () => {
    const createdProduct = { ...existingProduct, id: 'product-2', productCode: 'DSL', name: 'Diesel' };
    const api = jasmine.createSpyObj<LabApiService>('LabApiService', [
      'getLabEnvironment',
      'getProducts',
      'createProduct',
      'updateProduct',
      'archiveProduct',
    ]);
    api.getLabEnvironment.and.returnValues(of(environment), of(environment));
    api.getProducts.and.returnValues(of([]), of([createdProduct]));
    api.createProduct.and.returnValue(of(createdProduct));

    await TestBed.configureTestingModule({
      imports: [ProductsComponent],
      providers: [{ provide: LabApiService, useValue: api }],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    fixture.componentInstance.productForm.setValue({
      labEnvironmentId: 'env-1',
      productCode: 'DSL',
      name: 'Diesel',
      grade: 'D50',
      colorHex: '#16a34a',
      unitPrice: 1.49,
      currencyCode: 'USD',
      isActive: true,
    });

    await fixture.componentInstance.saveProduct();

    expect(api.createProduct).toHaveBeenCalledWith({
      labEnvironmentId: 'env-1',
      productCode: 'DSL',
      name: 'Diesel',
      grade: 'D50',
      colorHex: '#16a34a',
      unitPrice: 1.49,
      currencyCode: 'USD',
      isActive: true,
    });
    expect(fixture.componentInstance.actionMessage()).toContain('Created DSL');
  });

  it('surfaces backend archive errors', async () => {
    const api = jasmine.createSpyObj<LabApiService>('LabApiService', [
      'getLabEnvironment',
      'getProducts',
      'createProduct',
      'updateProduct',
      'archiveProduct',
    ]);
    api.getLabEnvironment.and.returnValue(of(environment));
    api.getProducts.and.returnValue(of([existingProduct]));
    api.archiveProduct.and.returnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: {
              message: 'Product is still assigned to one or more nozzles.',
            },
          }),
      ),
    );

    await TestBed.configureTestingModule({
      imports: [ProductsComponent],
      providers: [{ provide: LabApiService, useValue: api }],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    await fixture.componentInstance.archiveProduct();

    expect(fixture.componentInstance.errorMessage()).toContain('Product is still assigned');
  });
});
