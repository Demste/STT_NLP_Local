$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = (Resolve-Path -LiteralPath (Join-Path $ScriptDir "../../../..")).Path
$PramRoot = Join-Path $ProjectRoot "Library/PramData"
$CacheRoot = Join-Path $PramRoot "AndroidApplicationController/Cache"
$LogPath = Join-Path $ScriptDir "chaquopy_debug_build.log"
$TaskName = ":unityLibrary:STTBridge.androidlib:extractDebugPythonBuildPackages"

if (-not (Test-Path -LiteralPath $PramRoot)) {
    Write-Host "PRAM data was not found:"
    Write-Host "  $PramRoot"
    Write-Host "Build from Unity once so it generates PRAM Gradle files, then rerun this script."
    exit 1
}

if (-not (Test-Path -LiteralPath $CacheRoot)) {
    Write-Host "Generated Android Gradle cache was not found:"
    Write-Host "  $CacheRoot"
    Write-Host "Build from Unity once so it generates PRAM Gradle files, then rerun this script."
    exit 1
}

$GradlewCandidates = @(Get-ChildItem -LiteralPath $PramRoot -Recurse -File -Filter "gradlew.bat" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending)

if ($GradlewCandidates.Count -eq 0) {
    Write-Host "Could not find gradlew.bat under:"
    Write-Host "  $PramRoot"
    Write-Host "Build from Unity once so it generates PRAM Gradle files, then rerun this script."
    exit 1
}

$Gradlew = $GradlewCandidates[0].FullName

$SettingsCandidates = @(Get-ChildItem -LiteralPath $CacheRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq "settings.gradle" -or $_.Name -eq "settings.gradle.kts" } |
    Sort-Object LastWriteTime -Descending)

if ($SettingsCandidates.Count -gt 0) {
    $GradleProjectDir = $SettingsCandidates[0].Directory.FullName
} else {
    $ProjectCandidates = @(Get-ChildItem -LiteralPath $CacheRoot -Directory -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)

    if ($ProjectCandidates.Count -eq 0) {
        Write-Host "Could not locate a generated Gradle project under:"
        Write-Host "  $CacheRoot"
        Write-Host "Build from Unity once so it generates PRAM Gradle files, then rerun this script."
        exit 1
    }

    $GradleProjectDir = $ProjectCandidates[0].FullName
}

Write-Host "Using gradlew:"
Write-Host "  $Gradlew"
Write-Host "Using generated Gradle project:"
Write-Host "  $GradleProjectDir"
Write-Host "Running task:"
Write-Host "  $TaskName --stacktrace --info --warning-mode all"
Write-Host "Writing full log:"
Write-Host "  $LogPath"
Write-Host ""

$Arguments = @(
    "-p",
    $GradleProjectDir,
    $TaskName,
    "--stacktrace",
    "--info",
    "--warning-mode",
    "all"
)

& $Gradlew @Arguments *>&1 | Tee-Object -FilePath $LogPath
$ExitCode = $LASTEXITCODE

Write-Host ""
Write-Host "Gradle exit code: $ExitCode"
Write-Host "Diagnostic log:"
Write-Host "  $LogPath"

exit $ExitCode
