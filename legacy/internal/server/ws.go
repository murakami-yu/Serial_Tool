package server

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"github.com/coder/websocket"

	"serial-tool/internal/backend"
)

// handleWS 升级 HTTP 连接为 WebSocket 并处理客户端消息。
func (s *Server) handleWS(w http.ResponseWriter, r *http.Request) {
	c, err := websocket.Accept(w, r, &websocket.AcceptOptions{
		OriginPatterns: []string{"http://127.0.0.1:*", "http://localhost:*"},
	})
	if err != nil {
		return
	}
	defer c.Close(websocket.StatusInternalError, "connection closed")

	s.mu.Lock()
	s.clients[c] = struct{}{}
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		delete(s.clients, c)
		s.mu.Unlock()
	}()

	for {
		_, data, err := c.Read(context.Background())
		if err != nil {
			return
		}
		s.dispatch(c, data)
	}
}

// dispatch 解析并执行客户端命令。
func (s *Server) dispatch(c *websocket.Conn, data []byte) {
	var req struct {
		Type string          `json:"type"`
		Data json.RawMessage `json:"data"`
	}
	if json.Unmarshal(data, &req) != nil {
		s.send(c, map[string]any{"type": "error", "msg": "消息格式错误"})
		return
	}

	switch req.Type {
	case "scan":
		ports, err := s.be.Scan(context.Background())
		if err != nil {
			s.send(c, map[string]any{"type": "error", "msg": err.Error()})
			return
		}
		s.send(c, map[string]any{"type": "ports", "data": ports})

	case "open":
		var cfg backend.Config
		if json.Unmarshal(req.Data, &cfg) != nil {
			s.send(c, map[string]any{"type": "error", "msg": "参数格式错误"})
			return
		}
		if err := s.be.Open(cfg); err != nil {
			s.send(c, map[string]any{"type": "error", "msg": err.Error()})
			return
		}
		s.send(c, map[string]any{"type": "opened", "data": cfg})

	case "close":
		if err := s.be.Close(); err != nil {
			s.send(c, map[string]any{"type": "error", "msg": err.Error()})
			return
		}
		s.send(c, map[string]any{"type": "closed"})

	case "write":
		var wq struct {
			Mode string `json:"mode"` // hex / ascii
			Data string `json:"data"`
		}
		if json.Unmarshal(req.Data, &wq) != nil {
			s.send(c, map[string]any{"type": "error", "msg": "参数格式错误"})
			return
		}
		raw, err := parsePayload(wq.Mode, wq.Data)
		if err != nil {
			s.send(c, map[string]any{"type": "error", "msg": err.Error()})
			return
		}
		if err := s.be.Write(raw); err != nil {
			s.send(c, map[string]any{"type": "error", "msg": err.Error()})
		}
	}
}

// parsePayload 按模式解析发送内容：hex（容忍空格/换行）或 ascii 原文。
func parsePayload(mode, data string) ([]byte, error) {
	switch mode {
	case "hex":
		clean := strings.NewReplacer(" ", "", "\n", "", "\r", "", "\t", "").Replace(data)
		return hex.DecodeString(clean)
	default:
		return []byte(data), nil
	}
}

// send 向单个客户端发送 JSON 消息（带超时保护）。
func (s *Server) send(c *websocket.Conn, v any) {
	b, err := json.Marshal(v)
	if err != nil {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = c.Write(ctx, websocket.MessageText, b)
}
