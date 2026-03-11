variable "environment" {
  description = "Deployment environment name."
  type        = string
}

variable "metric_namespace" {
  description = "CloudWatch custom metric namespace."
  type        = string
  default     = "FccMiddleware/CloudBackend"
}

variable "dashboard_name_prefix" {
  description = "Prefix for dashboard names."
  type        = string
  default     = "fccmiddleware"
}

variable "alarm_actions" {
  description = "SNS or PagerDuty integration ARNs for alarm notifications."
  type        = list(string)
  default     = []
}

variable "ok_actions" {
  description = "Notification targets for alarm recovery."
  type        = list(string)
  default     = []
}

variable "tags" {
  description = "Tags to apply to supported resources."
  type        = map(string)
  default     = {}
}
