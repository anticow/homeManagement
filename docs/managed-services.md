# AWX-enrolled managed services

HomeManagement provides status and Start/Stop for application backends wrapped by
Windows SCM/WinSW or Linux systemd. AWX owns packages, accounts, wrapper definitions
and enrollment. No browser-defined executables, command lines, PID killing,
installation, startup-mode changes or restart controls are exposed.

## Per-node enrollment (source-only integration)

The Gateway now has opt-in startup, offline initialization and internal enrollment
APIs. Broker/UI controls and Helm/AWX integration are not wired yet. This is not a
deployable enrollment rollout: existing deployments still use the static keys and
revocation path described below. Do not treat this checkpoint as permission to
initialize production state or change credentials.

The core keeps explicit AgentId, inventory hostname, expected wire hostname and
optional machine GUID bindings. IDs are bounded lowercase ASCII identifiers;
DNS names are case-normalized with an optional trailing dot, never domain-stripped.
A short wire hostname is not interchangeable with its inventory FQDN. Distinct
IDs can bind the same short wire hostname in different domains. Machine GUIDs and
inventory hostnames are unique; offline control must use the explicit binding.
Handshakes cannot create or rebind identities.

An administrator preassigns nodes. Pending records explicitly permit provisioning
and authenticated admission; observation does not implicitly enable them. Ensure
retries return the same key/version and cannot create or reinstate nodes. Rotation,
revocation and fresh-key re-enablement require the current record version.
Authentication-changing mutations advance the credential generation; rebind is
allowed only while revoked and remains revoked. Tombstones survive restart.
Registry mode never falls back to static credentials or a second revocation list.
The existing unversioned revoke method refuses registry-mode mutations.

Storage uses an authenticated versioned AES-256-GCM envelope, a separately mounted
canonical 32-byte wrapping key, and a lifetime lock on a separate stable file.
Explicit offline initialization requires an existing empty dedicated directory
and an approved assignment/rejection manifest. Normal loading requires both
encrypted state and its matching initialization marker; it never creates an
empty store when either is missing. Initialization restricts directory access to
the current account and SYSTEM on Windows, or owner-only on Unix. Provision and
protect the wrapping key separately; never place it in Git, ordinary configuration,
or the node registry. No developer 1Password session is a runtime dependency.

Commits write and flush a private same-directory temporary file, replace the
state, then publish the new in-memory snapshot. Storage failures fence all
authentication, control and sessions and report unhealthy through the core health
check; recovery requires restart from valid state. A crash after replacement but
before acknowledgement can leave the new generation committed: read metadata
before retrying a versioned action. Durability across sudden power loss depends
on the filesystem/PVC's rename and directory persistence semantics; file flushing
alone is not a universal directory-fsync guarantee.

Session publication and lifecycle commits share a lock. Heartbeats, responses,
queued sends and cleanup are fenced by session reference and credential generation;
transport aborts run outside that lock. An already delivered remote operation
cannot be undone by revocation. This is not hardware identity: a copied credential
can impersonate its assigned node until revoked.
Lifecycle notifications attempt every subscriber even when one fails, retaining
storage failures alongside notification errors. An old, already-fenced transport
abort failure is logged without failing ownership transfer of a new authenticated
session; other lifecycle abort failures remain explicit operation errors.

Future deployment must use a single writer/replica with Recreate, protected
durable storage and independent enrollment TLS gates. Back up the state, marker
and wrapping key consistently and separately protect the key backup. Wrapping-key
rotation is an offline maintenance operation, not a hot reload. Restoring an old
valid state/marker pair can resurrect old credentials or revocations: AES-GCM
does not provide external anti-rollback protection. Never automatically bootstrap
after loss or silently switch back to static authentication. Cutover requires an
approved manifest preserving existing rejections and a maintenance outage; no
mixed-mode per-node migration or claim of all-node coverage is provided here.

### Gateway startup and offline initialization

`AgentGateway:Enrollment:Enabled` defaults to false. When enabled, set `Directory`,
`WrappingKeyPath` and `ProvisioningKeyPath` beneath that configuration section.
Key settings are paths to separately protected mounted files, not inline secrets.
The wrapping, provisioning, control-plane and every node key must be strong,
canonical Base64-encoded random 32-byte values and distinct from one another.
Startup validates existing node/privileged-key separation; later assignments and
mutations enforce the same policy. The process reads mounted keys once; replacing
a key file is not a supported hot rotation operation.

An enabled Gateway loads the registry and its session coordinator before accepting
requests; no legacy key validator or revocation store is instantiated. `/readyz`
executes the tagged enrollment health check and becomes 503 after a latched storage
fault or disposal. `/healthz` remains process liveness, not permission to accept
agents. Other hosts retain their existing readiness behavior.

Offline initialization is a separate executable mode, before service construction:

```text
HomeManagement.AgentGateway.Host --initialize-agent-enrollment
  --directory <existing-empty-protected-directory>
  --wrapping-key-file <protected-key-file>
  --assignment-file <approved-manifest-file>
```

Pass these on one command line. Only file paths belong in arguments. Provision
the wrapping key separately using approved secret management. The bounded manifest
has `schemaVersion: 1` and an `assignments` array; each entry supplies `agentId`,
`inventoryHostname`, `expectedWireHostname`, optional `machineId`, and `revoked`
for previously rejected nodes. Explicitly review that rejection list before
initialization; no old keys are imported. Repeated, incomplete or mixed bootstrap/
service arguments fail; initialization never starts the service. Incomplete
initialization requires explicit operator recovery, never a service auto-retry.

### Internal HTTP boundaries

`POST /internal/agent-provisioning/ensure` requires HTTPS and the dedicated
`x-agent-provisioning-api-key` header. Its JSON body contains `agentId`,
`inventoryHostname` and `expectedWireHostname`. It returns metadata and that one
node's credential for authorized secure provisioning. It cannot create, rebind,
rotate or reinstate nodes. Interrupted provisioning can retrieve the same current
credential; possession of this key is therefore privileged impersonation authority.
Never expose this route through public ingress or permit HTTP body/header capture,
proxy response caching, audit payload capture or debug logging on it.

The control-plane key cannot call provisioning, and the provisioning key cannot
call control/admin routes. Duplicate headers, plaintext requests and forwarded
scheme claims are rejected. Enrollment responses, including errors, use
`Cache-Control: no-store`; request JSON is bounded before parsing. Errors contain
no exception payloads, keys or request bodies.

The existing control-plane key protects HTTPS metadata/lifecycle routes:

- `GET /internal/agents/capabilities` reports `enrollmentEnabled`.
- `GET /internal/agents/enrollments` lists metadata only.
- `GET /internal/agents/enrollments/machines/{machineId}` resolves an explicit binding.
- `POST /internal/agents/enrollments` administratively assigns an explicit record.
- `POST /internal/agents/enrollments/{agentId}/transition` requires
  `expectedVersion` and `transition` (0 rotate, 1 revoke, 2 fresh-key re-enable).
- `POST /internal/agents/enrollments/{agentId}/rebind` requires `expectedVersion`,
  both hostnames and an explicit nullable `machineId`; it remains revoked.

These are internal service-to-service administration APIs, not browser-authorized
routes. User authorization/auditing and machine-scoped controls remain the next
Broker integration gate. Legacy revoke requires an explicit confirmed version in
registry mode; legacy reinstate is refused. Disabled-server enrollment calls return
409, never static fallback. Clients must check authenticated capabilities, reject
mode mismatch and redirects, validate TLS trust and use bounded timeouts. A 409 or
uncertain/503 mutation result requires metadata refresh, not automatic replay:
notification failure can follow a committed state change.

Current HTTP tests use in-process requests with explicit schemes; they verify
runtime HTTP rejection, not real TLS certificate/SAN/CA trust. The offline CLI and
missing-state startup are also exercised as real child processes with synthetic
keys. Public/private listener isolation, mounted resources, end-to-end TLS trust,
Broker authorization and AWX delivery still block deployment.

## Enable explicitly

Set `ManagedServices:Enabled=true` in the **web** configuration. Default is off,
preserving existing web behavior. Configure HTTPS `BrokerApi:BaseUrl` and
`AuthApi:BaseUrl`; startup rejects plaintext URLs when enabled and the web rejects
plaintext console requests. This feature is fail-closed, not a TLS provisioning
mechanism.

When enabled, Web Auth/Broker/Admin HTTP clients do not follow redirects, avoiding
HTTPS-to-HTTP credential/body forwarding. The Gateway control client also refuses
automatic redirects.

Configure HTTPS `AgentGateway:BaseUrl` on the Broker. Provision TLS on the actual
Broker/Auth/Gateway backends, not only their public ingress. Agent sessions must
also be encrypted at the Gateway's request boundary. TLS termination followed by
plaintext backend traffic is not accepted for managed-service commands. Use TLS
passthrough or authenticated, certificate-validated backend TLS. Do not trust
arbitrary forwarded headers to manufacture `IsHttps`.

The Gateway requires `AgentGateway:RevocationListPath` on durable protected
storage. Use **one authoritative Gateway replica** with this file-backed store;
independent replicas are not a supported shared revocation implementation.
Corrupt state prevents startup; write failure fails the operation and blocks agent
authentication until storage is repaired and the process restarted.

Provision unique Base64-encoded random 32-byte per-agent API keys through the
existing Gateway configuration, matched to the enrolled node's short hostname/
AgentId. Managed-service routing deliberately uses this approved identity instead
of trusting another agent's self-reported hostname. Duplicate active identities
are rejected; shared or weak keys cannot authorize managed-service commands.
TLS authenticates the server and a scoped per-agent credential authenticates the
agent; existing client-certificate support remains optional.

On enrolled agents:

```json
{
  "Agent": {
    "UseTls": true,
    "ManagedServicesOnly": true,
    "ManagedServicesPath": "C:\\ProgramData\\HomeManagement\\Workloads\\managed-services.json"
  }
}
```

Use `/opt/homemanagement-workloads/managed-services.json` on Linux, or the actual
AWX path override. `ManagedServicesPath` must be absolute. Keep existing AgentId,
ControlServer, API key and certificate settings; this fragment is not a complete
agent configuration.

AWX supplies a protected, atomically replaced manifest:

```json
{
  "schemaVersion": 1,
  "services": [
    { "id": "comfyui", "displayName": "ComfyUI", "serviceName": "HMcomfyui" }
  ]
}
```

No configured path means no enrollment. A configured but missing, corrupt,
unsupported or duplicate manifest is an error, not an empty success response.
The agent reloads the manifest for each operation and serializes service operations.
Withdrawal takes effect on the next operation; it does not stop existing apps.
App controls do not inherit the Windows agent's child-process resource cap
because SCM starts the application service independently.

## Permissions, API and web

### Managed node enrollment (source opt-in; deployment integration pending)

Gateway registry enrollment is independent of application workloads. The Broker
client uses `AgentGateway:EnrollmentEnabled`; the web console uses
`AgentEnrollment:Enabled`. Neither enables `ManagedServices:Enabled`. When the
console enrollment flag is enabled, Broker/Auth URLs and browser requests must
use HTTPS and its clients refuse redirects. Keep normal certificate/CA validation.
These settings do not yet configure Helm TLS, storage, ingress or AWX delivery.
Do not activate this source checkpoint before its exact runtime-binding review
and deployment gates. Machine runtime now uses explicit Gateway-side bindings;
managed connection observations are deliberately not persisted into inventory.

Administrators use `/api/machines/{machineId}/agent-enrollment` (GET metadata),
`/assign`, `/transition` and `/rebind` (POST). All require authenticated
AdminSettings permission and HTTPS. Assignment requires an explicit agent ID,
inventory hostname matching the machine FQDN (or hostname when no FQDN exists),
and explicit wire hostname. The route GUID supplies the binding; body IDs/tags
cannot redirect the operation. Only Agent-transport machines are eligible,
including offline machines. No identity is guessed from hostname or heartbeat.
Transitions require the displayed `expectedVersion` and explicit transition:
0 rotate, 1 revoke, 2 re-enable. Rebind also requires that version and explicit
hostnames; it retains this machine GUID, works only while revoked, and remains
revoked. Moving an identity to another machine requires separately scoped
administration, not an implicit rebind through this machine route.

Gateway capabilities are authenticated before each lifecycle request. Client/server
mode mismatch, missing capabilities, redirects, absent bindings and stale versions
fail explicitly without legacy fallback. The client never substitutes the latest
version for the administrator's confirmation. Binding resolution and mutation
share a 30-second budget; responses are bounded to 16 KiB. No lifecycle mutation
is automatically retried, including after authentication, cancellation, conflict
or an uncertain response. Audit intent must persist before dispatch, followed by
success or failure/uncertain-outcome audit. Audit failure after commit cannot undo
the mutation.

The metadata-only node panel is shown independently of the workload panel, requires
explicit refresh and confirmation, freezes the version/assignment at confirmation
selection, and refreshes after uncertain results without retrying the mutation.
It does not expose provisioning credentials or grant application permissions.
Existing Blazor server events and antiforgery middleware remain in place.
Legacy break-peering is hidden with the enrollment console enabled; its Gateway
client refuses managed mode even when its own enrollment flag is off. Use the
versioned enrollment revoke route instead. Legacy control request binding is
unchanged; only the new Gateway enrollment APIs use the strict 8 KiB JSON reader.
The offline bootstrap CLI currently reports assignment count, not a digest.

### Machine-bound runtime and observations

With matching managed mode on Broker and Gateway, inventory-targeted commands,
ManagedServices commands, metadata/connection tests and update requests use the
machine GUID. Gateway captures the registry UUID, binding version, credential
generation and exact session reference under its authoritative gate. Queue
admission, the final gRPC sender and response completion reject invalidated
tokens; queued work never moves to a replacement session, even within the same
credential generation. Lifecycle changes fence the old session independently
of transport abort success. No lock is held over a network await. A write already
handed to the transport or remote operation already delivered cannot be recalled.

Internal routes are `/internal/agents/machines/{machineId}/metadata`,
`/observation`, `/commands` and `/updates`. They require managed enrollment,
HTTPS and control-plane authority, never the provisioning key. New command/update
bodies use the bounded 8 KiB reader. Runtime command responses are bounded to
4 MiB; small metadata/observation responses to 16 KiB. Metadata/update/observation
requests share a 30-second budget with mode verification; commands use their
command timeout plus 30 seconds. Updates acknowledge queue admission, not
successful installation. No runtime mutation is replayed automatically.
The production executor bypasses the generic retry/30-second-attempt pipeline
only for verified managed Agent commands. Their command-aware budget is retained;
uncertain failures are surfaced without resubmission or selection of a replacement
binding. Legacy Agent, SSH and WinRM resilience behavior is unchanged.

Command validation precedes pending-command registration and queue admission.
JSON requires `commandText` (empty is allowed) and `timeout`; explicit null text
or command type, invalid elevation and null environment values are rejected.
Omitted command type retains the `Shell` default. Supported timeout is one second
through 24 hours, rounded upward to whole seconds on the wire and in timer
budgets; zero, negative and unbounded sentinel timeouts are not supported.
Update requests require a nonempty version, an HTTP(S) download URL without
embedded credentials, and a non-null empty or 32-byte checksum. An empty checksum
retains existing compatibility behavior; validation does not claim artifact trust.
Invalid requests return an error without queued work or pending commands.

Clients verify authenticated capabilities before runtime routing and hosted
observation startup; client/server mismatch or unverifiable capabilities fails
explicitly. Even legacy mode now requires a Gateway providing the capabilities
endpoint; unsupported older servers do not receive a compatibility fallback.
Client authority selection is fixed for the process. Managed mode refuses legacy
ID/snapshot runtime APIs through the inventory client; explicitly ID-scoped
privileged Gateway APIs remain distinct. A changed server mode rejects subsequent
runtime requests and terminates the hosted observer at its next five-second
mode check. Capability failure during hosted startup/monitoring faults that
service (the host's default fatal-background-service policy stops the process).
Managed mode never starts legacy snapshots or subscribes to connection events.
It cannot create inventory machines, bind identities, change transport/protocol,
or mark inventory Online/Offline from a handshake or delayed disconnect.

Authenticated machine-read access can explicitly refresh
`GET /api/machines/{machineId}/agent-runtime/observation` over HTTPS. Deleted,
missing or non-Agent inventory records are rejected before Gateway dispatch.
The minimal result contains registry/machine/agent IDs, binding version,
credential generation, a current session ID when connected, and observation time.
A valid bound but disconnected machine can be reported disconnected. Unknown,
revoked or unavailable authority is an error, not a fabricated healthy/offline
result. Nothing in that result authorizes a command; every dispatch independently
revalidates its binding. The display is instantly stale by definition.

The web live-connection panel is separate from historical inventory state and
contact time. It clears old success on refresh/failure, fences out-of-order
responses, and invalidates in-flight work on machine navigation/disposal. It
does not poll or write inventory. Persisting managed observations would require
a separately designed ordering/conditional-application contract; no distributed
atomicity, event replay or linearizable inventory-status claim is made here.
Broker no-store handling currently covers entered handlers/filters, not every
authentication or model-binding rejection before them.

The machine detail page shows enrolled applications, current SCM/systemd state
and observation timestamp. Refresh is explicit, not background polling.
Viewer/Auditor roles can read; Operator/Admin roles can Start/Stop; only Admin
can break agent peering. The Broker enforces the same permissions and HTTPS:

- `GET /api/machines/{machineId}/managed-services`
- `POST /api/machines/{machineId}/managed-services/control`
  with `{"id":"comfyui","action":0}` for Start or `action:1` for Stop.
- `POST /api/machines/{machineId}/managed-services/break-peering`

Intent is audited before mutation and completion is audited afterwards. Failure
to record intent prevents dispatch. A lost connection or post-action observation/
audit failure cannot undo a completed SCM action; refresh before retrying.
Control confirms the desired service state within a bounded command timeout.
Service state is **not** HTTP readiness or GPU health. Linux failed units are
displayed as Failed and can be started again.

Stop requires explicit web confirmation and warns that active generation can be
interrupted. This initial on/off implementation does **not** drain ComfyUI queues,
perform application health probes or force-kill processes. OS wrapper shutdown
timeouts still apply. Check app workload activity before stopping.

Restricted enrolled agents reject legacy Shell, ServiceControl, patch operations
and remote update directives. Package/agent upgrades on those nodes belong to AWX.
Generic Broker agent-command endpoints reject ManagedServices to prevent bypassing
its authorized, audited machine endpoint. AWX enrollment templates are hidden and
cannot be launched via HomeManagement's generic AWX endpoint.

## Breaking and re-pairing

Admin Break peering persists revocation, aborts the active gRPC request, fails
pending operations and rejects reconnects. The agent creates fresh queues on each
connection, so buffered work is not replayed across a disconnect. No app is
automatically stopped. Already completed actions cannot be undone and cancellation
of in-progress OS operations is best effort.

A local administrator can create `<ManagedServicesPath>.disabled` to reject
commands immediately and disconnect within one second, or stop the agent.
Removing the local sentinel is an explicit local re-enable; it does not override
server revocation. AWX leaves the sentinel unchanged.

## Helm configuration

The Helm chart defaults `managedServices.enabled` to false. Opt-in switches all
five application listeners, internal clients, YARP routes and health probes to
HTTPS, verifies ingress backend certificates, uses GRPCS for the agent ingress,
and mounts a durable revocation claim with a Recreate/single-replica Gateway.
The unauthenticated HTTP health ingress is narrowed to `/platform-health`, not
arbitrary API paths.

Provision resources in the release namespace **before enabling**:

```yaml
managedServices:
  enabled: true
  tlsSecretName: hm-control-tls
  trustBundleConfigMap: hm-control-trust
  revocationExistingClaim: hm-agent-revocations
agentGateway:
  replicaCount: 1
```

The protected TLS Secret contains PEM `tls.crt`, `tls.key`, and `ca.crt` for
ingress backend verification. The server certificate needs Server Authentication
usage and DNS SANs `hm-control`, `hm-web`, `hm-broker`, `hm-auth`, `hm-agent-gw`
and `hm-gateway`. This initial chart shares one internal certificate between these
five trusted components; restrict Kubernetes Secret access accordingly. Public
ingress retains its separately configured public certificate. The trust ConfigMap
contains `ca-bundle.pem`: the internal issuing CA **plus the normal public root
bundle**, since `SSL_CERT_FILE` also affects outbound AWX/Action1 connections.
Do not use skip-certificate-validation overrides.

The existing durable claim must be writable by the Gateway container identity
and protected from other workloads. Do not use ephemeral/local temporary storage
or restore a pre-revocation backup as an ordinary repair. The chart does not mint
certificates, install a CA or create the claim. nginx ingress configuration is
required; custom annotation overrides must not duplicate/disable the enforced TLS
verification settings. Certificate and pod/runtime trust behavior require an
authorized end-to-end check before rollout.

Standalone manifests under `deploy/kubernetes` remain the legacy configuration;
they do not opt into these Helm settings. Apply the equivalent certificates,
HTTPS URLs/listeners and durable single-replica storage before enabling the feature
there.

Re-pairing is deliberately not a web button: provision a **new unique key** in AWX
and Gateway config, reload Gateway credentials, then remove the old identity's
revocation through its HTTPS internal administrator API. The legacy internal
reinstate endpoint does not enforce rotation itself, so administrator procedures
must not reinstate the old credential. Rotate first, then reinstate; an old token
must no longer reconnect. Protect access to the internal control-plane key/API.

## Rollout and validation

See the Ansible repository's `docs/homemanagement-workloads.md` for package contracts,
explicit host enrollment, account permissions and rollout ordering. Publish a
new agent release before applying manifest/restricted-mode configuration.
No machines, packages or live AWX jobs are automatically changed by these source
changes.

Targeted tests cover malformed/withdrawn enrollment, unsupported actions,
plaintext controls, permissions, local break, durable revocation and duplicate
identities. They use fake supervisors/TestServer; real service-account startup,
GPU access, protected storage and end-to-end TLS need a separately authorized
deployment trial.

Offline chart contracts (Helm and Python with PyYAML required):

```powershell
python -B deploy\helm\test_managed_services.py -v
```

These render both feature states and reject unsafe storage/TLS configurations;
the placeholder secrets are render-only and must never be deployed.
