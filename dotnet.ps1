$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sdkDirectory = Join-Path $projectRoot 'work\dotnet-sdk'
$sdkExecutable = Join-Path $sdkDirectory 'dotnet.exe'
if (-not (Test-Path -LiteralPath $sdkExecutable)) {
    throw 'Project-local .NET SDK is missing: work\dotnet-sdk\dotnet.exe'
}

$sessionSettings = @{
    DOTNET_ROOT = $sdkDirectory
    DOTNET_ROOT_X64 = $sdkDirectory
    DOTNET_CLI_HOME = (Join-Path $projectRoot 'work\dotnet-home')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    NUGET_PACKAGES = (Join-Path $projectRoot 'work\nuget-packages')
}
$previousSettings = @{}
$sdkExitCode = 0
try {
    foreach ($settingName in $sessionSettings.Keys) {
        $previousSettings[$settingName] = [Environment]::GetEnvironmentVariable($settingName, 'Process')
        [Environment]::SetEnvironmentVariable($settingName, $sessionSettings[$settingName], 'Process')
    }
    & $sdkExecutable @args
    $sdkExitCode = $LASTEXITCODE
}
finally {
    foreach ($settingName in $previousSettings.Keys) {
        [Environment]::SetEnvironmentVariable($settingName, $previousSettings[$settingName], 'Process')
    }
}
exit $sdkExitCode
