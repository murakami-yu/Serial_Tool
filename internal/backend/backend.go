// Package backend 定义统一硬件后端接口。
//
// UART / I2C / CAN 三类总线后端均实现该接口，向 server 层输出
// 统一的 {Ts, Bytes} 事件流，上层不感知总线类型。
package backend

import (
	"context"
	"time"
)

// PortInfo 设备枚举信息（VID/PID 目前仅串口后端可提供，后续适配器补齐）。
type PortInfo struct {
	Port string `json:"port"`
	VID  string `json:"vid,omitempty"`
	PID  string `json:"pid,omitempty"`
	Desc string `json:"desc,omitempty"`
}

// Config 后端打开参数（JSON tag 与前端字段对齐）。
type Config struct {
	Port     string  `json:"port"`
	Baud     int     `json:"baud"`
	DataBits int     `json:"dataBits"`
	Parity   string  `json:"parity"`   // N / E / O
	StopBits float64 `json:"stopBits"` // 1 / 1.5 / 2
	Flow     bool    `json:"flow"`     // 硬件流控（RTS/CTS）
}

// TimedData 统一数据事件：时间戳 + 原始字节。
type TimedData struct {
	Ts    time.Time
	Bytes []byte
}

// Backend 统一硬件后端接口：三类总线同构，插拔式扩展。
type Backend interface {
	// Scan 枚举当前可用设备。
	Scan(ctx context.Context) ([]PortInfo, error)
	// Open 打开设备并启动读取循环。
	Open(cfg Config) error
	// Write 发送原始字节。
	Write(data []byte) error
	// OnData 返回接收数据事件流（只读通道，由实现方维持）。
	OnData() <-chan TimedData
	// Close 关闭设备，幂等。
	Close() error
}
