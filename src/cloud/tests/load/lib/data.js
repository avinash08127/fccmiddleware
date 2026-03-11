import exec from 'k6/execution';

import { config } from './config.js';

function uniqueSuffix(prefix) {
  return [
    prefix,
    config.siteCode,
    exec.scenario.name,
    `vu${exec.vu.idInTest}`,
    `it${exec.scenario.iterationInTest}`,
    Date.now(),
  ].join('-');
}

function buildTransactionTimes(sequence) {
  const completedAt = new Date(Date.now() - (sequence % 15) * 1000);
  const startedAt = new Date(completedAt.getTime() - 120000);

  return {
    startedAt: startedAt.toISOString(),
    completedAt: completedAt.toISOString(),
  };
}

export function buildIngestRequest() {
  const transactionId = uniqueSuffix('DOMS-TX');
  const sequence = exec.scenario.iterationInTest + 1;
  const times = buildTransactionTimes(sequence);

  return {
    fccVendor: config.fccVendor,
    siteCode: config.siteCode,
    capturedAt: new Date().toISOString(),
    rawPayload: {
      transactionId,
      pumpNumber: (sequence % 8) + 1,
      nozzleNumber: (sequence % 4) + 1,
      productCode: config.productCode,
      volumeMicrolitres: 25000000 + (sequence % 10) * 100000,
      amountMinorUnits: 35000 + (sequence % 10) * 250,
      unitPriceMinorPerLitre: 1400 + (sequence % 10) * 10,
      startTime: times.startedAt,
      endTime: times.completedAt,
      attendantId: `ATT-${(sequence % 25) + 1}`,
      receiptNumber: uniqueSuffix('R'),
    },
  };
}

export function buildUploadRequest(batchSize = 100) {
  const transactions = [];

  for (let index = 0; index < batchSize; index += 1) {
    const sequence = exec.scenario.iterationInTest * batchSize + index + 1;
    const times = buildTransactionTimes(sequence);

    transactions.push({
      fccTransactionId: uniqueSuffix(`EDGE-${index}`),
      siteCode: config.siteCode,
      fccVendor: config.fccVendor,
      pumpNumber: (sequence % 8) + 1,
      nozzleNumber: (sequence % 4) + 1,
      productCode: config.productCode,
      volumeMicrolitres: 24000000 + (sequence % 10) * 125000,
      amountMinorUnits: 34000 + (sequence % 10) * 250,
      unitPriceMinorPerLitre: 1400 + (sequence % 10) * 10,
      currencyCode: config.currencyCode,
      startedAt: times.startedAt,
      completedAt: times.completedAt,
      fccCorrelationId: uniqueSuffix('CORR'),
      attendantId: `EDGE-ATT-${(sequence % 25) + 1}`,
    });
  }

  return { transactions };
}

export function buildAcknowledgeRequest(transactions) {
  return {
    acknowledgements: transactions.map((transaction) => ({
      id: transaction.id,
      odooOrderId: uniqueSuffix(`POS-${transaction.siteCode}`),
    })),
  };
}
