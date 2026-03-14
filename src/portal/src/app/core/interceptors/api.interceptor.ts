import { inject } from '@angular/core';
import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { catchError, switchMap, throwError, from, of } from 'rxjs';
import { Router } from '@angular/router';
import { MessageService } from 'primeng/api';
import { MsalService } from '@azure/msal-angular';
import { environment } from '../../../environments/environment';
import { LoggingService } from '../services/logging.service';

export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const router = inject(Router);
  const messageService = inject(MessageService);
  const logger = inject(LoggingService);
  const msal = inject(MsalService);

  const absoluteUrl = `${environment.apiBaseUrl}${req.url}`;

  // Acquire token and attach it before sending the request
  const account = msal.instance.getActiveAccount() ?? msal.instance.getAllAccounts()[0];
  const token$ = account
    ? from(msal.instance.acquireTokenSilent({
        scopes: [`${environment.msalClientId}/.default`],
        account,
      })).pipe(catchError(() => of(null)))
    : of(null);

  return token$.pipe(
    switchMap((tokenResult) => {
      const headers: Record<string, string> = {};
      if (tokenResult?.accessToken) {
        headers['Authorization'] = `Bearer ${tokenResult.accessToken}`;
      }
      const apiReq = req.clone({ url: absoluteUrl, setHeaders: headers });
      return next(apiReq);
    }),
    catchError((error: HttpErrorResponse) => {
      if (error.status === 401) {
        logger.warn('ApiInterceptor', 'Received 401 — attempting MSAL token refresh', {
          url: req.url,
        });
        // L-3: Use MSAL redirect flow instead of page reload to preserve form state
        const account = msal.instance.getActiveAccount() ?? msal.instance.getAllAccounts()[0];
        if (account) {
          msal.acquireTokenSilent({
            scopes: [`${environment.msalClientId}/.default`],
            account,
          }).subscribe({
            error: () => {
              // Silent refresh failed — redirect to login
              msal.acquireTokenRedirect({
                scopes: [`${environment.msalClientId}/.default`],
              });
            },
          });
        } else {
          // No account — redirect to login
          msal.acquireTokenRedirect({
            scopes: [`${environment.msalClientId}/.default`],
          });
        }
      } else if (error.status === 403) {
        logger.warn('ApiInterceptor', 'Received 403 — access denied', {
          url: req.url,
        });
        router.navigate(['/access-denied']);
      } else if (error.status >= 500) {
        logger.error('ApiInterceptor', `Server error ${error.status}`, {
          url: req.url,
          statusText: error.statusText,
          message: error.message,
        });
        messageService.add({
          severity: 'error',
          summary: 'Server Error',
          detail: 'An unexpected error occurred. Please try again later.',
          life: 5000,
        });
      } else {
        logger.warn('ApiInterceptor', `HTTP error ${error.status}`, {
          url: req.url,
          statusText: error.statusText,
        });
      }
      return throwError(() => error);
    })
  );
};
