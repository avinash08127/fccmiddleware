import { HttpParams } from '@angular/common/http';

/**
 * Builds HttpParams from a plain object, skipping undefined/null values
 * and converting non-string values to their string representation.
 */
export function buildHttpParams<T extends object>(obj: T): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(obj as Record<string, unknown>)) {
    if (value !== undefined && value !== null) {
      params = params.set(key, String(value));
    }
  }
  return params;
}
