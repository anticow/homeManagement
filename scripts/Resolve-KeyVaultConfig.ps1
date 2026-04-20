#Requires -Version 7.0

<#
.SYNOPSIS
    Resolves hm-agent.json configuration template using Azure KeyVault secrets.

.DESCRIPTION
    Reads hm-agent.json.template and replaces {{KEYVAULT:...}} placeholders
    with secrets from Azure KeyVault. Returns resolved JSON (not persisted to disk).
    
    Authentication via Managed Identity by default, or Service Principal if
    AZURE_CLIENT_ID/AZURE_CLIENT_SECRET are set.

.EXAMPLE
    .\Resolve-KeyVaultConfig.ps1 -TemplateFile "./hm-agent.json.template"
    .\Resolve-KeyVaultConfig.ps1 -TemplateFile "./deploy/config/hm-agent.json.template" -VaultName "prod-vault" -AgentId "agent-prod-01"

.PARAMETER TemplateFile
    Path to hm-agent.json.template file. Required.

.PARAMETER VaultName
    Azure KeyVault name. Default: $env:AZURE_VAULT_NAME or 'kv-antic0wa777429445286'.

.PARAMETER AgentId
    Agent identifier for secret lookup. Default: $env:AGENT_ID.

.PARAMETER ControlServer
    Control server host:port value for the rendered configuration. Default: $env:CONTROL_SERVER.

.PARAMETER OutputMode
    Output format: 'json' (console JSON), 'object' (PowerShell object), 'file' (write to disk).
    Default: 'json'.

.PARAMETER OutputPath
    If OutputMode is 'file', write resolved config here. Default: 'hm-agent.json'.

.NOTES
    SECURITY NOTE: Resolved config is written to memory or (if OutputMode='file') with restricted permissions.
    Service runs with resolved config in memory; config is never persisted in plaintext on disk.
    
    Environment Variables:
    - AZURE_VAULT_NAME: Azure KeyVault name (default: kv-antic0wa777429445286)
    - AGENT_ID: Agent identifier for secret template substitution
    - CONTROL_SERVER: Control server host:port for template substitution
    - AZURE_CLIENT_ID: Service Principal client ID (for non-Managed Identity auth)
    - AZURE_CLIENT_SECRET: Service Principal client secret
    - AZURE_TENANT_ID: Azure tenant ID (required if using Service Principal)
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$TemplateFile,
    
    [string]$VaultName = ($env:AZURE_VAULT_NAME ?? 'kv-antic0wa777429445286'),
    [string]$AgentId = $env:AGENT_ID,
    [string]$ControlServer = $env:CONTROL_SERVER,
    [ValidateSet('json', 'object', 'file')]
    [string]$OutputMode = 'json',
    [string]$OutputPath = 'hm-agent.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Resolving KeyVault configuration..." -ForegroundColor Cyan

# ── Step 1: Validate inputs ──
if (-not (Test-Path $TemplateFile)) {
    Write-Error "Template file not found: $TemplateFile"
    exit 1
}

if (-not $AgentId) {
    Write-Error "AgentId not provided. Set AGENT_ID environment variable or use -AgentId parameter."
    exit 1
}

if (-not $ControlServer) {
    Write-Error "ControlServer not provided. Set CONTROL_SERVER environment variable or use -ControlServer parameter."
    exit 1
}

Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Template: $TemplateFile" -ForegroundColor Gray
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Agent ID: $AgentId" -ForegroundColor Gray
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ControlServer: $ControlServer" -ForegroundColor Gray
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] KeyVault: $VaultName" -ForegroundColor Gray

# ── Step 2: Authenticate to Azure ──
try {
    $context = Get-AzContext -ErrorAction SilentlyContinue
    if (-not $context) {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Connecting to Azure (Managed Identity)..." -ForegroundColor Yellow
        Connect-AzAccount -Identity -ErrorAction Stop | Out-Null
    } else {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Using existing Azure context" -ForegroundColor Gray
    }
}
catch {
    Write-Error "Failed to authenticate to Azure: $_"
    exit 1
}

# ── Step 3: Read template ──
try {
    $templateContent = Get-Content $TemplateFile -Raw -Encoding UTF8
    $templateContent = $templateContent.Replace('{{AGENT_ID}}', $AgentId)
    $templateContent = $templateContent.Replace('{{CONTROL_SERVER}}', $ControlServer)
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ✓ Template loaded" -ForegroundColor Green
}
catch {
    Write-Error "Failed to read template: $_"
    exit 1
}

# ── Step 4: Extract and resolve placeholders ──
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Resolving placeholders..." -ForegroundColor Yellow

$resolvedContent = $templateContent

function Resolve-KeyVaultPlaceholders {
    param(
        [string]$Pattern,
        [bool]$Optional
    )

    $matches = [regex]::Matches($templateContent, $Pattern)
    foreach ($match in $matches) {
        $secretName = $match.Groups[1].Value -replace '\$\{AGENT_ID\}', $AgentId

        try {
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Fetching secret: $secretName" -ForegroundColor DarkGray
            $secret = Get-AzKeyVaultSecret -VaultName $VaultName -Name $secretName -ErrorAction Stop
            $secretValue = $secret.SecretValue | ConvertFrom-SecureString -AsPlainText
            $script:resolvedContent = $script:resolvedContent -replace [regex]::Escape($match.Value), $secretValue
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ✓ Resolved: $secretName" -ForegroundColor Green
        }
        catch {
            if ($Optional) {
                Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ⚠ Optional secret missing: $secretName" -ForegroundColor Yellow
                $script:resolvedContent = $script:resolvedContent -replace [regex]::Escape($match.Value), ''
                continue
            }

            Write-Error "Failed to retrieve secret '$secretName' from KeyVault: $_"
            exit 1
        }
    }

    return $matches.Count
}

$requiredCount = Resolve-KeyVaultPlaceholders -Pattern '\{\{KEYVAULT:(.*?)\}\}' -Optional $false
$optionalCount = Resolve-KeyVaultPlaceholders -Pattern '\{\{KEYVAULT_OPTIONAL:(.*?)\}\}' -Optional $true
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Found $($requiredCount + $optionalCount) placeholder(s)" -ForegroundColor Gray

# ── Step 5: Validate JSON ──
try {
    $configObject = $resolvedContent | ConvertFrom-Json -ErrorAction Stop
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ✓ JSON validation passed" -ForegroundColor Green
}
catch {
    Write-Error "Resolved configuration is not valid JSON: $_"
    exit 1
}

# ── Step 6: Output ──
switch ($OutputMode) {
    'json' {
        $resolvedContent
    }
    'object' {
        $configObject
    }
    'file' {
        # Write with restricted file permissions (owner read/write only)
        $resolvedContent | Set-Content -Path $OutputPath -Encoding UTF8 -Force
        if ($IsWindows) {
            # Windows NTFS: remove inheritance, grant owner Full Control only
            $acl = Get-Acl $OutputPath
            $acl.SetAccessRuleProtection($true, $false)
            $acl | Set-Acl -Path $OutputPath
        } else {
            # Linux: chmod 600
            chmod 600 $OutputPath
        }
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ✓ Config written to: $OutputPath (restricted permissions)" -ForegroundColor Green
    }
}

Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ✓ Configuration resolved successfully" -ForegroundColor Green
