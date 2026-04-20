#Requires -Version 7.0

<#
.SYNOPSIS
    Generates new secure API keys for HomeManagement agents.

.DESCRIPTION
    Creates cryptographically secure API keys suitable for agent authentication.
    Keys are base64-encoded, 32+ bytes (256 bits) of random data.
    
    Output includes:
    - New API key in plaintext (display once, save to secure location)
    - SHA256 hash for comparison/audit trail
    - Recommended rotation schedule (90 days)

.EXAMPLE
    .\New-AgentApiKey.ps1
    .\New-AgentApiKey.ps1 -AgentId "agent-001" -OutputFile "api-keys.csv"
    .\New-AgentApiKey.ps1 -Count 5 -OutputFile "batch-keys.json"

.PARAMETER AgentId
    Optional agent identifier for labeling. Default: generated UUID.

.PARAMETER Count
    Number of API keys to generate. Default: 1.

.PARAMETER OutputFile
    Save keys to CSV or JSON file. Omit to display only (recommended for manual entry to vault).

.PARAMETER KeyLengthBytes
    Length of random key material in bytes. Default: 32 (256 bits = 2^256 entropy).

.NOTES
    SECURITY NOTE: Never commit API keys to version control.
    Store in Azure KeyVault or equivalent before sharing with agents.
#>

param(
    [string]$AgentId = [System.Guid]::NewGuid().ToString(),
    [int]$Count = 1,
    [string]$OutputFile = '',
    [int]$KeyLengthBytes = -1  # -1 means use config default
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Load centralized Azure configuration ──
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptPath 'config-azure.ps1')

$apiKeyConfig = Get-ApiKeyConfig

# Use config default if not specified
if ($KeyLengthBytes -eq -1) {
    $KeyLengthBytes = $apiKeyConfig.KeyLengthBytes
}

Write-Host "╔════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  HomeManagement Agent API Key Generator               ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "Configuration:" -ForegroundColor Cyan
Write-Host "  Key Length Format : $($apiKeyConfig.EncodingFormat)" -ForegroundColor Gray
Write-Host "  Key Length Bytes  : $KeyLengthBytes" -ForegroundColor Gray
Write-Host ""

# Validate parameters
if ($KeyLengthBytes -lt 32) {
    Write-Error "KeyLengthBytes must be >= 32. Specified: $KeyLengthBytes"
    exit 1
}

if ($Count -lt 1) {
    Write-Error "Count must be >= 1. Specified: $Count"
    exit 1
}

Write-Host "Generating $Count agent API key(s)..." -ForegroundColor Yellow
Write-Host "Agent ID    : $AgentId" -ForegroundColor Gray
Write-Host "Key Length  : $KeyLengthBytes bytes ($($KeyLengthBytes * 8) bits entropy)" -ForegroundColor Gray
Write-Host ""

$keys = @()
for ($i = 0; $i -lt $Count; $i++) {
    $randomBytes = [byte[]]::new($KeyLengthBytes)
    [System.Security.Cryptography.RNGCryptoServiceProvider]::new().GetBytes($randomBytes)
    
    $apiKey = [System.Convert]::ToBase64String($randomBytes)
    $keyHash = [System.Security.Cryptography.SHA256]::HashData($randomBytes) | 
        ForEach-Object { $_.ToString('x2') } | 
        Join-String
    
    $keyObject = [PSCustomObject]@{
        Index          = $i + 1
        AgentId        = if ($Count -eq 1) { $AgentId } else { "$AgentId-key-$($i+1)" }
        ApiKey         = $apiKey
        ApiKeyHash     = $keyHash
        Generated      = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        ExpiresAfter   = (Get-Date).AddDays(90).ToString('yyyy-MM-dd')
        KeyLengthBits  = $KeyLengthBytes * 8
    }
    
    $keys += $keyObject
    
    Write-Host "[$($i+1)/$Count] ✓ Generated API key for $($keyObject.AgentId)" -ForegroundColor Green
}

Write-Host ""
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "KEY GENERATION RESULTS" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Display keys with security warning
foreach ($key in $keys) {
    Write-Host "Agent ID     : $($key.AgentId)" -ForegroundColor Cyan
    Write-Host "Generated    : $($key.Generated)" -ForegroundColor Gray
    Write-Host "Expires      : $($key.ExpiresAfter)" -ForegroundColor Gray
    Write-Host "---" -ForegroundColor Gray
    Write-Host "API Key (BASE64):" -ForegroundColor Yellow
    Write-Host "  $($key.ApiKey)" -ForegroundColor White
    Write-Host "---" -ForegroundColor Gray
    Write-Host "SHA256 Hash (for audit):" -ForegroundColor Yellow
    Write-Host "  $($key.ApiKeyHash)" -ForegroundColor White
    Write-Host ""
}

# Option to save to file
if ($OutputFile) {
    if ($OutputFile -match '\.json$') {
        $keys | ConvertTo-Json | Set-Content -Path $OutputFile -Encoding UTF8
        Write-Host "✓ Keys saved to JSON: $OutputFile" -ForegroundColor Green
    } elseif ($OutputFile -match '\.csv$') {
        $keys | Export-Csv -Path $OutputFile -NoTypeInformation -Encoding UTF8
        Write-Host "✓ Keys saved to CSV: $OutputFile" -ForegroundColor Green
    } else {
        Write-Error "OutputFile must end in .json or .csv"
        exit 1
    }
    Write-Host "  WARNING: File contains plaintext API keys. Protect with file-level encryption." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "⚠️  SECURITY INSTRUCTIONS" -ForegroundColor Red
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "1. COPY the API key above to your secure location (Azure KeyVault, etc.)"
Write-Host "2. NEVER commit this key to version control"
Write-Host "3. Use the SHA256 hash to verify key identity in logs/audit trails"
Write-Host "4. ROTATE keys every 90 days or immediately if suspected compromise"
Write-Host "5. Delete this terminal history after securing all keys"
Write-Host ""
Write-Host "Next step: Upload keys to Azure KeyVault" -ForegroundColor Yellow
Write-Host "  Example: Set-AzKeyVaultSecret -VaultName 'kv-antic0wa777429445286' -Name 'agent-apikey-$AgentId' -SecretValue (ConvertTo-SecureString \$ApiKey -AsPlainText -Force)"
Write-Host ""
