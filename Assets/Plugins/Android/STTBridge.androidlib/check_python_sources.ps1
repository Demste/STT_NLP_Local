$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PythonDir = Join-Path $ScriptDir "src/main/python"
$ParserPath = Join-Path $PythonDir "command_parser.py"
$ModelDir = Join-Path $PythonDir "tr_core_news_md"
$Python310 = "C:/Users/3mehm/AppData/Local/Programs/Python/Python310/python.exe"

$Failed = $false

Write-Host "Checking Chaquopy Python source layout..."
Write-Host "  $PythonDir"

if (Test-Path -LiteralPath $Python310) {
    Write-Host "Python 3.10 found. Running py_compile only:"
    Write-Host "  $Python310 -B -m py_compile $ParserPath"
    & $Python310 -B -m py_compile $ParserPath

    if ($LASTEXITCODE -ne 0) {
        $Failed = $true
        Write-Error "py_compile failed for command_parser.py"
    }

    $PyCacheDir = Join-Path $PythonDir "__pycache__"
    if (Test-Path -LiteralPath $PyCacheDir) {
        Remove-Item -LiteralPath $PyCacheDir -Recurse -Force
    }
} else {
    Write-Warning "Python 3.10 was not found at $Python310. Skipping py_compile."
}

if (Test-Path -LiteralPath $ParserPath) {
    Write-Host "OK: command_parser.py exists."
} else {
    $Failed = $true
    Write-Error "Missing command_parser.py"
}

if (Test-Path -LiteralPath $ModelDir) {
    Write-Host "OK: tr_core_news_md package folder exists."
} else {
    $Failed = $true
    Write-Error "Missing tr_core_news_md package folder. Run download_tr_core_news_md.ps1."
}

$DistInfoDirs = @(Get-ChildItem -LiteralPath $PythonDir -Directory -Filter "tr_core_news_md-*.dist-info" -ErrorAction SilentlyContinue)
if ($DistInfoDirs.Count -gt 0) {
    Write-Warning "tr_core_news_md dist-info is present. It is skipped by default to avoid Chaquopy dependency metadata conflicts:"
    $DistInfoDirs | ForEach-Object { Write-Warning "  $($_.FullName)" }
} else {
    Write-Host "OK: tr_core_news_md dist-info is absent."
}

Write-Host ""
Write-Host "This script intentionally does not import spaCy locally."
Write-Host "Windows import success is not proof of Android Chaquopy runtime success."

if ($Failed) {
    exit 1
}

Write-Host "Python source layout check completed."
