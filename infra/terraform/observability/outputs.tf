output "dashboard_names" {
  description = "CloudWatch dashboard names created by this module."
  value = [
    aws_cloudwatch_dashboard.ingestion_pipeline.dashboard_name,
    aws_cloudwatch_dashboard.odoo_sync.dashboard_name,
    aws_cloudwatch_dashboard.reconciliation.dashboard_name,
    aws_cloudwatch_dashboard.edge_fleet_health.dashboard_name
  ]
}

output "alarm_names" {
  description = "CloudWatch alarm names created by this module."
  value = [
    aws_cloudwatch_metric_alarm.ingestion_error_rate.alarm_name,
    aws_cloudwatch_metric_alarm.odoo_poll_latency.alarm_name,
    aws_cloudwatch_metric_alarm.edge_agent_offline.alarm_name,
    aws_cloudwatch_metric_alarm.buffer_depth_high.alarm_name,
    aws_cloudwatch_metric_alarm.stale_transactions.alarm_name
  ]
}
