# App-only local benchmark

Export a fresh `carrier-a` or `carrier-b` Keycloak JWT as `DOCKFLOW_LOAD_BEARER` in the environment; do not put it in the command line, print it, or enable `--http-debug`. Obtain it before the run so Keycloak is not benchmarked. The script only reads `https://localhost:5191/api/v1/appointments` and does not follow redirects.

```sh
# Use only if the isolated lab's self-signed cert isn't in k6's trust store.
DOCKFLOW_LOAD_INSECURE_LAB=1 k6 run tests/load/appointments.js
```

Default: 1 request/s for 30s; let the selected carrier's 60-request bucket refill after prior tests. For an explicit isolated-lab rate-limit override, set `DOCKFLOW_LOAD_RPS` (integer 1–20); don't use higher rates against a default bucket and interpret policy-driven 429s as application capacity. The setup probe validates auth/TLS and list shape; its latency is excluded from request thresholds. k6 returns nonzero on failed preflight or thresholds.

Target thresholds (not measured results): successful-read p95 < 250 ms and p99 < 500 ms; failed HTTP and invalid list rates each < 1%; zero dropped iterations and all scheduled iterations complete. Compare runs only at comparable DB size/token scope/load. Record k6 version, configured RPS, iteration count, p95/p99, HTTP failures and drops. If failures rise, check the k6 status counts and app logs for `429` (policy), `401/403` (token or claims), and `5xx` (server/dependencies) before interpreting latency. Drops indicate load-generator/VU capacity or slow requests; investigate rather than silently reducing the requested rate.
