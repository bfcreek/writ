# Runs tests, writes release notes, commits the working tree, bumps the patch version, and pushes the branch and an annotated tag.
# Pushing the tag starts the Release workflow. The tag points at that commit.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$branch = git rev-parse --abbrev-ref HEAD
if ($LASTEXITCODE -ne 0) {
    throw "git rev-parse failed ($LASTEXITCODE)."
}

if ($branch -eq "HEAD") {
    throw "Checkout a branch before releasing."
}

$versions = @()
foreach ($name in @(git tag -l "v*")) {
    if ($name -match "^v(\d+)\.(\d+)\.(\d+)$") {
        $versions += [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
    }
}

$current = if ($versions.Count -eq 0) { [version]::new(0, 0, 0) } else { $versions | Sort-Object | Select-Object -Last 1 }
$next = [version]::new($current.Major, $current.Minor, $current.Build + 1)
$tag = "v$next"

git rev-parse -q --verify "refs/tags/$tag" 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    throw "Tag $tag already exists."
}

Write-Host "Patch bump: v$current -> $tag"
Write-Host "Branch: $branch"
Write-Host "Commit: $(git rev-parse --short HEAD) $(git log -1 --format=%s)"
Write-Host ""

Write-Host "Running tests (dotnet test Writ.sln -c Release)."
dotnet test Writ.sln -c Release --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed ($LASTEXITCODE)."
}

$logArgs = @("log", "--pretty=format:- %s")
if ($current -ne [version]::new(0, 0, 0)) {
    $logArgs += "v$current..HEAD"
}

$subjects = @(git @logArgs)
if ($LASTEXITCODE -ne 0) {
    throw "git log failed ($LASTEXITCODE)."
}

$lines = New-Object System.Collections.Generic.List[string]
foreach ($subject in $subjects) {
    if (-not [string]::IsNullOrWhiteSpace($subject)) {
        $lines.Add($subject.Trim())
    }
}

$pending = git status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw "git status failed ($LASTEXITCODE)."
}

if ($pending) {
    foreach ($row in @($pending)) {
        if ([string]::IsNullOrWhiteSpace($row) -or $row.Length -lt 4) {
            continue
        }

        $lines.Add("- $($row.Substring(3).Trim())")
    }
}

if ($lines.Count -eq 0) {
    throw "Release notes are empty."
}

$notes = ($lines -join "`n").Trim() + "`n"

Write-Host ""
Write-Host "Tag: $tag"
Write-Host "Notes:"
Write-Host $notes

$pending = git status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw "git status failed ($LASTEXITCODE)."
}

if ($pending) {
    Write-Host "Local changes to commit:"
    git status --short
    git add -A
    if ($LASTEXITCODE -ne 0) {
        throw "git add failed ($LASTEXITCODE)."
    }

    $commitFile = New-TemporaryFile
    try {
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $commitMessage = "Release $tag`n`n$notes"
        [System.IO.File]::WriteAllText($commitFile.FullName, $commitMessage, $utf8)
        git commit -F $commitFile.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "git commit failed ($LASTEXITCODE)."
        }
    }
    finally {
        Remove-Item -LiteralPath $commitFile.FullName -Force -ErrorAction SilentlyContinue
    }
}

git push origin HEAD
if ($LASTEXITCODE -ne 0) {
    throw "Branch push failed ($LASTEXITCODE)."
}

$notesFile = New-TemporaryFile
try {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($notesFile.FullName, $notes, $utf8)
    git tag -a $tag -F $notesFile.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "git tag failed ($LASTEXITCODE)."
    }
}
finally {
    Remove-Item -LiteralPath $notesFile.FullName -Force -ErrorAction SilentlyContinue
}

git push origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) {
    git tag -d $tag | Out-Null
    throw "Push failed ($LASTEXITCODE). Removed local tag $tag."
}

Write-Host "Pushed $tag. The Release workflow is running."
