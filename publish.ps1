param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'app'))
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$launcher = Join-Path $projectRoot 'dotnet.ps1'
$projectFile = Join-Path $projectRoot 'src\GdutNetwork.App\GdutNetwork.App.csproj'
Push-Location $projectRoot
try {
    & $launcher restore $projectFile --runtime win-x64 '-p:SelfContained=true' '-p:PublishSingleFile=true' --configfile (Join-Path $projectRoot 'NuGet.Config') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Runtime package restore failed.' }
    & $launcher publish $projectFile --configuration Release --runtime win-x64 --self-contained true --no-restore '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=none' '-p:DebugSymbols=false' --output $OutputDirectory --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
}
finally { Pop-Location }
