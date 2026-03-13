#!/usr/bin/env node
/**
 * Pre-build validation: ensures environment placeholder values have been
 * replaced before producing a production or staging build.
 *
 * Runs as part of `npm run build` via the prebuild hook.
 * Skips validation when NODE_ENV is not set or equals "development".
 */
const fs = require('fs');
const path = require('path');

const PLACEHOLDER_PATTERN = /YOUR_[A-Z_]+/g;

const envDir = path.join(__dirname, '..', 'src', 'environments');

const filesToCheck =
  process.env.NODE_ENV === 'production'
    ? ['environment.prod.ts']
    : process.env.NODE_ENV === 'staging'
      ? ['environment.staging.ts']
      : null;

if (!filesToCheck) {
  // Local dev — no validation needed
  process.exit(0);
}

let failed = false;

for (const file of filesToCheck) {
  const filePath = path.join(envDir, file);
  if (!fs.existsSync(filePath)) continue;

  const content = fs.readFileSync(filePath, 'utf8');
  const matches = content.match(PLACEHOLDER_PATTERN);

  if (matches) {
    console.error(
      `\x1b[31mERROR: ${file} contains unreplaced placeholders: ${[...new Set(matches)].join(', ')}\x1b[0m`,
    );
    console.error(
      `  Ensure your CI/CD pipeline substitutes these values before building.`,
    );
    failed = true;
  }
}

if (failed) {
  process.exit(1);
}
