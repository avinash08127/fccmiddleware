#!/usr/bin/env node

import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.resolve(__dirname, '..');
const seedPath = path.join(rootDir, 'config', 'benchmark-seed.json');
const signalrModulePath = path.join(
  rootDir,
  'ui',
  'virtual-lab',
  'node_modules',
  '@microsoft',
  'signalr',
  'dist',
  'esm',
  'index.js',
);
const baseUrl = process.env.VIRTUAL_LAB_BASE_URL ?? 'http://localhost:5099';
const iterations = Number.parseInt(process.env.VIRTUAL_LAB_BENCH_ITERATIONS ?? '15', 10);

async function loadSeedProfile() {
  const json = await fs.readFile(seedPath, 'utf8');
  return JSON.parse(json);
}

async function loadProbeSummary() {
  const url = new URL('/api/diagnostics/latency', baseUrl);
  url.searchParams.set('iterations', String(iterations));

  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`Diagnostics endpoint returned ${response.status} ${response.statusText}`);
  }

  return response.json();
}

function percentile(values, p) {
  if (values.length === 0) {
    return 0;
  }

  const sorted = [...values].sort((a, b) => a - b);
  const index = Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1);
  return sorted[index];
}

async function measureEndpoint(url) {
  const startedAt = performance.now();
  const response = await fetch(url);
  const endedAt = performance.now();

  if (!response.ok) {
    throw new Error(`Endpoint ${url.pathname} returned ${response.status} ${response.statusText}`);
  }

  await response.text();
  return endedAt - startedAt;
}

async function resolveSiteCode() {
  const url = new URL('/api/sites', baseUrl);
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`Sites endpoint returned ${response.status} ${response.statusText}`);
  }

  const sites = await response.json();
  const activeSite = sites.find((site) => site.isActive) ?? sites[0];
  if (!activeSite?.siteCode) {
    throw new Error('No benchmark site could be resolved from /api/sites.');
  }

  return activeSite.siteCode;
}

async function createSignalRProbe() {
  const { HubConnectionBuilder, LogLevel } = await import(pathToFileURL(signalrModulePath).href);
  const hubUrl = new URL('/hubs/live', baseUrl).toString();
  const pending = new Map();
  const connection = new HubConnectionBuilder().withUrl(hubUrl).configureLogging(LogLevel.None).build();

  connection.on('lab-event', (payload) => {
    const correlationId = payload?.correlationId;
    const resolver = correlationId ? pending.get(correlationId) : undefined;
    if (resolver) {
      pending.delete(correlationId);
      resolver(payload);
    }
  });

  return {
    async start() {
      await connection.start();
    },
    async stop() {
      await connection.stop();
    },
    async sample() {
      const correlationId = `bench-${Date.now()}-${Math.random().toString(16).slice(2)}`;
      const received = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => {
          pending.delete(correlationId);
          reject(new Error(`Timed out waiting for SignalR event ${correlationId}.`));
        }, 5000);

        pending.set(correlationId, (payload) => {
          clearTimeout(timeout);
          resolve(payload);
        });
      });

      const url = new URL('/api/diagnostics/live-broadcast', baseUrl);
      url.searchParams.set('correlationId', correlationId);

      const startedAt = performance.now();
      const response = await fetch(url, { method: 'POST' });
      if (!response.ok) {
        throw new Error(`Live broadcast endpoint returned ${response.status} ${response.statusText}`);
      }

      await received;
      return performance.now() - startedAt;
    },
  };
}

function evaluateGuardrails(probePayload, measurements) {
  const checks = [
    {
      name: 'dashboard-endpoint',
      thresholdMs: probePayload.thresholds.dashboardLoadP95Ms,
      observedMs: percentile(measurements.dashboard, 95),
    },
    {
      name: 'sites-endpoint',
      thresholdMs: probePayload.thresholds.dashboardLoadP95Ms,
      observedMs: percentile(measurements.sites, 95),
    },
    {
      name: 'fcc-health-endpoint',
      thresholdMs: probePayload.thresholds.fccEmulatorP95Ms,
      observedMs: percentile(measurements.fccHealth, 95),
    },
    {
      name: 'transaction-pull-endpoint',
      thresholdMs: probePayload.thresholds.transactionPullP95Ms,
      observedMs: percentile(measurements.transactionPull, 95),
    },
    {
      name: 'signalr-end-to-end',
      thresholdMs: probePayload.thresholds.signalRUpdateP95Ms,
      observedMs: percentile(measurements.signalr, 95),
    },
  ];

  return checks.map((check) => ({
    ...check,
    pass: check.observedMs <= check.thresholdMs,
  }));
}

async function main() {
  const seedProfile = await loadSeedProfile();
  const probePayload = await loadProbeSummary();
  const siteCode = await resolveSiteCode();
  const signalrProbe = await createSignalRProbe();
  const measurements = {
    dashboard: [],
    sites: [],
    fccHealth: [],
    transactionPull: [],
    signalr: [],
  };

  await signalrProbe.start();

  try {
    const dashboardUrl = new URL('/api/dashboard', baseUrl);
    const sitesUrl = new URL('/api/sites', baseUrl);
    const fccHealthUrl = new URL(`/fcc/${encodeURIComponent(siteCode)}/health`, baseUrl);
    const transactionPullUrl = new URL(`/fcc/${encodeURIComponent(siteCode)}/transactions`, baseUrl);
    transactionPullUrl.searchParams.set('limit', '100');

    for (let index = 0; index < iterations; index += 1) {
      measurements.dashboard.push(await measureEndpoint(dashboardUrl));
      measurements.sites.push(await measureEndpoint(sitesUrl));
      measurements.fccHealth.push(await measureEndpoint(fccHealthUrl));
      measurements.transactionPull.push(await measureEndpoint(transactionPullUrl));
      measurements.signalr.push(await signalrProbe.sample());
    }
  } finally {
    await signalrProbe.stop();
  }

  const checks = evaluateGuardrails(probePayload, measurements);
  const failedChecks = checks.filter((check) => !check.pass);
  const replaySignature = probePayload.replaySignature ?? 'n/a';

  console.log(`Virtual Lab benchmark seed: ${seedProfile.profileName}`);
  console.log(`Base URL: ${baseUrl}`);
  console.log(`Site code: ${siteCode}`);
  console.log(`Replay signature: ${replaySignature}`);
  console.log(
    `Server probe p95: dashboard=${probePayload.measurements.dashboardQueryP95Ms.toFixed(2)}ms sites=${probePayload.measurements.siteLoadP95Ms.toFixed(2)}ms fcc=${probePayload.measurements.fccHealthP95Ms.toFixed(2)}ms pull=${probePayload.measurements.transactionPullP95Ms.toFixed(2)}ms signalr-send=${probePayload.measurements.signalRBroadcastP95Ms.toFixed(2)}ms`,
  );

  for (const check of checks) {
    console.log(
      `${check.pass ? 'PASS' : 'FAIL'} ${check.name}: observed=${check.observedMs.toFixed(2)}ms threshold=${check.thresholdMs.toFixed(2)}ms`,
    );
  }

  if (failedChecks.length > 0) {
    process.exitCode = 1;
  }
}

await main();
