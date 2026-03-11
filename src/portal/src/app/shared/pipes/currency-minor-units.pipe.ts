import { Pipe, PipeTransform } from '@angular/core';

/**
 * Converts a long minor-unit amount to a display string.
 * Example: currencyMinorUnits(12345, 'MWK') → 'MWK 123.45'
 */
@Pipe({ name: 'currencyMinorUnits', standalone: true })
export class CurrencyMinorUnitsPipe implements PipeTransform {
  transform(minorUnits: number | null | undefined, currencyCode = '', decimals = 2): string {
    if (minorUnits == null) return '';
    const divisor = Math.pow(10, decimals);
    const amount = (minorUnits / divisor).toFixed(decimals);
    return currencyCode ? `${currencyCode} ${amount}` : amount;
  }
}
