# PowerShell script to create a clean distribution folder after build
# This script copies only the necessary files for distribution when using Costura Fody

param(
    [Parameter(Mandatory=$true)]
    [string]$OutputPath
)

# Fix: Remove any trailing quotes that MSBuild/PowerShell might add due to backslash escaping
$OutputPath = $OutputPath.TrimEnd('"')

Write-Host "Creating clean distribution folder..." -ForegroundColor Green
Write-Host "Output Path: $OutputPath" -ForegroundColor Yellow

# Find the main executable (look for NWRS*.exe specifically)
$ExeFiles = Get-ChildItem -Path $OutputPath -Filter "NWRS*.exe" | Where-Object { $_.Name -notlike "*.vshost.exe" }

if ($ExeFiles.Count -eq 0) {
    Write-Host "ERROR: No NWRS executable found in output path!" -ForegroundColor Red
    Write-Host "Looking for: NWRS*.exe in $OutputPath" -ForegroundColor Red
    exit 1
}

if ($ExeFiles.Count -gt 1) {
    Write-Host "WARNING: Multiple NWRS executables found, using the first one: $($ExeFiles[0].Name)" -ForegroundColor Yellow
}

$TargetPath = $ExeFiles[0].FullName
$TargetName = $ExeFiles[0].BaseName

Write-Host "Found executable: $($ExeFiles[0].Name)" -ForegroundColor Cyan
Write-Host ""

# Define the distribution folder path
$DistFolder = Join-Path -Path $OutputPath -ChildPath "Distribution"

# Remove existing Distribution folder if it exists
if (Test-Path $DistFolder) {
    Write-Host "Removing existing Distribution folder..." -ForegroundColor Yellow
    Remove-Item -Path $DistFolder -Recurse -Force
}

# Create Distribution folder
Write-Host "Creating Distribution folder at: $DistFolder" -ForegroundColor Cyan
New-Item -Path $DistFolder -ItemType Directory -Force | Out-Null

# Copy the main executable
Write-Host "Copying executable: $TargetPath" -ForegroundColor Cyan
Copy-Item -Path $TargetPath -Destination $DistFolder -Force

# Copy PDB file if it exists (for debugging)
$PdbFile = Join-Path $OutputPath "$TargetName.pdb"
if (Test-Path $PdbFile) {
    Write-Host "Copying PDB file for debugging..." -ForegroundColor Cyan
    Copy-Item -Path $PdbFile -Destination $DistFolder -Force
}

# Copy .exe.config file if it exists
$ConfigFile = "$TargetPath.config"
if (Test-Path $ConfigFile) {
    Write-Host "Copying config file..." -ForegroundColor Cyan
    Copy-Item -Path $ConfigFile -Destination $DistFolder -Force
}

# Copy Assets folder (including SD Icons)
$AssetsFolder = Join-Path $OutputPath "Assets"
if (Test-Path $AssetsFolder) {
    Write-Host "Copying Assets folder..." -ForegroundColor Cyan
    $DestAssets = Join-Path $DistFolder "Assets"
    Copy-Item -Path $AssetsFolder -Destination $DestAssets -Recurse -Force
}

# Copy UiObserver directory
$UiObserverFolder = Join-Path $OutputPath "UiObserver"
if (Test-Path $UiObserverFolder) {
    Write-Host "Copying UiObserver folder..." -ForegroundColor Cyan
    $DestUiObserver = Join-Path $DistFolder "UiObserver"
    Copy-Item -Path $UiObserverFolder -Destination $DestUiObserver -Recurse -Force
}

Write-Host ""
Write-Host "Distribution folder created successfully!" -ForegroundColor Green
Write-Host "Location: $DistFolder" -ForegroundColor Green
Write-Host "Contents:" -ForegroundColor Cyan
Get-ChildItem -Path $DistFolder -Recurse | ForEach-Object {
    Write-Host "  $($_.FullName.Replace($DistFolder, ''))" -ForegroundColor Gray
}
