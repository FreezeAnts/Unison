#Requires -Version 5.1
param(
    [ValidateSet("none", "patch", "minor", "major")]
    [string]$Bump = "patch"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\publish"
$installerOut = Join-Path $repoRoot "artifacts\installer"
$iss = Join-Path $PSScriptRoot "Unison.iss"
$project = Join-Path $repoRoot "Unison\Unison.csproj"

function Get-ProjectVersion([string]$csprojPath) {
    [xml]$xml = Get-Content -Path $csprojPath
    $node = $xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    if (-not $node) {
        throw "No <Version> in $csprojPath"
    }
    return [string]$node.Version
}

function Set-ProjectVersion([string]$csprojPath, [string]$version) {
    $text = Get-Content -Path $csprojPath -Raw
    $updated = [regex]::Replace($text, "(<Version>)[^<]+(</Version>)", "`${1}$version`${2}")
    if ($updated -eq $text) {
        throw "Failed to write <Version> in $csprojPath"
    }
    Set-Content -Path $csprojPath -Value $updated -NoNewline
}

function Step-SemVer([string]$version, [string]$bump) {
    if ($bump -eq "none") {
        return $version
    }
    $parts = $version.Split(".")
    while ($parts.Count -lt 3) {
        $parts += "0"
    }
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]
    switch ($bump) {
        "major" { $major++; $minor = 0; $patch = 0 }
        "minor" { $minor++; $patch = 0 }
        "patch" { $patch++ }
    }
    return "$major.$minor.$patch"
}

$current = Get-ProjectVersion $project
$version = Step-SemVer $current $Bump
if ($version -ne $current) {
    Set-ProjectVersion $project $version
    Write-Host "Version $current -> $version"
} else {
    Write-Host "Version $version (unchanged)"
}

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6."
}

Write-Host "Publishing Unison $version (unpackaged, self-contained x64)..."
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
    -p:Version=$version `
    --output $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Write-Host "Compiling installer with $iscc ..."
New-Item -ItemType Directory -Force -Path $installerOut | Out-Null
& $iscc "/O$installerOut" "/DPublishDir=$publishDir" "/DAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed."
}

$setup = Join-Path $installerOut "Unison-Setup-$version.exe"
Write-Host "Installer written to $setup"
Write-Host "Next: commit the csproj version bump, then gh release create v$version `"$setup`""
