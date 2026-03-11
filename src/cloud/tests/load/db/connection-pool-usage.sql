WITH settings AS (
    SELECT current_setting('max_connections')::int AS max_connections
),
connections AS (
    SELECT
        count(*) AS total_connections,
        count(*) FILTER (WHERE state = 'active') AS active_connections,
        count(*) FILTER (WHERE state = 'idle') AS idle_connections,
        count(*) FILTER (WHERE wait_event IS NOT NULL) AS waiting_connections
    FROM pg_stat_activity
    WHERE datname = current_database()
)
SELECT
    settings.max_connections,
    connections.total_connections,
    connections.active_connections,
    connections.idle_connections,
    connections.waiting_connections,
    round((connections.total_connections::numeric / settings.max_connections::numeric) * 100, 2) AS utilization_pct
FROM settings
CROSS JOIN connections;
