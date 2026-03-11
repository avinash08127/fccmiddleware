import { Pipe, PipeTransform } from '@angular/core';
import { DatePipe } from '@angular/common';

/**
 * Converts a UTC ISO 8601 string to the user's local timezone display.
 * Example: utcDate('2026-03-11T14:30:00Z', 'medium') → 'Mar 11, 2026, 4:30:00 PM'
 */
@Pipe({ name: 'utcDate', standalone: true })
export class UtcDatePipe implements PipeTransform {
  private readonly datePipe = new DatePipe('en-US');

  transform(
    value: string | Date | null | undefined,
    format = 'medium',
    timezone: string = Intl.DateTimeFormat().resolvedOptions().timeZone
  ): string {
    if (!value) return '';
    return this.datePipe.transform(value, format, timezone) ?? '';
  }
}
