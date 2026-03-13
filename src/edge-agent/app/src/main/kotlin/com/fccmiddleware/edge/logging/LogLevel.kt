package com.fccmiddleware.edge.logging

/**
 * Log severity levels, parsed from [TelemetryDto.logLevel] config field.
 *
 * Ordered by severity: DEBUG < INFO < WARN < ERROR.
 * Messages below the configured level are dropped from file output.
 */
enum class LogLevel(val severity: Int) {
    DEBUG(0),
    INFO(1),
    WARN(2),
    ERROR(3);

    companion object {
        fun fromString(value: String): LogLevel =
            entries.firstOrNull { it.name.equals(value, ignoreCase = true) } ?: INFO
    }
}
