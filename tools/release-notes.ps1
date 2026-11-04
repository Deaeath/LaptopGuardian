<#
.SYNOPSIS
    Writes a release's notes. Used by the release workflow, and by hand to preview or re-publish notes.
.DESCRIPTION
    If docs/releases/<version>.md exists, its {{VERSION}}, {{TAG}}, {{ASSETS}} and {{HASH}} placeholders are filled in
    ({{ASSETS}} is the raw URL of docs/releases/<version>/ at -AssetsRef, the tag by default, so its images must be
    committed before tagging). Otherwise the version's section of CHANGELOG.md is used. Either way, install and
    verification instructions are added at the end.
.EXAMPLE
    .\tools\release-notes.ps1 -Version 2.2.0 -Hash <sha256> -Out notes.md
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Hash,
    [string]$Tag = "v$Version",
    [string]$Repo = 'Deaeath/LaptopGuardian',
    [string]$AssetsRef,
    [Parameter(Mandatory)] [string]$Out
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $AssetsRef) { $AssetsRef = $Tag }

$custom = Join-Path $root "docs/releases/$Version.md"
if (Test-Path $custom) {
    $body = [IO.File]::ReadAllText($custom).
        Replace('{{VERSION}}', $Version).
        Replace('{{TAG}}', $Tag).
        Replace('{{HASH}}', $Hash).
        Replace('{{ASSETS}}', "https://raw.githubusercontent.com/$Repo/$AssetsRef/docs/releases/$Version")
}
else {
    $lines = Get-Content (Join-Path $root 'CHANGELOG.md')
    $start = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l -like "## $Version*" })
    $section = @()
    if ($start -ge 0) { for ($i = $start + 1; $i -lt $lines.Count -and $lines[$i] -notlike '## *'; $i++) { $section += $lines[$i] } }
    $body = "<p align=`"center`"><a href=`"https://github.com/$Repo/releases/download/$Tag/LaptopGuardian.exe`"><img src=`"https://img.shields.io/badge/Download_for_Windows-1F6FEB?style=for-the-badge`" alt=`"Download LaptopGuardian.exe`"></a></p>`n`n" + ($section -join "`n")
}

$tail = @"

---

<details>
<summary><b>Install, update and check the download</b></summary>

**Install:** download **LaptopGuardian.exe** and run it. It installs itself for your user account: no administrator rights, no .NET install. Copies already installed update themselves within a day.

**"Windows protected your PC"?** The program is not code-signed yet. Choose **More info**, then **Run anyway**.

**Check the download:** its SHA-256 is ``$Hash``. It was built by GitHub Actions from the tagged source; with GitHub CLI 2.49 or newer you can confirm that:

``````
gh attestation verify LaptopGuardian.exe --repo $Repo
``````
</details>
"@
[IO.File]::WriteAllText($Out, $body.TrimEnd() + "`n" + $tail + "`n")
Write-Host "Wrote $Out"
