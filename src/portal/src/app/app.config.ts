import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZoneChangeDetection,
} from '@angular/core';
import {
  provideRouter,
  withEnabledBlockingInitialNavigation,
} from '@angular/router';
import {
  provideHttpClient,
  withInterceptors,
  withInterceptorsFromDi,
  HTTP_INTERCEPTORS,
} from '@angular/common/http';
import {
  MSAL_GUARD_CONFIG,
  MSAL_INSTANCE,
  MSAL_INTERCEPTOR_CONFIG,
  MsalBroadcastService,
  MsalGuard,
  MsalInterceptor,
  MsalService,
} from '@azure/msal-angular';
import { providePrimeNG } from 'primeng/config';
import Lara from '@primeuix/themes/lara';
import { MessageService } from 'primeng/api';

import { routes } from './app.routes';
import { apiInterceptor } from './core/interceptors/api.interceptor';
import {
  msalGuardConfigFactory,
  msalInstanceFactory,
  msalInterceptorConfigFactory,
} from './core/auth/auth.config';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withEnabledBlockingInitialNavigation()),

    // HTTP client — functional interceptors + DI-based class interceptors (MSAL)
    provideHttpClient(withInterceptors([apiInterceptor]), withInterceptorsFromDi()),

    // MSAL providers
    { provide: MSAL_INSTANCE, useFactory: msalInstanceFactory },
    { provide: MSAL_GUARD_CONFIG, useFactory: msalGuardConfigFactory },
    { provide: MSAL_INTERCEPTOR_CONFIG, useFactory: msalInterceptorConfigFactory },
    { provide: HTTP_INTERCEPTORS, useClass: MsalInterceptor, multi: true },
    MsalService,
    MsalGuard,
    MsalBroadcastService,

    // PrimeNG — Lara Light theme
    providePrimeNG({
      theme: {
        preset: Lara,
        options: { prefix: 'p', darkModeSelector: '.dark-mode', cssLayer: false },
      },
    }),

    // MessageService — consumed by api.interceptor toast notifications
    MessageService,
  ],
};
