[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$GoServerRoot = 'E:\code\_github\magic-card-server-golang'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
} else {
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param(
        $Actual,
        $Expected,
        [string]$Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Assert-SetEqual {
    param(
        [object[]]$Actual,
        [object[]]$Expected,
        [string]$Message
    )

    $ActualSet = @($Actual | Sort-Object -Unique)
    $ExpectedSet = @($Expected | Sort-Object -Unique)
    $Difference = Compare-Object -ReferenceObject $ExpectedSet -DifferenceObject $ActualSet
    if ($null -ne $Difference) {
        throw "$Message Difference: $($Difference | ConvertTo-Json -Compress)"
    }
}

$VersionFile = Join-Path $ProjectRoot 'ProjectSettings\ProjectVersion.txt'
$VersionLines = Get-Content -LiteralPath $VersionFile
Assert-True ($VersionLines -contains 'm_EditorVersion: 6000.2.7f2') `
    'Unity editor version must remain pinned to 6000.2.7f2.'
Assert-True ($VersionLines -contains 'm_EditorVersionWithRevision: 6000.2.7f2 (2b518236b676)') `
    'Unity editor revision must remain pinned to 2b518236b676.'

$Manifest = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'Packages\manifest.json') |
    ConvertFrom-Json
$ExpectedPins = [ordered]@{
    'com.cysharp.r3' = 'https://github.com/Cysharp/R3.git?path=src/R3.Unity/Assets/R3.Unity#1.3.1'
    'com.cysharp.unitask' = 'https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#2.5.11'
    'com.github-glitchenzo.nugetforunity' = 'https://github.com/GlitchEnzo/NuGetForUnity.git?path=/src/NuGetForUnity#v4.5.0'
    'com.unity.addressables' = '2.7.6'
    'com.unity.inputsystem' = '1.17.0'
    'com.unity.nuget.newtonsoft-json' = '3.2.1'
    'com.unity.test-framework' = '1.6.0'
    'com.unity.test-framework.performance' = '3.2.0'
    'jp.hadashikick.vcontainer' = 'https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.19.0'
    'com.unity.pipeline' = '0.4.0-exp.1'
}

foreach ($Package in $ExpectedPins.GetEnumerator()) {
    $Property = $Manifest.dependencies.PSObject.Properties[$Package.Key]
    Assert-True ($null -ne $Property) "Packages/manifest.json is missing '$($Package.Key)'."
    Assert-Equal $Property.Value $Package.Value "Package '$($Package.Key)' is not pinned."
}
Assert-True (@($Manifest.testables) -contains 'com.echo.harness') `
    'Packages/manifest.json must expose com.echo.harness to the Unity Test Framework.'

$PackageManifest = Get-Content -Raw -LiteralPath (
    Join-Path $ProjectRoot 'Packages\com.echo.harness\package.json') | ConvertFrom-Json
Assert-Equal $PackageManifest.unity '6000.2' 'The embedded Harness package Unity baseline changed.'
Assert-Equal $PackageManifest.version '0.1.0' 'The Harness package version changed unexpectedly.'

$ExpectedRuntimeReferences = [ordered]@{
    'Echo.Harness.Domain' = @()
    'Echo.Harness.Contracts' = @()
    'Echo.Harness.Application' = @(
        'Echo.Harness.Domain', 'Echo.Harness.Contracts', 'UniTask')
    'Echo.Harness.Infrastructure' = @(
        'Echo.Harness.Application', 'Echo.Harness.Contracts', 'UniTask', 'Unity.Addressables')
    'Echo.Harness.Presentation' = @(
        'Echo.Harness.Application', 'R3.Unity')
    'Echo.Harness.Bootstrap' = @(
        'Echo.Harness.Domain',
        'Echo.Harness.Contracts',
        'Echo.Harness.Application',
        'Echo.Harness.Infrastructure',
        'Echo.Harness.Presentation',
        'VContainer')
}

# The reference list alone does not enforce layer purity; these two flags do.
#
# noEngineReferences:true is what makes `using UnityEngine;` fail to compile in
# Domain and Contracts. overrideReferences:true is what stops every
# auto-referenced precompiled assembly in the project from being visible, so it
# is what keeps `using R3;` (R3.dll under Assets/Packages) out of Contracts -- a
# word this gate bans by source text in Domain and Application but cannot see in
# Contracts, which has no source-text assertion at all. Without these
# assertions, flipping either flag to make an illegal DTO field compile leaves
# `references` at [] and the whole gate green.
#
# The values are deliberately NOT uniform: the three lower layers are engine
# free while the three Unity-facing ones are not, and only Contracts overrides
# its precompiled reference set. precompiledReferences is pinned alongside
# overrideReferences because an override is only as strong as the list it
# substitutes -- adding R3.dll there would reopen exactly the hole above.
#
# The [bool] casts mean a DELETED key is compared against Unity's own default
# for that key (false), so the gate pins effective behaviour rather than JSON
# shape.
$ExpectedRuntimeAssemblyFlags = [ordered]@{
    'Echo.Harness.Domain' = @{
        NoEngineReferences = $true
        OverrideReferences = $false
        PrecompiledReferences = @()
    }
    'Echo.Harness.Contracts' = @{
        NoEngineReferences = $true
        OverrideReferences = $true
        PrecompiledReferences = @('Newtonsoft.Json.dll')
    }
    'Echo.Harness.Application' = @{
        NoEngineReferences = $true
        OverrideReferences = $false
        PrecompiledReferences = @()
    }
    'Echo.Harness.Infrastructure' = @{
        NoEngineReferences = $false
        OverrideReferences = $false
        PrecompiledReferences = @()
    }
    'Echo.Harness.Presentation' = @{
        NoEngineReferences = $false
        OverrideReferences = $false
        PrecompiledReferences = @()
    }
    'Echo.Harness.Bootstrap' = @{
        NoEngineReferences = $false
        OverrideReferences = $false
        PrecompiledReferences = @()
    }
}

Assert-SetEqual @($ExpectedRuntimeAssemblyFlags.Keys) @($ExpectedRuntimeReferences.Keys) `
    'The two runtime assembly tables in this gate disagree about the assembly set.'

$RuntimeAsmdefs = Get-ChildItem -LiteralPath (
    Join-Path $ProjectRoot 'Packages\com.echo.harness\Runtime') -Recurse -Filter '*.asmdef'
Assert-Equal $RuntimeAsmdefs.Count $ExpectedRuntimeReferences.Count `
    'The runtime assembly count changed without updating the architecture gate.'

foreach ($AsmdefFile in $RuntimeAsmdefs) {
    $Asmdef = Get-Content -Raw -LiteralPath $AsmdefFile.FullName | ConvertFrom-Json
    Assert-True $ExpectedRuntimeReferences.Contains($Asmdef.name) `
        "Unexpected runtime assembly '$($Asmdef.name)'."
    Assert-SetEqual @($Asmdef.references) @($ExpectedRuntimeReferences[$Asmdef.name]) `
        "Assembly references changed for '$($Asmdef.name)'."
    Assert-True (-not (@($Asmdef.references) -contains 'Echo.Harness.TestKit')) `
        "Runtime assembly '$($Asmdef.name)' must not depend on TestKit."

    $ExpectedFlags = $ExpectedRuntimeAssemblyFlags[$Asmdef.name]
    Assert-Equal ([bool]$Asmdef.noEngineReferences) $ExpectedFlags.NoEngineReferences `
        ("noEngineReferences changed for '$($Asmdef.name)'; that flag, not the " +
            'reference list, is what decides whether the assembly can compile ' +
            'against UnityEngine at all.')
    Assert-Equal ([bool]$Asmdef.overrideReferences) $ExpectedFlags.OverrideReferences `
        ("overrideReferences changed for '$($Asmdef.name)'; that flag is what " +
            'stops every auto-referenced precompiled assembly in the project ' +
            'from becoming usable from this assembly.')
    Assert-SetEqual @($Asmdef.precompiledReferences) @($ExpectedFlags.PrecompiledReferences) `
        "precompiledReferences changed for '$($Asmdef.name)'."
}

$DomainSources = Get-ChildItem -LiteralPath (
    Join-Path $ProjectRoot 'Packages\com.echo.harness\Runtime\Domain') -Recurse -Filter '*.cs'
$DomainText = ($DomainSources | Get-Content -Raw) -join "`n"
Assert-True ($DomainText -notmatch '\b(UnityEngine|Cysharp|R3|VContainer|XLua)\b') `
    'Domain sources may not reference Unity or third-party frameworks.'

$ApplicationSources = Get-ChildItem -LiteralPath (
    Join-Path $ProjectRoot 'Packages\com.echo.harness\Runtime\Application') -Recurse -Filter '*.cs'
$ApplicationText = ($ApplicationSources | Get-Content -Raw) -join "`n"
Assert-True ($ApplicationText -notmatch '\b(UnityEngine|Addressables|R3|VContainer|XLua)\b') `
    'Application sources may only use the approved UniTask async boundary.'

$Contract = Get-Content -Raw -LiteralPath (
    Join-Path $ProjectRoot 'Packages\com.echo.harness\Fixtures\protocol.contract.json') |
    ConvertFrom-Json
Assert-Equal $Contract.version 'legacy-v1' 'Protocol fixture version changed.'
Assert-Equal $Contract.frame.byte_order 'big_endian' 'Protocol byte order changed.'
Assert-Equal $Contract.frame.length_prefix_bytes 4 'Protocol length-prefix width changed.'
Assert-Equal $Contract.frame.message_id_bytes 2 'Protocol message-id width changed.'
Assert-Equal $Contract.frame.length_includes_message_id $false `
    'The payload-length prefix must continue to exclude the message id.'
Assert-Equal $Contract.frame.body_encoding 'utf-8-json' 'Protocol body encoding changed.'
Assert-Equal $Contract.frame.max_payload_bytes 1048576 'Protocol payload cap changed.'
Assert-Equal @($Contract.messages).Count 39 'Protocol fixture must contain 39 message ids.'
Assert-Equal @($Contract.messages.id | Sort-Object -Unique).Count 39 `
    'Protocol fixture contains duplicate message ids.'

[xml]$NuGet = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot 'Assets\packages.config')
$ActualNuGet = @{}
foreach ($Package in $NuGet.packages.package) {
    $ActualNuGet[$Package.id] = $Package.version
}
$ExpectedNuGet = [ordered]@{
    'Microsoft.Bcl.AsyncInterfaces' = '6.0.0'
    'Microsoft.Bcl.TimeProvider' = '8.0.0'
    'R3' = '1.3.1'
    'System.ComponentModel.Annotations' = '5.0.0'
    'System.Runtime.CompilerServices.Unsafe' = '6.0.0'
    'System.Threading.Channels' = '8.0.0'
}
Assert-SetEqual @($ActualNuGet.Keys) @($ExpectedNuGet.Keys) `
    'NuGet package set changed without updating the architecture gate.'
foreach ($Package in $ExpectedNuGet.GetEnumerator()) {
    Assert-Equal $ActualNuGet[$Package.Key] $Package.Value `
        "NuGet package '$($Package.Key)' is not pinned."
}

# Protocol fixture drift gate.
#
# protocol.contract.json is generated by Tools/protocol from the authoritative
# Go source. Regenerating and byte-comparing is what makes a server-side JSON
# tag change fail the build instead of surfacing as a runtime bug months later.
#
# The gate skips with a warning when the Go source is absent. The hosted CI
# runner in .github/workflows/unity-tests.yml runs this script without checking
# out the sibling Go repository, and docs/verification-matrix.md documents that
# boundary; a hard dependency would break the architecture job.
#
# The go TOOLCHAIN is guarded the same way, and for the same promise. With
# $ErrorActionPreference = 'Stop' a missing `go` makes the invocation below throw
# CommandNotFoundException -- an error that names neither this gate nor its
# optional dependency. CI runs this script with no -GoServerRoot, so it resolves
# the default path on a self-hosted Windows runner where the toolchain may be
# absent even though the source is present. Both skips are warnings; when the
# source AND the toolchain are both present the gate stays strict and real drift
# still fails the build.
$FixturePath = Join-Path $ProjectRoot 'Packages\com.echo.harness\Fixtures\protocol.contract.json'
$GoProtocolSource = Join-Path $GoServerRoot 'internal\protocol'
$GoToolchain = Get-Command -Name 'go' -CommandType Application -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath $GoProtocolSource -PathType Container)) {
    Write-Warning "Go protocol source not found at $GoProtocolSource; skipping the protocol fixture drift gate."
} elseif ($null -eq $GoToolchain) {
    Write-Warning 'The go toolchain was not found on PATH; skipping the protocol fixture drift gate.'
} else {
    Push-Location -LiteralPath (Join-Path $ProjectRoot 'Tools\protocol')
    try {
        & go run . -source $GoProtocolSource -check $FixturePath
        if ($LASTEXITCODE -ne 0) {
            throw "The protocol fixture no longer matches $GoProtocolSource. Regenerate it with Tools/protocol -out."
        }
    } finally {
        Pop-Location
    }
}

Write-Host 'Architecture verification passed.'
