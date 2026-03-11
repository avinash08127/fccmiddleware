locals {
  dashboard_prefix = "${var.dashboard_name_prefix}-${var.environment}"

  ingestion_error_search = "SUM(SEARCH('{${var.metric_namespace},MetricName,environment} MetricName=\"ingestion_error_count\" environment=\"${var.environment}\"', 'Sum', 300))"
  ingestion_success_search = "SUM(SEARCH('{${var.metric_namespace},MetricName,environment} MetricName=\"ingestion_success_count\" environment=\"${var.environment}\"', 'Sum', 300))"
  ingestion_error_rate_expression = "IF((${local.ingestion_success_search} + ${local.ingestion_error_search}) > 0, 100 * ${local.ingestion_error_search} / (${local.ingestion_success_search} + ${local.ingestion_error_search}), 0)"

  odoo_latency_p50 = "MAX(SEARCH('{${var.metric_namespace},MetricName,environment} MetricName=\"odoo_poll_latency_ms\" environment=\"${var.environment}\"', 'p50', 300))"
  odoo_latency_p95 = "MAX(SEARCH('{${var.metric_namespace},MetricName,environment} MetricName=\"odoo_poll_latency_ms\" environment=\"${var.environment}\"', 'p95', 300))"
  odoo_latency_p99 = "MAX(SEARCH('{${var.metric_namespace},MetricName,environment} MetricName=\"odoo_poll_latency_ms\" environment=\"${var.environment}\"', 'p99', 300))"
  reconciliation_match_rate = "AVG(SEARCH('{${var.metric_namespace},MetricName,environment} MetricName=\"reconciliation_match_rate_percent\" environment=\"${var.environment}\"', 'Average', 300))"
  buffer_depth_max = "MAX(SEARCH('{${var.metric_namespace},MetricName,environment} MetricName=\"edge_buffer_depth_records\" environment=\"${var.environment}\"', 'Maximum', 300))"
  offline_hours_max = "MAX(SEARCH('{${var.metric_namespace},MetricName,environment} MetricName=\"edge_agent_offline_hours\" environment=\"${var.environment}\"', 'Maximum', 300))"
  stale_count_max = "MAX(SEARCH('{${var.metric_namespace},MetricName,environment} MetricName=\"stale_transaction_count\" environment=\"${var.environment}\"', 'Maximum', 300))"
  error_count_by_category = "SEARCH('{${var.metric_namespace},MetricName,environment,category} MetricName=\"application_error_count\" environment=\"${var.environment}\"', 'Sum', 300)"
}

resource "aws_cloudwatch_dashboard" "ingestion_pipeline" {
  dashboard_name = "${local.dashboard_prefix}-ingestion-pipeline"
  dashboard_body = jsonencode({
    widgets = [
      {
        type   = "metric"
        x      = 0
        y      = 0
        width  = 12
        height = 6
        properties = {
          title   = "Ingestion Throughput (tx/sec by source)"
          region  = "us-east-1"
          view    = "timeSeries"
          stat    = "Sum"
          period  = 60
          metrics = [
            [{ expression = "SEARCH('{${var.metric_namespace},MetricName,environment,source} MetricName=\"ingestion_success_count\" environment=\"${var.environment}\"', 'Sum', 60)", id = "e1", label = "Accepted tx/min by source" }]
          ]
        }
      },
      {
        type   = "metric"
        x      = 12
        y      = 0
        width  = 12
        height = 6
        properties = {
          title   = "Ingestion Error Rate (%)"
          region  = "us-east-1"
          view    = "timeSeries"
          metrics = [
            [{ expression = local.ingestion_error_rate_expression, id = "e1", label = "Error rate %" }]
          ]
        }
      },
      {
        type   = "metric"
        x      = 0
        y      = 6
        width  = 24
        height = 6
        properties = {
          title   = "Error Rate by Category"
          region  = "us-east-1"
          view    = "timeSeries"
          stat    = "Sum"
          period  = 300
          metrics = [
            [{ expression = local.error_count_by_category, id = "e1", label = "Errors by category" }]
          ]
        }
      }
    ]
  })
}

resource "aws_cloudwatch_dashboard" "odoo_sync" {
  dashboard_name = "${local.dashboard_prefix}-odoo-sync"
  dashboard_body = jsonencode({
    widgets = [
      {
        type   = "metric"
        x      = 0
        y      = 0
        width  = 16
        height = 6
        properties = {
          title   = "Odoo Poll Latency (p50/p95/p99)"
          region  = "us-east-1"
          view    = "timeSeries"
          metrics = [
            [{ expression = local.odoo_latency_p50, id = "e1", label = "p50" }],
            [{ expression = local.odoo_latency_p95, id = "e2", label = "p95" }],
            [{ expression = local.odoo_latency_p99, id = "e3", label = "p99" }]
          ]
        }
      },
      {
        type   = "metric"
        x      = 16
        y      = 0
        width  = 8
        height = 6
        properties = {
          title   = "Edge Agent Offline Hours"
          region  = "us-east-1"
          view    = "singleValue"
          metrics = [
            [{ expression = local.offline_hours_max, id = "e1", label = "Max offline hours" }]
          ]
        }
      }
    ]
  })
}

resource "aws_cloudwatch_dashboard" "reconciliation" {
  dashboard_name = "${local.dashboard_prefix}-reconciliation"
  dashboard_body = jsonencode({
    widgets = [
      {
        type   = "metric"
        x      = 0
        y      = 0
        width  = 12
        height = 6
        properties = {
          title   = "Reconciliation Match Rate"
          region  = "us-east-1"
          view    = "timeSeries"
          metrics = [
            [{ expression = local.reconciliation_match_rate, id = "e1", label = "Match rate %" }]
          ]
        }
      },
      {
        type   = "metric"
        x      = 12
        y      = 0
        width  = 12
        height = 6
        properties = {
          title   = "Stale Pending Transactions"
          region  = "us-east-1"
          view    = "singleValue"
          metrics = [
            [{ expression = local.stale_count_max, id = "e1", label = "Stale count" }]
          ]
        }
      }
    ]
  })
}

resource "aws_cloudwatch_dashboard" "edge_fleet_health" {
  dashboard_name = "${local.dashboard_prefix}-edge-fleet-health"
  dashboard_body = jsonencode({
    widgets = [
      {
        type   = "metric"
        x      = 0
        y      = 0
        width  = 12
        height = 6
        properties = {
          title   = "Buffer Depths"
          region  = "us-east-1"
          view    = "timeSeries"
          metrics = [
            [{ expression = "SEARCH('{${var.metric_namespace},MetricName,environment,siteCode,deviceId} MetricName=\"edge_buffer_depth_records\" environment=\"${var.environment}\"', 'Maximum', 300)", id = "e1", label = "Pending upload backlog" }]
          ]
        }
      },
      {
        type   = "metric"
        x      = 12
        y      = 0
        width  = 12
        height = 6
        properties = {
          title   = "FCC Heartbeat Age (minutes)"
          region  = "us-east-1"
          view    = "timeSeries"
          metrics = [
            [{ expression = "SEARCH('{${var.metric_namespace},MetricName,environment,siteCode,deviceId} MetricName=\"fcc_heartbeat_age_minutes\" environment=\"${var.environment}\"', 'Maximum', 300)", id = "e1", label = "Heartbeat age" }]
          ]
        }
      }
    ]
  })
}

resource "aws_cloudwatch_metric_alarm" "ingestion_error_rate" {
  alarm_name          = "${local.dashboard_prefix}-ingestion-error-rate"
  alarm_description   = "Triggers when ingestion error rate exceeds 5 percent over 5 minutes."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  threshold           = 5
  treat_missing_data  = "notBreaching"
  alarm_actions       = var.alarm_actions
  ok_actions          = var.ok_actions
  tags                = var.tags

  metric_query {
    id          = "error_rate"
    expression  = local.ingestion_error_rate_expression
    label       = "Ingestion error rate percent"
    return_data = true
  }
}

resource "aws_cloudwatch_metric_alarm" "odoo_poll_latency" {
  alarm_name          = "${local.dashboard_prefix}-odoo-poll-latency-p95"
  alarm_description   = "Triggers when Odoo poll latency p95 exceeds 1 second."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  threshold           = 1000
  treat_missing_data  = "notBreaching"
  alarm_actions       = var.alarm_actions
  ok_actions          = var.ok_actions
  tags                = var.tags

  metric_query {
    id          = "latency_p95"
    expression  = local.odoo_latency_p95
    label       = "Odoo poll latency p95"
    return_data = true
  }
}

resource "aws_cloudwatch_metric_alarm" "edge_agent_offline" {
  alarm_name          = "${local.dashboard_prefix}-edge-agent-offline"
  alarm_description   = "Triggers when any active Edge Agent is offline for more than 4 hours."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  threshold           = 4
  treat_missing_data  = "notBreaching"
  alarm_actions       = var.alarm_actions
  ok_actions          = var.ok_actions
  tags                = var.tags

  metric_query {
    id          = "offline_hours"
    expression  = local.offline_hours_max
    label       = "Max Edge Agent offline hours"
    return_data = true
  }
}

resource "aws_cloudwatch_metric_alarm" "buffer_depth_high" {
  alarm_name          = "${local.dashboard_prefix}-buffer-depth-high"
  alarm_description   = "Warning alarm when any Edge Agent buffer depth exceeds 1000 pending records."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  threshold           = 1000
  treat_missing_data  = "notBreaching"
  alarm_actions       = var.alarm_actions
  ok_actions          = var.ok_actions
  tags                = var.tags

  metric_query {
    id          = "buffer_depth"
    expression  = local.buffer_depth_max
    label       = "Max Edge Agent buffer depth"
    return_data = true
  }
}

resource "aws_cloudwatch_metric_alarm" "stale_transactions" {
  alarm_name          = "${local.dashboard_prefix}-stale-transaction-count"
  alarm_description   = "Triggers when stale pending transactions exceed 50."
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  threshold           = 50
  treat_missing_data  = "notBreaching"
  alarm_actions       = var.alarm_actions
  ok_actions          = var.ok_actions
  tags                = var.tags

  metric_query {
    id          = "stale_count"
    expression  = local.stale_count_max
    label       = "Stale transaction count"
    return_data = true
  }
}
