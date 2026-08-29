// Package server 提供 HTTP + WebSocket 服务，将硬件后端暴露给浏览器 UI。
package server

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"log"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/coder/websocket"

	"serial-tool/internal/backend"
	"serial-tool/internal/backend/serial"
	"serial-tool/web"
)

// Server 持有硬件后端与 WebSocket 客户端集合。
type Server struct {
	be      backend.Backend
	clients map[*websocket.Conn]struct{}
	mu      sync.Mutex
}

// Start 启动服务并阻塞运行。
func Start(addr string) error {
	s := &Server{
		be:      serial.New(),
		clients: map[*websocket.Conn]struct{}{},
	}

	// 订阅后端数据流，广播给所有已连接客户端。
	go s.pump()

	mux := http.NewServeMux()
	mux.HandleFunc("/ws", s.handleWS)
	mux.Handle("/", http.FileServerFS(web.FS))

	log.Printf("HTTP 服务已就绪: http://%s", addr)
	return http.ListenAndServe(addr, checkHost(addr, mux))
}

// pump 将后端事件流转换为 rx 消息并广播。
func (s *Server) pump() {
	for d := range s.be.OnData() {
		msg, err := json.Marshal(map[string]any{
			"type": "rx",
			"ts":   d.Ts.Format("15:04:05.000"),
			"data": hex.EncodeToString(d.Bytes),
		})
		if err != nil {
			continue
		}
		s.mu.Lock()
		for c := range s.clients {
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			_ = c.Write(ctx, websocket.MessageText, msg)
			cancel()
		}
		s.mu.Unlock()
	}
}

// checkHost 仅允许本机 Host 访问，防 DNS rebinding 与局域网探测。
func checkHost(addr string, next http.Handler) http.Handler {
	_, port, err := net.SplitHostPort(addr)
	if err != nil {
		port = "8970"
	}
	allowed := []string{"127.0.0.1:" + port, "localhost:" + port, "[::1]:" + port}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		for _, a := range allowed {
			if strings.EqualFold(r.Host, a) {
				next.ServeHTTP(w, r)
				return
			}
		}
		http.Error(w, "forbidden: invalid Host header", http.StatusForbidden)
	})
}
