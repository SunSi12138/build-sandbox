param([string]$Pnpm = 'pnpm', [switch]$SkipBuild)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskRelease=Join-Path $taskRoot 'release'
$taskApp=Join-Path $taskRoot '运行版'
if(-not $SkipBuild){
  Push-Location (Join-Path $taskRoot 'web')
  try {
    & $Pnpm install --frozen-lockfile
    if($LASTEXITCODE -ne 0){throw '前端依赖安装失败'}
    & $Pnpm generate
    if($LASTEXITCODE -ne 0){throw '前端构建失败'}
  } finally { Pop-Location }
  & dotnet publish (Join-Path $taskRoot 'server/StreetEcoTool.csproj') -c Release -r win-x64 -p:PublishAot=true -o $taskRelease
  if($LASTEXITCODE -ne 0){throw 'Native AOT 构建失败'}
}
New-Item -ItemType Directory -Path $taskApp -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRelease 'CommunityGIS.exe') -Destination $taskApp -Force
$taskWeb=Join-Path $taskApp 'wwwroot'
New-Item -ItemType Directory -Path $taskWeb -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'web/.output/public') -Force | Copy-Item -Destination $taskWeb -Recurse -Force
if(-not(Test-Path -LiteralPath (Join-Path $taskApp 'tiles/jlyh'))){Copy-Item -LiteralPath (Join-Path $taskRoot 'tiles') -Destination $taskApp -Recurse}
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/开始使用.txt') -Destination $taskApp -Force
$taskLauncher="@echo off`r`ncd /d `"%~dp0`"`r`nCommunityGIS.exe`r`nif errorlevel 1 pause`r`n"
[IO.File]::WriteAllText((Join-Path $taskApp '启动社区管理.cmd'),$taskLauncher,[Text.Encoding]::ASCII)
if(-not(Test-Path -LiteralPath (Join-Path $taskApp 'appsettings.json'))){[IO.File]::WriteAllText((Join-Path $taskApp 'appsettings.json'),'{"port":5180,"listen":["127.0.0.1"]}')}
Write-Output "已生成：$taskApp"
