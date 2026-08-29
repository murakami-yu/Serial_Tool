#!/usr/bin/env bash
# 交叉编译 win/mac × amd64/arm64 单二进制（免安装、免签名分发）
# 用法: ./scripts/build.sh
# 注意: CGO_ENABLED=0 产出纯静态二进制；未来 I2C 若经 cgo 接 D2XX，
#       该变量需按平台调整（见技术栈调查文档 §0）。
set -euo pipefail
cd "$(dirname "$0")/.."

mkdir -p dist

for os in windows darwin; do
  for arch in amd64 arm64; do
    out="dist/serial-tool-${os}-${arch}"
    [ "${os}" = "windows" ] && out="${out}.exe"
    echo "==> ${out}"
    CGO_ENABLED=0 GOOS="${os}" GOARCH="${arch}" \
      go build -trimpath -ldflags="-s -w" -o "${out}" ./cmd/serial-tool
  done
done

echo "构建完成 → dist/"
ls -lh dist/
