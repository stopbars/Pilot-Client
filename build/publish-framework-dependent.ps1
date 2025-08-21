param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [string]$Project = "..\BARS-Client-V2.csproj",
    [string]$OutRoot = "..\dist",
    [string]$Version
)

$ErrorActionPreference = 'Stop'

Write-Host "=== BARS Client Publish (Framework-Dependent) ===" -ForegroundColor Cyan

# Resolve paths
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$projectPath = Resolve-Path $Project
$distRoot = Resolve-Path $OutRoot -ErrorAction SilentlyContinue
if (-not $distRoot) { New-Item -ItemType Directory -Path $OutRoot | Out-Null; $distRoot = Resolve-Path $OutRoot }

# Determine version (priority: param -> <Version> -> <FileVersion> -> <AssemblyVersion>)
if ($Version) {
    $resolvedVersion = $Version
} else {
    $raw = Get-Content $projectPath -Raw
    try {
        [xml]$csproj = $raw
    } catch {
        Write-Warning "XML parse warning: $($_.Exception.Message)"
    }
    if ($csproj) {
        $pgs = @($csproj.Project.PropertyGroup)
        $candidates = @()
        foreach ($pg in $pgs) {
            if ($pg.Version) { $candidates += $pg.Version.InnerText }
            if ($pg.FileVersion) { $candidates += $pg.FileVersion.InnerText }
            if ($pg.AssemblyVersion) { $candidates += $pg.AssemblyVersion.InnerText }
        }
        $resolvedVersion = ($candidates | Where-Object { $_ -and $_.Trim().Length -gt 0 } | Select-Object -First 1)
    }
    if (-not $resolvedVersion) {
        # Regex fallback: match <Version>...</Version>
        $match = [regex]::Match($raw, '<Version>([^<]+)</Version>', 'IgnoreCase')
        if ($match.Success) { $resolvedVersion = $match.Groups[1].Value.Trim() }
    }
}
if (-not $resolvedVersion) {
    Write-Host "--- DEBUG VERSION EXTRACTION ---" -ForegroundColor Yellow
    Get-Content $projectPath | Select-Object -First 40 | ForEach-Object { Write-Host $_ }
    throw "Unable to resolve version: supply -Version or add <Version> to csproj (debug above)."
}
if ($resolvedVersion -match "\s") { throw "Version contains whitespace: '$resolvedVersion'" }
$version = $resolvedVersion

Write-Host "Project: $projectPath" -ForegroundColor DarkGray
Write-Host "Version: $version" -ForegroundColor DarkGray
Write-Host "Runtime: $Runtime" -ForegroundColor DarkGray
Write-Host "Configuration: $Configuration" -ForegroundColor DarkGray

if (-not $SkipBuild) {
    Write-Host "-- dotnet publish" -ForegroundColor Yellow
    dotnet restore $projectPath
    dotnet publish $projectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        -p:PublishReadyToRun=true `
        -p:PublishSingleFile=false `
        -p:DebugType=none
}

$publishDir = Join-Path (Split-Path $projectPath -Parent) "bin\\$Configuration\\net8.0-windows\\$Runtime\\publish"
if (-not (Test-Path $publishDir)) { throw "Publish directory not found: $publishDir" }

# Staging folder versioned
$stageDir = Join-Path $distRoot "BARSClient-$version-$Runtime"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
Copy-Item $publishDir $stageDir -Recurse

# Remove unwanted files (patterns)
Get-ChildItem -Path $stageDir -Recurse -Include *.pdb,*.xml | ForEach-Object { Remove-Item $_.FullName -Force }

# Generate file hash list
$files = Get-ChildItem -Path $stageDir -File -Recurse | Sort-Object FullName
$hashEntries = @()
foreach ($f in $files) {
    $rel = (Resolve-Path $f.FullName) -replace [regex]::Escape((Resolve-Path $stageDir)), ''
    if ($rel.StartsWith('\')) { $rel = $rel.Substring(1) }
    $h = Get-FileHash $f.FullName -Algorithm SHA256
    $hashEntries += [PSCustomObject]@{ path = $rel; sha256 = $h.Hash; size = $f.Length }
}

$manifest = [PSCustomObject]@{
    appId = 'com.stopbars.barsclient'
    name = 'BARS Client'
    version = $version
    runtime = $Runtime
    frameworkDependent = $true
    publishedAt = (Get-Date).ToString('o')
    files = $hashEntries
}

$manifestPath = Join-Path $stageDir 'manifest.json'
$manifest | ConvertTo-Json -Depth 5 | Out-File -FilePath $manifestPath -Encoding UTF8

# Create zip
$zipName = "BARSClient-$version-$Runtime.zip"
$zipPath = Join-Path $distRoot $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -Force

# latest.json (pointer)
$latest = [PSCustomObject]@{
    version = $version
    runtime = $Runtime
    zip = $zipName
    sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash
    manifest = 'manifest.json'
    generatedAt = (Get-Date).ToString('o')
}
$latest | ConvertTo-Json -Depth 4 | Out-File (Join-Path $distRoot 'latest.json') -Encoding UTF8

Write-Host "-- Output --" -ForegroundColor Green
Write-Host "Zip: $zipPath" -ForegroundColor Green
Write-Host "Manifest inside staging folder: $manifestPath" -ForegroundColor Green
Write-Host "latest.json: $(Join-Path $distRoot 'latest.json')" -ForegroundColor Green

Write-Host "Publish complete." -ForegroundColor Cyan
