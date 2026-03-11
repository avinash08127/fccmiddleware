# Observability Terraform

This module creates the CloudWatch dashboards and alarms required by `CB-6.3`.

It expects the application to emit custom metrics into the `FccMiddleware/CloudBackend` namespace via CloudWatch Embedded Metric Format logs. The API host emits ingestion, Odoo latency, telemetry, and error metrics. The worker host emits offline-hour and stale-transaction gauges.

## Usage

```hcl
module "observability" {
  source      = "./infra/terraform/observability"
  environment = "staging"

  alarm_actions = [aws_sns_topic.ops_alerts.arn]
  ok_actions    = [aws_sns_topic.ops_alerts.arn]

  tags = {
    Service = "fccmiddleware-cloud"
    Env     = "staging"
  }
}
```

## Dashboards

- `ingestion-pipeline`: throughput, ingestion error rate, error categories
- `odoo-sync`: Odoo poll latency percentiles and offline hours
- `reconciliation`: reconciliation match rate and stale pending count
- `edge-fleet-health`: buffer depth and FCC heartbeat age

## Alarms

- Ingestion error rate `> 5%`
- Odoo poll latency `p95 > 1000ms`
- Edge Agent offline `> 4h`
- Buffer depth `> 1000`
- Stale pending transactions `> 50`
