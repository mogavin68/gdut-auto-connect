$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$launcher = Join-Path $projectRoot 'dotnet.ps1'
$projectFile = Join-Path $projectRoot 'src\GdutNetwork.App\GdutNetwork.App.csproj'
Push-Location $projectRoot
try {
    & $launcher restore $projectFile --configfile (Join-Path $projectRoot 'NuGet.Offline.Config') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $launcher build $projectFile --configuration Release --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
finally { Pop-Location }
