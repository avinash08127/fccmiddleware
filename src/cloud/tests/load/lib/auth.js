import encoding from 'k6/encoding';
import crypto from 'k6/crypto';

import { config } from './config.js';

function toBase64Url(value) {
  return encoding.b64encode(value, 'rawstd')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/g, '');
}

export function buildDeviceJwt() {
  const nowSeconds = Math.floor(Date.now() / 1000);
  const header = {
    alg: 'HS256',
    typ: 'JWT',
  };

  const payload = {
    sub: config.deviceId,
    oid: config.deviceId,
    site: config.siteCode,
    lei: config.legalEntityId,
    iss: config.deviceJwtIssuer,
    aud: config.deviceJwtAudience,
    iat: nowSeconds,
    nbf: nowSeconds - 5,
    exp: nowSeconds + 3600,
  };

  const encodedHeader = toBase64Url(JSON.stringify(header));
  const encodedPayload = toBase64Url(JSON.stringify(payload));
  const signingInput = `${encodedHeader}.${encodedPayload}`;
  const signature = crypto.hmac('sha256', config.deviceJwtSigningKey, signingInput, 'base64');
  const encodedSignature = signature
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/g, '');

  return `${signingInput}.${encodedSignature}`;
}

export function buildOdooHeaders(extraHeaders = {}) {
  return {
    'Content-Type': 'application/json',
    'X-Api-Key': config.odooApiKey,
    ...extraHeaders,
  };
}

export function buildFccHeaders(method, path, body, extraHeaders = {}) {
  const timestamp = new Date().toISOString();
  const bodyHash = crypto.sha256(body, 'hex');
  const canonical = `${method}${path}${timestamp}${bodyHash}`;
  const signature = crypto.hmac('sha256', config.fccHmacSecret, canonical, 'hex');

  return {
    'Content-Type': 'application/json',
    'X-Api-Key': config.fccApiKeyId,
    'X-Signature': signature,
    'X-Timestamp': timestamp,
    ...extraHeaders,
  };
}

export function buildDeviceHeaders(extraHeaders = {}) {
  return {
    'Content-Type': 'application/json',
    Authorization: `Bearer ${buildDeviceJwt()}`,
    ...extraHeaders,
  };
}
