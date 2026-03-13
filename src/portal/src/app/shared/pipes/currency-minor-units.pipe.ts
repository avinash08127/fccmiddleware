import { Pipe, PipeTransform } from '@angular/core';

/**
 * ISO 4217 minor-unit counts for currencies that do NOT use 2 decimal places.
 * Currencies not listed here default to 2.
 */
const CURRENCY_MINOR_UNITS: Record<string, number> = {
  // 0 decimal places
  BIF: 0, CLP: 0, DJF: 0, GNF: 0, ISK: 0, JPY: 0, KMF: 0, KRW: 0,
  PYG: 0, RWF: 0, UGX: 0, UYI: 0, VND: 0, VUV: 0, XAF: 0, XOF: 0, XPF: 0,
  // 3 decimal places
  BHD: 3, IQD: 3, JOD: 3, KWD: 3, LYD: 3, OMR: 3, TND: 3,
};

function getMinorUnits(currencyCode: string): number {
  return CURRENCY_MINOR_UNITS[currencyCode.toUpperCase()] ?? 2;
}

/**
 * Converts a long minor-unit amount to a display string.
 * Automatically resolves decimal places from the ISO 4217 currency code.
 * Example: currencyMinorUnits(12345, 'USD') → 'USD 123.45'
 * Example: currencyMinorUnits(1000, 'JPY')  → 'JPY 1000'
 * Example: currencyMinorUnits(12345, 'KWD') → 'KWD 12.345'
 */
@Pipe({ name: 'currencyMinorUnits', standalone: true })
export class CurrencyMinorUnitsPipe implements PipeTransform {
  transform(minorUnits: number | null | undefined, currencyCode = '', decimals?: number): string {
    if (minorUnits == null) return '';
    const d = decimals ?? getMinorUnits(currencyCode);
    const divisor = Math.pow(10, d);
    const amount = (minorUnits / divisor).toFixed(d);
    return currencyCode ? `${currencyCode} ${amount}` : amount;
  }
}
