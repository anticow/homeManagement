# Fleet device-state projection

The HM Fleet (`/machines`) is a **database-only, last-known view**. Its initial
load, pagination and **Refresh stored fleet view** button call
`GET /api/device-state?page=1&pageSize=25`. This endpoint reads inventory and
`DeviceStates` only; it cannot invoke gateway, device or Prometheus validation.
It requires authentication and `machines:read`, returns `Cache-Control: no-store`,
and excludes soft-deleted machines. Opening the separate Action1 tab retains its
existing Action1 API behavior; HM Fleet does not preload that tab.

## Collection and configuration

The broker starts a background validation cycle on startup and schedules subsequent
cycles every 300 seconds. A single worker awaits each cycle before starting another
(no overlapping cycles in a broker process). Cycles have a 240-second cancellation
budget, gateway/metric operations have linked 30-second budgets, and up to eight
machines validate concurrently with separate persistence scopes. Inventory is read
in pages of 100, continuing beyond the initial page. Before validation, the broker
builds a GUID-deduplicated work list ordered by the oldest persisted validation
attempt (never-attempted machines first), with GUID as the stable tie-breaker.
The existing metrics-attempt timestamp is written for every machine before either
source is contacted, even when monitoring is disabled. This is a durable fair
queue: budget-expired/cancelled attempts move behind unvisited machines on the
next cycle, including after a broker restart, rather than restarting validation
at inventory page one. Each cycle re-enumerates inventory, so additions, renames
and soft deletions do not invalidate a saved page offset. Shutdown cancels in-flight work.
Scheduling is process-local, matching the existing single-broker deployment; it
does not provide a distributed collector lease for multiple broker replicas.
If a cycle cannot finish within its budget, unvisited machines retain their stored
evidence and receive priority in subsequent cycles; requests never compensate with live reads.

Broker configuration (defaults):

```json
{
  "DeviceState": {
    "ValidationIntervalSeconds": 300,
    "StaleAfterSeconds": 1500
  }
}
```

Environment equivalents are `DeviceState__ValidationIntervalSeconds` and
`DeviceState__StaleAfterSeconds`. The interval must be 1..86400 seconds; the stale
threshold must be at least the interval and no more than 604800 seconds. Existing
authentication, enrollment-mode and Prometheus configuration are unchanged.

## Evidence and failure semantics

- Inventory `Machine.State` remains historical and is **never written** by this
  collector. Non-agent transports display **Not applicable** for agent state.
- Each projection stores separate agent and metric validation attempts and successful
  validation timestamps, last-known connectivity, actual last received heartbeat,
  bound managed observation metadata, last-known metric values and sanitized source
  failures. Attempts are committed before network work.
- Successful managed observations retain machine GUID, registry, binding version,
  credential generation and session validation through `IMachineAgentRuntime`.
  HTTPS and authority/mode checks remain mandatory. No managed hostname lookup or
  legacy fallback is permitted.
- Legacy collection uses a new explicit fresh gateway read that throws on HTTP,
  malformed-response or authority failure. It never uses the existing gateway
  client's swallowed-error/cache fallback. Resolution is exact, case-insensitive
  **hostname first, then AgentId**; no prefixes or shortened names. Only absence
  from a successfully fetched fresh list can validate disconnection.
- Managed `LastHeartbeatUtc` is optional and is populated only after an actual
  heartbeat reaches the current bound session. Legacy snapshots add optional
  `LastReceivedHeartbeatUtc` for the same purpose. The historical legacy
  `LastHeartbeatUtc` field keeps its old connection-initialized semantics for
  existing consumers and is **not** used as an actual heartbeat by the projection.
  Older gateways lacking actual heartbeat evidence display unknown, not an
  invented timestamp. `ObservedAtUtc` is never treated as a heartbeat.
- Authoritative disconnected success becomes Offline while retaining the last
  known heartbeat. Validation failures retain prior successful connectivity,
  heartbeat and metrics; they never manufacture Offline. Failures are generic,
  not raw upstream exceptions, bodies or credentials.
- Freshness is computed server-side at every DB read using `TimeProvider` and the
  last successful source timestamp. At **age >= 1500 seconds**, agent state becomes
  Unknown/stale; last-known state remains separately visible. Never-validated
  machines are Unknown. No persistent "fresh forever" flag exists.
- Prometheus/exporter state is not HM agent connectivity. Missing `up`, disabled
  monitoring and health-provider errors are conservatively recorded as metric
  unavailability, retaining prior values independently of agent evidence.
  Health aggregation uses failure-aware Prometheus query results: a successful
  empty vector (for example an unsupported metric) is distinct from HTTP/API,
  parsing or timeout failure. If **any** queried metric fails, the projection keeps
  the entire last-good metrics snapshot, status and successful timestamp, and
  records a sanitized metrics error. It does not merge a successful CPU result
  with a failed memory query or downgrade a previous Critical snapshot to Healthy.
  Legitimately absent metrics need not all be non-null for validation to succeed.
  Metric freshness uses the provider's original `LastUpdatedUtc`, not retrieval
  time, so cached metrics do not gain a new successful timestamp.

Evidence is observational, **never command authorization**. Other live detail,
inventory and runtime endpoints retain their previous semantics. Existing
best-effort Prometheus query APIs still return empty results on upstream errors;
health summaries now explicitly flag failed component queries rather than
misclassifying partial failures as Healthy. Errors are visible before expiry,
but do **not** make retained evidence immediately stale: the exact >=1500-second
age rule remains unchanged.

## Persistence and rollout

`DeviceStates` is keyed by machine GUID with a foreign key and soft-delete query
filter. UTC scalar timestamps and nullable connectivity work with SQLite and SQL
Server; managed metadata and health payloads are stored as JSON text. Writes are
staged through `IDeviceStateRepository` and committed through `IUnitOfWork`.

The new **AddDeviceStateProjection** migration is required in both provider
migration assemblies:

- SQLite: `20261104160024_AddDeviceStateProjection`
- SQL Server: `20261104160043_AddDeviceStateProjection`

The existing broker startup migration path applies the configured provider's
migration before hosted services start. Existing machines initially have no
projection and therefore display Unknown until validated. No inventory state
backfill or enrollment/auth configuration change is needed.

## Regression checks

Focused tests cover durable storage across closed database contexts, migration
application and SQL Server script generation, exact freshness boundaries with a
controlled clock, failure preservation, authoritative disconnection, non-agent
and deleted machines, multi-page collection, cancellation, startup/periodic
non-overlapping validation, managed binding rejection, fresh-only legacy reads,
and DB-only authenticated endpoint/component refreshes. Deterministic
budget-cancelled cycles cover progress past 64 slow machines across inventory
pages and inventory changes. Tests through the real health service and
Prometheus HTTP adapter cover complete success followed by CPU success plus
memory HTTP 503/API/malformed-response failure, successful empty unsupported
metrics, disabled monitoring and missing exporters.

No live cluster verification is implied by these tests.
