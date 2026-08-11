function Get-PublicReleaseHeading([string]$text) {
    return [regex]::Match($text, '(?m)^##\s+(v\d{4}\.\d{2}\.\d{2}(?:\.\d+)?)\s*[—-]')
}

function Get-FirstChangelogSectionHeading([string]$text) {
    return [regex]::Match($text, '(?m)^##\s+([^\r\n]+?)\s*$')
}

function Assert-PrePublishReleaseContract([string]$rootDir, [string]$releaseTag, [string]$candidateSha) {
    $changeLogPath = Join-Path $rootDir "CHANGELOG.md"
    if (-not (Test-Path $changeLogPath -PathType Leaf)) {
        throw "Pre-publish contract failed: CHANGELOG.md is missing."
    }

    $changeLogText = Get-Content $changeLogPath -Raw
    $firstReleaseHeading = Get-PublicReleaseHeading $changeLogText
    if (-not $firstReleaseHeading.Success -or $firstReleaseHeading.Groups[1].Value -ne $releaseTag) {
        throw "Pre-publish contract failed: first CHANGELOG release is not '$releaseTag'."
    }

    $firstSectionHeading = Get-FirstChangelogSectionHeading $changeLogText
    if ($firstSectionHeading.Success -and $firstSectionHeading.Groups[1].Value.Trim() -match '^Unreleased$') {
        throw "Pre-publish contract failed: current public CHANGELOG section is Unreleased."
    }
    if ([string]::IsNullOrWhiteSpace($candidateSha)) {
        throw "Pre-publish contract failed: candidate SHA is empty."
    }
}

function Assert-PostPackageReleaseContract(
    [string]$packageRoot,
    [string]$releaseTag,
    [string]$candidateSha,
    [string]$productVersion) {
    if ($productVersion -notmatch [regex]::Escape($releaseTag) -or
        $productVersion -notmatch [regex]::Escape($candidateSha)) {
        throw "Post-package contract failed: ProductVersion must contain '$releaseTag' and candidate SHA '$candidateSha'."
    }

    $publicDocs = @()
    $publicDocs += Get-ChildItem $packageRoot -File -Filter *.md -ErrorAction SilentlyContinue
    $userDocsRoot = Join-Path $packageRoot "UserDocs"
    if (Test-Path $userDocsRoot -PathType Container) {
        $publicDocs += Get-ChildItem $userDocsRoot -Recurse -File -Filter *.md
    }
    foreach ($doc in $publicDocs) {
        $releaseHeading = Get-PublicReleaseHeading (Get-Content $doc.FullName -Raw)
        if ($releaseHeading.Success -and $releaseHeading.Groups[1].Value -ne $releaseTag) {
            throw "Post-package contract failed: release version mismatch in $($doc.Name)."
        }
    }
}
