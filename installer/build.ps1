<#
.SYNOPSIS
    Publishes T4Power as a single-file exe and packages it with Inno Setup.

.DESCRIPTION
    One command from a clean tree to a distributable installer. The version is read from the
    csproj rather than duplicated here, so there is one place to bump it.

    Note the deliberate absence of -p:PublishTrimmed: LibreHardwareMonitor is reflection-heavy
    and trimming breaks it at runtime rather than at build time.

.PARAMETER SkipPublish
    Package whatever is already in publish\ instead of rebuilding it. Useful when iterating on
    the .iss file, since the publish step dominates the build time.

.EXAMPLE
    .\installer\build.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipPublish,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo       = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repo 'src\T4Power\T4Power.csproj'
$publishDir = Join-Path $repo 'publish'
$outputDir  = Join-Path $PSScriptRoot 'Output'
$script     = Join-Path $PSScriptRoot 'T4Power.iss'

# --- locate the Inno Setup compiler -------------------------------------------------------

$iscc = @(
    'C:\Program Files\Inno Setup 7\ISCC.exe'
    'C:\Program Files (x86)\Inno Setup 7\ISCC.exe'
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
}

if (-not $iscc) {
    throw "Inno Setup's command-line compiler (ISCC.exe) was not found. Install it from https://jrsoftware.org/isdl.php"
}

# --- version, from the one place that defines it ------------------------------------------

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ }
if (-not $version) { throw "Could not read <Version> from $project" }
$version = "$version".Trim()

Write-Host "T4Power $version" -ForegroundColor Cyan
Write-Host "  compiler: $iscc"

# --- publish ------------------------------------------------------------------------------

if ($SkipPublish) {
    Write-Host '  publish:  skipped'
} else {
    Write-Host '  publish:  building single-file win-x64...'

    dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishDir `
        --nologo `
        -v quiet

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

$exe = Join-Path $publishDir 'T4Power.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe but it is not there." }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "  exe:      $exe ($sizeMb MB)"

# --- package ------------------------------------------------------------------------------

Write-Host '  packing:  running Inno Setup...'

& $iscc "/DAppVersion=$version" "/DSourceExe=$exe" "/O$outputDir" $script | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Join-Path $outputDir "T4Power-$version-setup.exe"
if (-not (Test-Path $setup)) { throw "ISCC reported success but $setup is missing." }

$setupMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)

Write-Host ''
Write-Host "Installer ready: $setup ($setupMb MB)" -ForegroundColor Green
