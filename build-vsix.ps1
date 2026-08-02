<#
.SYNOPSIS
    Builds the Forge Pilot VSIX.

.DESCRIPTION
    Must use MSBuild from a Visual Studio install, NOT `dotnet build`.

    The VSSDK targets that produce the package — CreateVsixContainer,
    VSCTCompile, GeneratePkgDef — ship with the "Visual Studio extension
    development" workload and only exist inside a VS installation. Under the
    .NET SDK the import is skipped, the assemblies still compile, and no .vsix
    is produced at all: a silent no-op that looks like success.

.EXAMPLE
    pwsh build-vsix.ps1
    pwsh build-vsix.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\ForgePilot.VSExtension\ForgePilot.VSExtension.csproj'
if (-not (Test-Path $project)) { throw "Project not found: $project" }

# Prefer vswhere; fall back to the well-known layout when it is absent or, as on
# some VS 2026 installs, does not report the instance.
$msbuild = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $path = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if ($path) { $msbuild = Join-Path $path 'MSBuild\Current\Bin\MSBuild.exe' }
}
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    $msbuild = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Recurse -Filter 'MSBuild.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\MSBuild\\Current\\Bin\\MSBuild\.exe$' } |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    throw "MSBuild from a Visual Studio install was not found. Install the 'Visual Studio extension development' workload."
}

Write-Host "MSBuild : $msbuild"
Write-Host "Config  : $Configuration`n"

& $msbuild $project /p:Configuration=$Configuration /t:Restore`;Build /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$vsix = Get-ChildItem (Join-Path $PSScriptRoot "src\ForgePilot.VSExtension\bin\$Configuration") `
    -Recurse -Filter '*.vsix' -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $vsix) {
    throw "Build succeeded but produced no .vsix. The VSSDK targets were most likely skipped — check that the 'Visual Studio extension development' workload is installed."
}

Write-Host ("`nVSIX : {0}" -f $vsix.FullName)
Write-Host ("Size : {0:N2} MB" -f ($vsix.Length / 1MB))
