import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { AdapterFieldDefinition, AdapterSchema } from '../../core/models';

type FormMode = 'defaults' | 'site';

@Component({
  selector: 'app-adapter-config-form',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    CardModule,
    InputTextModule,
    InputNumberModule,
    SelectModule,
    ToggleSwitchModule,
    ButtonModule,
  ],
  template: `
    @if (!schema) {
      <p class="empty-copy">No adapter schema available.</p>
    } @else {
      @for (group of groupedFields(); track group.name) {
        <div class="field-group">
          <div class="field-group__header">
            <h3>{{ group.name }}</h3>
          </div>

          <div class="field-grid">
            @for (field of group.fields; track field.key) {
              <div class="field" [class.field--wide]="field.type === 'json'">
                <label [for]="field.key">
                  {{ field.label }}
                  @if (field.required) {
                    <span class="required">*</span>
                  }
                  @if (mode === 'site' && fieldSources[field.key]) {
                    <small class="source-badge">{{ fieldSources[field.key] }}</small>
                  }
                </label>

                @if (field.description) {
                  <small class="hint">{{ field.description }}</small>
                }

                @switch (field.type) {
                  @case ('text') {
                    <input
                      pInputText
                      [id]="field.key"
                      [ngModel]="stringValue(field.key)"
                      (ngModelChange)="onTextChange(field, $event)"
                      [disabled]="!editMode"
                    />
                  }
                  @case ('secret') {
                    <input
                      pInputText
                      type="password"
                      [id]="field.key"
                      [ngModel]="stringValue(field.key)"
                      (ngModelChange)="onSecretChange(field, $event)"
                      [placeholder]="secretPlaceholder(field.key)"
                      [disabled]="!editMode"
                    />
                    @if (secretState[field.key]) {
                      <small class="secret-state">Configured</small>
                    } @else {
                      <small class="secret-state secret-state--empty">Not set</small>
                    }
                  }
                  @case ('number') {
                    <p-inputnumber
                      [inputId]="field.key"
                      [ngModel]="numberValue(field.key)"
                      (ngModelChange)="onNumberChange(field, $event)"
                      [min]="field.min ?? undefined"
                      [max]="field.max ?? undefined"
                      [showButtons]="false"
                      [useGrouping]="false"
                      [disabled]="!editMode"
                    />
                  }
                  @case ('boolean') {
                    <p-toggleswitch
                      [inputId]="field.key"
                      [ngModel]="booleanValue(field.key)"
                      (ngModelChange)="onBooleanChange(field, $event)"
                      [disabled]="!editMode"
                    />
                  }
                  @case ('select') {
                    <p-select
                      [inputId]="field.key"
                      [options]="field.options ?? []"
                      optionLabel="label"
                      optionValue="value"
                      [ngModel]="stringValue(field.key)"
                      (ngModelChange)="onSelectChange(field, $event)"
                      [disabled]="!editMode"
                    />
                  }
                  @case ('json') {
                      <textarea
                      pInputText
                      rows="5"
                      [id]="field.key"
                      [ngModel]="jsonDraft[field.key]"
                      (ngModelChange)="onJsonChange(field, $event)"
                      [disabled]="!editMode"
                    ></textarea>
                    @if (jsonErrors[field.key]) {
                      <small class="validation-error">{{ jsonErrors[field.key] }}</small>
                    }
                  }
                }

                @if (editMode && missingRequired(field)) {
                  <small class="validation-error">This field is required.</small>
                }
              </div>
            }
          </div>
        </div>
      }
    }
  `,
  styles: [
    `
      .empty-copy {
        margin: 0;
        color: var(--p-text-muted-color, #64748b);
      }
      .field-group + .field-group {
        margin-top: 1.25rem;
      }
      .field-group__header h3 {
        margin: 0 0 0.75rem 0;
        font-size: 0.9rem;
        font-weight: 700;
        color: var(--p-primary-color, #2563eb);
        text-transform: uppercase;
        letter-spacing: 0.04em;
      }
      .field-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
        gap: 1rem 1.25rem;
      }
      .field {
        display: flex;
        flex-direction: column;
        gap: 0.35rem;
      }
      .field--wide {
        grid-column: 1 / -1;
      }
      .field label {
        font-size: 0.78rem;
        font-weight: 700;
        color: var(--p-text-muted-color, #64748b);
        text-transform: uppercase;
        letter-spacing: 0.04em;
      }
      .required {
        color: var(--p-red-600, #dc2626);
      }
      .hint {
        color: var(--p-text-muted-color, #64748b);
      }
      .validation-error {
        color: var(--p-red-600, #dc2626);
      }
      .source-badge {
        margin-left: 0.35rem;
        font-size: 0.68rem;
        color: var(--p-primary-color, #2563eb);
      }
      .secret-state {
        color: var(--p-text-muted-color, #64748b);
      }
      .secret-state--empty {
        color: var(--p-orange-700, #c2410c);
      }
      textarea {
        width: 100%;
        min-height: 7rem;
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
      }
    `,
  ],
})
export class AdapterConfigFormComponent implements OnChanges {
  @Input() schema: AdapterSchema | null = null;
  @Input() values: Record<string, unknown> | null = null;
  @Input() secretState: Record<string, boolean> = {};
  @Input() fieldSources: Record<string, string> = {};
  @Input() mode: FormMode = 'defaults';
  @Input() editMode = false;
  @Output() formChange = new EventEmitter<Record<string, unknown>>();

  draft: Record<string, unknown> = {};
  jsonDraft: Record<string, string> = {};
  jsonErrors: Record<string, string | null> = {};

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['values'] || changes['schema']) {
      this.resetDraft();
    }
  }

  groupedFields(): Array<{ name: string; fields: AdapterFieldDefinition[] }> {
    if (!this.schema) return [];

    const filtered = this.schema.fields.filter((field) =>
      this.mode === 'defaults' ? field.defaultable : field.siteConfigurable,
    );

    const visible = filtered.filter((field) => this.isVisible(field));
    const groups = new Map<string, AdapterFieldDefinition[]>();
    for (const field of visible) {
      const bucket = groups.get(field.group) ?? [];
      bucket.push(field);
      groups.set(field.group, bucket);
    }

    return Array.from(groups.entries()).map(([name, fields]) => ({ name, fields }));
  }

  isValid(): boolean {
    const fields = this.schema?.fields.filter((field) =>
      this.mode === 'defaults' ? field.defaultable : field.siteConfigurable,
    );
    if (!fields) return false;
    if (Object.values(this.jsonErrors).some((value) => !!value)) return false;

    return fields
      .filter((field) => this.isVisible(field))
      .every((field) => !this.missingRequired(field));
  }

  stringValue(key: string): string {
    const value = this.draft[key];
    return typeof value === 'string' ? value : '';
  }

  numberValue(key: string): number | null {
    const value = this.draft[key];
    return typeof value === 'number' ? value : null;
  }

  booleanValue(key: string): boolean {
    return Boolean(this.draft[key]);
  }

  secretPlaceholder(key: string): string {
    return this.secretState[key] ? 'Leave blank to keep current value' : 'Enter secret';
  }

  onTextChange(field: AdapterFieldDefinition, value: string): void {
    this.setValue(field.key, value.trim() ? value : null);
  }

  onSecretChange(field: AdapterFieldDefinition, value: string): void {
    if (value.trim()) {
      this.setValue(field.key, value);
    } else {
      delete this.draft[field.key];
      this.emitChange();
    }
  }

  onNumberChange(field: AdapterFieldDefinition, value: number | null): void {
    this.setValue(field.key, value);
  }

  onBooleanChange(field: AdapterFieldDefinition, value: boolean): void {
    this.setValue(field.key, value);
  }

  onSelectChange(field: AdapterFieldDefinition, value: string | null): void {
    this.setValue(field.key, value);
  }

  onJsonChange(field: AdapterFieldDefinition, rawValue: string): void {
    this.jsonDraft[field.key] = rawValue;

    if (!rawValue.trim()) {
      this.jsonErrors[field.key] = null;
      this.setValue(field.key, {});
      return;
    }

    try {
      const parsed = JSON.parse(rawValue) as Record<string, unknown>;
      this.jsonErrors[field.key] = null;
      this.setValue(field.key, parsed);
    } catch (error) {
      this.jsonErrors[field.key] =
        error instanceof Error ? error.message : 'Invalid JSON payload.';
    }
  }

  missingRequired(field: AdapterFieldDefinition): boolean {
    if (!field.required || !this.isVisible(field)) return false;

    if (field.type === 'secret') {
      return !this.secretState[field.key] && !this.stringValue(field.key).trim();
    }

    const value = this.draft[field.key];
    return value === null || value === undefined || value === '';
  }

  private isVisible(field: AdapterFieldDefinition): boolean {
    if (!field.visibleWhenKey || !field.visibleWhenValue) return true;
    return this.stringValue(field.visibleWhenKey) === field.visibleWhenValue;
  }

  private resetDraft(): void {
    this.draft = this.cloneObject(this.values ?? {});
    this.jsonDraft = {};
    this.jsonErrors = {};

    const jsonFields = this.schema?.fields.filter((field) => field.type === 'json') ?? [];
    for (const field of jsonFields) {
      const value = this.draft[field.key];
      this.jsonDraft[field.key] =
        value && typeof value === 'object' ? JSON.stringify(value, null, 2) : '{}';
      this.jsonErrors[field.key] = null;
    }

    this.emitChange();
  }

  private setValue(key: string, value: unknown): void {
    this.draft[key] = value;
    this.emitChange();
  }

  private emitChange(): void {
    this.formChange.emit(this.cloneObject(this.draft));
  }

  private cloneObject<T>(value: T): T {
    return JSON.parse(JSON.stringify(value)) as T;
  }
}
