# Lists .writ files in ./programs, asks which one to compile, and writes the
# PowerShell emission (script, Writ.psm1, and IR) to ./out.
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$programsDir = Join-Path $repoRoot 'programs'
$outDir = Join-Path $repoRoot 'out'
$cliProject = Join-Path $repoRoot 'src\Writ.Cli\Writ.Cli.csproj'

if (-not (Test-Path -LiteralPath $programsDir)) {
    Write-Error "Programs directory not found: $programsDir"
}

$files = @(Get-ChildItem -LiteralPath $programsDir -Filter '*.writ' -File | Sort-Object Name)
if ($files.Count -eq 0) {
    Write-Error "No .writ files in $programsDir"
}

Write-Host "Writ programs in .\programs"
Write-Host ''
for ($i = 0; $i -lt $files.Count; $i++) {
    Write-Host ('  {0}. {1}' -f ($i + 1), $files[$i].Name)
}
Write-Host ''

$raw = Read-Host 'Select a number'
if ($raw -notmatch '^\d+$') {
    Write-Error "Enter a number from 1 to $($files.Count)."
}

$index = [int]$raw - 1
if ($index -lt 0 -or $index -ge $files.Count) {
    Write-Error "Enter a number from 1 to $($files.Count)."
}

$selected = $files[$index]
Write-Host ''
Write-Host "Compiling $($selected.Name) to PowerShell in .\out"

& dotnet run --project $cliProject -- compile $selected.FullName --out $outDir --target powershell
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
