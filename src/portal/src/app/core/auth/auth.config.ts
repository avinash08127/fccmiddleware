import {
  BrowserCacheLocation,
  IPublicClientApplication,
  InteractionType,
  LogLevel,
  PublicClientApplication,
} from '@azure/msal-browser';
import {
  MsalGuardConfiguration,
  MsalInterceptorConfiguration,
} from '@azure/msal-angular';
import { environment } from '../../../environments/environment';

export function msalInstanceFactory(): IPublicClientApplication {
  return new PublicClientApplication({
    auth: {
      clientId: environment.msalClientId,
      authority: environment.msalAuthority,
      redirectUri: environment.msalRedirectUri,
    },
    cache: {
      cacheLocation: BrowserCacheLocation.LocalStorage,
    },
    system: {
      loggerOptions: {
        logLevel: environment.production ? LogLevel.Error : LogLevel.Warning,
        piiLoggingEnabled: false,
      },
    },
  });
}

export function msalGuardConfigFactory(): MsalGuardConfiguration {
  return {
    interactionType: InteractionType.Redirect,
    authRequest: {
      scopes: [environment.msalApiScope],
    },
  };
}

export function msalInterceptorConfigFactory(): MsalInterceptorConfiguration {
  const protectedResourceMap = new Map<string, string[]>();
  protectedResourceMap.set(`${environment.apiBaseUrl}/api/*`, [environment.msalApiScope]);

  return {
    interactionType: InteractionType.Redirect,
    protectedResourceMap,
  };
}
