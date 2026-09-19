# 自包含单文件发布：免安装、零运行时依赖，双击即用
# 产物: dist/v<版本号>/SerialTool_v<版本号>_win-x64.exe (~70MB)
# 版本目录制（2026-09-19 用户要求）：每次发布新建版本目录，不覆盖历史版本；
# 同版本号重复发布自动加 _yyyyMMdd_HHmmss 后缀。发新版本 = 先升 csproj <Version>。
# dist 根部的 SerialTool.App.exe/Config/Logs/RELEASE_NOTES.md 为版本目录制之前的遗留，本脚本不再触碰。
# 注意: WPF 不支持 PublishTrimmed（会启动失败），勿开启裁剪
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

# 版本号唯一来源 = csproj <Version>（exe 文件版本号随之一致）
$csprojPath = "src/SerialTool.App/SerialTool.App.csproj"
$version = ([xml](Get-Content $csprojPath -Encoding UTF8)).Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw "无法从 $csprojPath 读取 <Version>" }
$version = $version.Trim()

$dest = "dist/v$version"
if (Test-Path $dest) {
    $dest = "dist/v${version}_$(Get-Date -Format yyyyMMdd_HHmmss)"
    Write-Host "dist/v$version 已存在 → 改发布到 $dest（不覆盖历史版本）" -ForegroundColor Yellow
}

dotnet publish src/SerialTool.App `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $dest
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }

# 版本化命名（对齐 RELEASE_NOTES 惯例：SerialTool_v1.2.0_win-x64.exe）
$rawExe = Join-Path $dest "SerialTool.App.exe"
$namedExe = Join-Path $dest "SerialTool_v${version}_win-x64.exe"
if (Test-Path $rawExe) { Move-Item $rawExe $namedExe -Force }

# 最新版本指针（脚本/人工快速定位当前版本目录）
Set-Content -Path "dist/LATEST.txt" -Value $dest -Encoding UTF8

Write-Host "`n发布完成 → $dest"
Get-ChildItem $dest | Select-Object Name, @{N = "SizeMB"; E = { [math]::Round($_.Length / 1MB, 1) } }
