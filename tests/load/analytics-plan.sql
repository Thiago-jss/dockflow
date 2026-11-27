-- Synthetic, session-local comparison. Does not modify the application's schema.
BEGIN;
CREATE TEMP TABLE completed_scan(tenant_id uuid, occurred_at timestamptz, kind text);
INSERT INTO completed_scan
SELECT md5((i % 25)::text)::uuid, TIMESTAMPTZ '2026-01-01T00:00:00Z' + (i % (365*1440)) * interval '1 minute',
       CASE WHEN (i/25) % 8 = 0 THEN 'UnloadingCompleted' ELSE 'AppointmentScheduled' END
FROM generate_series(1, 250000) AS s(i);
ANALYZE completed_scan;
EXPLAIN (ANALYZE, BUFFERS) SELECT count(*) FROM completed_scan
WHERE tenant_id=md5('6')::uuid AND kind='UnloadingCompleted'
  AND occurred_at>=TIMESTAMPTZ '2026-03-01T00:00:00Z' AND occurred_at<TIMESTAMPTZ '2026-03-02T00:00:00Z';
CREATE INDEX completed_scan_by_day ON completed_scan(tenant_id,occurred_at) WHERE kind='UnloadingCompleted';
ANALYZE completed_scan;
EXPLAIN (ANALYZE, BUFFERS) SELECT count(*) FROM completed_scan
WHERE tenant_id=md5('6')::uuid AND kind='UnloadingCompleted'
  AND occurred_at>=TIMESTAMPTZ '2026-03-01T00:00:00Z' AND occurred_at<TIMESTAMPTZ '2026-03-02T00:00:00Z';
ROLLBACK;
