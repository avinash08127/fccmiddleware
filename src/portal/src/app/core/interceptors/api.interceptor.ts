import { inject } from '@angular/core';
import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { Router } from '@angular/router';
import { MessageService } from 'primeng/api';
import { environment } from '../../../environments/environment';

export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const router = inject(Router);
  const messageService = inject(MessageService);

  const apiReq = req.clone({
    url: `${environment.apiBaseUrl}${req.url}`,
  });

  return next(apiReq).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 401) {
        // Silent token refresh is handled by MsalInterceptor.
        // If we still receive 401, the session has expired — reload.
        window.location.reload();
      } else if (error.status === 403) {
        router.navigate(['/access-denied']);
      } else if (error.status >= 500) {
        messageService.add({
          severity: 'error',
          summary: 'Server Error',
          detail: 'An unexpected error occurred. Please try again later.',
          life: 5000,
        });
      }
      return throwError(() => error);
    })
  );
};
