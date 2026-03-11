import { Component, Input, Output, EventEmitter, forwardRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ControlValueAccessor, NG_VALUE_ACCESSOR, FormsModule } from '@angular/forms';
import { DatePickerModule } from 'primeng/datepicker';

export interface DateRange {
  from: Date | null;
  to: Date | null;
}

@Component({
  selector: 'app-date-range-picker',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePickerModule],
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => DateRangePickerComponent),
      multi: true,
    },
  ],
  template: `
    <p-datepicker
      [(ngModel)]="range"
      selectionMode="range"
      [placeholder]="placeholder"
      [showIcon]="true"
      [showButtonBar]="true"
      [readonlyInput]="true"
      dateFormat="yy-mm-dd"
      (onSelect)="onRangeSelect()"
      (onClearClick)="onClear()"
    />
  `,
})
export class DateRangePickerComponent implements ControlValueAccessor {
  @Input() placeholder = 'Select date range';
  @Output() rangeSelected = new EventEmitter<DateRange>();

  range: Date[] | null = null;

  private onChange: (value: DateRange) => void = () => {};
  private onTouched: () => void = () => {};

  onRangeSelect(): void {
    if (this.range && this.range.length === 2) {
      const dateRange: DateRange = { from: this.range[0], to: this.range[1] };
      this.onChange(dateRange);
      this.rangeSelected.emit(dateRange);
    }
  }

  onClear(): void {
    this.range = null;
    const empty: DateRange = { from: null, to: null };
    this.onChange(empty);
    this.rangeSelected.emit(empty);
  }

  writeValue(value: DateRange | null): void {
    if (value?.from && value?.to) {
      this.range = [value.from, value.to];
    } else {
      this.range = null;
    }
  }

  registerOnChange(fn: (value: DateRange) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }
}
