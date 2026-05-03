#!/usr/bin/env bash
# deploy/docker/scripts/setup-dev-mssql-cert.sh
#
# One-time setup: generates a self-signed certificate for the SQL Server dev container,
# exports it to deploy/docker/certs/ so that application containers can trust it at build
# time, removing the need for TrustServerCertificate=True in connection strings.
#
# Prerequisites: Docker must be running and the sqlserver container must be up.
#
# Usage:
#   cd deploy/docker
#   docker compose up -d sqlserver
#   bash scripts/setup-dev-mssql-cert.sh
#   docker compose build   # rebuilds app images with the cert baked in
#   docker compose up -d
#
# After running this script, set HM_TRUST_SERVER_CERT=false in your .env file.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CERTS_DIR="$SCRIPT_DIR/../certs"
CONTAINER_NAME="${COMPOSE_PROJECT_NAME:-docker}-sqlserver-1"

echo "==> Waiting for SQL Server container to be healthy..."
for i in $(seq 1 30); do
  STATUS=$(docker inspect --format='{{.State.Health.Status}}' "$CONTAINER_NAME" 2>/dev/null || echo "not_found")
  if [ "$STATUS" = "healthy" ]; then
    echo "    SQL Server is healthy."
    break
  fi
  if [ "$i" -eq 30 ]; then
    echo "ERROR: SQL Server container '$CONTAINER_NAME' is not healthy after 30s."
    echo "       Run: docker compose up -d sqlserver"
    echo "       Or set COMPOSE_PROJECT_NAME if your project name differs."
    exit 1
  fi
  sleep 1
done

echo "==> Extracting SQL Server TLS certificate..."
mkdir -p "$CERTS_DIR"

# SQL Server on Linux stores its auto-generated cert at /var/opt/mssql/security/ca.crt.
# If that path is absent, fall back to grabbing it from the live TLS handshake.
docker exec "$CONTAINER_NAME" bash -c \
  'if [ -f /var/opt/mssql/security/ca.crt ]; then
     cat /var/opt/mssql/security/ca.crt
   else
     openssl s_client -connect localhost:1433 -starttls mssql </dev/null 2>/dev/null \
       | openssl x509
   fi' \
  > "$CERTS_DIR/mssql-dev.crt"

if [ ! -s "$CERTS_DIR/mssql-dev.crt" ]; then
  echo "ERROR: Could not extract SQL Server certificate."
  echo "       Place a PEM certificate manually at: $CERTS_DIR/mssql-dev.crt"
  exit 1
fi

echo "==> Certificate written to: $CERTS_DIR/mssql-dev.crt"
echo ""
echo "Next steps:"
echo "  1. Rebuild the app images (they COPY and trust the cert at build time):"
echo "       docker compose build broker auth agent-gw"
echo "  2. In .env, set: HM_TRUST_SERVER_CERT=false"
echo "  3. Restart services: docker compose up -d"
echo ""
echo "NOTE: certs/mssql-dev.crt is a public cert (not a secret)."
echo "      Commit it to version control once you verify the build succeeds."
