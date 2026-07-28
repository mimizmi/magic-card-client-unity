[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
} else {
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}

$ExpectedAssemblies = @(
    'Assets\Packages\Microsoft.Bcl.AsyncInterfaces.6.0.0\lib\netstandard2.1\Microsoft.Bcl.AsyncInterfaces.dll'
    'Assets\Packages\Microsoft.Bcl.TimeProvider.8.0.0\lib\netstandard2.0\Microsoft.Bcl.TimeProvider.dll'
    'Assets\Packages\R3.1.3.1\lib\netstandard2.1\R3.dll'
    'Assets\Packages\System.ComponentModel.Annotations.5.0.0\lib\netstandard2.1\System.ComponentModel.Annotations.dll'
    'Assets\Packages\System.Runtime.CompilerServices.Unsafe.6.0.0\lib\netstandard2.0\System.Runtime.CompilerServices.Unsafe.dll'
    'Assets\Packages\System.Threading.Channels.8.0.0\lib\netstandard2.1\System.Threading.Channels.dll'
)

function Get-MissingAssembly {
    @(
        foreach ($RelativePath in $ExpectedAssemblies) {
            $Path = Join-Path $ProjectRoot $RelativePath
            if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
                $RelativePath
            }
        }
    )
}

$Missing = Get-MissingAssembly
if ($Missing.Count -eq 0) {
    Write-Host "NuGetForUnity restore check passed ($($ExpectedAssemblies.Count) assemblies)."
    return
}

if ($CheckOnly) {
    throw "NuGet restore is incomplete. Missing:`n - $($Missing -join "`n - ")"
}

$NuGetForUnity = Get-Command 'nugetforunity' -ErrorAction SilentlyContinue
if ($null -eq $NuGetForUnity) {
    throw @"
NuGet restore is incomplete and the NuGetForUnity CLI is not installed.
Install the pinned CLI, then run this script again:
  dotnet tool install --global NuGetForUnity.Cli --version 4.5.0
Missing:
 - $($Missing -join "`n - ")
"@
}

Write-Host "Restoring packages.config with NuGetForUnity CLI..."
& $NuGetForUnity.Source restore $ProjectRoot
if ($LASTEXITCODE -ne 0) {
    throw "NuGetForUnity restore failed with exit code $LASTEXITCODE."
}

$Missing = Get-MissingAssembly
if ($Missing.Count -gt 0) {
    throw "NuGetForUnity completed but assemblies are still missing:`n - $($Missing -join "`n - ")"
}

Write-Host "NuGetForUnity restore completed and all pinned assemblies are present."
