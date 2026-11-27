# PostgreSQL scheduling core

Run `InfrastructureServices.MigrateAsync` as the schema owner before starting runtime services. EF Core owns migration history; versioned SQL in the EF migrations defines the schema and the worker-facing contract. Requires PostgreSQL 15+ (`UNIQUE NULLS NOT DISTINCT`) with `btree_gist` available. The owner creates the extension if needed. Runtime uses parameterized Npgsql queries, not EF model-driven DDL.

`DockFlow:SeedDemo` defaults to false and is consulted only by the explicit migration command. With it enabled, the migration runner idempotently provisions these docks (reruns do not reopen blocked docks):

| Dock | Tenant | UUID |
| --- | --- | --- |
| G-01 | `11111111-1111-1111-1111-111111111111` | `11111111-1111-1111-1111-111111111101` |
| G-02 | `11111111-1111-1111-1111-111111111111` | `11111111-1111-1111-1111-111111111102` |
| R-01 | `11111111-1111-1111-1111-111111111111` | `11111111-1111-1111-1111-111111111103` |
| G-01 | `22222222-2222-2222-2222-222222222222` | `22222222-2222-2222-2222-222222222201` |

- Booking normalizes both endpoints down to the UTC minute and rejects an empty range. `visits_no_overlapping_reservations` enforces half-open `[start,end)` ranges for active reservations; the named `23P01` maps to `409 reservation_overlap`. General cargo prefers a general dock, then the least excess capacity; only refrigerated docks accept refrigerated cargo. All eligible candidates are locked in stable UUID order before automatic selection; the explicit path locks only the requested dock.
- `visits_one_physical_occupant` independently prevents a second `Assigned` or `InProgress` visit at the same dock, even after an appointment's end. `BlockAsync` prevents new reservations and assignments immediately, retains existing visits, and publishes `DockUnavailable` with the dock UUID. Reassignment is explicit and limited to `Scheduled`/`CheckedIn`; an already physically assigned vehicle can finish at its existing dock.
- A stale transition or reassignment returns `409 stale_version` without mutation. Booking keys are scoped to tenant, carrier and subject; matching retries return the original booking response, and a different normalized payload returns `409 idempotency_mismatch`. Operators see the whole tenant board; carriers see the shared tenant dock catalog but only their own visits and visit-linked incidents. Noncarrier actors require a provisioned tenant, and carrier actors also require provisioned membership.
- Lifecycle writes, chronological `public.audit_log` entries (ordered by `sequence` per aggregate), and `public.outbox` rows commit atomically. `public.inbox`, `public.incidents`, `public.visits`, and `public.docks` provide the worker's shared contract. Migration `20261127000200_ProtectAuditHistory` revokes UPDATE, DELETE and TRUNCATE on `public.audit_log` from `PUBLIC` and the pre-existing `dockflow` runtime role; keep that role distinct from the owner. Deployments that add role grants later must preserve this revoke. An explicit `Maximum Pool Size` overrides the default 32 per API instance.

Run the xUnit suite with Docker available: `dotnet test tests/DockFlow.CoreTests`. Each test provisions a disposable PostgreSQL 17 container and exercises the production EF migrations; no external database credentials are needed. Verified in the .NET 10 SDK container with locked restore: **8 passed, 0 failed, 0 skipped**.
