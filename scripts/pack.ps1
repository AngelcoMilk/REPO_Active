param(
    [ValidateSet("test", "release")]
    [string]$Variant = "test",
    [string]$Configuration = "Release",
    [string]$ProjectPath = ".\src\REPO_Active\REPO_Active.csproj",
    [string]$OutputZip = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$enableFileLog = if ($Variant -eq "test") { "true" } else { "false" }
if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = ".\REPO_Active_${Variant}_r2modman.zip"
}

$staging = Join-Path $repoRoot "dist\package_$Variant"
$pluginOutDir = Join-Path $repoRoot "src\REPO_Active\bin\$Configuration\netstandard2.1"
$pluginDll = Join-Path $pluginOutDir "REPO_Active.dll"
$readme = Join-Path $repoRoot "README.md"
$manifest = Join-Path $repoRoot "manifest.json"
$icon = Join-Path $repoRoot "icon.png"

# Version consistency check: manifest.json vs Plugin.cs
$pluginCs = Join-Path $repoRoot "src\REPO_Active\Plugin.cs"
$manifestObj = Get-Content $manifest -Raw | ConvertFrom-Json
$manifestVersion = [string]$manifestObj.version_number
$pluginText = Get-Content $pluginCs -Raw
$pluginMatch = [regex]::Match($pluginText, 'public\s+const\s+string\s+PluginVersion\s*=\s*"([^"]+)"\s*;')
if (-not $pluginMatch.Success) {
    throw "Cannot read PluginVersion from $pluginCs"
}
$pluginVersion = $pluginMatch.Groups[1].Value
if ($manifestVersion -ne $pluginVersion) {
    throw "Version mismatch: manifest.json=$manifestVersion, Plugin.cs=$pluginVersion"
}
Write-Host "[PACK] version check ok: $pluginVersion"

# Manifest website_url check
$websiteUrl = [string]$manifestObj.website_url
if ([string]::IsNullOrWhiteSpace($websiteUrl) -or -not $websiteUrl.StartsWith("https://")) {
    throw "Invalid website_url in manifest.json: '$websiteUrl' (must be non-empty https URL)"
}
Write-Host "[PACK] website check ok: $websiteUrl"
Write-Host "[PACK] variant=$Variant EnableFileLog=$enableFileLog"

Write-Host "[PACK] build start"
$buildArgs = @($ProjectPath, "-c", $Configuration, "/p:EnableFileLog=$enableFileLog")
dotnet build @buildArgs | Out-Host

if (!(Test-Path $pluginDll)) {
    throw "Build output not found: $pluginDll"
}

if (Test-Path $staging) {
    Remove-Item $staging -Recurse -Force
}

New-Item -ItemType Directory -Force -Path (Join-Path $staging "BepInEx\plugins\REPO_Active") | Out-Null

Copy-Item $pluginDll (Join-Path $staging "BepInEx\plugins\REPO_Active\REPO_Active.dll") -Force
Copy-Item $manifest (Join-Path $staging "manifest.json") -Force
Copy-Item $readme (Join-Path $staging "README.md") -Force
Copy-Item $icon (Join-Path $staging "icon.png") -Force

# README BOM check (must be UTF-8 without BOM)
$bytes = [System.IO.File]::ReadAllBytes((Join-Path $staging "README.md"))
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
    throw "README.md in package has UTF-8 BOM. Re-save without BOM before packaging."
}

$packFiles = @(
    (Join-Path $staging "BepInEx\plugins\REPO_Active\REPO_Active.dll"),
    (Join-Path $staging "manifest.json"),
    (Join-Path $staging "README.md"),
    (Join-Path $staging "icon.png")
)
Write-Host "[PACK] staged file list:"
foreach ($f in $packFiles) {
    if (!(Test-Path $f)) { throw "Missing staged file: $f" }
    $it = Get-Item $f
    Write-Host ("[PACK] - {0} ({1} bytes)" -f $it.FullName.Substring($staging.Length + 1), $it.Length)
}

if (Test-Path $OutputZip) {
    Remove-Item $OutputZip -Force
}

Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $OutputZip -CompressionLevel Optimal

Write-Host "[PACK] done -> $OutputZip"