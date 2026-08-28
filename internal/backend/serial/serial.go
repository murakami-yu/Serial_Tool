// Package serial 实现 UART / RS232 / RS485 后端（V1）。
//
// 三种电气形态对软件完全透明：同一 USB 转串口芯片 + 串口配置，
// 差别仅在电平/接线（TTL / ±12V / A-B 差分），由硬件转接层承担。
package serial

import (
	"context"
	"errors"
	"time"

	"go.bug.st/serial"

	"serial-tool/internal/backend"
)

// Backend 实现 backend.Backend 接口的串口后端。
type Backend struct {
	port serial.Port
	ch   chan backend.TimedData
}

// New 创建串口后端实例。
func New() *Backend {
	return &Backend{ch: make(chan backend.TimedData, 1024)}
}

// Scan 枚举系统可用串口（COMx / /dev/cu.*）。
func (b *Backend) Scan(_ context.Context) ([]backend.PortInfo, error) {
	ports, err := serial.GetPortsList()
	if err != nil {
		return nil, err
	}
	list := make([]backend.PortInfo, 0, len(ports))
	for _, p := range ports {
		list = append(list, backend.PortInfo{Port: p})
	}
	return list, nil
}

// Open 打开串口并启动后台读取循环。
func (b *Backend) Open(cfg backend.Config) error {
	// 重复打开前先关闭旧端口，保证幂等。
	if err := b.Close(); err != nil {
		return err
	}

	port, err := serial.Open(cfg.Port, &serial.Mode{
		BaudRate: cfg.Baud,
		DataBits: cfg.DataBits,
		Parity:   toParity(cfg.Parity),
		StopBits: toStopBits(cfg.StopBits),
	})
	if err != nil {
		return err
	}
	if cfg.Flow {
		if err := port.SetRTS(true); err != nil {
			port.Close()
			return err
		}
	}
	b.port = port
	go b.readLoop()
	return nil
}

// readLoop 持续读取并投递到事件通道；端口关闭时退出。
func (b *Backend) readLoop() {
	buf := make([]byte, 4096)
	for {
		n, err := b.port.Read(buf)
		if n > 0 {
			data := make([]byte, n)
			copy(data, buf[:n])
			b.ch <- backend.TimedData{Ts: time.Now(), Bytes: data}
		}
		if err != nil {
			return // 端口关闭或设备拔出，结束读取循环
		}
	}
}

// Write 发送原始字节。
func (b *Backend) Write(data []byte) error {
	if b.port == nil {
		return errors.New("端口未打开")
	}
	_, err := b.port.Write(data)
	return err
}

// OnData 返回接收事件流。
func (b *Backend) OnData() <-chan backend.TimedData { return b.ch }

// Close 关闭串口，幂等。
func (b *Backend) Close() error {
	if b.port == nil {
		return nil
	}
	err := b.port.Close()
	b.port = nil
	return err
}

func toParity(s string) serial.Parity {
	switch s {
	case "E":
		return serial.EvenParity
	case "O":
		return serial.OddParity
	default:
		return serial.NoParity
	}
}

func toStopBits(n float64) serial.StopBits {
	switch n {
	case 2:
		return serial.TwoStopBits
	case 1.5:
		return serial.OnePointFiveStopBits
	default:
		return serial.OneStopBit
	}
}
