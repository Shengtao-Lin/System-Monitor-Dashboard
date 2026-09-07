# System Monitor Dashboard

一个面向 Windows 与 Wallpaper Engine 的本地硬件监控仪表盘。它通过轻量级 .NET 服务读取硬件传感器，再由纯 HTML/CSS/JavaScript 前端实时展示 CPU、GPU、内存、网络、主板、硬盘与可选的 Corsair 水冷数据。

> 数据只在本机采集和展示。服务默认仅监听 `127.0.0.1:8765`，不会向局域网或互联网开放。

## 功能

- CPU：温度、负载、功耗、频率与 CCD 温度
- GPU：核心/热点/显存温度、负载、功耗、显存占用与风扇转速
- 内存：已用、可用、总容量、负载与 DIMM 温度
- 网络：活动网卡、实时上传与下载速率
- 主板：温度与风扇传感器
- 存储：容量、占用、温度与健康度
- Corsair iCUE 水冷：冷却液温度、风扇和水泵数据（可选）
- 三代仪表盘界面，`Wallpaper_v3` 为当前版本
- Windows 托盘控制：启动、停止、重启、健康检查、日志和开机启动

## 项目结构

```text
System_Monitor_Dashboard/
├─ MonitorServer/       # ASP.NET Core 本地硬件数据服务
├─ MonitorServer_V2/    # Windows 托盘管理程序
├─ Wallpaper_v1/        # 第一版界面
├─ Wallpaper_v2/        # 第二版界面
├─ Wallpaper_v3/        # 当前版界面
└─ start-monitor-server.bat
```

```mermaid
flowchart LR
    Sensors[Windows hardware sensors] --> Server[MonitorServer<br/>127.0.0.1:8765]
    ICUE[iCUE sensor CSV logs] -. optional .-> Server
    Server --> API[/health · /stats · /cooler]
    API --> Dashboard[Wallpaper Engine dashboard]
    Tray[MonitorServer_V2 tray app] --> Server
```

## 环境要求

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Wallpaper Engine](https://www.wallpaperengine.io/)（仅将界面用作动态壁纸时需要）
- Corsair iCUE（仅水冷监控需要）

部分硬件传感器可能需要管理员权限才能读取。不同硬件暴露的传感器名称和数量也会有所差异。

## 从源码运行

克隆仓库后，先从模板创建本机配置：

```powershell
Copy-Item .env.example .env
```

按需编辑 `.env`，然后在仓库根目录执行：

```powershell
dotnet restore .\MonitorServer\MonitorServer.csproj
dotnet run --project .\MonitorServer\MonitorServer.csproj
```

确认服务状态：

```powershell
Invoke-RestMethod http://127.0.0.1:8765/health
```

可用接口：

| 地址 | 用途 |
| --- | --- |
| `GET /health` | 服务健康状态 |
| `GET /stats` | 主要硬件传感器数据 |
| `GET /cooler` | 缓存后的 iCUE 水冷数据 |

直接在浏览器打开 `Wallpaper_v3/index.html` 即可预览当前仪表盘；服务离线时页面会显示 `OFFLINE`。

## 在 Wallpaper Engine 中使用

1. 先启动 `MonitorServer`。
2. 打开 Wallpaper Engine 的壁纸编辑器。
3. 新建 Web 类型壁纸，并选择 `Wallpaper_v3/index.html`。
4. 保存并应用壁纸。

页面每秒从 `http://127.0.0.1:8765/stats` 获取一次数据。`Wallpaper_v1` 和 `Wallpaper_v2` 保留用于对比历史设计。

## 发布与打包

只发布数据服务：

```powershell
dotnet publish .\MonitorServer\MonitorServer.csproj `
  -c Release -r win-x64 --self-contained false `
  -o .\MonitorServer\publish
```

随后可运行仓库根目录的 `start-monitor-server.bat`。脚本使用相对路径，因此仓库移动到其他磁盘后仍可工作。

要同时打包托盘程序与服务，可使用下面的目录布局：

```powershell
dotnet publish .\MonitorServer_V2\MonitorServer_V2.csproj `
  -c Release -r win-x64 --self-contained false `
  -o .\dist

dotnet publish .\MonitorServer\MonitorServer.csproj `
  -c Release -r win-x64 --self-contained false `
  -o .\dist\MonitorServer
```

运行 `dist/MonitorServer_V2.exe` 后，托盘程序会自动找到 `dist/MonitorServer/MonitorServer.exe`，并提供启动、停止、重启、打开日志和开机启动等操作。`dist/` 属于构建产物，不纳入 Git。

## iCUE 水冷数据（可选）

服务读取 iCUE 导出的传感器 CSV 日志，不会直接轮询 Corsair USB 设备。先在 iCUE 中启用传感器日志，再复制并编辑配置模板：

```powershell
Copy-Item .env.example .env
# 编辑 .env 中的 MONITOR_ICUE_LOG_DIR
dotnet run --project .\MonitorServer\MonitorServer.csproj
```

程序会从当前目录和程序目录开始向上查找 `.env`。已存在的系统环境变量优先于 `.env`，因此部署脚本仍可覆盖本地配置。`.env` 不会被 Git 跟踪，只有安全的 `.env.example` 模板会进入仓库。

支持的环境变量：

| 变量 | 默认值 | 说明 |
| --- | ---: | --- |
| `MONITOR_ENABLE_COOLER` | `true` | 设为 `false` 可关闭水冷日志读取 |
| `MONITOR_ICUE_LOG_DIR` | 程序目录下的 `icue_logs` | iCUE CSV 日志目录 |
| `MONITOR_COOLER_REFRESH_SECONDS` | `5` | 正常读取间隔（5–3600 秒） |
| `MONITOR_COOLER_BACKOFF_SECONDS` | `300` | 读取失败后的退避间隔（60–3600 秒） |
| `MONITOR_COOLER_FAN_MAX_RPM` | `2000` | 风扇转速量程（500–5000 RPM） |
| `MONITOR_COOLER_PUMP_MAX_RPM` | `2850` | 水泵转速量程（1000–6000 RPM） |

不需要水冷信息时，可以直接设置 `MONITOR_ENABLE_COOLER=false`。主仪表盘数据不受影响。

## 常见问题

### 页面一直显示 OFFLINE

确认 `MonitorServer` 正在运行，并访问 `http://127.0.0.1:8765/health`。若端口被占用，请先结束占用 `8765` 的程序。

### 某些温度或风扇显示 `--`

这通常表示当前硬件或驱动没有暴露对应传感器。尝试以管理员身份运行服务，并确认 LibreHardwareMonitor 能识别该设备。

### 水冷数据显示错误或过期

确认 iCUE 正在生成 CSV 传感器日志，并检查 `MONITOR_ICUE_LOG_DIR` 是否指向正确目录。服务读取失败时会自动延长轮询间隔，避免持续占用文件。

## 技术栈

- .NET 8 / ASP.NET Core Minimal API
- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- Windows Forms 托盘程序
- 原生 HTML、CSS、JavaScript 与 Canvas

## 开发验证

```powershell
dotnet build .\MonitorServer\MonitorServer.csproj -c Release
dotnet build .\MonitorServer_V2\MonitorServer_V2.csproj -c Release
```

构建目录、发布文件、运行日志、iCUE CSV 数据和本机 `.env` 均已通过 `.gitignore` 排除。
