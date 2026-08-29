$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "HeatTurbo.csproj"
$output = Join-Path $PSScriptRoot "release\win-x64"

dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $output

Write-Host "HeatTurbo publicado em: $output" -ForegroundColor Red
