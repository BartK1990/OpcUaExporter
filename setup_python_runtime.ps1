# setup_python_runtime.ps1
# Run once from the solution root to set up the embedded Python runtime.
# Requires internet access.

$ErrorActionPreference = "Stop"

$PY_VERSION  = "3.11.9"
$PY_ZIP      = "python-$PY_VERSION-embed-amd64.zip"
$PY_URL      = "https://www.python.org/ftp/python/$PY_VERSION/$PY_ZIP"
$RUNTIME_DIR = Join-Path $PSScriptRoot "OpcUaExporter\Python\runtime"
$PY_EXE      = Join-Path $RUNTIME_DIR "python.exe"
$TMP_ZIP     = Join-Path $env:TEMP $PY_ZIP

Write-Host "=== OPC UA Exporter – Python Runtime Setup ===" -ForegroundColor Cyan

# 1. Create runtime directory
if (-not (Test-Path $RUNTIME_DIR)) {
    New-Item -ItemType Directory -Path $RUNTIME_DIR | Out-Null
}

# 2. Download embeddable Python (skip if already present)
if (-not (Test-Path $PY_EXE)) {
    Write-Host "Downloading $PY_ZIP ..."
    Invoke-WebRequest -Uri $PY_URL -OutFile $TMP_ZIP -UseBasicParsing
    Write-Host "Extracting to $RUNTIME_DIR ..."
    Expand-Archive -Path $TMP_ZIP -DestinationPath $RUNTIME_DIR -Force
    Remove-Item $TMP_ZIP
} else {
    Write-Host "Python runtime already present at $RUNTIME_DIR" -ForegroundColor Green
}

# 3. Enable site-packages in the embeddable distribution
#    The ._pth file disables import site by default; we must uncomment "import site"
$pthFile = Get-ChildItem -Path $RUNTIME_DIR -Filter "python311._pth" | Select-Object -First 1
if ($pthFile) {
    $content = Get-Content $pthFile.FullName -Raw
    $updated = $content -replace "#import site", "import site"
    # Also add Lib sub-directory so pip-installed packages are found
    if ($updated -notmatch "Lib") {
        $updated = $updated.TrimEnd() + "`r`nLib`r`n"
    }
    Set-Content -Path $pthFile.FullName -Value $updated
    Write-Host "Patched $($pthFile.Name) to enable site-packages" -ForegroundColor Yellow
}

# 4. Install pip
$GET_PIP_URL = "https://bootstrap.pypa.io/get-pip.py"
$GET_PIP     = Join-Path $env:TEMP "get-pip.py"
Write-Host "Downloading get-pip.py ..."
Invoke-WebRequest -Uri $GET_PIP_URL -OutFile $GET_PIP -UseBasicParsing
Write-Host "Installing pip into embedded runtime ..."
& $PY_EXE $GET_PIP --no-warn-script-location
Remove-Item $GET_PIP

# 5. Install opcua library
Write-Host "Installing opcua library ..." -ForegroundColor Cyan
& $PY_EXE -m pip install opcua --no-warn-script-location

Write-Host ""
Write-Host "=== Setup complete! ===" -ForegroundColor Green
Write-Host "Python runtime : $RUNTIME_DIR"
Write-Host "Python exe     : $PY_EXE"
Write-Host ""
Write-Host "You can now build and run OpcUaExporter."
