[CmdletBinding()]
param(
    [switch]$IncludeDistInfo
)

$ErrorActionPreference = "Stop"

$ModelUrl = "https://huggingface.co/turkish-nlp-suite/tr_core_news_md/resolve/main/tr_core_news_md-1.0-py3-none-any.whl"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PythonDir = Join-Path $ScriptDir "src/main/python"
$TempDir = Join-Path $ScriptDir ".model_tmp"
$WheelPath = Join-Path $TempDir "tr_core_news_md-any-py3-none-any.whl"
$ExtractDir = Join-Path $TempDir "extracted"

try {
    if (Test-Path -LiteralPath $TempDir) {
        Remove-Item -LiteralPath $TempDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $TempDir | Out-Null
    New-Item -ItemType Directory -Force -Path $ExtractDir | Out-Null
    New-Item -ItemType Directory -Force -Path $PythonDir | Out-Null

    Write-Host "Downloading tr_core_news_md from:"
    Write-Host "  $ModelUrl"
    Invoke-WebRequest -Uri $ModelUrl -OutFile $WheelPath

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($WheelPath, $ExtractDir)

    $PackageDir = Get-ChildItem -LiteralPath $ExtractDir -Directory -Filter "tr_core_news_md" | Select-Object -First 1
    $DistInfoDir = Get-ChildItem -LiteralPath $ExtractDir -Directory -Filter "tr_core_news_md-*.dist-info" | Select-Object -First 1

    if ($null -eq $PackageDir) {
        throw "Could not find tr_core_news_md package folder in the wheel."
    }

    $ExistingPackage = Join-Path $PythonDir "tr_core_news_md"
    if (Test-Path -LiteralPath $ExistingPackage) {
        Remove-Item -LiteralPath $ExistingPackage -Recurse -Force
    }

    Get-ChildItem -LiteralPath $PythonDir -Directory -Filter "tr_core_news_md-*.dist-info" |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }

    Copy-Item -LiteralPath $PackageDir.FullName -Destination $PythonDir -Recurse

    if ($IncludeDistInfo) {
        if ($null -eq $DistInfoDir) {
            throw "Could not find tr_core_news_md dist-info folder in the wheel."
        }

        Copy-Item -LiteralPath $DistInfoDir.FullName -Destination $PythonDir -Recurse
    }

    Write-Host ""
    Write-Host "SUCCESS: tr_core_news_md was unpacked into:"
    Write-Host "  $PythonDir"
    Write-Host ""
    Write-Host "Vendored folders:"
    Write-Host "  $(Join-Path $PythonDir $PackageDir.Name)"

    if ($IncludeDistInfo) {
        Write-Host "  $(Join-Path $PythonDir $DistInfoDir.Name)"
    } else {
        Write-Host ""
        Write-Host "NOTE: tr_core_news_md-*.dist-info was intentionally skipped."
        Write-Host "This avoids build-time dependency metadata conflicts with spaCy 3.8.7 on Android."
        Write-Host "Use -IncludeDistInfo only for targeted experiments."
    }
} catch {
    Write-Error "FAILED to vendor tr_core_news_md: $($_.Exception.Message)"
    exit 1
} finally {
    if (Test-Path -LiteralPath $TempDir) {
        Remove-Item -LiteralPath $TempDir -Recurse -Force
    }
}
