# 交叉编译 win/mac × amd64/arm64 单二进制（免安装、免签名分发）
# 用法: .\scripts\build.ps1
# 注意: CGO_ENABLED=0 产出纯静态二进制；未来 I2C 若经 cgo 接 D2XX，
#       该变量需按平台调整（见技术栈调查文档 §0）。
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

New-Item -ItemType Directory -Force -Path "dist" | Out-Null

foreach ($os in @("windows", "darwin")) {
  foreach ($arch in @("amd64", "arm64")) {
    $out = "dist/serial-tool-$os-$arch"
    if ($os -eq "windows") { $out += ".exe" }
    Write-Host "==> $out"
    $env:CGO_ENABLED = "0"
    $env:GOOS = $os
    $env:GOARCH = $arch
    go build -trimpath -ldflags="-s -w" -o $out ./cmd/serial-tool
  }
}

Write-Host "构建完成 → dist/"
Get-ChildItem dist | Select-Object Name, Length
