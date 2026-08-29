// Package web 内嵌前端静态资源，编译进单二进制实现免安装分发。
package web

import "embed"

// FS 前端资源文件系统（index.html / app.js / style.css）。
// 显式列出文件而非目录模式：go:embed 模式禁止含 '.' 路径元素。
//
//go:embed *.html *.js *.css
var FS embed.FS
