import { HttpParams } from '@angular/common/http';

/**
 * Builds HttpParams from a plain object, skipping undefined/null values
 * and converting non-string values to their string representation.
 */
export function buildHttpParams(obj: Record<string, unknown>): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(obj)) {
    if (value !== undefined && value !== null) {
      params = params.set(key, String(value));
    }
  }
  return params;
}
