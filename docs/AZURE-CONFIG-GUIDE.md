# Azure Configuration Management

**Location**: `scripts/config-azure.ps1` (PowerShell)

This centralized configuration ensures consistency across all agent credential rotation scripts.

**DNS Alignment**: Configured to use `cowgomu.net` internal DNS zone, matching existing environment setup.

---

## Overview

The configuration system provides:
- Single source of truth for Azure subscription, tenant, and resource IDs
- Environment-specific agent configurations (dev, staging, prod)
- Certificate and API key generation defaults
- Audit logging and report paths
- Helper functions for common operations

---

## Configuration Files

### `scripts/config-azure.ps1` (Primary)
**Type**: PowerShell module  
**Usage**: Source in other scripts  
**Format**: Structured hashtables with helper functions  

**Contents**:
- `$AzureConfig` — Subscription and resource IDs
- `$EnvironmentConfig` — Dev, staging, prod agent configurations
- `$CertificateConfig` — Certificate generation defaults
- `$ApiKeyConfig` — API key generation settings
- `$AuditConfig` — Logging and report paths
- Helper functions (Get-*, Test-*, Ensure-*, Write-*)

---

## Using the Configuration

### In PowerShell Scripts

**Source the configuration**:
```powershell
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptPath 'config-azure.ps1')
```

**Access configuration values**:
```powershell
# Get Azure subscription details
$azureConfig = Get-AzureSubscriptionConfig
$subscriptionId = $azureConfig.SubscriptionId
$vaultName = $azureConfig.KeyVaultName

# Get environment-specific config
$envConfig = Get-EnvironmentConfig -Environment 'prod'
$agents = $envConfig.AgentIds
$approvalRequired = $envConfig.ApprovalRequired

# Get other configs
$certConfig = Get-CertificateConfig
$apiKeyConfig = Get-ApiKeyConfig
$auditConfig = Get-AuditConfig
```

**Use helper functions**:
```powershell
# Verify Azure connectivity
if (Test-AzureConnectivity) {
    Write-Host "Connected to Azure subscription"
}

# Ensure log directories exist
Ensure-AzureDirectories

# Write audit log entry
Write-AuditLog -Event 'KEY_GENERATED' -AgentId 'agent-001' -Details 'New key uploaded to vault'
```

---

## Configuration Values

### Azure Subscription

```powershell
$AzureConfig = @{
    SubscriptionId   = '<your-subscription-id>'    # az account show --query id -o tsv
    SubscriptionName = '<your-subscription-name>'
    TenantId         = '<your-tenant-id>'          # az account show --query tenantId -o tsv

    KeyVaultName     = 'homemanagement-vault'
    ResourceGroup    = 'homemanagement-dns'
    Location         = 'eastus'

    DnsZoneName      = '<your-dns-zone>'
}
```

**How to Find These Values**:
```bash
# Subscription ID and Tenant ID
az account show --query "{subscriptionId:id, tenantId:tenantId}" -o json

# KeyVault name
az keyvault list --query "[].name" -o tsv

# Resource group
az group list --query "[].name" -o tsv
```

### Environment Configuration

Each environment (dev, staging, prod) has:
- `AgentIds` — List of agent identifiers
- `ControlServer` — HTTP and HTTPS endpoints
- `CertificateValidity` — Certificate validity in days
- `KeyVaultPrefix` — Prefix for secret naming in vault
- `ApprovalRequired` — Whether manual approval is needed before deployment
- `RolloutStrategy` — 'all' (deploy all at once) or 'rolling' (1 per day)

**Example**:
```powershell
$EnvironmentConfig['prod'] = @{
    AgentIds              = @('agent-prod-01', 'agent-prod-02', 'agent-prod-03', 'agent-prod-04', 'agent-prod-05')
    ControlServer         = 'control-server.prod.cowgomu.net:9444'
    ControlServerTls      = 'control-server.prod.cowgomu.net:9445'
    CertificateValidity   = 365
    KeyVaultPrefix        = 'prod'
    ApprovalRequired      = $true
    RolloutStrategy       = 'rolling'
}
```

---

## Modifying Configuration

### Adding a New Environment

1. **Edit `scripts/config-azure.ps1`**:
```powershell
$EnvironmentConfig = @{
    # ... existing dev, staging, prod ...
    
    'qa' = @{
        AgentIds              = @('agent-qa-01', 'agent-qa-02')
        ControlServer         = 'control-server.qa.homemanagement.local:9444'
        ControlServerTls      = 'control-server.qa.homemanagement.local:9445'
        CertificateValidity   = 365
        KeyVaultPrefix        = 'qa'
        ApprovalRequired      = $true
        RolloutStrategy       = 'rolling'
    }
}
```

2. **Re-run scripts**— They now automatically use the new environment:
```powershell
.\scripts\Rotate-AgentCredentials.ps1 -Environment 'qa' -GenerateOnly
```

### Updating Azure Account Information

If you change Azure subscriptions or regions:

1. **Get new account details**:
```bash
az account show --query "{subscriptionId:id, tenantId:tenantId}" -o json
az keyvault list --query "[0].{name:name, location:location}" -o json
```

2. **Update `scripts/config-azure.ps1`**:
```powershell
$AzureConfig = @{
    SubscriptionId   = 'NEW_SUBSCRIPTION_ID'
    TenantId         = 'NEW_TENANT_ID'
    KeyVaultName     = 'NEW_VAULT_NAME'
    # ...
}
```

3. **Verify changes**:
```powershell
$config = Get-AzureSubscriptionConfig
$config | Format-Table -AutoSize
```

---

## Consistency Guarantees

By centralizing configuration:

✅ **All scripts use same subscription ID** — No mismatched environments  
✅ **Agent IDs always match** — Dev/staging/prod configs stay in sync  
✅ **Certificate paths consistent** — All tools use same storage locations  
✅ **Audit logs go to same place** — Easy to review rotation history  
✅ **Secrets stored in same vault** — No credential fragmentation  

---

## Validation

### Check Configuration Consistency

```powershell
# Load config
. .\scripts\config-azure.ps1

# Verify Azure connectivity
if (-not (Test-AzureConnectivity)) {
    Write-Error "Cannot connect to Azure with current configuration"
    exit 1
}

# Display all environments
$EnvironmentConfig.Keys | ForEach-Object {
    $env = Get-EnvironmentConfig -Environment $_
    Write-Host "Environment: $_"
    Write-Host "  Agents: $($env.AgentIds -join ', ')"
    Write-Host "  Approval: $($env.ApprovalRequired)"
}

# Verify directories exist
Ensure-AzureDirectories
Write-Host "✓ All directories validated"
```

### Quick Consistency Check

Run this before starting a rotation:
```powershell
cd f:\git\homeManagement
. .\scripts\config-azure.ps1

# Should output subscription/vault/resource group without errors
Get-AzureSubscriptionConfig | ConvertTo-Json
```

---

## Troubleshooting

### Configuration Not Loading

**Problem**: Scripts can't find `config-azure.ps1`

**Solution**:
```powershell
# Make sure you're in the git repo root
cd f:\git\homeManagement

# Run the script from the scripts directory
cd scripts
.\Rotate-AgentCredentials.ps1 -Environment dev -GenerateOnly
```

### Wrong Subscription

**Problem**: Script connects to wrong Azure subscription

**Solution**:
```powershell
# Check what's configured
. .\scripts\config-azure.ps1
$config = Get-AzureSubscriptionConfig
Write-Host "Configured subscription: $($config.SubscriptionId)"

# Check what you're logged into
az account show --query id -o tsv

# If different, update config-azure.ps1 with correct subscription ID
```

### Vault Not Found

**Problem**: Script can't access the configured KeyVault

**Solution**:
```powershell
# Check vault name in config
. .\scripts\config-azure.ps1
$config = Get-AzureSubscriptionConfig
Write-Host "Configured vault: $($config.KeyVaultName)"

# List available vaults
az keyvault list --query "[].name" -o tsv

# Update config if vault name is incorrect
```

---

## Related Documentation

- [security/security-report.md](security/security-report.md) — Security audit findings and remediation record
- [architecture/20-AI-CONFIGURATION-AND-SECRETS-STRATEGY.md](architecture/20-AI-CONFIGURATION-AND-SECRETS-STRATEGY.md) — Secrets management strategy

---

## Summary

| File | Purpose | Updates Needed |
|------|---------|-------------------|
| `scripts/config-azure.ps1` | Primary config, sourced by scripts | When Azure account changes, adding new environments |
| Scripts | All load `.ps1` and use helper functions | None (automatic) |

**Key Principle**: Update `scripts/config-azure.ps1` once, and all scripts automatically use the new values. ✅
