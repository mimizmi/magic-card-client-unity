[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$UnityEditorPath,
    [bool]$PreferConnectedEditor = $true
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
} else {
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}

$ArtifactsDirectory = Join-Path $ProjectRoot 'Artifacts'
New-Item -ItemType Directory -Path $ArtifactsDirectory -Force | Out-Null

function ConvertFrom-NativeJson {
    param([string[]]$Lines)

    ($Lines -join "`n") | ConvertFrom-Json
}

function Invoke-ConnectedUnityTests {
    param([System.Management.Automation.CommandInfo]$UnityCli)

    Write-Host 'Running EditMode and PlayMode tests through the connected Unity editor...'
    $RunOutput = & $UnityCli.Source --json command --project-path $ProjectRoot run_tests
    if ($LASTEXITCODE -ne 0) {
        throw "Unity Pipeline run_tests exited with code $LASTEXITCODE."
    }

    $Run = ConvertFrom-NativeJson $RunOutput
    if (-not $Run.success -or -not $Run.data.success) {
        throw "Unity Pipeline run_tests failed: $($Run.errors -join '; ')"
    }

    $EditMode = $Run.data.result.Summary
    if ($null -eq $EditMode) {
        throw 'Unity Pipeline did not return an EditMode summary.'
    }
    Write-Host "EditMode: $($EditMode.Passed)/$($EditMode.Total) passed."
    if ([int]$EditMode.Passed -ne [int]$EditMode.Total) {
        $Failures = @(
            $Run.data.result.Results |
                Where-Object Status -eq 'Failed' |
                ForEach-Object { "$($_.FullName): $($_.Message)" }
        )
        throw @"
EditMode did not pass every test: passed=$($EditMode.Passed), total=$($EditMode.Total),
failed=$($EditMode.Failed), skipped=$($EditMode.Skipped),
inconclusive=$($EditMode.Inconclusive).
Failures:
 - $($Failures -join "`n - ")
"@
    }

    $Deadline = [DateTimeOffset]::UtcNow.AddMinutes(6)
    do {
        if ([DateTimeOffset]::UtcNow -ge $Deadline) {
            throw 'Timed out waiting for Unity PlayMode tests.'
        }

        Start-Sleep -Milliseconds 500
        $StatusOutput = & $UnityCli.Source --json command --project-path $ProjectRoot test_status
        if ($LASTEXITCODE -ne 0) {
            throw "Unity Pipeline test_status exited with code $LASTEXITCODE."
        }

        $StatusEnvelope = ConvertFrom-NativeJson $StatusOutput
        if (-not $StatusEnvelope.success -or -not $StatusEnvelope.data.success) {
            throw "Unity Pipeline test_status failed: $($StatusEnvelope.errors -join '; ')"
        }
        $PlayMode = $StatusEnvelope.data.result | ConvertFrom-Json
    } while ($PlayMode.status -ne 'completed')

    Write-Host "PlayMode: $($PlayMode.summary.passed)/$($PlayMode.summary.total) passed."
    if ([int]$PlayMode.summary.passed -ne [int]$PlayMode.summary.total) {
        $Failures = @(
            $PlayMode.results |
                Where-Object Status -eq 'Failed' |
                ForEach-Object { "$($_.FullName): $($_.Message)" }
        )
        throw @"
PlayMode did not pass every test: passed=$($PlayMode.summary.passed),
total=$($PlayMode.summary.total), failed=$($PlayMode.summary.failed),
skipped=$($PlayMode.summary.skipped),
inconclusive=$($PlayMode.summary.inconclusive).
Failures:
 - $($Failures -join "`n - ")
"@
    }

    $Result = [ordered]@{
        runner = 'connected-editor'
        editMode = $EditMode
        playMode = $PlayMode.summary
    }
    $Result | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $ArtifactsDirectory 'unity-test-summary.json') `
            -Encoding utf8
}

function Invoke-BatchModeUnityTests {
    if ([string]::IsNullOrWhiteSpace($UnityEditorPath)) {
        $UnityEditorPath = $env:UNITY_EDITOR_PATH
    }
    if ([string]::IsNullOrWhiteSpace($UnityEditorPath)) {
        $LocalEditor = 'E:\code\_Unity\editor\6000.2.7f2\Editor\Unity.exe'
        if (Test-Path -LiteralPath $LocalEditor -PathType Leaf) {
            $UnityEditorPath = $LocalEditor
        }
    }
    if ([string]::IsNullOrWhiteSpace($UnityEditorPath) -or
        -not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
        throw 'UnityEditorPath is required when no connected editor is available.'
    }

    foreach ($Mode in @('EditMode', 'PlayMode')) {
        $ResultPath = Join-Path $ArtifactsDirectory "$Mode-results.xml"
        $LogPath = Join-Path $ArtifactsDirectory "$Mode-unity.log"
        $Arguments = @(
            '-batchmode'
            '-nographics'
            '-quit'
            '-projectPath'
            $ProjectRoot
            '-runTests'
            '-testPlatform'
            $Mode
            '-testResults'
            $ResultPath
            '-logFile'
            $LogPath
        )

        Write-Host "Running $Mode tests with Unity batchmode..."
        $Process = Start-Process `
            -FilePath $UnityEditorPath `
            -ArgumentList $Arguments `
            -WindowStyle Hidden `
            -Wait `
            -PassThru
        if ($Process.ExitCode -ne 0) {
            throw "$Mode tests failed with exit code $($Process.ExitCode). See $LogPath."
        }
        if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
            throw "$Mode did not produce $ResultPath."
        }
    }
}

$Connected = $false
$UnityCli = Get-Command 'unity' -ErrorAction SilentlyContinue
if ($PreferConnectedEditor -and $null -ne $UnityCli) {
    try {
        $StatusOutput = & $UnityCli.Source status --project-path $ProjectRoot --json
        if ($LASTEXITCODE -eq 0) {
            $Status = ConvertFrom-NativeJson $StatusOutput
            $Connected = $Status.success -and
                @($Status.data.instances | Where-Object state -eq 'ready').Count -gt 0
        }
    } catch {
        Write-Verbose "Connected Unity probe failed: $($_.Exception.Message)"
    }
}

if ($Connected) {
    Invoke-ConnectedUnityTests $UnityCli
} else {
    Invoke-BatchModeUnityTests
}

Write-Host 'Unity test verification passed.'
