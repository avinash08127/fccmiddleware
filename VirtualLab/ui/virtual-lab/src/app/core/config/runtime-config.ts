import { environment } from '../../../environments/environment';

export interface RuntimeConfig {
  production: boolean;
  environmentName: string;
  apiBaseUrl: string;
  signalRHubUrl: string;
}

export const runtimeConfig: RuntimeConfig = {
  production: environment.production,
  environmentName: environment.environmentName,
  apiBaseUrl: normalizeBaseUrl(environment.apiBaseUrl),
  signalRHubUrl: normalizeHubUrl(environment.apiBaseUrl, environment.signalRHubUrl),
};

export async function loadRuntimeConfig(): Promise<void> {
  try {
    const response = await fetch('/assets/config/runtime-config.json', {
      cache: 'no-store',
    });

    if (!response.ok) {
      return;
    }

    const overrides = (await response.json()) as Partial<RuntimeConfig>;
    const apiBaseUrl = normalizeBaseUrl(overrides.apiBaseUrl ?? runtimeConfig.apiBaseUrl);
    const signalRHubUrl = normalizeHubUrl(apiBaseUrl, overrides.signalRHubUrl ?? runtimeConfig.signalRHubUrl);

    runtimeConfig.environmentName = overrides.environmentName?.trim() || runtimeConfig.environmentName;
    runtimeConfig.apiBaseUrl = apiBaseUrl;
    runtimeConfig.signalRHubUrl = signalRHubUrl;
  } catch {
    runtimeConfig.apiBaseUrl = normalizeBaseUrl(runtimeConfig.apiBaseUrl);
    runtimeConfig.signalRHubUrl = normalizeHubUrl(runtimeConfig.apiBaseUrl, runtimeConfig.signalRHubUrl);
  }
}

function normalizeBaseUrl(value: string | undefined): string {
  const trimmed = value?.trim() ?? '';
  return trimmed.replace(/\/+$/, '');
}

function normalizeHubUrl(apiBaseUrl: string, signalRHubUrl: string | undefined): string {
  const trimmedHubUrl = signalRHubUrl?.trim() ?? '';
  if (!trimmedHubUrl) {
    return apiBaseUrl ? `${apiBaseUrl}/hubs/live` : '/hubs/live';
  }

  if (/^https?:\/\//i.test(trimmedHubUrl)) {
    return trimmedHubUrl.replace(/\/+$/, '');
  }

  const normalizedRelativePath = trimmedHubUrl.replace(/^\/+/, '');
  if (!apiBaseUrl) {
    return `/${normalizedRelativePath}`;
  }

  return trimmedHubUrl.startsWith('/')
    ? `${apiBaseUrl}/${normalizedRelativePath}`
    : `${apiBaseUrl}/${normalizedRelativePath}`;
}
