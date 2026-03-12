import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, catchError } from 'rxjs';

export type LogLevel = 'DEBUG' | 'INFO' | 'WARN' | 'ERROR';

interface StructuredLogEntry {
  ts: string;
  lvl: LogLevel;
  source: string;
  msg: string;
  extra?: Record<string, unknown>;
  url?: string;
}

/**
 * Structured logging service for the portal.
 *
 * Replaces ad-hoc console.error/console.log with structured entries.
 * Writes to browser console with structured format and optionally
 * POSTs to backend (when enabled via config).
 */
@Injectable({ providedIn: 'root' })
export class LoggingService {
  private readonly http = inject(HttpClient);
  private backendLoggingEnabled = false;

  debug(source: string, msg: string, extra?: Record<string, unknown>): void {
    this.log('DEBUG', source, msg, extra);
  }

  info(source: string, msg: string, extra?: Record<string, unknown>): void {
    this.log('INFO', source, msg, extra);
  }

  warn(source: string, msg: string, extra?: Record<string, unknown>): void {
    this.log('WARN', source, msg, extra);
  }

  error(source: string, msg: string, extra?: Record<string, unknown>): void {
    this.log('ERROR', source, msg, extra);
  }

  private log(lvl: LogLevel, source: string, msg: string, extra?: Record<string, unknown>): void {
    const entry: StructuredLogEntry = {
      ts: new Date().toISOString(),
      lvl,
      source,
      msg,
      extra,
      url: typeof window !== 'undefined' ? window.location.href : undefined,
    };

    // Console output
    const formatted = `[${entry.ts}] ${lvl} [${source}] ${msg}`;
    switch (lvl) {
      case 'DEBUG': console.debug(formatted, extra ?? ''); break;
      case 'INFO':  console.info(formatted, extra ?? '');  break;
      case 'WARN':  console.warn(formatted, extra ?? '');  break;
      case 'ERROR': console.error(formatted, extra ?? ''); break;
    }

    // Optional backend POST for ERROR level
    if (this.backendLoggingEnabled && lvl === 'ERROR') {
      this.http.post('/api/v1/portal/client-logs', entry)
        .pipe(catchError(() => EMPTY))
        .subscribe();
    }
  }
}
