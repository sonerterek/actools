<#
.SYNOPSIS
    Compatibility wrapper for AcManager's DeployNWRSLauncher MSBuild target.

.DESCRIPTION
    AcManager now owns NWRS Launcher deployment through its project target. This
    wrapper preserves the existing manual command while using that same target.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Platform = 'x86'
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'AcManager\AcManager.csproj'
& msbuild $project /t:DeployNWRSLauncher /p:Configuration=$Configuration /p:Platform=$Platform /nologo
if ($LASTEXITCODE -ne 0) {
    throw 'AcManager deployment failed.'
}

