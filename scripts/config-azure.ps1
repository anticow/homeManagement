#Requires -Version 7.0

<#
        AgentIds              = @(
            'sidobits',
            'zombox',
            'pheonix',
            'zweihander',
            'dc2',
            'dc5',
            'dc6',
            'adfs1',
            'srv',
            'sql',
            'juggler',
            'monbox',
            'heathers-mini'
        )
    Centralized Azure configuration for agent credential rotation scripts.

.DESCRIPTION
    This file contains all Azure-specific settings, resource IDs, and environment mappings.
    Source this file in other scripts to ensure consistency.

    Usage in scripts:
    . (Join-Path $PSScriptRoot 'config-azure.ps1')

.NOTES
    Updated: 2026-04-05
    Consistency: All scripts source this file for Azure configuration
#>

# ══════════════════════════════════════════════════════════════════════════════
# AZURE SUBSCRIPTION CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════

$AzureConfig = @{
    # Azure subscription — populate from: az account show --query "{id:id,tenantId:tenantId}" -o json
    SubscriptionId   = $env:HM_AZURE_SUBSCRIPTION_ID ?? '<your-subscription-id>'
    SubscriptionName = $env:HM_AZURE_SUBSCRIPTION_NAME ?? '<your-subscription-name>'
    TenantId         = $env:HM_AZURE_TENANT_ID ?? '<your-tenant-id>'
    ExpectedUser     = $env:HM_AZURE_EXPECTED_USER ?? '<your-azure-user>'

    # Azure resources
    KeyVaultName     = $env:HM_AZURE_KEYVAULT_NAME ?? 'homemanagement-vault'
    ResourceGroup    = $env:HM_AZURE_RESOURCE_GROUP ?? 'homemanagement-rg'
    Location         = $env:HM_AZURE_LOCATION ?? 'eastus'

    # DNS configuration
    DnsZoneName      = $env:HM_DNS_ZONE ?? '<your-dns-zone>'
}

# ══════════════════════════════════════════════════════════════════════════════
# ENVIRONMENT CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════

$EnvironmentConfig = @{
    'dev' = @{
        AgentIds              = @('agent-dev-01', 'agent-dev-02')
        ControlServer         = 'localhost:9444'
        ControlServerTls      = 'agentgw.dev.cowgomu.net:443'
        CertificateValidity   = 365
        KeyVaultPrefix        = 'dev'
        ApprovalRequired      = $false
        RolloutStrategy       = 'all'  # all = deploy all at once, rolling = 1 per day
    }
    
    'staging' = @{
        AgentIds              = @('agent-stg-01', 'agent-stg-02', 'agent-stg-03')
        ControlServer         = 'agentgw.cowgomu.net:443'
        ControlServerTls      = 'agentgw.cowgomu.net:443'
        CertificateValidity   = 365
        KeyVaultPrefix        = 'staging'
        ApprovalRequired      = $true
        RolloutStrategy       = 'rolling'
    }
    
    'prod' = @{
        AgentIds              = @('agent-prod-01', 'agent-prod-02', 'agent-prod-03', 'agent-prod-04', 'agent-prod-05')
        ControlServer         = 'agentgw.cowgomu.net:443'
        ControlServerTls      = 'agentgw.cowgomu.net:443'
        CertificateValidity   = 365
        KeyVaultPrefix        = 'prod'
        ApprovalRequired      = $true
        RolloutStrategy       = 'rolling'
    }
}

# ══════════════════════════════════════════════════════════════════════════════
# CERTIFICATE CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════

$CertificateConfig = @{
    # Certificate defaults
    Subject              = 'CN=homemanagement-agent'
    KeyAlgorithm         = 'RSA'
    KeySize              = 2048
    SignatureAlgorithm   = 'SHA256'
    
    # Certificate paths
    CertPath             = Join-Path $PSScriptRoot '..\certs'
    CertificateBackup    = Join-Path $PSScriptRoot '..\certs\backup'
    
    # CA configuration
    CaSubject            = 'CN=HomeManagement-CA, O=HomeManagement, C=US'
    CaValidityYears      = 5
}

# ══════════════════════════════════════════════════════════════════════════════
# API KEY CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════

$ApiKeyConfig = @{
    # Key generation
    KeyLengthBytes       = 32  # 256-bit entropy
    EncodingFormat       = 'Base64'
    
    # Key storage
    KeyVaultSecretFormat = 'agent-apikey-{0}'  # {0} = AgentId
    CertPathSecretFormat = 'agent-cert-path-{0}'
    CaCertPathSecret     = 'agent-cacert-path'
}

# ══════════════════════════════════════════════════════════════════════════════
# AUDIT & LOGGING CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════════

$AuditConfig = @{
    # Audit logs
    AuditLogPath         = Join-Path $PSScriptRoot '..\logs\audit'
    ReportPath           = Join-Path $PSScriptRoot '..\logs\rotation-reports'
    
    # Report naming
    ReportNameFormat     = 'rotation-{0}-{1:yyyyMMdd-HHmmss}.json'  # {0}=environment, {1}=datetime
    
    # Log retention
    LogRetentionDays     = 90
}

# ══════════════════════════════════════════════════════════════════════════════
# HELPER FUNCTIONS
# ══════════════════════════════════════════════════════════════════════════════

function Get-AzureSubscriptionConfig {
    <#
    .SYNOPSIS
        Returns the Azure subscription configuration hash.
    #>
    return $AzureConfig
}

function Get-EnvironmentConfig {
    <#
    .SYNOPSIS
        Returns configuration for a specific environment.
    
    .PARAMETER Environment
        Environment name: 'dev', 'staging', or 'prod'
    #>
    param([ValidateSet('dev', 'staging', 'prod')][string]$Environment)
    
    if (-not $EnvironmentConfig.ContainsKey($Environment)) {
        throw "Unknown environment: $Environment"
    }
    
    return $EnvironmentConfig[$Environment]
}

function Get-CertificateConfig {
    <#
    .SYNOPSIS
        Returns the certificate configuration hash.
    #>
    return $CertificateConfig
}

function Get-ApiKeyConfig {
    <#
    .SYNOPSIS
        Returns the API key configuration hash.
    #>
    return $ApiKeyConfig
}

function Get-AuditConfig {
    <#
    .SYNOPSIS
        Returns the audit and logging configuration hash.
    #>
    return $AuditConfig
}

function Test-AzureConnectivity {
    <#
    .SYNOPSIS
        Verifies Azure subscription connectivity and permissions.
    
    .RETURNS
        $true if connected and authorized, $false otherwise.
    #>
    try {
        $context = Get-AzContext -ErrorAction SilentlyContinue
        if (-not $context) {
            Write-Host "Not connected to Azure. Attempting connection with Managed Identity..." -ForegroundColor Yellow
            Connect-AzAccount -Identity -SubscriptionId $AzureConfig.SubscriptionId -ErrorAction Stop | Out-Null
        }
        
        # Verify we're in the correct subscription
        $currentSub = (Get-AzContext).Subscription.Id
        if ($currentSub -ne $AzureConfig.SubscriptionId) {
            Write-Host "Current subscription ($currentSub) does not match configured subscription ($($AzureConfig.SubscriptionId))" -ForegroundColor Yellow
            Write-Host "Switching to configured subscription..." -ForegroundColor Gray
            Set-AzContext -SubscriptionId $AzureConfig.SubscriptionId | Out-Null
        }
        
        # Test KeyVault access
        $vault = Get-AzKeyVault -VaultName $AzureConfig.KeyVaultName -ErrorAction SilentlyContinue
        if (-not $vault) {
            Write-Error "Cannot access KeyVault: $($AzureConfig.KeyVaultName)"
            return $false
        }
        
        return $true
    }
    catch {
        Write-Error "Azure connectivity test failed: $_"
        return $false
    }
}

function Ensure-AzureDirectories {
    <#
    .SYNOPSIS
        Ensures all required local directories exist.
    #>
    $auditCfg = Get-AuditConfig
    $certCfg = Get-CertificateConfig
    
    @(
        $auditCfg.AuditLogPath,
        $auditCfg.ReportPath,
        $certCfg.CertPath,
        $certCfg.CertificateBackup
    ) | ForEach-Object {
        if (-not (Test-Path $_)) {
            Write-Host "Creating directory: $_" -ForegroundColor Gray
            New-Item -ItemType Directory -Path $_ -Force | Out-Null
        }
    }
}

function Write-AuditLog {
    <#
    .SYNOPSIS
        Writes an entry to the audit log.
    
    .PARAMETER Event
        Event type (e.g., 'KEY_GENERATED', 'KEY_UPLOADED', 'AGENT_ROTATED')
    
    .PARAMETER AgentId
        Agent identifier
    
    .PARAMETER Details
        Additional details (JSON-friendly)
    #>
    param(
        [string]$Event,
        [string]$AgentId,
        [string]$Details = ''
    )
    
    $auditCfg = Get-AuditConfig
    $timestamp = Get-Date -Format 'o'
    $logEntry = @{
        Timestamp    = $timestamp
        Event        = $Event
        AgentId      = $AgentId
        Details      = $Details
    } | ConvertTo-Json -Compress
    
    $logFile = Join-Path $auditCfg.AuditLogPath "audit-$(Get-Date -Format 'yyyyMMdd').log"
    Add-Content -Path $logFile -Value $logEntry -Encoding UTF8
}

# ══════════════════════════════════════════════════════════════════════════════
# INITIALIZATION
# ══════════════════════════════════════════════════════════════════════════════

# Ensure required directories exist
Ensure-AzureDirectories

Write-Host "✓ Azure configuration loaded" -ForegroundColor Green
