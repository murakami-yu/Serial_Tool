// Serial Tool 前端逻辑：WebSocket 通信 + 收发面板。
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);

  let ws = null;
  let connected = false;

  const els = {
    connStatus: $("connStatus"),
    connText: $("connText"),
    portSelect: $("portSelect"),
    btnScan: $("btnScan"),
    baud: $("baudSelect"),
    dataBits: $("dataBitsSelect"),
    stopBits: $("stopBitsSelect"),
    parity: $("paritySelect"),
    btnOpen: $("btnOpen"),
    rxOutput: $("rxOutput"),
    rxMode: $("rxMode"),
    rxTime: $("rxTime"),
    btnClear: $("btnClear"),
    txInput: $("txInput"),
    txMode: $("txMode"),
    btnSend: $("btnSend"),
    statusBar: $("statusBar"),
  };

  // ---------- 连接管理 ----------

  function connect() {
    const proto = location.protocol === "https:" ? "wss" : "ws";
    ws = new WebSocket(`${proto}://${location.host}/ws`);

    ws.onopen = () => {
      setConn(true);
      send({ type: "scan" });
    };
    ws.onclose = () => {
      setConn(false);
      setStatus("与后端连接断开，尝试重连中...");
      setTimeout(connect, 2000);
    };
    ws.onerror = () => ws && ws.close();
    ws.onmessage = (ev) => handleMessage(JSON.parse(ev.data));
  }

  function setConn(on) {
    connected = on;
    els.connStatus.classList.toggle("on", on);
    els.connText.textContent = on ? "已连接" : "未连接";
    if (on) setStatus("就绪");
  }

  function setStatus(text) {
    els.statusBar.textContent = text;
  }

  function send(obj) {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify(obj));
    }
  }

  // ---------- 消息处理 ----------

  function handleMessage(msg) {
    switch (msg.type) {
      case "ports":
        fillPorts(msg.data || []);
        break;
      case "rx":
        appendRx(msg.ts, msg.data);
        break;
      case "opened":
        onOpened(true);
        setStatus(`已打开 ${msg.data.port} @ ${msg.data.baud}`);
        break;
      case "closed":
        onOpened(false);
        setStatus("端口已关闭");
        break;
      case "error":
        setStatus(`错误: ${msg.msg}`);
        break;
    }
  }

  function fillPorts(ports) {
    els.portSelect.innerHTML = "";
    if (!ports.length) {
      els.portSelect.innerHTML = '<option value="">（未发现串口）</option>';
      return;
    }
    for (const p of ports) {
      const opt = document.createElement("option");
      opt.value = p.port;
      opt.textContent = p.desc ? `${p.port} (${p.desc})` : p.port;
      els.portSelect.appendChild(opt);
    }
  }

  // ---------- 打开 / 关闭 ----------

  function onOpened(isOpen) {
    els.btnOpen.textContent = isOpen ? "关闭" : "打开";
    els.btnOpen.classList.toggle("danger", isOpen);
    els.portSelect.disabled = isOpen;
    els.btnScan.disabled = isOpen;
  }

  function toggleOpen() {
    if (els.btnOpen.textContent === "打开") {
      const cfg = {
        port: els.portSelect.value,
        baud: parseInt(els.baud.value, 10),
        dataBits: parseInt(els.dataBits.value, 10),
        stopBits: parseFloat(els.stopBits.value),
        parity: els.parity.value,
        flow: false,
      };
      if (!cfg.port) {
        setStatus("请先选择串口");
        return;
      }
      send({ type: "open", data: cfg });
    } else {
      send({ type: "close" });
    }
  }

  // ---------- 接收显示 ----------

  function appendRx(ts, hexStr) {
    const bytes = hexToBytes(hexStr);
    const isHex = els.rxMode.value === "hex";

    const timePrefix = els.rxTime.checked ? `[${ts}] ` : "";
    const body = isHex ? hexStr.toUpperCase() : bytesToAscii(bytes);
    const line = `${timePrefix}${body}`;

    els.rxOutput.value += line + "\n";
    els.rxOutput.scrollTop = els.rxOutput.scrollHeight;
  }

  function hexToBytes(hexStr) {
    const out = [];
    for (let i = 0; i + 1 < hexStr.length; i += 2) {
      out.push(parseInt(hexStr.substr(i, 2), 16));
    }
    return out;
  }

  function bytesToAscii(bytes) {
    let s = "";
    for (const b of bytes) {
      s += b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : ".";
    }
    return s;
  }

  // ---------- 发送 ----------

  function sendPayload() {
    if (!connected) {
      setStatus("未连接后端");
      return;
    }
    if (els.btnOpen.textContent !== "关闭") {
      setStatus("请先打开串口");
      return;
    }
    const data = els.txInput.value.trim();
    if (!data) return;
    send({ type: "write", data: { mode: els.txMode.value, data } });
  }

  // ---------- 事件绑定 ----------

  els.btnScan.addEventListener("click", () => send({ type: "scan" }));
  els.btnOpen.addEventListener("click", toggleOpen);
  els.btnClear.addEventListener("click", () => (els.rxOutput.value = ""));
  els.btnSend.addEventListener("click", sendPayload);
  els.txInput.addEventListener("keydown", (ev) => {
    if (ev.key === "Enter" && !ev.shiftKey) {
      ev.preventDefault();
      sendPayload();
    }
  });

  connect();
})();
