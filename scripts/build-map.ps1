param(
  [string]$Dotnet = 'dotnet',
  [string]$Npm = 'npm',
  [switch]$SkipWeb,
  [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root '运行版' }
if (-not $SkipWeb) {
  Push-Location (Join-Path $root 'web')
  try {
    & $Npm ci
    if ($LASTEXITCODE -ne 0) { throw '前端依赖安装失败' }
    & $Npm run generate
    if ($LASTEXITCODE -ne 0) { throw '前端构建失败' }
  } finally { Pop-Location }
}
& $Dotnet publish (Join-Path $root 'server/StreetEcoTool.csproj') -c Release -r win-x86 -o (Join-Path $root 'release') -p:PublishAot=true -p:SelfContained=true
if ($LASTEXITCODE -ne 0) { throw 'Windows 程序构建失败' }
New-Item -ItemType Directory -Force $Output | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'release/StreetEcoTool.exe') -Destination (Join-Path $Output 'StreetEcoTool-win-x86.exe') -Force
New-Item -ItemType Directory -Force (Join-Path $Output 'wwwroot') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'web/.output/public') -Force | Copy-Item -Destination (Join-Path $Output 'wwwroot') -Recurse -Force
foreach ($name in @('map-providers.local.json', 'tiles')) {
  $source = Join-Path $root $name
  if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $Output -Recurse -Force }
}
Copy-Item -LiteralPath (Join-Path $root 'docs/地图版使用说明.txt') -Destination $Output -Force
if (-not (Test-Path -LiteralPath (Join-Path $Output 'appsettings.json'))) {
  [IO.File]::WriteAllText((Join-Path $Output 'appsettings.json'), '{"port":5181,"listen":["127.0.0.1"]}')
}
[IO.File]::WriteAllText((Join-Path $Output '启动工具.cmd'), "@echo off`r`ncd /d `"%~dp0`"`r`nStreetEcoTool-win-x86.exe`r`nif errorlevel 1 pause`r`n", [Text.Encoding]::ASCII)
Write-Output "已生成：$Output"
