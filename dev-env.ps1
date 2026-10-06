$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot '.tools/http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $PSScriptRoot '.tools/plugins-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:TEMP = Join-Path $PSScriptRoot '.tools/tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
