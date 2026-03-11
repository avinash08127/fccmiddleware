import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { MsalGuard } from '@azure/msal-angular';
import { map, catchError, of } from 'rxjs';

/**
 * Wraps MsalGuard. Redirects unauthenticated users to Entra login.
 */
export const authGuard: CanActivateFn = (route, state) => {
  const msalGuard = inject(MsalGuard);
  const router = inject(Router);

  return msalGuard.canActivate(route, state).pipe(
    map((result) => result),
    catchError(() => {
      router.navigate(['/']);
      return of(false);
    })
  );
};
