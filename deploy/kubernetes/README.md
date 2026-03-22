# Raw Kubernetes Manifests

Status: reference-only / development scaffolding.

These files are not the supported production deployment artifact.

Supported production path:

- use the Helm chart in `deploy/helm/homemanagement`
- validate chart rendering in CI before release
- keep environment-specific secrets and TLS settings in Helm values overrides, not in these raw manifests

Why this folder remains:

- quick local inspection of the original resource shapes
- reference when comparing generated or templated resources
- temporary migration aid while the Helm chart remains the only maintained deployment path

Constraints:

- do not treat these manifests as production-ready
- they may intentionally lag the Helm chart
- any runtime fix must land in the Helm chart first