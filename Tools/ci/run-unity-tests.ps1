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

# The one test class permitted to skip itself, and the only reason a skip is
# tolerated below. It talks to the authoritative Go server over a real socket, and
# the endpoint is supplied through ECHO_SERVER_HOST rather than committed, because
# it is a developer address that does not belong in the repository. The cost is
# stated plainly in docs/verification-matrix.md: on a machine without that
# variable the suite goes green having never run the one test that can disagree
# with our own reading of the protocol.
$SanctionedSkipClass = 'Echo.Harness.Tests.EditMode.GoServerEndToEndTests'

# Every check that decides whether a run passed, in one place, so that both
# runners are graded identically.
#
# They were not. The connected path carried all of this inline while the batch
# path - the path CI is configured to take - verified nothing but the process exit
# code and the existence of the results file. So on that path there was no
# empty-run guard, no accounting, no skip count, and above all no sanctioned-skip
# check, which is the whole of the invariant that a test quietly ceasing to run is
# not a pass. A machine with no ECHO_SERVER_HOST is also permanently in the skip
# state, which is precisely the state that check exists to police.
#
# "Configured to take" was long meant literally, and it is not the same as "runs":
# the unity-tests job needs a self-hosted runner, and for most of this repository's
# history none was registered, so this batch path never executed in CI once. A
# runner carrying the required labels is registered now, and the job no longer sits
# behind `workflow_dispatch`. docs/verification-matrix.md carries both the history
# and the current arrangement under "CI boundary".
#
# $Summary must expose total/passed/failed/skipped/inconclusive; $Results must be
# one row per test with FullName, Status and Message. Both runners are adapted to
# that shape before they get here rather than this function learning two.
function Assert-UnityTestRunPassed {
    param(
        [string]$Mode,
        $Summary,
        $Results
    )

    if ($null -eq $Summary) {
        throw "Unity $Mode produced no summary at all, which is not a pass."
    }

    # A run that found nothing is a failure, not a pass. When the test list comes
    # back empty - an assembly that failed to compile, or a filter that matches
    # nothing - the runner writes "completed" with every counter at zero, and a
    # bare passed-ne-total check reads 0 -ne 0 as success and turns the whole gate
    # green. The previous shape of this script caught that with a null check on
    # the summary; this replaces it with one that also catches an empty run.
    if ([int]$Summary.total -le 0) {
        throw "Unity $Mode reported no tests at all. An assembly that fails to " +
              "compile reaches here as an empty run, which is not a pass."
    }

    $Passed = [int]$Summary.passed
    $Total = [int]$Summary.total
    $Failed = [int]$Summary.failed
    $Skipped = [int]$Summary.skipped
    $Inconclusive = [int]$Summary.inconclusive
    $Rows = @($Results)

    # The skip count is printed whenever it is non-zero, so a tier that quietly
    # stopped running can never be invisible in a green log. A bare "149/152
    # passed." reads like a partial failure; "149/152 passed, 3 skipped." says
    # what actually happened.
    $Line = "$($Mode): $Passed/$Total passed"
    if ($Skipped -gt 0) {
        $Line += ", $Skipped skipped"
    }
    Write-Host "$Line."

    if ($Failed -gt 0 -or $Inconclusive -gt 0) {
        $Failures = @(
            $Rows |
                Where-Object { $_.Status -eq 'Failed' } |
                ForEach-Object { "$($_.FullName): $($_.Message)" }
        )
        throw @"
$Mode did not pass every test: passed=$Passed, total=$Total, failed=$Failed,
skipped=$Skipped, inconclusive=$Inconclusive.
Failures:
 - $($Failures -join "`n - ")
"@
    }

    # Dead against one producer and load-bearing against the other, and which is
    # which is the opposite of what an earlier version of this comment said.
    #
    # Connected path: PipelineTestRunner defines total as
    # PassCount + FailCount + SkipCount + InconclusiveCount, so this condition is
    # algebraically `failed + inconclusive -ne 0` and the throw immediately above
    # has already fired. Dead there, kept for a producer that grows a fifth
    # outcome or counts discovered tests instead of summing results.
    #
    # Batch path - the one CI takes: ConvertFrom-NUnitResultsXml sets total to the
    # ROW COUNT, and the four counters are independent filters over those same
    # rows. A row carrying any result string the filters do not recognise is
    # counted by total and by none of them. Measured: a results file holding
    # <test-case result="Warning"/> reaches here as failed=0, inconclusive=0 and
    # passes the check above untouched; this one is the only thing that catches it
    # (`passed=1, skipped=0, total=2`). Do not delete it as redundant.
    if ($Passed + $Skipped -ne $Total) {
        throw @"
$Mode did not account for every test: passed=$Passed, skipped=$Skipped,
total=$Total.
"@
    }

    # The rows must corroborate the counter, because the skip check below is only
    # as good as the rows it reads. Measured adversarially against the connected
    # producer's shape, all three of these passed the gate before this check
    # existed: summary.skipped=1 with the row simply absent from results; a row
    # whose Status read 'Ignored' rather than 'Skipped'; and results missing
    # altogether. Each is a skipped test the sanctioned-skip filter never sees, so
    # it fails open - the exact opposite of what that filter is for.
    #
    # Tautological where a runner derives its summary from these same rows, which
    # is the batch path's case. It is not free even there: it pins that the
    # derivation stays a derivation.
    $SkippedRows = @($Rows | Where-Object { $_.Status -eq 'Skipped' })
    if ($SkippedRows.Count -ne $Skipped) {
        throw @"
$Mode reports skipped=$Skipped but $($SkippedRows.Count) of its $($Rows.Count)
result row(s) say 'Skipped'. The two disagree, so no skip can be judged against
the one class allowed to skip itself. A row missing, or labelled anything other
than 'Skipped', hides exactly the test that stopped running.
"@
    }

    # One sanctioned skip, and no more. `passed -ne total` used to fail the gate
    # outright, which was right for a suite where nothing may skip; the end-to-end
    # tier changed that, because its endpoint is deliberately not committed and so
    # every machine that has not opted in - CI included - skips it. Tolerating it
    # by class rather than by a blanket "skips are fine" rule keeps the invariant
    # that matters: a test which quietly stops running anywhere else still fails
    # the gate, and gets named while doing it.
    if ($Skipped -gt 0) {
        $Unsanctioned = @(
            $SkippedRows |
                Where-Object { $_.FullName -notlike "$SanctionedSkipClass.*" } |
                ForEach-Object { "$($_.FullName): $($_.Message)" }
        )
        if ($Unsanctioned.Count -gt 0) {
            throw @"
$Mode skipped $($Unsanctioned.Count) test(s) outside $SanctionedSkipClass, which
is the only class allowed to skip itself. A test that stops running is not a
pass:
 - $($Unsanctioned -join "`n - ")
"@
        }
    }

    return $Summary
}

# The batch runner's NUnit XML, reshaped into what Assert-UnityTestRunPassed
# reads.
#
# The summary is DERIVED from the test-case rows rather than read from the
# test-run element's own attributes, and that is a stated limit rather than an
# oversight. NUnit 3's `total` is a summary count whose relationship to the
# skipped, ignored and explicit categories is not the same as the connected
# producer's, and this project has no way to measure it: producing a batch-mode
# results file means launching a second editor against a project a developer
# keeps open, which is how a real session was destroyed during this iteration.
# Grading the rows is correct regardless of what that attribute means, and the
# rows are what the sanctioned-skip check needs anyway.
#
# The consequence is written down in docs/verification-matrix.md rather than left
# implied, and it is one check rather than two. The skipped-row cross-check runs
# the identical filter on both sides here, so on this path it compares a
# derivation against itself and cannot catch a miscount. The accounting check is
# NOT in that class: total below is the row count, not the sum of the four
# counters, so a row whose result string none of the filters recognise is caught
# by it and by nothing else. Everything else - the empty-run guard, named
# failures, the skip count, and the sanctioned-skip check - grades identically on
# both paths.
function ConvertFrom-NUnitResultsXml {
    param([string]$Path)

    [xml]$Document = Get-Content -Raw -LiteralPath $Path
    $Cases = @($Document.SelectNodes('//test-case'))
    if ($Cases.Count -eq 0) {
        # Deliberately not left to the empty-run guard downstream. A results file
        # that parsed but held no cases and a results file whose shape this
        # function no longer understands are different problems, and only one of
        # them is "the suite ran nothing".
        throw "No <test-case> elements were found in $Path. Either the run " +
              "produced nothing or the results format changed."
    }

    $Results = @(
        $Cases | ForEach-Object {
            $MessageNode = $_.SelectSingleNode('failure/message')
            if ($null -eq $MessageNode) {
                # Assert.Ignore writes its reason here rather than under failure,
                # and that reason is what names the skipping class in the gate's
                # own error text.
                $MessageNode = $_.SelectSingleNode('reason/message')
            }

            $Message = $null
            if ($null -ne $MessageNode) {
                $Message = $MessageNode.InnerText.Trim()
            }

            [pscustomobject]@{
                FullName = $_.fullname
                Status = $_.result
                Message = $Message
            }
        }
    )

    [pscustomobject]@{
        summary = [pscustomobject]@{
            total = $Results.Count
            passed = @($Results | Where-Object { $_.Status -eq 'Passed' }).Count
            failed = @($Results | Where-Object { $_.Status -eq 'Failed' }).Count
            skipped = @($Results | Where-Object { $_.Status -eq 'Skipped' }).Count
            inconclusive = @($Results | Where-Object { $_.Status -eq 'Inconclusive' }).Count
        }
        results = $Results
    }
}

# Written by both runners, so a green run leaves the same machine-readable
# evidence behind whichever path produced it. The batch path used to leave none,
# which meant the artifact CI uploads said nothing about what CI had run.
function Write-UnityTestSummary {
    param(
        [string]$Runner,
        $EditMode,
        $PlayMode
    )

    $Result = [ordered]@{
        runner = $Runner
        editMode = $EditMode
        playMode = $PlayMode
    }
    $Result | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $ArtifactsDirectory 'unity-test-summary.json') `
            -Encoding utf8
}

function Invoke-ConnectedUnityTestMode {
    param(
        [System.Management.Automation.CommandInfo]$UnityCli,
        [string]$Mode
    )

    # Belt-and-braces, not the load-bearing guard. A stale "completed" from the
    # mode that ran before this one is indistinguishable from this one's, and that
    # is not hypothetical - a PlayMode request issued while EditMode's result was
    # still on disk read back EditMode's own 135 passes. But the cause was the old
    # shape: the combined run_tests ran EditMode through the synchronous path,
    # which does not clear the status file. The per-mode --async_tests call below
    # clears it itself before starting, so this delete closes no window the
    # package leaves open. It is kept because it costs nothing and makes the
    # invariant local instead of a property of a package version.
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

        # Every terminal status, not just the happy one. The runner also writes
        # "error" - on async setup failure, on a results-write failure, and on
        # "Tests did not complete" - and "cancelled". Waiting for "completed"
        # alone turns each of those into fifteen minutes of silence followed by a
        # timeout that names the wrong cause and discards the message explaining
        # the right one.
        if ($Status.status -eq 'error' -or $Status.status -eq 'cancelled') {
            throw "Unity $Mode tests reported status '$($Status.status)': $($Status.message)"
        }
    } while ($Status.status -ne 'completed')

    return Assert-UnityTestRunPassed `
        -Mode $Mode `
        -Summary $Status.summary `
        -Results $Status.results
}

function Wait-ForUnityCompile {
    param([System.Management.Automation.CommandInfo]$UnityCli)

    # Run before any test request, because the test list is enumerated from the
    # assemblies as they are at that moment. An editor that has not yet rebuilt
    # after a source edit answers with the OLD list, and the run passes against
    # code that was never compiled - the gate reporting success on work it did not
    # test. This is not hypothetical: adding one test to SendBudgetTests and
    # running verify.ps1 reported 135/135, the count from before the edit; the
    # identical run immediately afterwards reported 136/136.
    #
    # --focus is not passed: the editor is usually unfocused or minimised here and
    # stealing focus mid-run is worse than waiting.
    $CompileOutput = & $UnityCli.Source --json command --project-path $ProjectRoot recompile
    if ($LASTEXITCODE -ne 0) {
        throw "Unity Pipeline recompile exited with code $LASTEXITCODE."
    }

    $CompileEnvelope = ConvertFrom-NativeJson $CompileOutput
    if (-not $CompileEnvelope.success -or -not $CompileEnvelope.data.success) {
        throw "Unity Pipeline recompile failed: $($CompileEnvelope.errors -join '; ')"
    }

    # The decision to wait is taken from the response to the command that triggers
    # the compile, never from a following recompile_status read. That read is
    # racy: issued before the editor has begun, it answers with the PREVIOUS
    # run's "up_to_date" and the wait returns immediately - which put the domain
    # reload inside the next run_tests call, where it exceeded that command's own
    # 30 second dispatch timeout and failed the gate with exit code 6.
    if ($CompileEnvelope.data.result.status -eq 'up_to_date') {
        return
    }

    Write-Host 'Waiting for Unity to finish recompiling...'

    # editor_status, not recompile_status, is what the wait turns on. Compilation
    # finishing is not the same event as the editor being usable again: the domain
    # reload that follows it tears down and re-registers the pipeline server, and a
    # run_tests dispatched into that window waits out its own 30 second timeout and
    # fails the gate with exit code 6. recompile_status reported "completed"
    # through exactly that window. editor_status distinguishes the two, so the
    # wait ends only when the editor says it is ready, not compiling, and not
    # mid-reload.
    $Deadline = [DateTimeOffset]::UtcNow.AddMinutes(10)
    do {
        # First in the block, not last, and the placement is the whole of the
        # guard. PowerShell's `continue` inside do{}while() jumps to the CONDITION,
        # which here is $true, so everything below the unreachable-editor branch
        # was skipped for as long as that branch kept being taken. An editor that
        # died during a domain reload is unreachable forever, and this loop spun in
        # 500 ms sleeps with nothing left that could ever end it. The sibling loop
        # in Invoke-ConnectedUnityTestMode already checks its deadline first; this
        # matches it, and a check that cannot be jumped over cannot be missed.
        if ([DateTimeOffset]::UtcNow -ge $Deadline) {
            throw 'Timed out waiting for Unity to finish compiling and reloading.'
        }

        Start-Sleep -Milliseconds 500

        $StatusOutput = & $UnityCli.Source --json command --project-path $ProjectRoot editor_status
        if ($LASTEXITCODE -ne 0) {
            # The editor is unreachable while the domain reloads, which is the
            # condition being waited out rather than a failure to report.
            continue
        }

        $Envelope = ConvertFrom-NativeJson $StatusOutput
        if ($Envelope.success -and $Envelope.data.success) {
            $Editor = $Envelope.data.result
            if ($Editor.status -eq 'ready' -and
                -not $Editor.compiling -and
                -not $Editor.domainReloadInProgress) {
                break
            }
        }
    } while ($true)

    # Read after the editor settles, not during. A compile that finished with
    # errors is reported here as compile errors rather than as whatever the stale
    # test list happens to do next.
    #
    # A failed read throws, exactly as the recompile call above does. It used to be
    # wrapped in `if ($LASTEXITCODE -eq 0)`, so a recompile_status that failed for
    # any reason skipped the compile-error check silently and the run continued.
    # That is not a small hole: when compilation fails the assemblies on disk are
    # the PREVIOUS good ones, so run_tests answers with a full, all-passing, stale
    # list that satisfies the empty-run guard, the accounting check and the skip
    # check alike. It reopens precisely the false-pass class this wait exists to
    # close. Not knowing whether the compile succeeded has to fail the gate.
    $CompileStatusOutput = & $UnityCli.Source --json command --project-path $ProjectRoot recompile_status
    if ($LASTEXITCODE -ne 0) {
        throw "Unity Pipeline recompile_status exited with code $LASTEXITCODE, so " +
              "whether the scripts compiled is unknown. A failed compile leaves the " +
              "previous assemblies in place and the whole suite passes against code " +
              "that was never built."
    }

    $CompileStatus = (ConvertFrom-NativeJson $CompileStatusOutput).data.result | ConvertFrom-Json
    if ($CompileStatus.failed) {
        throw @"
Unity scripts failed to compile:
 - $($CompileStatus.errors -join "`n - ")
"@
    }
}

function Invoke-ConnectedUnityTests {
    param([System.Management.Automation.CommandInfo]$UnityCli)

    Wait-ForUnityCompile $UnityCli

    # Sequential, not one combined run_tests call. The combined form runs EditMode
    # synchronously, which is what hit the ceiling described above, and both modes
    # share a single status file so they cannot be polled concurrently anyway.
    $EditMode = Invoke-ConnectedUnityTestMode $UnityCli 'EditMode'
    $PlayMode = Invoke-ConnectedUnityTestMode $UnityCli 'PlayMode'

    Write-UnityTestSummary -Runner 'connected-editor' -EditMode $EditMode -PlayMode $PlayMode
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

    # Two Unity editors cannot share one project directory. This fallback is
    # reached whenever the connected probe does not see an instance in state
    # 'ready', and a busy editor -- compiling, importing, mid domain reload, or
    # running PlayMode -- is not 'ready'. So an ordinary verification run could
    # launch a second editor against a project a developer had open and end their
    # session. That happened repeatedly during this iteration.
    #
    # Existence of the lockfile is not the test: a crashed editor leaves one
    # behind. A running editor holds it open, so failing to open it exclusively
    # is what proves the project is actually in use.
    $Lockfile = Join-Path $ProjectRoot 'Temp\UnityLockfile'
    if (Test-Path -LiteralPath $Lockfile -PathType Leaf) {
        $Held = $false
        try {
            $Stream = [System.IO.File]::Open($Lockfile, 'Open', 'ReadWrite', 'None')
            $Stream.Dispose()
        } catch [System.IO.IOException] {
            $Held = $true
        }
        if ($Held) {
            throw ("A Unity editor already has $ProjectRoot open, so this run " +
                'refuses to start a batch-mode editor against it. Let that ' +
                'editor finish what it is doing and re-run, so the tests go ' +
                'through the connected-editor path instead.')
        }
    }

    $Summaries = [ordered]@{}
    foreach ($Mode in @('EditMode', 'PlayMode')) {
        $ResultPath = Join-Path $ArtifactsDirectory "$Mode-results.xml"
        $LogPath = Join-Path $ArtifactsDirectory "$Mode-unity.log"

        # Deleted before the run, not merely overwritten by it. A previous run's
        # XML left on disk is indistinguishable from this run's to every check
        # below, so an editor that dies before writing results would otherwise be
        # graded against whatever passed last time - the same stale-evidence
        # false pass the compile wait exists to close, arriving by another door.
        #
        # And asserted, because SilentlyContinue swallows the one failure that
        # matters. A file another process holds open cannot be deleted, and the
        # suppressed error left exactly the stale results this comment claims are
        # gone - the guard silently doing nothing while reading as though it had.
        # The suppression stays for the ordinary case of the file not existing.
        Remove-Item -LiteralPath $ResultPath -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $ResultPath) {
            throw ("$ResultPath survived deletion, so this run would be graded " +
                'against the previous run results. Something is holding that ' +
                'file open - close it and re-run.')
        }

        # No -quit. It is not merely redundant alongside -runTests: it shuts the
        # editor down before the test runner engages, so no results XML is ever
        # written and this fallback - the only path CI takes - could never run the
        # suite at all. -runTests closes the editor itself once the run finishes.
        #
        # An earlier version of this comment reported a measured batch narrative
        # for that ("Unity exits 0, the log ends at 'Batchmode quit successfully
        # invoked - shutting down!'"). It should not have. The one batch log in
        # the tree predates this work, still carries -quit, and ends with return
        # code 1, contradicting both halves; whatever produced that narrative left
        # no artifact behind. The -quit reasoning above is the Unity documented
        # behaviour and the reason the flag is absent, and it is stated as that
        # rather than as something this repository observed.
        $Arguments = @(
            '-batchmode'
            '-nographics'
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
        if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
            throw ("$Mode did not produce $ResultPath (editor exit code " +
                "$($Process.ExitCode)). See $LogPath.")
        }

        # Graded before the exit code is consulted, deliberately. A run with
        # failures exits non-zero, and "exit code 2" names nothing; the XML names
        # the tests. The exit code is still checked, below, because a run whose
        # results parse clean and whose editor then died is not a pass either -
        # that is the one thing the XML cannot say.
        $Run = ConvertFrom-NUnitResultsXml $ResultPath
        $Summaries[$Mode] = Assert-UnityTestRunPassed `
            -Mode $Mode `
            -Summary $Run.summary `
            -Results $Run.results

        if ($Process.ExitCode -ne 0) {
            throw ("$Mode tests exited with code $($Process.ExitCode) even though " +
                "$ResultPath grades clean, so the editor failed outside the run " +
                "itself. See $LogPath.")
        }
    }

    Write-UnityTestSummary `
        -Runner 'batchmode' `
        -EditMode $Summaries['EditMode'] `
        -PlayMode $Summaries['PlayMode']
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
