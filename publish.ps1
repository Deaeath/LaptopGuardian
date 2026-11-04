<#
.SYNOPSIS
Tests and builds LaptopGuardian locally; optionally installs it.
.DESCRIPTION
    Runs the unit tests and publishes a self-contained single-file build to dist\.
    -Install replaces the copy in %LOCALAPPDATA%\Programs\LaptopGuardian and starts it.
    The version comes from src\Directory.Build.props. Releases are not made here: pushing a tag v<version>
    makes GitHub Actions test, build and publish it from that source (.github/workflows/release.yml).
    Needs the .NET 10 SDK: on PATH, or installed per-user at %LOCALAPPDATA%\Microsoft\dotnet10.
.EXAMPLE
    .\publish.ps1 -Install
#>
param([switch]$Install, [string]$VersionOverride, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_ROOT = Split-Path (Get-Command $dotnet).Source

$root = $PSScriptRoot
$proj = Join-Path $root 'src\LaptopGuardian\LaptopGuardian.csproj'
[xml]$props = Get-Content (Join-Path $root 'src\Directory.Build.props')
$version = if ($VersionOverride) { $VersionOverride } else { $props.Project.PropertyGroup.Version }
$dist = Join-Path $root "dist\$version"

if (-not $SkipTests) {
    Write-Host 'Testing...'
    & $dotnet test (Join-Path $root 'src\LaptopGuardian.Tests\LaptopGuardian.Tests.csproj') -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing was published.' }
}

Write-Host "Publishing $version..."
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
& $dotnet publish $proj -c Release -r win-x64 --self-contained true "-p:Version=$version" `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$exe = Join-Path $dist 'LaptopGuardian.exe'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$sha = "$exe.sha256"
[IO.File]::WriteAllText($sha, "$hash  LaptopGuardian.exe`n")
Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB), SHA-256 $hash"

if ($Install) {
    # The exe installs itself (and replaces any running copy) when run from outside the install folder.
    Start-Process $exe
    Write-Host 'Installed and started.'
}
