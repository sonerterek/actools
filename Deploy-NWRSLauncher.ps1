<#
.SYNOPSIS
	Publishes the Content Manager (AC Launcher) build into the NWRS repo so it
	can be tested end-to-end against the NWRS SD Plugin.

	Release builds are Costura-packed and published from their `Distribution`
	sub-folder. Debug builds are intentionally left unpacked in `Output`, so the
	script mirrors the complete Debug output folder, including loose dependencies.

	This script mirrors the selected source folder into the NWRS target directory.
	By default it does a clean mirror (removes files in the target that are not
	in the source) so the deployed set always matches the build output. Use
	-NoDelete to keep extra files in the target.

.PARAMETER Configuration
	Build configuration to publish from. Release uses its Distribution folder;
	Debug uses the complete unpacked Output folder. Default: Release.

.PARAMETER Platform
	Build platform folder to publish from. Default: x86.

.PARAMETER NoDelete
	Do not remove files in the target that are absent from the source.

.PARAMETER WhatIf
	Show what would be copied/removed without making changes.

.EXAMPLE
	.\Deploy-NWRSLauncher.ps1

.EXAMPLE
	.\Deploy-NWRSLauncher.ps1 -WhatIf

.EXAMPLE
	.\Deploy-NWRSLauncher.ps1 -Configuration Debug
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
	[ValidateSet('Release', 'Debug')]
	[string] $Configuration = 'Release',
	[string] $Platform      = 'x86',
	[switch] $NoDelete
)

$ErrorActionPreference = 'Stop'

# Repo root is the folder this script lives in (actools).
$repoRoot = $PSScriptRoot
if (-not $repoRoot) { $repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }

# The NWRS repo is assumed to be a sibling of the actools repo under the same parent.
$reposParent = Split-Path -Parent $repoRoot

if ($Configuration -eq 'Debug') {
	$source = Join-Path $repoRoot ("Output\{0}\Debug" -f $Platform)
} else {
	$source = Join-Path $repoRoot ("AcManager\bin\{0}\Release\Distribution" -f $Platform)
}
$target = Join-Path $reposParent 'NWRS\bin\NWRS AC Launcher'

Write-Host "Source : $source"
Write-Host "Target : $target"
Write-Host ""

if (-not (Test-Path -LiteralPath $source)) {
	if ($Configuration -eq 'Debug') {
		throw "Debug output folder not found: '$source'. Build Content Manager ($Configuration|$Platform) first."
	}
	throw "Release distribution folder not found: '$source'. Build Content Manager ($Configuration|$Platform) first so the packed 'Distribution' folder is produced."
}

$exe = Join-Path $source 'Content Manager.exe'
if (-not (Test-Path -LiteralPath $exe)) {
	throw "Packed 'Content Manager.exe' not found in '$source'. The pack step may not have run."
}

$nwrsRoot = Join-Path $reposParent 'NWRS'
if (-not (Test-Path -LiteralPath $nwrsRoot)) {
	throw "NWRS repo not found at '$nwrsRoot'. This script assumes the 'actools' and 'NWRS' repos are siblings under the same parent folder ('$reposParent')."
}

if (-not (Test-Path -LiteralPath $target)) {
	if ($PSCmdlet.ShouldProcess($target, 'Create target directory')) {
		New-Item -ItemType Directory -Path $target -Force | Out-Null
	}
}

# Enumerate source files (relative paths).
$sourceFiles = Get-ChildItem -LiteralPath $source -Recurse -File
$copied = 0

foreach ($file in $sourceFiles) {
	$rel     = $file.FullName.Substring($source.Length).TrimStart('\')
	$destPath = Join-Path $target $rel

	$needsCopy = $true
	if (Test-Path -LiteralPath $destPath) {
		$destInfo = Get-Item -LiteralPath $destPath
		# Skip if size and last-write-time already match.
		if ($destInfo.Length -eq $file.Length -and $destInfo.LastWriteTimeUtc -eq $file.LastWriteTimeUtc) {
			$needsCopy = $false
		}
	}

	if ($needsCopy) {
		if ($PSCmdlet.ShouldProcess($destPath, 'Copy')) {
			$destDir = Split-Path -Parent $destPath
			if (-not (Test-Path -LiteralPath $destDir)) {
				New-Item -ItemType Directory -Path $destDir -Force | Out-Null
			}
			Copy-Item -LiteralPath $file.FullName -Destination $destPath -Force
		}
		Write-Host ("  COPY   {0}" -f $rel)
		$copied++
	}
}

# Clean mirror: remove target files that are not in the source.
$removed = 0
if (-not $NoDelete) {
	$sourceRel = @{}
	foreach ($file in $sourceFiles) {
		$sourceRel[$file.FullName.Substring($source.Length).TrimStart('\')] = $true
	}

	$targetFiles = Get-ChildItem -LiteralPath $target -Recurse -File -ErrorAction SilentlyContinue
	foreach ($file in $targetFiles) {
		$rel = $file.FullName.Substring($target.Length).TrimStart('\')
		if (-not $sourceRel.ContainsKey($rel)) {
			if ($PSCmdlet.ShouldProcess($file.FullName, 'Remove (not in source)')) {
				Remove-Item -LiteralPath $file.FullName -Force
			}
			Write-Host ("  DELETE {0}" -f $rel)
			$removed++
		}
	}

	# Remove now-empty directories in the target.
	Get-ChildItem -LiteralPath $target -Recurse -Directory -ErrorAction SilentlyContinue |
		Sort-Object { $_.FullName.Length } -Descending |
		ForEach-Object {
			if (-not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue)) {
				if ($PSCmdlet.ShouldProcess($_.FullName, 'Remove empty directory')) {
					Remove-Item -LiteralPath $_.FullName -Force
				}
			}
		}
}

Write-Host ""
Write-Host ("Done. Copied/updated: {0}, removed: {1}." -f $copied, $removed) -ForegroundColor Green
