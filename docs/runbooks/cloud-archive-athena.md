# Cloud Archive Athena Access

The archive worker writes Parquet files using a Hive-style S3 prefix:

- `s3://<archive-bucket>/archives/transactions/year=YYYY/month=MM/partition=<partition-name>/data.parquet`
- `s3://<archive-bucket>/archives/audit_events/year=YYYY/month=MM/partition=<partition-name>/data.parquet`

Each archived partition also gets a colocated `manifest.json` with row count, source partition, and archive timestamp.

## Athena external table: transactions

```sql
CREATE EXTERNAL TABLE IF NOT EXISTS archived_transactions (
  id string,
  createdat timestamp,
  legalentityid string,
  fcctransactionid string,
  sitecode string,
  pumpnumber int,
  nozzlenumber int,
  productcode string,
  volumemicrolitres bigint,
  amountminorunits bigint,
  unitpriceminorperlitre bigint,
  currencycode string,
  startedat timestamp,
  completedat timestamp,
  fiscalreceiptnumber string,
  fcccorrelationid string,
  fccvendor string,
  attendantid string,
  status string,
  ingestionsource string,
  rawpayloadref string,
  odooorderid string,
  syncedtoodooat timestamp,
  preauthid string,
  reconciliationstatus string,
  isduplicate boolean,
  duplicateofid string,
  isstale boolean,
  correlationid string,
  schemaversion int,
  updatedat timestamp
)
PARTITIONED BY (
  year string,
  month string,
  partition string
)
STORED AS PARQUET
LOCATION 's3://<archive-bucket>/archives/transactions/';
```

Register new partitions after archive runs:

```sql
MSCK REPAIR TABLE archived_transactions;
```

## Athena external table: audit events

```sql
CREATE EXTERNAL TABLE IF NOT EXISTS archived_audit_events (
  id string,
  createdat timestamp,
  legalentityid string,
  eventtype string,
  correlationid string,
  sitecode string,
  source string,
  payload string
)
PARTITIONED BY (
  year string,
  month string,
  partition string
)
STORED AS PARQUET
LOCATION 's3://<archive-bucket>/archives/audit_events/';
```

Run:

```sql
MSCK REPAIR TABLE archived_audit_events;
```

## Notes

- Production should configure `Storage:ArchiveBucket` and optionally `Storage:ArchiveKmsKeyId`.
- Dev/test can use `Storage:ArchiveLocalRoot`; the worker writes the same folder structure locally.
- The worker only detaches partitions whose upper bound is entirely older than the retention cutoff, so current monthly partitions stay attached and queryable.
