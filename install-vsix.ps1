<#
.SYNOPSIS
    Installs the Forge Pilot VSIX and forces Visual Studio to pick it up.

.DESCRIPTION
    Wraps the whole reinstall loop: uninstall any previous copy, install the
    freshly built package, then rebuild the extension and menu caches.

    That last step is the point of this script. On some Visual Studio 2026
    installs the shell does not consume the pending extension configuration
    change on the next launch - the `extensions.configurationchanged` marker is
    left in place and ExtensionMetadataCache.mpack is never rebuilt - so a
    newly installed extension contributes no menu commands and does not appear
    under Manage Extensions. `devenv /setup` and `/updateconfiguration` rebuild
    those caches explicitly.

    This is a host-side quirk, not something the package controls: the VSIX
    registers its package, menus and options pages through an ordinary pkgdef.

.EXAMPLE
    pwsh install-vsix.ps1
    pwsh install-vsix.ps1 -SkipConfigUpdate
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Skip the cache rebuild. Faster, but the extension may not surface.
    [switch]$SkipConfigUpdate
)

$ErrorActionPreference = 'Stop'

$vsix = Get-ChildItem (Join-Path $PSScriptRoot "src\ForgePilot.VSExtension\bin\$Configuration") `
    -Recurse -Filter '*.vsix' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $vsix) { throw "No .vsix found. Run build-vsix.ps1 first." }

Write-Host ("Package : {0} ({1:N2} MB)" -f $vsix.FullName, ($vsix.Length / 1MB))

if (Get-Process devenv -ErrorAction SilentlyContinue) {
    throw "Visual Studio is running. Close every instance first - an install applied under a running IDE is not picked up, and the cache rebuild cannot run."
}

$devenv = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Recurse -Filter 'devenv.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\Common7\\IDE\\devenv\.exe$' } | Select-Object -First 1
if (-not $devenv) { throw "devenv.exe not found." }

$installer = Get-ChildItem 'C:\Program Files (x86)\Microsoft Visual Studio\Installer' -Recurse -Filter 'VSIXInstaller.exe' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $installer) { throw "VSIXInstaller.exe not found." }

# Identity comes from the manifest so the two never drift apart.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($vsix.FullName)
try {
    $entry = $zip.Entries | Where-Object { $_.Name -eq 'extension.vsixmanifest' } | Select-Object -First 1
    $reader = New-Object System.IO.StreamReader($entry.Open())
    $identity = ([xml]$reader.ReadToEnd()).PackageManifest.Metadata.Identity.Id
    $reader.Close()
}
finally { $zip.Dispose() }

Write-Host "Identity: $identity`n"

# Native executables are invoked with $ErrorActionPreference relaxed and WITHOUT
# redirecting stderr. In Windows PowerShell 5.1, "2>&1" on a native command
# wraps each stderr line in an ErrorRecord, which under 'Stop' becomes a
# terminating NativeCommandError even when the exe succeeded - VSIXInstaller
# writes to stderr routinely, so the script died on a working uninstall.
# Exit codes are checked explicitly instead.
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @Arguments | Out-Null
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
}

# A missing previous install is the normal case, so its exit code is ignored.
Write-Host "Uninstalling any previous copy..."
Invoke-Native $installer.FullName @('/q', "/u:$identity") | Out-Null

Write-Host "Installing..."
$code = Invoke-Native $installer.FullName @('/q', $vsix.FullName)
if ($code -ne 0) {
    throw "VSIXInstaller returned $code. See the newest %TEMP%\dd_VSIXInstaller_*.log for the reason."
}

if (-not $SkipConfigUpdate) {
    Write-Host "Rebuilding extension and menu caches (this takes a minute or two)..."
    Invoke-Native $devenv.FullName @('/setup') | Out-Null
    Invoke-Native $devenv.FullName @('/updateconfiguration') | Out-Null
}

Write-Host "`nDone. Start Visual Studio, then View > Other Windows > Forge Pilot."
