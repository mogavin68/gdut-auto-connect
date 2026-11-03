$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$version = '10.0.403'
$url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip'
$expectedHash = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'
$sdkDirectory = Join-Path $projectRoot 'work\dotnet-sdk'
$downloadDirectory = Join-Path $projectRoot 'work\downloads'
if (Test-Path -LiteralPath $sdkDirectory) {
    & (Join-Path $projectRoot 'dotnet.ps1') --version
    if ($LASTEXITCODE -ne 0) { throw 'Existing local SDK is not usable. No files were overwritten.' }
    return
}
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$archive = Join-Path $downloadDirectory "dotnet-sdk-$version-win-x64.zip"
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 600 }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ine $expectedHash) { throw 'SDK checksum mismatch; archive will not be extracted.' }
Expand-Archive -LiteralPath $archive -DestinationPath $sdkDirectory
Push-Location $projectRoot
try {
    & (Join-Path $projectRoot 'dotnet.ps1') --info
    if ($LASTEXITCODE -ne 0) { throw 'SDK validation failed.' }
} finally { Pop-Location }
