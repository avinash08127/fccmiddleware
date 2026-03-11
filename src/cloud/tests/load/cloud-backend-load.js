import http from 'k6/http';
import { Counter } from 'k6/metrics';
import { check } from 'k6';

import { buildDeviceHeaders, buildFccHeaders, buildOdooHeaders } from './lib/auth.js';
import { buildOptions, config, validateRequiredEnv } from './lib/config.js';
import { buildAcknowledgeRequest, buildIngestRequest, buildUploadRequest } from './lib/data.js';

const loadCheckFailures = new Counter('load_check_failures');

export const options = buildOptions();

const missingEnv = validateRequiredEnv();
if (missingEnv.length > 0) {
  throw new Error(`Missing required k6 environment variables: ${missingEnv.join(', ')}`);
}

function recordChecks(response, assertions) {
  const success = check(response, assertions);
  if (!success) {
    loadCheckFailures.add(1);
  }
}

function maybeBuildPollQuery() {
  const params = [
    `pageSize=${encodeURIComponent(String(config.defaultPageSize))}`,
    `siteCode=${encodeURIComponent(config.siteCode)}`,
  ];

  if (config.includePollFrom) {
    params.push(`from=${encodeURIComponent(new Date(Date.now() - 3600000).toISOString())}`);
  }

  return params.join('&');
}

export function sustainedIngestion() {
  const body = JSON.stringify(buildIngestRequest());
  const response = http.post(
    `${config.baseUrl}/api/v1/transactions/ingest`,
    body,
    {
      headers: buildFccHeaders('POST', '/api/v1/transactions/ingest', body),
      tags: {
        endpoint: 'transactions_ingest',
        load_profile: config.profileName,
      },
    },
  );

  recordChecks(response, {
    'sustained ingest accepted': (r) => r.status === 202,
  });
}

export function burstIngestion() {
  const body = JSON.stringify(buildIngestRequest());
  const response = http.post(
    `${config.baseUrl}/api/v1/transactions/ingest`,
    body,
    {
      headers: buildFccHeaders('POST', '/api/v1/transactions/ingest', body),
      tags: {
        endpoint: 'transactions_ingest',
        load_profile: config.profileName,
      },
    },
  );

  recordChecks(response, {
    'burst ingest accepted': (r) => r.status === 202,
  });
}

export function odooPollUnderLoad() {
  const response = http.get(
    `${config.baseUrl}/api/v1/transactions?${maybeBuildPollQuery()}`,
    {
      headers: buildOdooHeaders(),
      tags: {
        endpoint: 'transactions_poll',
        load_profile: config.profileName,
      },
    },
  );

  recordChecks(response, {
    'poll responded 200': (r) => r.status === 200,
    'poll response has data array': (r) => Array.isArray((r.json() || {}).data),
  });
}

export function odooAcknowledgeBatch() {
  const pollResponse = http.get(
    `${config.baseUrl}/api/v1/transactions?${maybeBuildPollQuery()}`,
    {
      headers: buildOdooHeaders(),
      tags: {
        endpoint: 'transactions_poll_for_ack',
        load_profile: config.profileName,
      },
    },
  );

  recordChecks(pollResponse, {
    'ack pre-poll responded 200': (r) => r.status === 200,
  });

  const body = pollResponse.json() || {};
  const transactions = Array.isArray(body.data) ? body.data.slice(0, 100) : [];
  if (transactions.length === 0) {
    return;
  }

  const acknowledgeResponse = http.post(
    `${config.baseUrl}/api/v1/transactions/acknowledge`,
    JSON.stringify(buildAcknowledgeRequest(transactions)),
    {
      headers: buildOdooHeaders(),
      tags: {
        endpoint: 'transactions_acknowledge',
        load_profile: config.profileName,
      },
    },
  );

  recordChecks(acknowledgeResponse, {
    'acknowledge responded 200': (r) => r.status === 200,
    'acknowledge batch succeeded': (r) => {
      const json = r.json() || {};
      return typeof json.succeededCount === 'number' && json.failedCount === 0;
    },
  });
}

export function edgeAgentUpload() {
  const response = http.post(
    `${config.baseUrl}/api/v1/transactions/upload`,
    JSON.stringify(buildUploadRequest(100)),
    {
      headers: buildDeviceHeaders(),
      tags: {
        endpoint: 'transactions_upload',
        load_profile: config.profileName,
      },
    },
  );

  recordChecks(response, {
    'upload responded 200': (r) => r.status === 200,
    'upload batch fully accepted': (r) => {
      const json = r.json() || {};
      return json.acceptedCount === 100 && json.duplicateCount === 0 && json.rejectedCount === 0;
    },
  });
}

export function handleSummary(data) {
  const summary = {
    profile: config.profileName,
    baseUrl: config.baseUrl,
    metrics: data.metrics,
  };

  const output = {
    stdout: JSON.stringify(summary, null, 2),
  };

  if (config.summaryExport) {
    output[config.summaryExport] = JSON.stringify(summary, null, 2);
  }

  return output;
}
