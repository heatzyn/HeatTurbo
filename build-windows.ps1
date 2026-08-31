$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "HeatTurbo.csproj"
$output = Join-Path $PSScriptRoot "release\win-x64"
$installer = Join-Path $PSScriptRoot "installer\HeatTurboSetup.iss"

if (Test-Path $output) {
  Remove-Item -LiteralPath $output -Recurse -Force
}

dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $output

Write-Host "HeatTurbo publicado em: $output" -ForegroundColor Red

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($iscc) {
  & $iscc.Source $installer
  Write-Host "Instalador criado em: release\installer" -ForegroundColor Red
} else {
  Write-Host "Inno Setup não encontrado. O app portátil está pronto; instale o Inno Setup para gerar HeatTurbo-Setup.exe." -ForegroundColor Yellow
}
