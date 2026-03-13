# Replace Trace.WriteLine with DebugLog.WriteLine in ALL Navigator files
# This ensures Release builds have full logging without depending on TRACE constant

$files = Get-ChildItem -Path "AcManager\UiObserver" -Filter "Navigator*.cs" -Recurse | Select-Object -ExpandProperty FullName

# Add SDPClient too
$files += "AcManager\UiObserver\SDPClient.cs"

Write-Host "Found $($files.Count) files to process:" -ForegroundColor Cyan
$files | ForEach-Object { Write-Host "  - $(Split-Path $_ -Leaf)" }
Write-Host ""

$totalChanges = 0

foreach ($file in $files) {
    Write-Host "Processing: $(Split-Path $file -Leaf)" -NoNewline

    try {
        $content = Get-Content $file -Raw -ErrorAction Stop

        # Replace Trace.WriteLine with DebugLog.WriteLine
        $newContent = $content -replace 'Trace\.WriteLine\(', 'DebugLog.WriteLine('

        # Count how many replacements were made
        $changes = ([regex]::Matches($content, 'Trace\.WriteLine\(')).Count

        if ($changes -gt 0) {
            Set-Content -Path $file -Value $newContent -NoNewline -ErrorAction Stop
            Write-Host " ✓ ($changes replacements)" -ForegroundColor Green
            $totalChanges += $changes
        } else {
            Write-Host " - (no changes needed)" -ForegroundColor Gray
        }
    }
    catch {
        Write-Host " ✗ ERROR: $_" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Total replacements: $totalChanges" -ForegroundColor Yellow
Write-Host "Done! Run 'Build Solution' to verify." -ForegroundColor Cyan
