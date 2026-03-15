import {
  msalGuardConfigFactory,
  msalInterceptorConfigFactory,
  msalInstanceFactory,
} from './auth.config';
import { environment } from '../../../environments/environment';

describe('auth.config', () => {
  it('uses the API scope for guard auth requests', () => {
    const config = msalGuardConfigFactory();
    expect(typeof config.authRequest).not.toBe('function');

    if (typeof config.authRequest === 'function') {
      fail('Expected static guard auth request configuration');
      return;
    }

    expect(config.authRequest?.scopes).toEqual([environment.msalApiScope]);
  });

  it('protects API paths with a wildcard entry so MsalInterceptor matches routed endpoints', () => {
    const config = msalInterceptorConfigFactory();

    expect(config.protectedResourceMap.get(`${environment.apiBaseUrl}/api/*`)).toEqual([
      environment.msalApiScope,
    ]);
  });

  it('configures the public client application with local storage caching', () => {
    const instance = msalInstanceFactory();

    expect(instance.getConfiguration().cache.cacheLocation).toBe('localStorage');
  });
});
