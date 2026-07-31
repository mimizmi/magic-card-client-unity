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

$StatusPath = Join-Path $ProjectRoot 'Temp\pipeline_test_status.json'

function Invoke-ConnectedUnityTestMode {
    param(
        [System.Management.Automation.CommandInfo]$UnityCli,
        [string]$Mode
    )

    # The previous run's result stays on disk and test_status keeps serving it, so
    # a stale "completed" from the mode that ran before this one is indistinguishable
    # from this one's. Removing the file first makes test_status answer "no_tests"
    # until the new run writes it, which is the whole of what makes the poll below
    # unambiguous. This is not hypothetical: a PlayMode request issued while
    # EditMode's result was still on disk read back EditMode's own 135 passes.
    Remove-Item -LiteralPath $StatusPath -Force -ErrorAction SilentlyContinue

    Write-Host "Running $Mode tests through the connected Unity editor..."

    # --async_tests because the synchronous path is bounded by the CLI's 30 second
    # pipeline ceiling, which neither the command's --timeout nor the global one
    # lifts, and the EditMode suite now runs for about 30 seconds. That cost is
    # editor ticks rather than computation: a test driving a real socket waits for
    # the editor loop to resume each continuation, roughly 150 ms per await in a
    # background editor, so a test making 130 sequential sends spends twenty
    # seconds waiting. Left synchronous this fails with "Pipeline command
    # 'run_tests' timed out after 30000ms", which reads like a hung test rather
    # than a runner ceiling and cost one investigation already. PlayMode has no
    # synchronous path at all - it returns an empty summary and starts nothing.
    $RunOutput = & $UnityCli.Source --json command --project-path $ProjectRoot `
        run_tests --mode $Mode --async_tests true
    if ($LASTEXITCODE -ne 0) {
        throw "Unity Pipeline run_tests ($Mode) exited with code $LASTEXITCODE."
    }

    $Run = ConvertFrom-NativeJson $RunOutput
    if (-not $Run.success -or -not $Run.data.success) {
        throw "Unity Pipeline run_tests ($Mode) failed: $($Run.errors -join '; ')"
    }

    $Deadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
    do {
        if ([DateTimeOffset]::UtcNow -ge $Deadline) {
            throw "Timed out waiting for Unity $Mode tests."
        }

        Start-Sleep -Milliseconds 500
        $StatusOutput = & $UnityCli.Source --json command --project-path $ProjectRoot test_status
        if ($LASTEXITCODE -ne 0) {
            throw "Unity Pipeline test_status ($Mode) exited with code $LASTEXITCODE."
        }

        $StatusEnvelope = ConvertFrom-NativeJson $StatusOutput
        if (-not $StatusEnvelope.success -or -not $StatusEnvelope.data.success) {
            throw "Unity Pipeline test_status ($Mode) failed: $($StatusEnvelope.errors -join '; ')"
        }
        $Status = $StatusEnvelope.data.result | ConvertFrom-Json
    } while ($Status.status -ne 'completed')

    Write-Host "$($Mode): $($Status.summary.passed)/$($Status.summary.total) passed."
    if ([int]$Status.summary.passed -ne [int]$Status.summary.total) {
        $Failures = @(
            $Status.results |
                Where-Object Status -eq 'Failed' |
                ForEach-Object { "$($_.FullName): $($_.Message)" }
        )
        throw @"
$Mode did not pass every test: passed=$($Status.summary.passed),
total=$($Status.summary.total), failed=$($Status.summary.failed),
skipped=$($Status.summary.skipped),
inconclusive=$($Status.summary.inconclusive).
Failures:
 - $($Failures -join "`n - ")
"@
    }

    return $Status.summary
}

function Invoke-ConnectedUnityTests {
    param([System.Management.Automation.CommandInfo]$UnityCli)

    # Sequential, not one combined run_tests call. The combined form runs EditMode
    # synchronously, which is what hit the ceiling described above, and both modes
    # share a single status file so they cannot be polled concurrently anyway.
    $EditMode = Invoke-ConnectedUnityTestMode $UnityCli 'EditMode'
    $PlayMode = Invoke-ConnectedUnityTestMode $UnityCli 'PlayMode'

    $Result = [ordered]@{
        runner = 'connected-editor'
        editMode = $EditMode
        playMode = $PlayMode
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
