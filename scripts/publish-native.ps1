<#
.SYNOPSIS
    Publishes Quack.Adbc.Native with Native AOT for the requested runtime,
    then prints the path to the resulting shared library.

.DESCRIPTION
    Runs `dotnet publish` for src/Quack.Adbc.Native with PublishAot=true.
    On Windows, prepends vswhere.exe's install directory to PATH so the
    .NET SDK's findvcvarsall.bat can locate MSVC's link.exe without
    emitting a "vswhere.exe is not recognized" line that would otherwise
    be captured into the AOT linker command.

.PARAMETER Runtime
    Target runtime identifier (RID). Defaults to the host's RID. Examples:
    win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.EXAMPLE
    pwsh scripts/publish-native.ps1
    pwsh scripts/publish-native.ps1 -Runtime linux-arm64
#>

[CmdletBinding()]
param(
    [string] $Runtime,
    [string] $Configuration = "Release"
)

$ErrorActionPreference = 'Stop'

# Use IsOSPlatform rather than $IsWindows so this also works in
# Windows PowerShell 5.1.
$ri = [System.Runtime.InteropServices.RuntimeInformation]
$osp = [System.Runtime.InteropServices.OSPlatform]
$onWindows = $ri::IsOSPlatform($osp::Windows)
$onMacOS   = $ri::IsOSPlatform($osp::OSX)

if ([string]::IsNullOrEmpty($Runtime)) {
    $arch = $ri::OSArchitecture.ToString().ToLowerInvariant()
    if ($onWindows) { $Runtime = "win-$arch" }
    elseif ($onMacOS) { $Runtime = "osx-$arch" }
    else { $Runtime = "linux-$arch" }
}

if ($onWindows) {
    $vswhereDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    if (Test-Path (Join-Path $vswhereDir 'vswhere.exe')) {
        if (-not ($env:PATH -split ';' | Where-Object { $_ -eq $vswhereDir })) {
            $env:PATH = "$vswhereDir;$env:PATH"
        }
    } else {
        Write-Warning "vswhere.exe not found at $vswhereDir. AOT link may fail without MSVC tools."
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$project  = Join-Path $repoRoot 'src/Quack.Adbc.Native/Quack.Adbc.Native.csproj'

dotnet publish $project -c $Configuration -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

# AssemblyName=quack_adbc, so the AOT output is platform-suffixed:
#   Windows -> quack_adbc.dll
#   Linux   -> libquack_adbc.so
#   macOS   -> libquack_adbc.dylib
$publishDir = Join-Path $repoRoot "src/Quack.Adbc.Native/bin/$Configuration/net10.0/$Runtime/publish"
if ($Runtime -like 'win-*') {
    $libName = 'quack_adbc.dll'
} elseif ($Runtime -like 'osx-*') {
    $libName = 'libquack_adbc.dylib'
} else {
    $libName = 'libquack_adbc.so'
}
$libPath = Join-Path $publishDir $libName
if (-not (Test-Path $libPath)) {
    throw "Expected output not found: $libPath"
}

Write-Host ""
Write-Host "Published: $libPath"
Write-Host "Size: $([math]::Round((Get-Item $libPath).Length / 1MB, 2)) MB"
