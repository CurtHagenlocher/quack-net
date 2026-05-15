<#
.SYNOPSIS
    Publishes Quack.Adbc.Native with Native AOT and copies the resulting DLL.

.DESCRIPTION
    Native AOT on Windows invokes `vswhere.exe` to locate MSVC's `link.exe`.
    The .NET SDK's batch script (`findvcvarsall.bat`) assumes vswhere is on
    PATH, but it isn't by default. This script prepends the standard vswhere
    install location to PATH before running `dotnet publish`, which avoids a
    spurious "vswhere.exe is not recognized" line being captured into the
    AOT linker command.

.PARAMETER Runtime
    Target runtime identifier (RID), e.g. win-x64, win-arm64. Defaults to
    win-x64.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    pwsh scripts/publish-native.ps1
#>

[CmdletBinding()]
param(
    [string] $Runtime = "win-x64",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = 'Stop'

$vswhereDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path (Join-Path $vswhereDir 'vswhere.exe')) {
    if (-not ($env:PATH -split ';' | Where-Object { $_ -eq $vswhereDir })) {
        $env:PATH = "$vswhereDir;$env:PATH"
    }
} else {
    Write-Warning "vswhere.exe not found at $vswhereDir. AOT link may fail without MSVC tools."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$project  = Join-Path $repoRoot 'src/Quack.Adbc.Native/Quack.Adbc.Native.csproj'

dotnet publish $project -c $Configuration -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

$publishDir = Join-Path $repoRoot "src/Quack.Adbc.Native/bin/$Configuration/net10.0/$Runtime/publish"
$dll        = Join-Path $publishDir 'quack_adbc.dll'
if (-not (Test-Path $dll)) {
    throw "Expected output not found: $dll"
}

Write-Host ""
Write-Host "Published: $dll"
Write-Host "Size: $((Get-Item $dll).Length / 1MB) MB"
