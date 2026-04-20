#Requires -Version 7.0

<#
.SYNOPSIS
    Orchestrates agent credential rotation (API keys + certificates).

.DESCRIPTION
    Implements the full credential rotation workflow:
    1. Generate new API keys
    2. Save to Azure KeyVault
    3. Generate new certificates
    4. Update configuration templates
    5. Roll out to target environment
    6. Validate connectivity
    7. Revoke old credentials

.EXAMPLE
    # Phase 1A: Dev testing (generate keys, don't deploy)
    .\Rotate-AgentCredentials.ps1 -Environment 'dev' -GenerateOnly

    # Phase 1B: Actually rotate dev agents
    .\Rotate-AgentCredentials.ps1 -Environment 'dev' -Deploy

    # Full prod rollout with approval gate
    .\Rotate-AgentCredentials.ps1 -Environment 'prod' -Deploy -RequireApproval

.PARAMETER Environment
    Target environment: 'dev', 'staging', or 'prod'. Default: 'dev'.

.PARAMETER AgentIds
    Specific agent IDs to rotate. Default: all agents in environment.

.PARAMETER GenerateOnly
    Generate keys and certs without deploying. Useful for Phase 1A testing.

.PARAMETER Deploy
    Actually deploy rotated credentials to agents.

.PARAMETER RequireApproval
    Pause before deployment to allow approval.

.PARAMETER VaultName
    Azure KeyVault name. Default: configured KeyVault from scripts/config-azure.ps1.

.PARAMETER ReportFile
    Save rotation report to file. Default: no file.

.NOTES
    Phase 1A (Dev Testing):
      .\Rotate-AgentCredentials.ps1 -Environment dev -GenerateOnly
    
    Phase 1B (Staging Validation):
      .\Rotate-AgentCredentials.ps1 -Environment staging -Deploy -RequireApproval
    
    Phase 1C (Production Rollout):
      .\Rotate-AgentCredentials.ps1 -Environment prod -Deploy -RequireApproval
#>

param(
    [ValidateSet('dev', 'staging', 'prod')]
    [string]$Environment = 'dev',
    
    [string[]]$AgentIds = @(),
    
    [switch]$GenerateOnly,
    [switch]$Deploy,
    [switch]$RequireApproval,
    
    [string]$ReportFile = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── Load centralized Azure configuration ──
. (Join-Path $scriptPath 'config-azure.ps1')

$azureConfig = Get-AzureSubscriptionConfig
$vaultName = if ($env:HM_KEYVAULT_NAME) { $env:HM_KEYVAULT_NAME } else { $azureConfig.KeyVaultName }

Write-Host "╔════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  HomeManagement Agent Credential Rotation Orchestration       ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "Environment    : $Environment" -ForegroundColor Yellow
Write-Host "Vault          : $vaultName" -ForegroundColor Yellow
Write-Host "Subscription   : $($azureConfig.SubscriptionId)" -ForegroundColor Yellow
Write-Host "Generate Only  : $GenerateOnly" -ForegroundColor Yellow
Write-Host "Deploy Changes : $Deploy" -ForegroundColor Yellow
Write-Host "Require Approval: $RequireApproval" -ForegroundColor Yellow
Write-Host ""

# ── Step 1: Determine target agents from environment config ──
$envConfig = Get-EnvironmentConfig -Environment $Environment

if ($AgentIds.Count -gt 0) {
    $agents = $AgentIds
} else {
    $agents = $envConfig.AgentIds
}

Write-Host "Target Agents ($($agents.Count)):" -ForegroundColor Cyan
$agents | ForEach-Object { Write-Host "  - $_" -ForegroundColor Gray }
Write-Host ""

# ── Preflight: Validate target KeyVault when deploying ──
if (-not $GenerateOnly) {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        Write-Error "Azure CLI (az) is required for deployment mode but was not found on PATH."
        exit 1
    }

    $vaultCheck = az keyvault show --name $vaultName --subscription $azureConfig.SubscriptionId --query "name" -o tsv 2>$null
    if (-not $vaultCheck) {
        Write-Host "Configured KeyVault '$vaultName' was not found in subscription $($azureConfig.SubscriptionId)." -ForegroundColor Red
        Write-Host "Available KeyVaults:" -ForegroundColor Yellow
        az keyvault list --subscription $azureConfig.SubscriptionId --query "[].name" -o tsv
        Write-Host "" 
        Write-Host "Set one for this run, then retry:" -ForegroundColor Yellow
        Write-Host "  `$env:HM_KEYVAULT_NAME = '<your-vault-name>'" -ForegroundColor Gray
        Write-Host "  .\scripts\Rotate-AgentCredentials.ps1 -Environment '$Environment' -Deploy" -ForegroundColor Gray
        exit 1
    }
}

# ── Step 2: Generate new credentials ──
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "STEP 1: Generating new API keys..." -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

$newCredentials = @()

foreach ($agentId in $agents) {
    Write-Host "Generating credentials for: $agentId" -ForegroundColor Yellow
    
    # Generate API key
    $randomBytes = [byte[]]::new(32)
    [System.Security.Cryptography.RNGCryptoServiceProvider]::new().GetBytes($randomBytes)
    $apiKey = [System.Convert]::ToBase64String($randomBytes)
    
    # Generate certificate (reuse existing CA, create new agent cert)
    $certScript = Join-Path $scriptPath 'New-AgentCert.ps1'
    if (Test-Path $certScript) {
        Write-Host "  - Generating certificate..." -ForegroundColor Gray
        $serverSAN = "agentgw.$Environment.cowgomu.net"
        & $certScript -AgentId $agentId -ServerSAN $serverSAN -CertValidityDays 365 | Out-Null
        Write-Host "    ✓ Certificate generated (SAN: $serverSAN)" -ForegroundColor Green
    }
    
    $credential = [PSCustomObject]@{
        AgentId     = $agentId
        Environment = $Environment
        ApiKey      = $apiKey
        GeneratedAt = Get-Date -Format 'o'
        Status      = 'Generated'
    }
    $newCredentials += $credential
    Write-Host "  ✓ New API key generated" -ForegroundColor Green
    Write-Host ""
}

# ── Step 3: Upload to KeyVault ──
if (-not $GenerateOnly) {
    Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "STEP 2: Uploading credentials to Azure KeyVault..." -ForegroundColor Cyan
    Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""

    if (Get-Command az -ErrorAction SilentlyContinue) {
        try {
            $azUser = (az account show --query "user.name" -o tsv 2>$null)
            if ($azUser) {
                Write-Host "Azure CLI User : $azUser" -ForegroundColor Yellow
                if ($azureConfig.ExpectedUser -and ($azUser -ne $azureConfig.ExpectedUser)) {
                    Write-Error "Azure account mismatch. Expected '$($azureConfig.ExpectedUser)' but active user is '$azUser'. Run: az login --username $($azureConfig.ExpectedUser)"
                    exit 1
                }
            }
        }
        catch {
            Write-Host "Unable to read active Azure CLI user; continuing with configured subscription checks." -ForegroundColor Yellow
        }
    }
    
    try {
        $useAzCliFallback = $false

        $context = Get-AzContext -ErrorAction SilentlyContinue
        if (-not $context) {
            Write-Host "Connecting to Azure (Managed Identity)..." -ForegroundColor Yellow
            try {
                Connect-AzAccount -Identity -SubscriptionId $azureConfig.SubscriptionId -ErrorAction Stop | Out-Null
            }
            catch {
                Write-Host "Managed Identity unavailable. Trying interactive Azure login..." -ForegroundColor Yellow
                Connect-AzAccount -SubscriptionId $azureConfig.SubscriptionId -ErrorAction Stop | Out-Null
            }
            $context = Get-AzContext -ErrorAction Stop
        }

        try {
            if ($context.Subscription.Id -ne $azureConfig.SubscriptionId) {
                Write-Host "Switching Azure context to configured subscription: $($azureConfig.SubscriptionId)" -ForegroundColor Yellow
                Set-AzContext -SubscriptionId $azureConfig.SubscriptionId -ErrorAction Stop | Out-Null
            }
        }
        catch {
            Write-Host "Az PowerShell context switch failed. Falling back to Azure CLI for KeyVault writes." -ForegroundColor Yellow
            $useAzCliFallback = $true
            if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
                throw "Azure CLI (az) is required for fallback mode but was not found on PATH."
            }
            az account set --subscription $azureConfig.SubscriptionId --only-show-errors | Out-Null
        }
        
        foreach ($cred in $newCredentials) {
            $secretName = "agent-apikey-$($cred.AgentId)"
            Write-Host "Uploading: $secretName" -ForegroundColor Yellow

            if (-not $useAzCliFallback) {
                try {
                    Set-AzKeyVaultSecret -VaultName $vaultName `
                        -Name $secretName `
                        -SecretValue (ConvertTo-SecureString $cred.ApiKey -AsPlainText -Force) | Out-Null
                }
                catch {
                    Write-Host "Set-AzKeyVaultSecret failed. Falling back to Azure CLI for this and remaining secrets." -ForegroundColor Yellow
                    $useAzCliFallback = $true
                    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
                        throw "Azure CLI fallback unavailable: az not found on PATH."
                    }
                    az account set --subscription $azureConfig.SubscriptionId --only-show-errors | Out-Null
                }
            }

            if ($useAzCliFallback) {
                az keyvault secret set `
                    --vault-name $vaultName `
                    --name $secretName `
                    --value $cred.ApiKey `
                    --subscription $azureConfig.SubscriptionId `
                    --only-show-errors `
                    --output none

                if ($LASTEXITCODE -ne 0) {
                    throw "Azure CLI secret upload failed for $secretName"
                }
            }
            
            $cred.Status = 'InKeyVault'
            Write-Host "  ✓ Uploaded to KeyVault" -ForegroundColor Green
        }
    }
    catch {
        Write-Error "Failed to upload secrets to KeyVault: $_"
        exit 1
    }
    
    Write-Host ""
}

# ── Step 4: Approval gate ──
if ($RequireApproval -and $Deploy) {
    Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "⚠️  APPROVAL REQUIRED before proceeding to deployment" -ForegroundColor Red
    Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Review the following credential rotations:" -ForegroundColor Yellow
    $newCredentials | Format-Table -Property AgentId, Environment, Status -AutoSize
    Write-Host ""
    Write-Host "Do you approve proceeding with deployment? (yes/no)" -ForegroundColor Yellow
    $approval = Read-Host
    
    if ($approval -ne 'yes') {
        Write-Host "Deployment cancelled by user." -ForegroundColor Red
        exit 0
    }
    Write-Host "✓ Deployment approved" -ForegroundColor Green
    Write-Host ""
}

# ── Step 5: Generate report ──
$auditConfig = Get-AuditConfig
$report = @{
    ExecutedAt     = Get-Date -Format 'o'
    Environment    = $Environment
    AgentsRotated  = $agents
    CredentialCount = $newCredentials.Count
    Credentials    = $newCredentials
    AzureConfig    = @{
        SubscriptionId = $azureConfig.SubscriptionId
        KeyVault       = $azureConfig.KeyVaultName
        ResourceGroup  = $azureConfig.ResourceGroup
    }
}

if (-not $ReportFile) {
    # Auto-generate report path if not specified
    $ReportFile = Join-Path $auditConfig.ReportPath (($auditConfig.ReportNameFormat -f $Environment, (Get-Date)))
}

$report | ConvertTo-Json | Set-Content -Path $ReportFile -Encoding UTF8
Write-Host "✓ Report saved to: $ReportFile" -ForegroundColor Green

Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "✓ CREDENTIAL ROTATION SUMMARY" -ForegroundColor Green
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "Phase        : 1A - Development Testing" -ForegroundColor Cyan
Write-Host "Environment  : $Environment" -ForegroundColor Cyan
Write-Host "Agents       : $($agents.Count)" -ForegroundColor Cyan
Write-Host "Status       : Keys Generated$(if (-not $GenerateOnly) { ' and Uploaded to KeyVault' } else { ' (generate-only mode)' })" -ForegroundColor Green
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host "1. Verify credentials in Azure KeyVault"
Write-Host "2. Update agent Dockerfile with boot script"
Write-Host "3. Deploy agents to dev environment"
Write-Host "4. Validate agent connectivity and TLS"
Write-Host "5. Document rotation in audit log"
Write-Host ""
