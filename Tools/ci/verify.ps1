[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$GoServerRoot = 'E:\code\_github\magic-card-server-golang',
    [string]$UnityEditorPath
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
} else {
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}

& (Join-Path $PSScriptRoot 'restore-nuget.ps1') `
    -ProjectRoot $ProjectRoot `
    -CheckOnly
& (Join-Path $PSScriptRoot 'verify-architecture.ps1') `
    -ProjectRoot $ProjectRoot `
    -GoServerRoot $GoServerRoot
& (Join-Path $PSScriptRoot 'run-unity-tests.ps1') `
    -ProjectRoot $ProjectRoot `
    -UnityEditorPath $UnityEditorPath

if (-not (Test-Path -LiteralPath $GoServerRoot -PathType Container)) {
    throw "Go server root was not found: $GoServerRoot"
}

Write-Host "Running authoritative Go server baseline at $GoServerRoot..."
$PreviousTelemetry = $env:GOTELEMETRY
try {
    $env:GOTELEMETRY = 'off'
    Push-Location -LiteralPath $GoServerRoot
    try {
        & go test ./...
        if ($LASTEXITCODE -ne 0) {
            throw "go test ./... failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }
} finally {
    $env:GOTELEMETRY = $PreviousTelemetry
}

$ArtifactsDirectory = Join-Path $ProjectRoot 'Artifacts'
New-Item -ItemType Directory -Path $ArtifactsDirectory -Force | Out-Null
$Summary = @"
# Echo Harness Verification

- Timestamp (UTC): $([DateTimeOffset]::UtcNow.ToString('O'))
- Unity project: ``$ProjectRoot``
- Unity baseline: ``6000.2.7f2 (2b518236b676)``
- Architecture and package gates: passed
- NuGet restored assembly check: passed
- Unity EditMode and PlayMode tests: passed
- Go server ``go test ./...``: passed
- Scope: Harness only; no gameplay implementation
"@
$Summary | Set-Content -LiteralPath (
    Join-Path $ArtifactsDirectory 'verification-summary.md') -Encoding utf8

Write-Host 'Complete Harness verification passed.'
