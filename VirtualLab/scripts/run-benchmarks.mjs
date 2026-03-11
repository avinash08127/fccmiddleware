#!/usr/bin/env node

import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.resolve(__dirname, '..');
const seedPath = path.join(rootDir, 'config', 'benchmark-seed.json');
const baseUrl = process.env.VIRTUAL_LAB_BASE_URL ?? 'http://localhost:5099';
const iterations = Number.parseInt(process.env.VIRTUAL_LAB_BENCH_ITERATIONS ?? '15', 10);

async function loadSeedProfile() {
  const json = await fs.readFile(seedPath, 'utf8');
  return JSON.parse(json);
}

function percentile(values, p) {
  if (values.length === 0) {
    return 0;
  }

  const sorted = [...values].sort((a, b) => a - b);
  const index = Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1);
  return sorted[index];
}

async function sampleEndpoint(seedProfile) {
  const url = new URL('/api/diagnostics/latency', baseUrl);
  url.searchParams.set('iterations', String(iterations));
  url.searchParams.set('profileName', seedProfile.profileName);

  const startedAt = performance.now();
  const response = await fetch(url);
  const endedAt = performance.now();

  if (!response.ok) {
    throw new Error(`Diagnostics endpoint returned ${response.status} ${response.statusText}`);
  }

  const payload = await response.json();
  return {
    roundTripMs: endedAt - startedAt,
    payload,
  };
}

function evaluateGuardrails(samples) {
  const roundTrips = samples.map((sample) => sample.roundTripMs);
  const probePayload = samples.at(-1)?.payload;

  if (!probePayload) {
    throw new Error('No probe payloads were captured.');
  }

  const checks = [
    {
      name: 'dashboard-api-roundtrip',
      thresholdMs: 2000,
      observedMs: percentile(roundTrips, 95),
    },
    {
      name: 'fcc-emulator',
      thresholdMs: probePayload.thresholds.fccEmulatorP95Ms,
      observedMs: probePayload.measurements.fccHealthP95Ms,
    },
    {
      name: 'transaction-pull',
      thresholdMs: probePayload.thresholds.transactionPullP95Ms,
      observedMs: probePayload.measurements.transactionPullP95Ms,
    },
    {
      name: 'dashboard-query',
      thresholdMs: probePayload.thresholds.dashboardLoadP95Ms,
      observedMs: probePayload.measurements.dashboardQueryP95Ms,
    },
  ];

  return checks.map((check) => ({
    ...check,
    pass: check.observedMs <= check.thresholdMs,
  }));
}

async function main() {
  const seedProfile = await loadSeedProfile();
  const samples = [];

  for (let index = 0; index < iterations; index += 1) {
    samples.push(await sampleEndpoint(seedProfile));
  }

  const checks = evaluateGuardrails(samples);
  const failedChecks = checks.filter((check) => !check.pass);
  const replaySignature = samples.at(-1)?.payload.replaySignature ?? 'n/a';

  console.log(`Virtual Lab benchmark seed: ${seedProfile.profileName}`);
  console.log(`Base URL: ${baseUrl}`);
  console.log(`Replay signature: ${replaySignature}`);

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
