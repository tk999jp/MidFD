param (
    [ValidateSet("Portable", "Installed", "All")]
    [string]$Mode = "All",

    [string]$Configuration = "Release",

    [string]$Version = "dev-local",

    [switch]$SelfContained = $false
)

$ErrorActionPreference = "Stop"

$rootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactsDir = Join-Path $rootDir "artifacts"
$packageRoot = Join-Path $artifactsDir "local-package"
$tempRoot = Join-Path $packageRoot "temp"
$outputRoot = Join-Path $packageRoot "out"
$csprojPath = Join-Path $rootDir "MidFD.csproj"

$modes = if ($Mode -eq "All") { @("Portable", "Installed") } else { @($Mode) }

if (Test-Path $tempRoot) { Remove-Item -Recurse -Force $tempRoot }
$null = New-Item -ItemType Directory -Path $tempRoot -Force
$null = New-Item -ItemType Directory -Path $outputRoot -Force

function Copy-PackageDocs {
    param([string]$PublishDir)

    foreach ($doc in @("README.md", "CHANGELOG.md", "LICENSE")) {
        $source = Join-Path $rootDir $doc
        if (Test-Path $source) {
            Copy-Item $source -Destination (Join-Path $PublishDir $doc) -Force
        }
    }

    $userDocsSource = Join-Path $rootDir "UserDocs"
    if (Test-Path $userDocsSource) {
        Copy-Item $userDocsSource -Destination (Join-Path $PublishDir "UserDocs") -Recurse -Force
    }

    $readmeFirstSource = Join-Path $rootDir "packaging\runtime-guidance\README_FIRST.txt"
    if (Test-Path $readmeFirstSource) {
        Copy-Item $readmeFirstSource -Destination $PublishDir -Force
    }
}

foreach ($currentMode in $modes) {
    $modeLower = $currentMode.ToLowerInvariant()
    $publishDir = Join-Path $tempRoot $modeLower
    $zipPath = Join-Path $outputRoot "MidFD-win-x64-$modeLower.zip"

    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    $selfContainedValue = if ($SelfContained) { "true" } else { "false" }

    dotnet publish $csprojPath `
        -c $Configuration `
        -r win-x64 `
        --self-contained $selfContainedValue `
        -o $publishDir `
        /p:InformationalVersion=$Version

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $currentMode package."
    }

    Get-ChildItem $publishDir -Filter *.pdb -Recurse | Remove-Item -Force
    Copy-PackageDocs -PublishDir $publishDir

    $profileBootstrapPath = Join-Path $publishDir "storage-profile.json"
    if ($currentMode -eq "Installed") {
        Set-Content -Path $profileBootstrapPath -Value '{ "profile": "installed" }' -Encoding UTF8
        Set-Content -Path (Join-Path $publishDir "README_INSTALLED_PROFILE.txt") `
            -Value "This local package opts in to the Installed storage profile. Runtime settings are stored under %LOCALAPPDATA%\MidFD. To inspect diagnostics, run MidFD.exe --storage-profile-diagnostics-file .\storage-profile-diagnostics.txt." `
            -Encoding UTF8
    } elseif (Test-Path $profileBootstrapPath) {
        Remove-Item $profileBootstrapPath -Force
    }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

    $verifyRoot = Join-Path $tempRoot "verify-$modeLower"
    if (Test-Path $verifyRoot) { Remove-Item -Recurse -Force $verifyRoot }
    Expand-Archive -Path $zipPath -DestinationPath $verifyRoot -Force

    if (-not (Test-Path (Join-Path $verifyRoot "MidFD.exe"))) {
        throw "Package verification failed for ${currentMode}: MidFD.exe is missing."
    }

    $hasBootstrap = Test-Path (Join-Path $verifyRoot "storage-profile.json")
    if ($currentMode -eq "Portable" -and $hasBootstrap) {
        throw "Portable package must not include storage-profile.json."
    }

    if ($currentMode -eq "Installed" -and -not $hasBootstrap) {
        throw "Installed package must include storage-profile.json."
    }

    Write-Host "Created local $currentMode package: $zipPath"
}

Write-Host "Local package generation completed. Output: $outputRoot"
