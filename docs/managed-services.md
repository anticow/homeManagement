# AWX-enrolled managed services

HomeManagement provides status and Start/Stop for application backends wrapped by
Windows SCM/WinSW or Linux systemd. AWX owns packages, accounts, wrapper definitions
and enrollment. No browser-defined executables, command lines, PID killing,
installation, startup-mode changes or restart controls are exposed.

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
