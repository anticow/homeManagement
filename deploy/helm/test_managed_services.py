"""Offline chart contracts; requires helm and PyYAML. Does not contact Kubernetes."""

from pathlib import Path
import subprocess
import unittest

import yaml


class UniqueLoader(yaml.SafeLoader):
    pass


def unique_mapping(loader, node, deep=False):
    result = {}
    for key, value in node.value:
        key = loader.construct_object(key, deep=deep)
        if key in result:
            raise ValueError(f"Duplicate YAML key: {key}")
        result[key] = loader.construct_object(value, deep=deep)
    return result


UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, unique_mapping)


class ManagedServicesChartTests(unittest.TestCase):
    chart = Path(__file__).parent / "homemanagement"
    settings = [
        "managedServices.enabled=true",
        "managedServices.tlsSecretName=hm-control-tls",
        "managedServices.trustBundleConfigMap=hm-control-trust",
        "managedServices.revocationExistingClaim=hm-revocations",
    ]

    def render(self, settings):
        args = [
            "helm", "template", "hm", str(self.chart),
            "--set", "database.connectionString=render-only",
            "--set", "auth.jwtSigningKey=render-only",
            "--set", "agentGateway.apiKey=render-only",
        ]
        for value in settings:
            args.extend(["--set", value])
        return subprocess.run(args, capture_output=True, text=True, check=False)

    def test_legacy_and_encrypted_deployments(self):
        for enabled in (False, True):
            with self.subTest(enabled=enabled):
                proc = self.render(self.settings if enabled else [])
                self.assertEqual(proc.returncode, 0, proc.stderr)
                docs = [d for d in yaml.load_all(proc.stdout, Loader=UniqueLoader) if d]
                deployments = [d for d in docs if d["kind"] == "Deployment"]
                self.assertEqual(len(deployments), 5)
                scheme = "https://" if enabled else "http://"
                for deployment in deployments:
                    pod = deployment["spec"]["template"]["spec"]
                    container = pod["containers"][0]
                    env = {e["name"]: e.get("value") for e in container["env"]}
                    urls = [
                        v for k, v in env.items()
                        if k == "ASPNETCORE_URLS"
                        or k in ("BrokerApi__BaseUrl", "AuthApi__BaseUrl", "AgentGateway__BaseUrl")
                        or k.startswith("ReverseProxy__") and k.endswith("__Address")
                        or k.startswith("Kestrel__Endpoints__") and k.endswith("__Url")
                    ]
                    self.assertTrue(urls, deployment["metadata"]["name"])
                    self.assertTrue(all(v.startswith(scheme) for v in urls), urls)
                    for probe in ("livenessProbe", "readinessProbe"):
                        self.assertEqual(container[probe]["httpGet"]["scheme"],
                                         "HTTPS" if enabled else "HTTP")
                    if enabled:
                        self.assertEqual(env["SSL_CERT_FILE"], "/control-trust/ca-bundle.pem")
                        self.assertEqual(env["Kestrel__Certificates__Default__KeyPath"],
                                         "/control-tls/tls.key")
                        self.assertGreaterEqual(len(container["volumeMounts"]), 2)
                    if deployment["metadata"]["name"] == "hm-web":
                        self.assertEqual(env["ManagedServices__Enabled"], str(enabled).lower())
                gateway = next(d for d in deployments if d["metadata"]["name"] == "hm-agent-gw")
                if enabled:
                    self.assertEqual(gateway["spec"]["replicas"], 1)
                    self.assertEqual(gateway["spec"]["strategy"]["type"], "Recreate")
                    volumes = gateway["spec"]["template"]["spec"]["volumes"]
                    self.assertTrue(any(v.get("persistentVolumeClaim", {}).get("claimName")
                                        == "hm-revocations" for v in volumes))
                    for ingress in (d for d in docs if d["kind"] == "Ingress"):
                        annotations = ingress["metadata"]["annotations"]
                        name = ingress["metadata"]["name"]
                        self.assertEqual(annotations["nginx.ingress.kubernetes.io/backend-protocol"],
                                         "GRPCS" if name == "hm-agent-gw-ingress" else "HTTPS")
                        self.assertEqual(annotations["nginx.ingress.kubernetes.io/proxy-ssl-verify"], "on")
                        if name == "hm-health-ingress":
                            paths = ingress["spec"]["rules"][0]["http"]["paths"]
                            self.assertEqual(paths[0]["path"], "/platform-health")
                else:
                    self.assertNotIn("strategy", gateway["spec"])
                    self.assertNotIn("volumes", gateway["spec"]["template"]["spec"])

    def test_unsafe_configurations_are_rejected(self):
        cases = [
            ("agentGateway.replicaCount=2", "single authoritative"),
            ("managedServices.revocationExistingClaim=", "revocationExistingClaim"),
            ("managedServices.tlsSecretName=", "tlsSecretName"),
            ("managedServices.trustBundleConfigMap=", "trustBundleConfigMap"),
            ("ingress.tls.enabled=false", "ingress TLS"),
            ("ingress.sslRedirect=false", "SSL redirect"),
            ("ingress.className=traefik", "nginx"),
        ]
        for override, message in cases:
            with self.subTest(override=override):
                proc = self.render(self.settings + [override])
                self.assertNotEqual(proc.returncode, 0)
                self.assertIn(message, proc.stderr)


if __name__ == "__main__":
    unittest.main()
