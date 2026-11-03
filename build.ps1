param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$launcher = Join-Path $projectRoot 'dotnet.ps1'
$projectFile = Join-Path $projectRoot 'src\GdutNetwork.App\GdutNetwork.App.csproj'
Push-Location $projectRoot
try {
    $configFile = if ($Offline) { 'NuGet.Offline.Config' } else { 'NuGet.Config' }
    & $launcher restore $projectFile --configfile (Join-Path $projectRoot $configFile) --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $launcher build $projectFile --configuration Release --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
finally { Pop-Location }
