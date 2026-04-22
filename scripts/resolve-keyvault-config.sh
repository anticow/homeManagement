#!/bin/bash

################################################################################
# resolve-keyvault-config.sh
# 
# Resolves hm-agent.json configuration template using Azure KeyVault secrets.
# Runs before agent startup to populate credentials from stored secrets.
#
# Environment Variables:
#   AZURE_VAULT_NAME: Azure KeyVault name (defaults to kv-antic0wa777429445286)
#   AGENT_ID: Agent identifier (required)
#   CONTROL_SERVER: Control server host:port (required)
#   CONFIG_TEMPLATE: Path to template file (default: /app/config/hm-agent.json.template)
#   KEYVAULT_OPTIONAL: Optional placeholder prefix resolved to empty string when missing
#   AZURE_CLIENT_ID: Azure Service Principal client ID (if not using Managed Identity)
#   AZURE_CLIENT_SECRET: Azure Service Principal client secret
#   AZURE_TENANT_ID: Azure tenant ID
#
# Usage:
#   ./resolve-keyvault-config.sh
#   AGENT_ID=agent-001 AZURE_VAULT_NAME=prod-vault ./resolve-keyvault-config.sh
#
################################################################################

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TIMESTAMP=$(date '+%Y-%m-%d %H:%M:%S')

# Color output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
GRAY='\033[0;90m'
NC='\033[0m' # No Color

log_info() {
    echo -e "${CYAN}[${TIMESTAMP}]${NC} $1"
}

log_success() {
    echo -e "${GREEN}[${TIMESTAMP}] ✓${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[${TIMESTAMP}] ⚠${NC} $1"
}

log_error() {
    echo -e "${RED}[${TIMESTAMP}] ✗${NC} $1" >&2
}

# ── Validate inputs ──
AZURE_VAULT_NAME="${AZURE_VAULT_NAME:-kv-antic0wa777429445286}"
AGENT_ID="${AGENT_ID:-}"
CONTROL_SERVER="${CONTROL_SERVER:-}"
CONFIG_TEMPLATE="${CONFIG_TEMPLATE:-/app/config/hm-agent.json.template}"
CONFIG_OUTPUT="${CONFIG_OUTPUT:-/app/hm-agent.json}"

if [ -z "$AGENT_ID" ]; then
    log_error "AGENT_ID environment variable is required"
    exit 1
fi

if [ -z "$CONTROL_SERVER" ]; then
    log_error "CONTROL_SERVER environment variable is required"
    exit 1
fi

log_info "Resolving KeyVault configuration..."
log_info "  Agent ID    : $AGENT_ID"
log_info "  ControlSrv  : $CONTROL_SERVER"
log_info "  KeyVault    : $AZURE_VAULT_NAME"
log_info "  Template    : $CONFIG_TEMPLATE"
log_info "  Output      : $CONFIG_OUTPUT"

# ── Check template exists ──
if [ ! -f "$CONFIG_TEMPLATE" ]; then
    log_error "Template file not found: $CONFIG_TEMPLATE"
    exit 1
fi

log_success "Template found"

# ── Authenticate to Azure (Managed Identity) ──
log_info "Authenticating to Azure..."

if [ -n "${AZURE_CLIENT_ID:-}" ] && [ -n "${AZURE_CLIENT_SECRET:-}" ] && [ -n "${AZURE_TENANT_ID:-}" ]; then
    log_info "Using Service Principal authentication"
    az login --service-principal \
        -u "$AZURE_CLIENT_ID" \
        -p "$AZURE_CLIENT_SECRET" \
        --tenant "$AZURE_TENANT_ID" > /dev/null 2>&1 || {
        log_error "Failed to authenticate with Azure using Service Principal credentials"
        exit 1
    }
elif az account show > /dev/null 2>&1; then
    log_info "Using existing Azure CLI authentication context"
else
    log_info "Attempting Managed Identity authentication"
    az login --identity > /dev/null 2>&1 || {
        log_error "Failed to authenticate with Azure (no CLI session and Managed Identity unavailable)"
        exit 1
    }
fi

log_success "Authenticated to Azure"

# ── Read template ──
log_info "Reading template..."
TEMPLATE_CONTENT=$(cat "$CONFIG_TEMPLATE")
TEMPLATE_CONTENT="${TEMPLATE_CONTENT//\{\{AGENT_ID\}\}/$AGENT_ID}"
TEMPLATE_CONTENT="${TEMPLATE_CONTENT//\{\{CONTROL_SERVER\}\}/$CONTROL_SERVER}"
log_success "Template loaded"

# ── Resolve placeholders ──
log_info "Resolving KeyVault placeholders..."

RESOLVED_CONTENT="$TEMPLATE_CONTENT"
PLACEHOLDER_COUNT=0

# Find all {{KEYVAULT:...}} patterns after token substitution so nested placeholders
# like {{KEYVAULT:agent-apikey-{{AGENT_ID}}}} resolve correctly.
while IFS= read -r line; do
    if [[ $line =~ \{\{KEYVAULT:(.*)\}\} ]]; then
        SECRET_NAME="${BASH_REMATCH[1]}"
        # Support ${AGENT_ID} substitution
        SECRET_NAME="${SECRET_NAME//\$\{AGENT_ID\}/$AGENT_ID}"
        
        log_info "Fetching secret: $SECRET_NAME"
        
        # Retrieve secret from KeyVault
        SECRET_VALUE=$(az keyvault secret show \
            --vault-name "$AZURE_VAULT_NAME" \
            --name "$SECRET_NAME" \
            --query 'value' \
            -o tsv 2>&1) || {
            log_error "Failed to retrieve secret '$SECRET_NAME' from KeyVault"
            exit 1
        }
        
        # Replace placeholder with secret value (escape special chars)
        RESOLVED_CONTENT="${RESOLVED_CONTENT//"{{KEYVAULT:$SECRET_NAME}}"/$SECRET_VALUE}"
        
        log_success "Resolved: $SECRET_NAME"
        ((PLACEHOLDER_COUNT++))
    fi
done < <(printf '%s\n' "$TEMPLATE_CONTENT" | grep -o '{{KEYVAULT:[^"]*}}' | sort -u || true)

while IFS= read -r line; do
    if [[ $line =~ \{\{KEYVAULT_OPTIONAL:(.*)\}\} ]]; then
        SECRET_NAME="${BASH_REMATCH[1]}"
        SECRET_NAME="${SECRET_NAME//\$\{AGENT_ID\}/$AGENT_ID}"

        log_info "Fetching optional secret: $SECRET_NAME"

        if SECRET_VALUE=$(az keyvault secret show \
            --vault-name "$AZURE_VAULT_NAME" \
            --name "$SECRET_NAME" \
            --query 'value' \
            -o tsv 2>/dev/null); then
            RESOLVED_CONTENT="${RESOLVED_CONTENT//"{{KEYVAULT_OPTIONAL:$SECRET_NAME}}"/$SECRET_VALUE}"
            log_success "Resolved optional: $SECRET_NAME"
        else
            RESOLVED_CONTENT="${RESOLVED_CONTENT//"{{KEYVAULT_OPTIONAL:$SECRET_NAME}}"/}"
            log_warn "Optional secret missing: $SECRET_NAME"
        fi

        ((PLACEHOLDER_COUNT++))
    fi
done < <(printf '%s\n' "$TEMPLATE_CONTENT" | grep -o '{{KEYVAULT_OPTIONAL:[^"]*}}' | sort -u || true)

log_info "Resolved $PLACEHOLDER_COUNT placeholder(s)"

# ── Validate JSON ──
log_info "Validating JSON configuration..."
if ! echo "$RESOLVED_CONTENT" | jq . > /dev/null 2>&1; then
    log_error "Resolved configuration is not valid JSON"
    exit 1
fi
log_success "JSON validation passed"

# ── Write resolved config with restricted permissions ──
log_info "Writing resolved configuration..."
mkdir -p "$(dirname "$CONFIG_OUTPUT")"
echo "$RESOLVED_CONTENT" > "$CONFIG_OUTPUT"
chmod 600 "$CONFIG_OUTPUT"
log_success "Configuration written to: $CONFIG_OUTPUT (permissions: 0600)"

# ── Summary ──
echo ""
log_success "Configuration resolved successfully"
echo ""
log_info "Agent is ready to start with resolved configuration."
