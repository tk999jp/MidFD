param (
    [Parameter(Mandatory=$true)]
    [string]$ReleaseTag,

    [switch]$AllowDirty = $false,

    [switch]$SkipTagCheck = $false,

    [switch]$SelfContained = $false
)

# 1. Validation
if ($ReleaseTag -notmatch '^v\d{4}\.\d{2}\.\d{2}(\.\d+)?$') {
    Write-Error "ReleaseTag must follow the format 'vYYYY.MM.DD' or 'vYYYY.MM.DD.N' (e.g., v2026.05.20, v2026.05.24.1)."
    exit 1
}

# 2. Git status verification
if (-not $AllowDirty) {
    $dirty = git status --porcelain
    if ($dirty) {
        Write-Error "Working tree is dirty. Commit all changes or use -AllowDirty to bypass."
        exit 1
    }
}

if (-not $SkipTagCheck) {
    # Check if tag exists
    $tagExists = git tag -l $ReleaseTag
    if (-not $tagExists) {
        Write-Error "Git tag '$ReleaseTag' does not exist. Create the tag first or use -SkipTagCheck."
        exit 1
    }
    # Check if tag matches HEAD
    $tagCommit = (git rev-list -n 1 $ReleaseTag)
    if ($null -ne $tagCommit) { $tagCommit = $tagCommit.Trim() }
    $headCommit = (git rev-parse HEAD).Trim()
    if ($tagCommit -ne $headCommit) {
        Write-Error "Git tag '$ReleaseTag' commit ($tagCommit) does not match HEAD ($headCommit). Use -SkipTagCheck to bypass."
        exit 1
    }
}

# 3. Calculate version strings
$parts = $ReleaseTag.Substring(1).Split('.')
$year = [int]$parts[0]
$month = [int]$parts[1]
$day = [int]$parts[2]
$revision = if ($parts.Length -ge 4) { [int]$parts[3] } else { 0 }

$Version = "$year.$month.$day.$revision"
$AssemblyVersion = "$year.$month.$day.$revision"
$FileVersion = "$year.$month.$day.$revision"
$InformationalVersion = $ReleaseTag

Write-Host "--- Version Configurations ---"
Write-Host "ReleaseTag:           $ReleaseTag"
Write-Host "Version:              $Version"
Write-Host "AssemblyVersion:      $AssemblyVersion"
Write-Host "FileVersion:          $FileVersion"
Write-Host "InformationalVersion: $InformationalVersion"
Write-Host "-----------------------------"

$deploymentType = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
Write-Host "--- Publish Settings ---"
Write-Host "Runtime:              win-x64"
Write-Host "SelfContained:        $($SelfContained.ToString().ToLower())"
Write-Host "Deployment:           $deploymentType"
Write-Host "------------------------"

# 4. Resolve directories
$rootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactsDir = Join-Path $rootDir "artifacts"
$releaseDir = Join-Path $artifactsDir "release"
$releaseTestDir = Join-Path $artifactsDir "release-test"
$tempPublishDir = Join-Path $artifactsDir "temp-publish"

# Clean up stale directories
Write-Host "Cleaning up old/stale directories..."
if (Test-Path $releaseDir) { Remove-Item -Recurse -Force $releaseDir }
if (Test-Path $releaseTestDir) { Remove-Item -Recurse -Force $releaseTestDir }
if (Test-Path $tempPublishDir) { Remove-Item -Recurse -Force $tempPublishDir }

$null = New-Item -ItemType Directory -Path $releaseDir -Force
$null = New-Item -ItemType Directory -Path $tempPublishDir -Force

# 5. Run dotnet publish
Write-Host "Running dotnet publish..."
$csprojPath = Join-Path $rootDir "MidFD.csproj"
$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

dotnet publish $csprojPath `
  -c Release `
  -r win-x64 `
  --self-contained $selfContainedValue `
  -o $tempPublishDir `
  /p:Version=$Version `
  /p:AssemblyVersion=$AssemblyVersion `
  /p:FileVersion=$FileVersion `
  /p:InformationalVersion=$InformationalVersion

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit 1
}

# 5.5 Include public documentation and license in package
$docsToCopy = @("README.md", "CHANGELOG.md", "LICENSE")
foreach ($doc in $docsToCopy) {
    $source = Join-Path $rootDir $doc
    if (Test-Path $source) {
        Copy-Item $source -Destination (Join-Path $tempPublishDir $doc) -Force
    }
}

$userDocsSource = Join-Path $rootDir "UserDocs"
$userDocsDest = Join-Path $tempPublishDir "UserDocs"
if (Test-Path $userDocsSource) {
    Remove-Item $userDocsDest -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $userDocsSource -Destination $userDocsDest -Recurse -Force
}

# 6. Create ZIP archive
$zipPath = Join-Path $releaseDir "MidFD-win-x64.zip"
Write-Host "Creating ZIP archive at: $zipPath"
Compress-Archive -Path "$tempPublishDir\*" -DestinationPath $zipPath -Force

if (-not (Test-Path $zipPath)) {
    Write-Error "Failed to create ZIP archive."
    exit 1
}

# 7. Extract archive for verification
$extractedDest = Join-Path $releaseTestDir "MidFD-win-x64"
Write-Host "Extracting ZIP for verification to: $extractedDest"
$null = New-Item -ItemType Directory -Path $releaseTestDir -Force
Expand-Archive -Path $zipPath -DestinationPath $extractedDest -Force

# Verify exe exists
$exePath = Join-Path $extractedDest "MidFD.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "Verification failed: MidFD.exe is missing from the extracted ZIP."
    exit 1
}

# 8. Verify version metadata
Write-Host "Verifying ProductVersion in MidFD.exe..."
$fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$productVersion = $fileVersionInfo.ProductVersion
Write-Host "Found ProductVersion: $productVersion"

# Check tag inclusion
if ($productVersion -notmatch [regex]::Escape($ReleaseTag)) {
    Write-Error "Verification failed: ProductVersion does not contain the ReleaseTag '$ReleaseTag'."
    exit 1
}

# Check commit hash inclusion
$shortHash = (git rev-parse --short HEAD).Trim()
$fullHash = (git rev-parse HEAD).Trim()

if ($productVersion -notmatch $shortHash -and $productVersion -notmatch $fullHash) {
    Write-Error "Verification failed: ProductVersion does not contain the git commit hash ($shortHash or $fullHash)."
    exit 1
}

Write-Host "ProductVersion verification passed successfully!"

# 8.5. Verify absence of self-contained runtime files (for framework-dependent deployment)
if (-not $SelfContained) {
    Write-Host "Verifying absence of self-contained runtime files..."
    $forbiddenFiles = @("coreclr.dll", "hostfxr.dll", "System.Private.CoreLib.dll")
    foreach ($file in $forbiddenFiles) {
        $filePath = Join-Path $extractedDest $file
        if (Test-Path $filePath) {
            Write-Error "Verification failed: '$file' was found in framework-dependent deployment."
            exit 1
        }
    }
    Write-Host "Runtime files absence verification passed!"
}

# 9. Generate SHA256 file
$sha256Path = Join-Path $releaseDir "MidFD-win-x64.zip.sha256"
Write-Host "Generating SHA256 checksum at: $sha256Path"
$fileHash = Get-FileHash -Path $zipPath -Algorithm SHA256
$fileHash.Hash.ToLower() | Out-File -FilePath $sha256Path -Encoding ascii -NoNewline

# 10. Clean up temporary directory
Write-Host "Cleaning up temporary publish directory..."
Remove-Item -Recurse -Force $tempPublishDir

Write-Host "Release packaging completed successfully!"
