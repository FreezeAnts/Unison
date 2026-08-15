#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\publish"
$installerOut = Join-Path $repoRoot "artifacts\installer"
$iss = Join-Path $PSScriptRoot "Unison.iss"
$project = Join-Path $repoRoot "Unison\Unison.csproj"

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6."
}

Write-Host "Publishing Unison (unpackaged, self-contained x64)..."
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

dotnet publish $project `
    -c Release `
    -p:Platform=x64 `
    -p:RuntimeIdentifier=win-x64 `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:SelfContained=true `
    -p:PublishSingleFile=false `
    --output $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Write-Host "Compiling installer with $iscc ..."
New-Item -ItemType Directory -Force -Path $installerOut | Out-Null
& $iscc "/O$installerOut" "/DPublishDir=$publishDir" "/DAppVersion=0.1.0" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed."
}

Write-Host "Installer written to $installerOut"
