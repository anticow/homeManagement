[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$chart = Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy\helm\homemanagement'
$productionValues = Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy\argocd\values-production.yaml'

function Assert-Render {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Get-Manifest {
    param([string]$Rendered, [string]$Template)
    $pattern = '(?ms)^# Source: homemanagement/templates/' + [regex]::Escape($Template) +
        '\r?\n(.*?)(?=^---\s*$|\z)'
    return [regex]::Match($Rendered, $pattern).Groups[1].Value
}

$cases = @(
    @{ Name = 'production'; Overrides = @(); Health = $true; Tls = $true; Certificate = $true },
    @{ Name = 'health disabled'; Overrides = @('healthPage.enabled=false'); Health = $false; Tls = $true; Certificate = $true },
    @{ Name = 'TLS disabled'; Overrides = @('ingress.tls.enabled=false'); Health = $true; Tls = $false; Certificate = $false },
    @{ Name = 'ingress disabled'; Overrides = @('ingress.enabled=false'); Health = $false; Tls = $false; Certificate = $false },
    @{ Name = 'external certificate'; Overrides = @('ingress.clusterIssuer='); Health = $true; Tls = $true; Certificate = $false },
    @{
        Name = 'managed services'
        Overrides = @(
            'managedServices.enabled=true',
            'managedServices.tlsSecretName=control-tls',
            'managedServices.trustBundleConfigMap=control-trust',
            'managedServices.revocationExistingClaim=revocations'
        )
        Health = $true
        Tls = $true
        Certificate = $true
    }
)

foreach ($case in $cases) {
    $helmArgs = @(
        'template', 'homemanagement', $chart, '--values', $productionValues,
        '--set-string', 'database.connectionString=validation-only',
        '--set-string', 'auth.jwtSigningKey=validation-signing-key-that-is-long-enough-for-ci-checks-1234567890',
        '--set-string', 'agentGateway.apiKey=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8='
    )
    foreach ($override in $case.Overrides) {
        $helmArgs += @('--set', $override)
    }
    $rendered = & helm @helmArgs 2>&1 | Out-String
    Assert-Render ($LASTEXITCODE -eq 0) "Helm failed for $($case.Name): $rendered"
    $health = Get-Manifest $rendered 'health-ingress.yaml'
    $certificate = Get-Manifest $rendered 'certificate.yaml'
    Assert-Render ([bool]$health -eq $case.Health) "$($case.Name): health ingress presence"
    Assert-Render ([bool]$certificate -eq $case.Certificate) "$($case.Name): certificate presence"

    if ($case.Health) {
        $tls = [regex]::Match($health, '(?ms)^  tls:\r?\n(.*?)(?=^  \w|\z)').Groups[1].Value
        Assert-Render ([bool]$tls -eq $case.Tls) "$($case.Name): health TLS presence"
        if ($case.Tls) {
            Assert-Render ($tls -match '(?m)^        - "health\.cowgomu\.net"\r?$') "$($case.Name): health TLS hostname"
            Assert-Render ($tls -match '(?m)^      secretName: hm-wildcard-tls\r?$') "$($case.Name): shared TLS secret"
        }
        Assert-Render ($health.Contains('nginx.ingress.kubernetes.io/ssl-redirect: "false"')) "$($case.Name): HTTP compatibility"
        if ($case.Name -eq 'managed services') {
            Assert-Render ($health.Contains('nginx.ingress.kubernetes.io/proxy-ssl-verify: "on"')) 'Internal TLS verification'
            Assert-Render ($health.Contains('path: /platform-health')) 'Restricted managed health route'
        }
    }
    if ($case.Certificate) {
        Assert-Render ($certificate.Contains('name: letsencrypt-dns-sp-v2')) "$($case.Name): production issuer"
        Assert-Render ($certificate.Contains('secretName: hm-wildcard-tls')) "$($case.Name): certificate secret"
        foreach ($hostName in @('homemanagement', 'broker', 'authapi', 'agentgw', 'health')) {
            $expected = $hostName -ne 'health' -or $case.Health
            $pattern = '(?m)^    - "' + [regex]::Escape("$hostName.cowgomu.net") + '"\r?$'
            Assert-Render (($certificate -match $pattern) -eq $expected) "$($case.Name): $hostName certificate SAN"
        }
    }
    Write-Host "PASS: $($case.Name)"
}
