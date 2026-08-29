# 自包含单文件发布：免安装、零运行时依赖，双击即用
# 产物: dist/SerialTool.App.exe (~70MB)
# 注意: WPF 不支持 PublishTrimmed（会启动失败），勿开启裁剪
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

dotnet publish src/SerialTool.App `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -o dist

Write-Host "`n发布完成 → dist/"
Get-ChildItem dist | Select-Object Name, @{N = "SizeMB"; E = { [math]::Round($_.Length / 1MB, 1) } }
