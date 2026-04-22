[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'src'

function Get-RelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $relative = [System.IO.Path]::GetRelativePath($repoRoot, $Path)
    return $relative.Replace('\', '/')
}

$programFiles = Get-ChildItem -Path $sourceRoot -Recurse -Filter Program.cs -File
$violations = New-Object System.Collections.Generic.List[string]

foreach ($programFile in $programFiles) {
    $content = Get-Content -Path $programFile.FullName -Raw
    $relativePath = Get-RelativePath -Path $programFile.FullName

    if ($content -notmatch 'builder\.Host\.UseSerilog\(') {
        continue
    }

    if ($content -match 'SECURITY-GUARD:\s*logging-redaction-exempt') {
        continue
    }

    if ($content -notmatch '\.Enrich\.With<SensitivePropertyEnricher>\(\)') {
        $violations.Add("Missing SensitivePropertyEnricher in $relativePath")
    }
}

if ($violations.Count -gt 0) {
    Write-Host 'Host logging guard failures detected:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    throw 'Host logging guard checks failed.'
}

Write-Host 'Host logging guard checks passed.' -ForegroundColor Green