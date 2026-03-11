const profileName = (__ENV.K6_PROFILE || 'acceptance').toLowerCase();

const profiles = {
  smoke: {
    sustainedDuration: '30s',
    sustainedRate: 5,
    sustainedPreAllocatedVUs: 10,
    sustainedMaxVUs: 30,
    burstStartTime: '5s',
    burstDuration: '20s',
    burstRate: 15,
    burstPreAllocatedVUs: 10,
    burstMaxVUs: 40,
    pollStartTime: '10s',
    pollDuration: '30s',
    pollRate: 4,
    pollPreAllocatedVUs: 4,
    pollMaxVUs: 10,
    acknowledgeStartTime: '12s',
    acknowledgeDuration: '25s',
    acknowledgeRate: 1,
    acknowledgeTimeUnit: '5s',
    edgeUploadStartTime: '15s',
    edgeUploadVUs: 5,
    edgeUploadIterations: 1,
  },
  acceptance: {
    sustainedDuration: '1h',
    sustainedRate: 23,
    sustainedPreAllocatedVUs: 32,
    sustainedMaxVUs: 128,
    burstStartTime: '10m',
    burstDuration: '5m',
    burstRate: 100,
    burstPreAllocatedVUs: 64,
    burstMaxVUs: 256,
    pollStartTime: '2m',
    pollDuration: '55m',
    pollRate: 20,
    pollPreAllocatedVUs: 12,
    pollMaxVUs: 40,
    acknowledgeStartTime: '3m',
    acknowledgeDuration: '50m',
    acknowledgeRate: 1,
    acknowledgeTimeUnit: '5s',
    edgeUploadStartTime: '15m',
    edgeUploadVUs: 50,
    edgeUploadIterations: 1,
  },
};

function parseInteger(value, fallback) {
  if (value === undefined || value === null || value === '') {
    return fallback;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isNaN(parsed) ? fallback : parsed;
}

function parseBoolean(value, fallback = false) {
  if (value === undefined || value === null || value === '') {
    return fallback;
  }

  const normalized = String(value).trim().toLowerCase();
  if (['1', 'true', 'yes', 'on'].includes(normalized)) {
    return true;
  }

  if (['0', 'false', 'no', 'off'].includes(normalized)) {
    return false;
  }

  return fallback;
}

const profile = profiles[profileName] || profiles.acceptance;

export const config = {
  profileName,
  baseUrl: (__ENV.K6_BASE_URL || 'http://localhost:8080').replace(/\/+$/, ''),
  odooApiKey: __ENV.K6_ODOO_API_KEY || '',
  fccApiKeyId: __ENV.K6_FCC_API_KEY_ID || '',
  fccHmacSecret: __ENV.K6_FCC_HMAC_SECRET || '',
  deviceJwtSigningKey: __ENV.K6_DEVICE_JWT_SIGNING_KEY || '',
  deviceJwtIssuer: __ENV.K6_DEVICE_JWT_ISSUER || 'fcc-middleware-cloud',
  deviceJwtAudience: __ENV.K6_DEVICE_JWT_AUDIENCE || 'fcc-middleware-api',
  deviceId: __ENV.K6_DEVICE_ID || 'load-device-01',
  legalEntityId: __ENV.K6_LEGAL_ENTITY_ID || '',
  siteCode: __ENV.K6_SITE_CODE || 'LOAD-SITE-001',
  fccVendor: (__ENV.K6_FCC_VENDOR || 'DOMS').toUpperCase(),
  currencyCode: (__ENV.K6_CURRENCY_CODE || 'GHS').toUpperCase(),
  productCode: (__ENV.K6_PRODUCT_CODE || 'PMS').toUpperCase(),
  defaultPageSize: parseInteger(__ENV.K6_PAGE_SIZE, 100),
  includePollFrom: parseBoolean(__ENV.K6_INCLUDE_POLL_FROM, false),
  summaryExport: __ENV.K6_SUMMARY_EXPORT || '',
  profile,
};

export function buildOptions() {
  return {
    discardResponseBodies: false,
    scenarios: {
      sustained_ingestion: {
        executor: 'constant-arrival-rate',
        exec: 'sustainedIngestion',
        rate: profile.sustainedRate,
        timeUnit: '1s',
        duration: profile.sustainedDuration,
        preAllocatedVUs: profile.sustainedPreAllocatedVUs,
        maxVUs: profile.sustainedMaxVUs,
        tags: { scenario_type: 'sustained_ingestion' },
      },
      burst_ingestion: {
        executor: 'constant-arrival-rate',
        exec: 'burstIngestion',
        startTime: profile.burstStartTime,
        rate: profile.burstRate,
        timeUnit: '1s',
        duration: profile.burstDuration,
        preAllocatedVUs: profile.burstPreAllocatedVUs,
        maxVUs: profile.burstMaxVUs,
        tags: { scenario_type: 'burst_ingestion' },
      },
      odoo_poll_under_load: {
        executor: 'constant-arrival-rate',
        exec: 'odooPollUnderLoad',
        startTime: profile.pollStartTime,
        rate: profile.pollRate,
        timeUnit: '1s',
        duration: profile.pollDuration,
        preAllocatedVUs: profile.pollPreAllocatedVUs,
        maxVUs: profile.pollMaxVUs,
        tags: { scenario_type: 'odoo_poll_under_load' },
      },
      odoo_acknowledge: {
        executor: 'constant-arrival-rate',
        exec: 'odooAcknowledgeBatch',
        startTime: profile.acknowledgeStartTime,
        rate: profile.acknowledgeRate,
        timeUnit: profile.acknowledgeTimeUnit,
        duration: profile.acknowledgeDuration,
        preAllocatedVUs: 2,
        maxVUs: 8,
        tags: { scenario_type: 'odoo_acknowledge' },
      },
      edge_agent_upload: {
        executor: 'per-vu-iterations',
        exec: 'edgeAgentUpload',
        startTime: profile.edgeUploadStartTime,
        vus: profile.edgeUploadVUs,
        iterations: profile.edgeUploadIterations,
        maxDuration: '10m',
        tags: { scenario_type: 'edge_agent_upload' },
      },
    },
    thresholds: {
      'http_req_failed{scenario:sustained_ingestion}': ['rate==0'],
      'http_req_duration{scenario:sustained_ingestion}': ['p(99)<500'],
      'http_req_failed{scenario:burst_ingestion}': ['rate==0'],
      'http_req_duration{scenario:burst_ingestion}': ['p(99)<500'],
      'http_req_failed{scenario:odoo_poll_under_load}': ['rate==0'],
      'http_req_duration{scenario:odoo_poll_under_load}': ['p(95)<200'],
      'http_req_failed{scenario:odoo_acknowledge}': ['rate==0'],
      'http_req_failed{scenario:edge_agent_upload}': ['rate==0'],
      'http_req_duration{scenario:edge_agent_upload}': ['p(95)<2000'],
      load_check_failures: ['count==0'],
    },
  };
}

export function validateRequiredEnv() {
  const missing = [];

  if (!config.legalEntityId) {
    missing.push('K6_LEGAL_ENTITY_ID');
  }

  if (!config.odooApiKey) {
    missing.push('K6_ODOO_API_KEY');
  }

  if (!config.fccApiKeyId) {
    missing.push('K6_FCC_API_KEY_ID');
  }

  if (!config.fccHmacSecret) {
    missing.push('K6_FCC_HMAC_SECRET');
  }

  if (!config.deviceJwtSigningKey) {
    missing.push('K6_DEVICE_JWT_SIGNING_KEY');
  }

  return missing;
}
