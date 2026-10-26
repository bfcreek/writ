param(
    [string]$Version = "0.0.1",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^(\d+\.\d+\.\d+)') {
    throw "Version must start with major.minor.patch (got '$Version')."
}

$msiVersion = $Matches[1]
$platform = if ($Runtime -eq "win-arm64") { "arm64" } else { "x64" }
$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $PSScriptRoot "payload"

New-Item -ItemType Directory -Force -Path $payload | Out-Null

dotnet publish (Join-Path $root "src/Writ.Cli/Writ.Cli.csproj") `
    -c Release `
    -r $Runtime `
    --self-contained true `
    "-p:Version=$Version" `
    "-p:IncludeSourceRevisionInInformationalVersion=false" `
    "-p:PublishSingleFile=true" `
    "-p:IncludeNativeLibrariesForSelfExtract=true" `
    "-p:EnableCompressionInSingleFile=true" `
    "-p:DebugType=embedded" `
    -o $payload

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE)."
}

if (-not (Test-Path (Join-Path $payload "writ.exe"))) {
    throw "Publish did not produce payload/writ.exe."
}

Copy-Item (Join-Path $root "src\Writ.Runtime.PowerShell\Writ.psm1") (Join-Path $payload "Writ.psm1") -Force

dotnet build (Join-Path $PSScriptRoot "Writ.wixproj") `
    -c Release `
    "-p:Platform=$platform" `
    "-p:MsiVersion=$msiVersion" `
    "-p:PackageVersion=$Version" `
    "-p:RuntimeIdentifier=$Runtime"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed ($LASTEXITCODE)."
}

$msi = Join-Path $PSScriptRoot "bin\$platform\writ-$Version-$Runtime.msi"
if (-not (Test-Path $msi)) {
    throw "Expected installer at $msi."
}

Write-Host $msi
