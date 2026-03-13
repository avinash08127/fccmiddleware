import { inject } from '@angular/core';
import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
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

  const apiReq = req.clone({
    url: `${environment.apiBaseUrl}${req.url}`,
  });

  return next(apiReq).pipe(
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
