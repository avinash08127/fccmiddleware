import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZoneChangeDetection,
} from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
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
import { definePreset } from '@primeuix/themes';
import { MessageService } from 'primeng/api';

const PumaEnergyTheme = definePreset(Lara, {
    semantic: {
        primary: {
            50: '{red.50}',
            100: '{red.100}',
            200: '{red.200}',
            300: '{red.300}',
            400: '{red.400}',
            500: '{red.500}',
            600: '{red.600}',
            700: '{red.700}',
            800: '{red.800}',
            900: '{red.900}',
            950: '{red.950}',
        },
    },
});

import { routes } from './app.routes';
import { apiInterceptor } from './core/interceptors/api.interceptor';
import {
  msalGuardConfigFactory,
  msalInstanceFactory,
  msalInterceptorConfigFactory,
} from './core/auth/auth.config';
import { providePortalAuth } from './core/auth/auth-state';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideAnimationsAsync(),
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
    providePortalAuth(),

    // PrimeNG — Lara Light theme with Puma Energy Red primary
    providePrimeNG({
      theme: {
        preset: PumaEnergyTheme,
        options: { prefix: 'p', darkModeSelector: '.dark-mode', cssLayer: false },
      },
    }),

    // MessageService — consumed by api.interceptor toast notifications
    MessageService,
  ],
};
