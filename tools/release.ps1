# Asks to run tests, records release notes, bumps the patch version, and pushes an annotated tag.
# Pushing the tag starts the Release workflow. Commit the working tree first; the tag points at HEAD.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Read-Yes([string]$Prompt, [bool]$DefaultYes) {
    $hint = if ($DefaultYes) { "[Y/n]" } else { "[y/N]" }
    $answer = Read-Host "$Prompt $hint"
    if ([string]::IsNullOrWhiteSpace($answer)) {
        return $DefaultYes
    }

    return $answer -match "^[Yy]"
}

$dirty = git status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw "git status failed ($LASTEXITCODE)."
}

if ($dirty) {
    throw "Commit or stash changes before releasing. The tag is the current commit, not the working tree."
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
Write-Host "Commit: $(git rev-parse --short HEAD) $(git log -1 --format=%s)"
Write-Host ""

if (Read-Yes "Run tests (dotnet test Writ.sln -c Release)?" $true) {
    dotnet test Writ.sln -c Release --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed ($LASTEXITCODE)."
    }
}
else {
    Write-Host "Skipping tests."
}

Write-Host ""
Write-Host "Release notes for $tag. Enter a blank line to finish."
$lines = New-Object System.Collections.Generic.List[string]
while ($true) {
    $line = Read-Host
    if ($line.Length -eq 0) {
        break
    }

    $lines.Add($line)
}

if ($lines.Count -eq 0) {
    throw "Release notes are empty."
}

$notes = ($lines -join "`n").Trim() + "`n"

Write-Host ""
Write-Host "Tag: $tag"
Write-Host "Notes:"
Write-Host $notes

if (-not (Read-Yes "Push $tag to origin and start the release workflow?" $false)) {
    Write-Host "Stopped."
    exit 0
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
