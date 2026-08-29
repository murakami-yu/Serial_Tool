#!/usr/bin/env python3
"""帧解析功能试用的零硬件数据源。

应用以 TCP 客户端方式连接本脚本（默认 127.0.0.1:8899），脚本按剧情推送测试帧序列，
覆盖：正常帧 / 半包 / 粘包 / 帧前杂波 / 坏校验 / 长度域居中写帧 / 帧尾扫描。

用法：
  python scripts/feed_frames.py          # 剧情模式：应用连上后自动循环推送
  python scripts/feed_frames.py --echo   # 回显模式：应用发什么回什么，配合"多帧发送"自发自收
  python scripts/feed_frames.py --port 9000

前提：模板编辑器中 4 个内置模板的"校验字节序"选"低字节在前 (Modbus 标准)"。
"""

import argparse
import socket
import sys
import time


def crc16_modbus(data: bytes) -> bytes:
    """CRC16-Modbus，线上低字节在前（Modbus 标准）。"""
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return bytes([crc & 0xFF, (crc >> 8) & 0xFF])


def hx(b: bytes) -> str:
    return b.hex(" ").upper()


def modbus_read_resp(addr=0x01, payload=b"\x12\x34") -> bytes:
    """读响应：01 03 [len] [data...] CRC。"""
    body = bytes([addr, 0x03, len(payload)]) + payload
    return body + crc16_modbus(body)


def modbus_write(addr=0x01, data=b"\xDE\xAD\xBE\xEF") -> bytes:
    """写请求：01 10 [addr2] [qty2] [byteCount] [data...] CRC —— 长度域在帧中间。"""
    body = bytes([addr, 0x10, 0x00, 0x00, 0x00, len(data), len(data)]) + data
    return body + crc16_modbus(body)


def em_frame(src=0x01, dst=0x02, cmd=0x81, data=b"\x01\x02\xED\x03") -> bytes:
    """EM 帧：EA [src] [dst] [cmd] [data...] CRC ED —— 数据域嵌 0xED，考验逐位置帧尾校验。"""
    body = bytes([0xEA, src, dst, cmd]) + data
    return body + crc16_modbus(body) + b"\xED"


def scenarios():
    """剧情列表：(说明, 分段发送序列)。段内一次发完（粘包），段间停顿（半包）。"""
    good = modbus_read_resp()
    return [
        ("① 正常帧（读响应，长度域）", [good]),
        ("② 半包：同一帧拆 3 段到达", [good[:1], good[1:4], good[4:]]),
        ("③ 粘包：两帧一次到达", [modbus_read_resp(payload=b"\x00\x01") + modbus_read_resp(payload=b"\xA5\x5A")]),
        ("④ 帧前杂波（无帧头字节，应被静默丢弃）", [b"\xFF\x00\x7E" + modbus_read_resp(payload=b"\x11\x22")]),
        ("⑤ 坏校验帧（应显示 ✗ 与期望/实际 CRC）", [modbus_read_resp()[:-2] + b"\x00\x00"]),
        ("⑥ 写请求：长度域在帧中间（与读模板同帧头，靠结构+校验仲裁）", [modbus_write()]),
        ("⑦ EM 帧尾扫描：数据域内嵌帧尾字节 0xED", [em_frame()]),
        ("⑧ 无模板帧头杂波（RX 计数增加、接收区无输出）", [b"\x99\x98\x97\x96\x95\x94"]),
    ]


def run_scenario(sock, segs, gap=0.4):
    for seg in segs:
        sock.sendall(seg)
        time.sleep(gap)


def serve_story(host, port):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((host, port))
    srv.listen(1)
    print(f"[剧情模式] 等待应用连接 {host}:{port} …（应用：连接方式=TCP，地址 {host}，端口 {port}，打开）")
    while True:
        conn, addr = srv.accept()
        print(f"应用已连入: {addr}，2 秒后开始推送剧情（Ctrl+C 退出）")
        time.sleep(2)
        try:
            while True:
                for title, segs in scenarios():
                    print(f"\n== {title}")
                    for seg in segs:
                        print(f"   → {hx(seg)}")
                    run_scenario(conn, segs)
                    time.sleep(1.6)
                print("\n—— 一轮结束，4 秒后循环 ————")
                time.sleep(4)
        except (ConnectionResetError, BrokenPipeError):
            print("应用断开，等待重连…")
        finally:
            conn.close()


def serve_echo(host, port):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((host, port))
    srv.listen(1)
    print(f"[回显模式] 等待应用连接 {host}:{port} …（连接后用主界面或多帧面板发送，帧会被原样回传）")
    while True:
        conn, addr = srv.accept()
        print(f"应用已连入: {addr}，回显中…")
        try:
            while True:
                data = conn.recv(4096)
                if not data:
                    break
                print(f"   ← {hx(data)}  → 原样回传")
                conn.sendall(data)
        except (ConnectionResetError, BrokenPipeError):
            print("应用断开，等待重连…")
        finally:
            conn.close()


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8899)
    ap.add_argument("--echo", action="store_true", help="回显模式")
    args = ap.parse_args()
    try:
        (serve_echo if args.echo else serve_story)(args.host, args.port)
    except KeyboardInterrupt:
        sys.exit(0)
