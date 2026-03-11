import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';

export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const apiReq = req.clone({
    url: `${environment.apiBaseUrl}${req.url}`,
  });
  return next(apiReq).pipe(
    catchError((error: HttpErrorResponse) => {
      // Global error handling — log and rethrow
      console.error(`API error ${error.status}:`, error.message);
      return throwError(() => error);
    })
  );
};
