const API_URL = "http://127.0.0.1:8765/stats";
const $ = (id) => document.getElementById(id);

const HISTORY_LIMIT = 48;
const historyStore = {
  cpuTemp: [],
  cpuLoad: [],
  gpuTemp: [],
  gpuLoad: [],
  netUp: [],
  netDown: []
};

let lastActiveNetworkName = "--";

function updateScale() {
  const scale = Math.min(window.innerWidth / 1440, window.innerHeight / 2560);
  document.documentElement.style.setProperty("--scale", scale);
}

window.addEventListener("resize", updateScale);
updateScale();

function fmt(value, digits = 0) {
  if (value === null || value === undefined || Number.isNaN(Number(value))) return "--";
  return Number(value).toFixed(digits);
}

function setText(id, text) {
  const el = $(id);
  if (el) el.textContent = text;
}

function tempClass(value, type = "normal") {
  if (value === null || value === undefined) return "normal";
  if (type === "cpu") {
    if (value >= 85) return "hot";
    if (value >= 75) return "warm";
    return "normal";
  }
  if (type === "gpuHotspot") {
    if (value >= 95) return "hot";
    if (value >= 85) return "warm";
    return "normal";
  }
  if (type === "gpuCore") {
    if (value >= 80) return "hot";
    if (value >= 70) return "warm";
    return "normal";
  }
  if (value >= 70) return "hot";
  if (value >= 55) return "warm";
  return "normal";
}

function statusTextFromTemp(temp) {
  const cls = tempClass(temp);
  if (cls === "hot") return "CRITICAL";
  if (cls === "warm") return "ELEVATED";
  return "NOMINAL";
}

function setTemp(id, value, type = "normal") {
  const el = $(id);
  if (!el) return;
  el.textContent = fmt(value);
  el.className = `temp-value ${tempClass(value, type)}`;
}

function setMetricClass(id, value, type = "normal") {
  const el = $(id);
  if (el) el.className = tempClass(value, type);
}

function setBar(id, percent) {
  const el = $(id);
  if (!el) return;
  const safe = Math.max(0, Math.min(100, Number(percent || 0)));
  el.style.width = `${safe}%`;
}

function setRingProgress(id, percent) {
  const el = $(id);
  if (!el) return;
  const safe = Math.max(0, Math.min(100, Number(percent || 0)));
  el.style.setProperty("--progress", safe);
}

function formatThroughput(kb) {
  if (kb === null || kb === undefined) return "--";
  const value = Number(kb);
  if (value >= 1024) return `${(value / 1024).toFixed(2)} MB/s`;
  return `${value.toFixed(0)} KB/s`;
}

function pushHistory(key, value) {
  historyStore[key].push(Number(value || 0));
  if (historyStore[key].length > HISTORY_LIMIT) historyStore[key].shift();
}

function drawGrid(ctx, width, height) {
  ctx.strokeStyle = "rgba(91, 240, 255, 0.12)";
  ctx.lineWidth = 1;
  for (let i = 1; i < 4; i++) {
    const y = (height / 4) * i;
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(width, y);
    ctx.stroke();
  }
  for (let i = 1; i < 8; i++) {
    const x = (width / 8) * i;
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
    ctx.stroke();
  }
}

function drawSingleSeriesChart(canvasId, values, color, options = {}) {
  const canvas = $(canvasId);
  if (!canvas) return;

  const ctx = canvas.getContext("2d");
  const { width, height } = canvas;
  ctx.clearRect(0, 0, width, height);
  drawGrid(ctx, width, height);

  const series = values
    .filter(v => v !== null && v !== undefined && !Number.isNaN(Number(v)))
    .map(Number);
  if (series.length < 2) return;

  let minVal = options.min;
  let maxVal = options.max;
  if (minVal === undefined || maxVal === undefined) {
    minVal = Math.min(...series);
    maxVal = Math.max(...series);
    if (maxVal - minVal < 1) {
      maxVal += 0.5;
      minVal -= 0.5;
    }
  }

  const range = Math.max(maxVal - minVal, 1);
  const offset = HISTORY_LIMIT - series.length;

  ctx.beginPath();
  series.forEach((val, index) => {
    const x = ((index + offset) / (HISTORY_LIMIT - 1)) * width;
    const y = height - ((val - minVal) / range) * (height - 14) - 7;
    if (index === 0) ctx.moveTo(x, y);
    else ctx.lineTo(x, y);
  });

  ctx.strokeStyle = color;
  ctx.lineWidth = 3;
  ctx.shadowBlur = 14;
  ctx.shadowColor = color;
  ctx.stroke();
  ctx.shadowBlur = 0;
}

function drawDynamicPercentChart(canvasId, values, color) {
  const series = values.filter(v => v !== null && v !== undefined && !Number.isNaN(Number(v))).map(Number);
  if (series.length < 2) {
    drawSingleSeriesChart(canvasId, values, color, { min: 0, max: 100 });
    return;
  }

  let minVal = Math.max(0, Math.floor(Math.min(...series)));
  let maxVal = Math.min(100, Math.ceil(Math.max(...series)));
  if (maxVal - minVal < 5) {
    const mid = (maxVal + minVal) / 2;
    minVal = Math.max(0, Math.floor(mid - 2.5));
    maxVal = Math.min(100, Math.ceil(mid + 2.5));
  }
  drawSingleSeriesChart(canvasId, values, color, { min: minVal, max: maxVal });
}

function drawDualSeriesChart(canvasId, primaryValues, primaryColor, secondaryValues, secondaryColor) {
  const canvas = $(canvasId);
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const { width, height } = canvas;
  ctx.clearRect(0, 0, width, height);
  drawGrid(ctx, width, height);

  function draw(values, color) {
    const series = values.filter(v => v !== null && v !== undefined && !Number.isNaN(Number(v))).map(Number);
    if (series.length < 2) return;
    let minVal = Math.min(...series);
    let maxVal = Math.max(...series);
    if (maxVal - minVal < 1) {
      maxVal += 0.5;
      minVal -= 0.5;
    }
    const range = Math.max(maxVal - minVal, 1);
    const offset = HISTORY_LIMIT - series.length;

    ctx.beginPath();
    series.forEach((val, index) => {
      const x = ((index + offset) / (HISTORY_LIMIT - 1)) * width;
      const y = height - ((val - minVal) / range) * (height - 14) - 7;
      if (index === 0) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
    });

    ctx.strokeStyle = color;
    ctx.lineWidth = 2.8;
    ctx.shadowBlur = 12;
    ctx.shadowColor = color;
    ctx.stroke();
    ctx.shadowBlur = 0;
  }

  draw(primaryValues, primaryColor);
  draw(secondaryValues, secondaryColor);
}

function updateTempScaleLabel(id, values) {
  const series = values.filter(v => v !== null && v !== undefined && !Number.isNaN(Number(v))).map(Number);
  setText(id, series.length ? `${Math.min(...series).toFixed(0)} deg C ~ ${Math.max(...series).toFixed(0)} deg C` : "-- deg C");
}

function updatePercentScaleLabel(id, values) {
  const series = values.filter(v => v !== null && v !== undefined && !Number.isNaN(Number(v))).map(Number);
  setText(id, series.length ? `${Math.min(...series).toFixed(0)} - ${Math.max(...series).toFixed(0)}%` : "--%");
}

function renderBoardTemps(temps) {
  const container = $("boardTemps");
  if (!container) return;
  container.innerHTML = "";
  for (const item of temps || []) {
    const cls = tempClass(item.value);
    container.innerHTML += `
      <div class="board-item">
        <div class="board-name">${item.name}</div>
        <div class="board-value ${cls}">${fmt(item.value)} deg C</div>
      </div>
    `;
  }
}

function renderBoardFans(fans) {
  const container = $("boardFans");
  if (!container) return;
  container.innerHTML = "";
  const visibleFans = (fans || []).filter(item => item.name !== "Unknown Board Fan");
  if (visibleFans.length === 0) {
    container.innerHTML = `
      <div class="fan-item">
        <div class="fan-name">NO BOARD FAN SIGNAL</div>
        <div class="fan-rpm">--</div>
      </div>
    `;
    return;
  }
  for (const item of visibleFans) {
    container.innerHTML += `
      <div class="fan-item">
        <div class="fan-name">${item.name}</div>
        <div class="fan-rpm">${fmt(item.rpm)} RPM</div>
      </div>
    `;
  }
}

function renderStorage(drives) {
  const container = $("storageList");
  if (!container) return;
  container.innerHTML = "";

  const order = { "(C:)": 0, "(E:)": 1, "(F:)": 2 };
  const sortedDrives = [...(drives || [])].sort((a, b) => (order[a.name] ?? 99) - (order[b.name] ?? 99));

  sortedDrives.forEach((drive, index) => {
    const cls = tempClass(drive.temp);
    const used = Number(drive.used || 0);
    container.innerHTML += `
      <div class="drive-card">
        <div class="drive-slot-row">
          <div class="drive-slot">MODULE SLOT ${String(index + 1).padStart(2, "0")}</div>
          <div class="drive-life">LIFE ${fmt(drive.life)}%</div>
        </div>

        <div class="drive-name">${drive.name}</div>
        <div class="drive-raw">${drive.rawName}</div>

        <div class="drive-temp ${cls}">${fmt(drive.temp)} deg C</div>
        <div class="drive-status">${statusTextFromTemp(drive.temp)}</div>

        <div class="bar-zone">
          <div class="bar-head">
            <span>USED SPACE</span>
            <span>${fmt(drive.used, 1)}%</span>
          </div>
          <div class="hud-bar">
            <div class="hud-bar-fill" style="width:${Math.min(100, used)}%"></div>
          </div>
        </div>

        <div class="drive-capacity-line">
          <span>FREE ${fmt(drive.freeGb, 0)}G</span>
          <span>TOTAL ${fmt(drive.totalGb, 0)}G</span>
          <span>USED ${fmt(drive.used, 1)}%</span>
        </div>
      </div>
    `;
  });
}

function render(data) {
  $("serverStatus").textContent = "ONLINE";
  $("serverStatus").className = "status-pill online";

  const updated = new Date(data.updatedAt);
  setText("updatedAt", `SYNC ${updated.toLocaleTimeString()}`);

  const cpuLoad = Number(data.cpu.load || 0);
  const gpuLoad = Number(data.gpu.load || 0);
  const ramLoad = Number(data.ram.load || 0);
  const avgLoad = Math.round((cpuLoad + gpuLoad + ramLoad) / 3);
  const network = data.activeNetwork || {};

  if (network.name && network.name !== "--") lastActiveNetworkName = network.name;
  const activeNetworkName = network.name || lastActiveNetworkName || "--";
  const totalFlowKb = Number(network.uploadKb || 0) + Number(network.downloadKb || 0);

  setText("hubAvg", `${avgLoad}%`);
  setText("hubCpuLoad", `${fmt(cpuLoad, 1)}%`);
  setText("hubGpuLoad", `${fmt(gpuLoad, 1)}%`);
  setText("hubRamLoad", `${fmt(ramLoad, 1)}%`);
  setText("hubNetFlow", formatThroughput(totalFlowKb));
  setText("hubVram", `${fmt(data.gpu.vramUsedGb, 2)}GB`);
  setText("hubNetwork", activeNetworkName);

  setRingProgress("ringCpu", cpuLoad);
  setRingProgress("ringGpu", gpuLoad);
  setRingProgress("ringRam", ramLoad);

  setTemp("cpuTemp", data.cpu.temp, "cpu");
  setText("cpuLoad", `${fmt(cpuLoad, 1)}%`);
  setText("cpuPower", `${fmt(data.cpu.power, 1)}W`);
  setText("cpuClock", `${fmt(data.cpu.clock / 1000, 2)}GHz`);
  setText("cpuCcd", `${fmt(data.cpu.ccd, 1)} deg C`);
  setMetricClass("cpuCcd", data.cpu.ccd, "cpu");

  setTemp("gpuTemp", data.gpu.temp, "gpuCore");
  setText("gpuHotspot", `${fmt(data.gpu.hotspot, 1)} deg C`);
  setMetricClass("gpuHotspot", data.gpu.hotspot, "gpuHotspot");
  setText("gpuMemTemp", `${fmt(data.gpu.memoryTemp)} deg C`);
  setMetricClass("gpuMemTemp", data.gpu.memoryTemp);
  setText("gpuLoad", `${fmt(gpuLoad, 1)}%`);
  setText("gpuPower", `${fmt(data.gpu.power, 1)}W`);
  setText("vramText", `${fmt(data.gpu.vramUsedGb, 2)} / ${fmt(data.gpu.vramTotalGb, 2)} GB`);
  setBar("vramBar", (data.gpu.vramUsedGb / data.gpu.vramTotalGb) * 100);

  const fan1 = Number(data.gpu.fan1Rpm || 0);
  const fan2 = Number(data.gpu.fan2Rpm || 0);
  setText("gpuFan", fan1 === 0 && fan2 === 0 ? "GPU FANS: PASSIVE MODE" : `GPU FANS: ${fmt(fan1)} RPM / ${fmt(fan2)} RPM`);

  setText("ramUsed", fmt(data.ram.usedGb, 1));
  setText("ramTotal", fmt(data.ram.totalGb, 1));
  setText("ramAvailable", `${fmt(data.ram.availableGb, 1)}GB`);
  setText("ramText", `${fmt(ramLoad, 1)}%`);
  setBar("ramBar", ramLoad);
  setText("dimm0", `${fmt(data.ram.dimm0Temp, 1)} deg C`);
  setMetricClass("dimm0", data.ram.dimm0Temp);
  setText("dimm2", `${fmt(data.ram.dimm2Temp, 1)} deg C`);
  setMetricClass("dimm2", data.ram.dimm2Temp);

  setText("networkName", activeNetworkName);
  setText("netUp", formatThroughput(network.uploadKb));
  setText("netDown", formatThroughput(network.downloadKb));

  renderBoardTemps(data.motherboard.temps);
  renderBoardFans(data.motherboard.fans);
  renderStorage(data.storage);

  pushHistory("cpuTemp", data.cpu.temp);
  pushHistory("cpuLoad", cpuLoad);
  pushHistory("gpuTemp", data.gpu.temp);
  pushHistory("gpuLoad", gpuLoad);
  pushHistory("netUp", network.uploadKb || 0);
  pushHistory("netDown", network.downloadKb || 0);

  updateTempScaleLabel("cpuTempScale", historyStore.cpuTemp);
  updateTempScaleLabel("gpuTempScale", historyStore.gpuTemp);
  updatePercentScaleLabel("cpuLoadScale", historyStore.cpuLoad);
  updatePercentScaleLabel("gpuLoadScale", historyStore.gpuLoad);

  drawSingleSeriesChart("cpuTempChart", historyStore.cpuTemp, "#5bf0ff");
  drawDynamicPercentChart("cpuLoadChart", historyStore.cpuLoad, "#ffb02e");
  drawSingleSeriesChart("gpuTempChart", historyStore.gpuTemp, "#ff3864");
  drawDynamicPercentChart("gpuLoadChart", historyStore.gpuLoad, "#c7ff45");
  drawDualSeriesChart("netChart", historyStore.netUp, "#5bf0ff", historyStore.netDown, "#ff3864");
}

async function fetchStats() {
  try {
    const response = await fetch(API_URL, { cache: "no-store" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    render(await response.json());
  } catch (error) {
    $("serverStatus").textContent = "OFFLINE";
    $("serverStatus").className = "status-pill offline";
    setText("updatedAt", "MONITOR SERVER UNREACHABLE");
    console.error(error);
  }
}

fetchStats();
setInterval(fetchStats, 1000);
