const API_URL = "http://127.0.0.1:8765/stats";

function updateScale() {
  const designWidth = 1440;
  const designHeight = 2560;

  const scale = Math.min(
    window.innerWidth / designWidth,
    window.innerHeight / designHeight
  );

  document.documentElement.style.setProperty("--scale", scale);
}

window.addEventListener("resize", updateScale);
updateScale();

const $ = (id) => document.getElementById(id);

function fmt(value, digits = 0) {
  if (value === null || value === undefined || Number.isNaN(value)) return "--";
  return Number(value).toFixed(digits);
}

function tempClass(value, type = "normal") {
  if (value === null || value === undefined) return "normal";

  // 7800X3D / CPU Core 温度：放宽一点
  if (type === "cpu") {
    if (value >= 85) return "hot";
    if (value >= 75) return "warm";
    return "normal";
  }

  // GPU Hotspot 本来就比 GPU Core 高，单独放宽
  if (type === "gpuHotspot") {
    if (value >= 95) return "hot";
    if (value >= 85) return "warm";
    return "normal";
  }

  // SSD / 主板 / RAM / GPU Core 通用
  if (value >= 70) return "hot";
  if (value >= 55) return "warm";
  return "normal";
}

function setTemp(id, value, type = "normal", digits = 0) {
  const el = $(id);
  el.textContent = fmt(value, digits);
  el.className = tempClass(value, type);
}

function setText(id, text) {
  const el = $(id);
  if (el) el.textContent = text;
}

function setBar(id, percent) {
  const el = $(id);
  if (!el) return;
  const safe = Math.max(0, Math.min(100, Number(percent || 0)));
  el.style.width = `${safe}%`;
}

function updateClock() {
  const now = new Date();
  setText("time", now.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }));
  setText("date", now.toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" }));
}

function formatThroughput(kb) {
  if (kb === null || kb === undefined) return "--";
  const value = Number(kb);

  if (value >= 1024) {
    return `${(value / 1024).toFixed(2)} MB/s`;
  }

  return `${value.toFixed(0)} KB/s`;
}

function renderBoardTemps(temps) {
  const container = $("boardTemps");
  container.innerHTML = "";

  for (const item of temps || []) {
    const cls = tempClass(item.value);
    container.innerHTML += `
      <div class="sensor">
        <div class="sensor-name">${item.name}</div>
        <div class="sensor-value ${cls}">${fmt(item.value)}°C</div>
      </div>
    `;
  }
}

function renderBoardFans(fans) {
  const container = $("boardFans");
  container.innerHTML = "";

  for (const item of fans || []) {
    container.innerHTML += `
      <div class="sensor">
        <div class="sensor-name">${item.name}</div>
        <div class="sensor-value">${fmt(item.rpm)} RPM</div>
      </div>
    `;
  }
}

function renderStorage(drives) {
  const container = $("storageList");
  container.innerHTML = "";

  const order = {
    "(C:)": 0,
    "(E:)": 1,
    "(F:)": 2
  };

  const sortedDrives = [...(drives || [])].sort((a, b) => {
    return (order[a.name] ?? 99) - (order[b.name] ?? 99);
  });

  for (const drive of sortedDrives) {
    const cls = tempClass(drive.temp);
    const used = Number(drive.used || 0);

    container.innerHTML += `
      <div class="drive">
        <div>
          <div class="drive-name">${drive.name}</div>
          <div class="drive-meta">${drive.rawName}</div>
        </div>

        <div>
          <div class="bar-label">
            <span>USED SPACE</span>
            <span>${fmt(drive.used, 2)}%</span>
          </div>
          <div class="bar"><div class="bar-fill" style="width:${Math.min(100, used)}%"></div></div>
          <div class="drive-meta">${fmt(drive.freeGb, 0)} GB free / ${fmt(drive.totalGb, 0)} GB</div>
        </div>

        <div class="drive-temp ${cls}">${fmt(drive.temp)}°C</div>
        <div class="drive-life">LIFE ${fmt(drive.life)}%</div>
      </div>
    `;
  }
}

function render(data) {
  $("serverStatus").textContent = "SERVER ONLINE";
  $("serverStatus").className = "status-pill online";

  const updated = new Date(data.updatedAt);
  setText("updatedAt", `Updated ${updated.toLocaleTimeString()}`);

  setTemp("cpuTemp", data.cpu.temp, "cpu");
  setText("cpuLoad", `${fmt(data.cpu.load, 1)}%`);
  setText("cpuPower", `${fmt(data.cpu.power, 1)}W`);
  setText("cpuClock", `${fmt(data.cpu.clock / 1000, 2)}GHz`);
  setText("cpuCcd", `${fmt(data.cpu.ccd, 1)}°C`);

  setTemp("gpuTemp", data.gpu.temp);
  setText("gpuHotspot", `${fmt(data.gpu.hotspot, 1)}°C`);
  $("gpuHotspot").className = tempClass(data.gpu.hotspot, "gpuHotspot");
  setText("gpuMemTemp", `${fmt(data.gpu.memoryTemp)}°C`);
  $("gpuMemTemp").className = tempClass(data.gpu.memoryTemp);
  setText("gpuLoad", `${fmt(data.gpu.load, 1)}%`);
  setText("gpuPower", `${fmt(data.gpu.power, 1)}W`);
  setText("vramText", `${fmt(data.gpu.vramUsedGb, 2)} / ${fmt(data.gpu.vramTotalGb, 2)} GB`);
  setBar("vramBar", (data.gpu.vramUsedGb / data.gpu.vramTotalGb) * 100);
  setText("gpuFan", `GPU FANS: ${fmt(data.gpu.fan1Rpm)} RPM / ${fmt(data.gpu.fan2Rpm)} RPM`);

  setText("ramUsed", fmt(data.ram.usedGb, 1));
  setText("ramTotal", `${fmt(data.ram.totalGb, 1)}GB`);
  setText("ramAvailable", `${fmt(data.ram.availableGb, 1)}GB`);
  setText("ramText", `${fmt(data.ram.load, 1)}%`);
  setBar("ramBar", data.ram.load);
  setText("dimm0", `${fmt(data.ram.dimm0Temp, 1)}°C`);
  $("dimm0").className = tempClass(data.ram.dimm0Temp);
  setText("dimm2", `${fmt(data.ram.dimm2Temp, 1)}°C`);
  $("dimm2").className = tempClass(data.ram.dimm2Temp);

  renderBoardTemps(data.motherboard.temps);
  renderBoardFans(data.motherboard.fans);
  renderStorage(data.storage);

  const network = data.activeNetwork || {};
  setText("networkName", network.name || "--");
  setText("netUp", formatThroughput(network.uploadKb));
  setText("netDown", formatThroughput(network.downloadKb));
}

async function fetchStats() {
  try {
    const response = await fetch(API_URL, { cache: "no-store" });

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const data = await response.json();
    render(data);
  } catch (error) {
    $("serverStatus").textContent = "SERVER OFFLINE";
    $("serverStatus").className = "status-pill offline";
    setText("updatedAt", "Cannot connect to monitor server");
    console.error(error);
  }
}

updateClock();
setInterval(updateClock, 1000);

fetchStats();
setInterval(fetchStats, 1000);