// Serial Tool 通用串口调试工具入口。
//
// 启动后监听 127.0.0.1（仅本机），并尝试自动打开浏览器。
// 关闭/重启只需 Ctrl+C，无安装、无签名、无服务注册。
package main

import (
	"flag"
	"log"
	"os/exec"
	"runtime"
	"time"

	"serial-tool/internal/server"
)

func main() {
	addr := flag.String("addr", "127.0.0.1:8970", "监听地址（仅本机访问）")
	noBrowser := flag.Bool("no-browser", false, "启动时不自动打开浏览器")
	flag.Parse()

	url := "http://" + *addr
	log.Printf("Serial Tool 启动中... 访问 %s", url)

	// 服务就绪后自动打开浏览器（失败静默，不阻塞启动）。
	if !*noBrowser {
		time.AfterFunc(300*time.Millisecond, func() { openBrowser(url) })
	}

	if err := server.Start(*addr); err != nil {
		log.Fatalf("启动失败: %v", err)
	}
}

// openBrowser 尝试用系统默认浏览器打开工具页面，失败静默忽略
// （无头环境 / 远程终端下不阻塞启动）。
func openBrowser(url string) {
	var cmd *exec.Cmd
	switch runtime.GOOS {
	case "windows":
		cmd = exec.Command("rundll32", "url.dll,FileProtocolHandler", url)
	case "darwin":
		cmd = exec.Command("open", url)
	default:
		cmd = exec.Command("xdg-open", url)
	}
	_ = cmd.Start()
}

