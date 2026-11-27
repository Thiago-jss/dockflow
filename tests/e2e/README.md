# DockFlow local verification

Run from repository root after the isolated lab is ready: Caddy `https://localhost:5191`, Keycloak realm `http://localhost:5192/realms/dockflow`. The scripts do not provision the lab, delete rows, or send requests to deployed hosts. Do not enable secret or HTTP debugging. Export only the named credentials from the lab setup; never paste or print the values.

## API and contention

```sh
# Required environment variables: DOCKFLOW_CARRIER_A_SECRET, DOCKFLOW_CARRIER_B_SECRET,
# DOCKFLOW_CARRIER_C_SECRET.
python3 -B tests/e2e/api_lab.py --ca .local/tls.crt
# Isolated loopback lab alternative:
python3 -B tests/e2e/api_lab.py --insecure-lab
```

This must return health 200s and anonymous list 401, then submit 100 distinct keys simultaneously for seeded tenant-A general dock G-01 and one 30-minute window: 50 each from `carrier-a` and `carrier-b` so the two 60-request carrier buckets do not turn conflicts into 429s. Expect one 201 and 99 `reservation_overlap` 409s, replay of the winner with unchanged ID and one list entry, changed-input key reuse 409, an adjacent successful request, and refrigerated cargo rejected on G-01 with a successful refrigerated booking on R-01 as positive control. The other tenant-A carrier and tenant-B `carrier-c` must neither discover the winner by ID nor see it in their lists. The run prints the status histogram, duration and successful fixture ID/plate (never tokens/response bodies). Timeout exit 124 means the suite exceeded its 120-second hard wall limit.

Use `--anchor` with a **future unused UTC minute** in `YYYY-MM-DDTHH:MM:00Z` format (or `DOCKFLOW_E2E_ANCHOR`) for a deterministic window. Otherwise the run chooses a future randomized window and prints it before creating data. A run leaves its successful reservations behind. For repeats choose a fresh window and allow the preceding carrier rate buckets to refill (up to 60 seconds); do not run the k6 test simultaneously with the same principals. Do not purge the DB. This build expects the JSON cargo strings `"General"` and `"Refrigerated"`.

## Browser

Install Playwright Python and its Chromium in a local isolated test environment:

```sh
python3 -m venv tests/e2e/.venv
. tests/e2e/.venv/bin/activate
python -m pip install playwright
python -m playwright install chromium
# Required: DOCKFLOW_OPERATOR_PASSWORD exported by the local lab setup.
python -B tests/e2e/browser_lab.py --vehicle PLATE_FROM_API_RUN
```

The browser reaches a protected view, authenticates as `manager-a` through the actual Keycloak form, checks the returned board and optionally the carrier-created plate, confirms the view survives reload, then verifies local sign-out removes the app session cookie. This is not a Keycloak SSO logout test. A fresh Chromium context permits the lab cert only in that run; non-lab origins are blocked. The authenticated view is saved in ignored `tests/e2e/artifacts/`. Do not publish evidence without checking its contents. The harness never records the login form, Playwright traces, videos, HARs or storage state.
