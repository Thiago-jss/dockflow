# DockFlow.Web

## Configuration

| Environment variable | Purpose |
| --- | --- |
| `ConnectionStrings__DockFlow` | PostgreSQL connection string. |
| `Identity__Authority` | Exact public realm issuer, e.g. `http://localhost:5192/realms/dockflow` in the lab. |
| `Identity__InternalAuthority` | Optional fixed backchannel realm URL, e.g. `http://keycloak:8080/realms/dockflow`. Defaults to the public issuer. |
| `Identity__ClientId` | Confidential browser client; defaults to `dockflow-web`. |
| `Identity__ClientSecret` | Required confidential browser client secret. |
| `Identity__AllowHttp` | Explicit isolated lab opt-in only. Browser ingress and cookies still require HTTPS. |
| `Ingress__TrustLabProxy` | Trust one forwarded protocol hop from the isolated lab network, only when `Identity__AllowHttp=true`. Never publish the web container port in this configuration. |
| `Ingress__KnownProxies__0` (etc.) | Trusted proxy IPs for forwarding the protocol without the broad lab opt-in. Loopback is trusted by default. |
| `ConnectionStrings__Redis` | Optional authenticated cache for the completed-today aggregate only. Missing or unavailable Redis does not block bookings. |
| `Telemetry__Enabled` | Opt in to OpenTelemetry framework metrics, cache-read metrics and application traces (default: off). |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Collector endpoint when telemetry is enabled (gRPC defaults to `http://localhost:4317`). Set `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` if the collector uses HTTP. |

Caddy must preserve the public Host and forward the scheme. Register the exact browser redirect `https://localhost:5191/signin-oidc`. Keycloak discovery must advertise the public realm endpoints and issuer (keep dynamic backchannel hostname disabled). Configure top-level multivalued `roles`, `sub` and `tenant_id` in the browser ID token; configure `roles`, `sub`, `tenant_id`, `carrier_id`, and the `dockflow-api` audience in access tokens for carriers. Staff roles: `GateOperator`, `DockOperator`, `LogisticsManager`, and `Administrator`. API access requires `Carrier` and a signed carrier GUID. Browser logout clears the local session only.

`dotnet DockFlow.Web.dll --migrate` runs database migration and exits (no identity settings needed). `/health/live` has no dependencies; `/health/ready` probes PostgreSQL only. The API uses a per-tenant/carrier token bucket (60/minute per web replica). Book with `POST /api/v1/appointments` and the `Idempotency-Key` header; cancel with `POST /api/v1/appointments/{id}/cancel` and JSON `{ "version": 1 }` using the current version. Dates in API booking bodies are ISO-8601 with offsets. Scheduling and board state always read PostgreSQL. Only the separate count of completed unloadings for the tenant and UTC date is cached for 15 seconds. Cache reads and writes have a short wait budget and fall back to PostgreSQL.

SignalR sends only an invalidation to the authenticated tenant group after a successful operation. The dashboard polls its own cookie-authorized snapshot every 15 seconds while visible so worker and other-replica commits appear without a backplane; it defers refresh while the user is interacting with a form. If deploying multiple web replicas, share ASP.NET Core Data Protection keys and use affinity for SignalR SSE (polling does not require affinity when keys are shared).

The bundled browser library is `@microsoft/signalr` 10.0.11 from npm, with only the source-map URL removed; its MIT license and third-party notices are alongside it. The vendored file SHA-256 is `2a38702050c0892ba2510f311810c363fb8ec9d85660d557f8836aa60aa6d5db`. There is no CDN or npm build dependency.
