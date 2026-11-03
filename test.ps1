$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$launcher = Join-Path $projectRoot 'dotnet.ps1'
$testProject = Join-Path $projectRoot 'tests\GdutNetwork.Tests\GdutNetwork.Tests.csproj'
New-Item -ItemType Directory -Path (Join-Path $projectRoot 'work\diagnostics') -Force | Out-Null
Push-Location $projectRoot
try {
    & $launcher restore $testProject --configfile (Join-Path $projectRoot 'NuGet.Offline.Config') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
    & $launcher run --project $testProject --configuration Release --no-restore -- (Join-Path $projectRoot 'work\diagnostics\test-results.json')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $windowsTests = Join-Path $projectRoot 'tests\GdutNetwork.Windows.Tests\GdutNetwork.Windows.Tests.csproj'
    & $launcher restore $windowsTests --configfile (Join-Path $projectRoot 'NuGet.Offline.Config') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Windows test restore failed.' }
    & $launcher run --project $windowsTests --configuration Release --no-restore -- (Join-Path $projectRoot 'work\diagnostics\windows-test-results.json')
    if ($LASTEXITCODE -ne 0) { throw 'Windows integration tests failed.' }
    $appTests = Join-Path $projectRoot 'tests\GdutNetwork.App.Tests\GdutNetwork.App.Tests.csproj'
    & $launcher restore $appTests --configfile (Join-Path $projectRoot 'NuGet.Offline.Config') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'App test restore failed.' }
    & $launcher run --project $appTests --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'App integration tests failed.' }
}
finally { Pop-Location }
