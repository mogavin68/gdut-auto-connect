$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$app = Join-Path $projectRoot 'src\GdutNetwork.App\bin\Release\net10.0-windows\GdutNetwork.dll'
if (-not (Test-Path -LiteralPath $app)) { throw 'Run build.ps1 first.' }
& (Join-Path $projectRoot 'dotnet.ps1') $app @args
exit $LASTEXITCODE
