# DockFlow Worker

Independent .NET 10 Generic Host; no dependency on the web process. Apply the core PostgreSQL migration before starting the worker. The worker does not run migrations, change visit/dock state, serve HTTP, or contact external APIs. Incidents remain in `public.incidents` for the operator board.

## Configuration

| Configuration key | Environment variable | Value |
| --- | --- | --- |
| `ConnectionStrings:DockFlow` | `ConnectionStrings__DockFlow` | PostgreSQL connection string with explicit host, database, and username |
| `Messaging:Host` | `Messaging__Host` | RabbitMQ hostname or IP |
| `Messaging:User` | `Messaging__User` | RabbitMQ login |
| `Messaging:Password` | `Messaging__Password` | RabbitMQ password |
| `Messaging:Port` | `Messaging__Port` | Optional; defaults to `5672` |

Supply credentials at runtime. The broker connection is AMQP on vhost `/`; deploy it on a private network, not on the public Internet. The broker user must be allowed to declare, bind, publish and consume the named resources; PostgreSQL access is `SELECT` on `outbox`, `inbox`, `docks`, `visits`, and `incidents`, `INSERT` on `inbox` and `incidents`, and `UPDATE` on `outbox`. The supported broker baseline is RabbitMQ 4.3 with quorum queues and at-least-once dead-lettering enabled.

## Wire and delivery contract

- The durable topic exchange is `dockflow.events`. The relay publishes persistent `application/json` on `v1.event` (AMQP message ID is the event UUID); the worker's durable quorum queue `dockflow.incidents.v1` binds `v1.#`.
- The v1 JSON object contains `id`, `tenantId`, `kind`, `aggregateId`, `occurredAt`, `version`. IDs are nonempty UUIDs, `kind` is a nonblank case-sensitive string, `occurredAt` is an ISO-8601 timestamp with offset, and `version` is `1`. Bodies over 64 KiB, malformed/missing fields and unsupported versions received on the v1 route are poison. A well-formed v1 kind without an action is recorded in the inbox and acknowledged. Only trusted producers should have publish permission; the envelope is not an authentication credential.
- The relay only marks `public.outbox.published_at` after a tracked publisher confirmation for a mandatory publish. Returns, NACKs, timeouts, and broker outages leave the row eligible to retry. A crash between confirmation and the database update, or overlapping relay replicas, can publish the same event more than once. The independent worker loops reconnect with 1–15 second backoff.
- The consumer ACKs only after the inbox claim and any incident inserts commit as one PostgreSQL transaction. Transient PostgreSQL failures are delayed one second and rejected with requeue. The quorum queue enforces `x-delivery-limit=3` across sessions (unsuccessful deliveries from a connection loss may also count). Invalid messages and non-transient processing failures are rejected without requeue. The broker uses at-least-once dead-lettering to `dockflow.incidents.dead.v1` via `dockflow.incidents.dlx`; it retains the source copy until the dead-letter destination confirms. Monitor the DLQ; inspect and replay only after fixing the data/dependency, preserving the original event ID. Do not purge inbox rows while corresponding messages can still be replayed.
- `DockUnavailable` uses the dock UUID as its aggregate ID. Maintenance is recorded only if the dock still belongs to the event tenant and is nonoperational, for visits currently `Scheduled`, `CheckedIn`, `Assigned`, or `InProgress` at that dock. Overrun polling runs independently of RabbitMQ, using the database clock and the strict condition `status = 'InProgress' AND ends_at < statement_timestamp()`. Both types are written as `Open` and deduplicated by `(tenant_id, dock_id, visit_id, kind)` without overwriting an existing incident's status or detection time.

## Verification

From the repository root, `dotnet run --project tests/DockFlow.WorkerTests` checks the wire contract. `bash tests/DockFlow.WorkerTests/run-integration.sh` uses the .NET SDK Docker image, starts disposable PostgreSQL and RabbitMQ on random loopback ports, applies the core schema and checks relay/consumer/scanner behavior. It does not use the regular DockFlow stack.
