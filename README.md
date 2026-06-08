# LanAdmin · 智域终端

轻量级局域网终端管理系统，适用于企业办公、学校机房、培训机构、网吧等场景。无需云服务器，纯内网部署，支持设备注册、在线状态监控、分组管理和关机提醒。

---

## 功能特性

- **设备自动注册** — 客户端首次连接即上报主机名、IP、MAC、当前用户、操作系统等信息
- **在线/离线检测** — 心跳 30 秒，离线阈值 90 秒，实时掌握设备状态
- **设备分组** — 创建、重命名、删除分组，支持单台和批量分配
- **MAC 地址关联** — 重装系统或重装 Agent 后，通过 MAC 地址自动合并设备记录
- **事件日志** — 记录注册、上线、离线、分组变更、关机提醒等事件
- **关机阈值管理** — 可按设备配置运行天数阈值（默认 7 天，范围 1–3650 天）
- **自动关机提醒** — 设备运行时间超过阈值时，自动弹窗提醒用户关机（每天一次）
- **手动关机提醒** — 管理员可向选中设备即时推送关机提醒弹窗
- **设备搜索** — 按主机名或 Agent ID 搜索设备

## 系统架构

```
┌────────────────────┐
│  LanAdmin Console   │   WPF 桌面管理客户端
│  (管理界面)          │
└────────┬───────────┘
         │  HTTP REST API
         ▼
┌────────────────────┐
│  LanAdmin Server    │   ASP.NET Core，以 Windows 服务运行
│  (服务端)            │
└────────┬───────────┘
         │  WebSocket
         ▼
┌────────────────────┐
│     LanAgent        │   Worker Service，以 Windows 服务运行于每台被管终端
│  (客户端代理)        │
└────────────────────┘
```

## 技术栈

| 组件 | 技术 |
|---|---|
| 语言 | C# / .NET 8 |
| 服务端 | ASP.NET Core (Kestrel)，Minimal APIs |
| 管理客户端 | WPF (.NET 8 Windows) |
| 客户端代理 | .NET 8 Worker Service + Windows Forms（弹窗通知） |
| 数据库 | SQLite |
| 通信协议 | REST API（Console ↔ Server）、WebSocket（Agent ↔ Server） |
| 安装包 | Inno Setup |
| 服务管理 | Windows Service API |

## 项目结构

```
LanAdmin/
├── LanAdmin.sln                      # Visual Studio 解决方案
├── PRD.md                            # 产品需求文档
├── DEPLOY.md                         # 部署指南
├── src/
│   ├── LanAdmin.Contracts/           # 共享 DTO、协议定义、枚举
│   ├── LanAdmin.Server/              # ASP.NET Core 服务端
│   ├── LanAdmin.Console/             # WPF 管理客户端
│   ├── LanAgent/                     # 客户端代理（含通知弹窗）
│   └── LanAdmin.SetupWorker/         # 安装辅助工具（服务注册、配置）
├── scripts/                          # PowerShell 构建/发布脚本
├── installer/inno/                   # Inno Setup 安装脚本
└── artifacts/                        # 构建产出（发布二进制、安装包）
```

## 快速开始

### 环境要求

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022（推荐）

### 开发调试

```bash
# 克隆仓库
git clone <repo-url>
cd LanAdmin

# 使用 Visual Studio 打开解决方案
# 或命令行构建
dotnet build LanAdmin.sln
```

启动顺序：先运行 Server，再运行 Console 或 Agent。

### 生产部署

1. **构建安装包**（需安装 [Inno Setup](https://jrsoftware.org/isinfo.php)）：

   ```powershell
   .\scripts\build-inno-server.ps1 -ServerBaseUrl "http://192.168.1.10:5000"
   ```

2. **在服务器上安装** `LanAdminServerSetup.exe`，按提示配置监听地址、数据库路径等

3. **安装完成后**，配置好的 Agent 安装包会自动导出至：
   ```
   C:\Program Files\LanAdmin\agent-package\LanAgentSetup.exe
   ```

4. **在终端上安装** Agent（支持静默安装）：
   ```powershell
   LanAgentSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
   ```

5. **打开 LanAdmin Console** 即可开始管理设备

> 构建产物为 `win-x64` 自包含发布，目标机器无需安装 .NET 运行时。

## API 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/devices` | 查询设备列表（支持搜索） |
| DELETE | `/api/devices/{id}` | 删除设备 |
| POST | `/api/devices/{id}/assign-group` | 分配设备到分组 |
| POST | `/api/devices/assign-group-batch` | 批量分配分组 |
| POST | `/api/devices/{id}/shutdown-threshold` | 设置关机阈值 |
| POST | `/api/devices/shutdown-threshold-batch` | 批量设置关机阈值 |
| POST | `/api/devices/prompt-shutdown-reminder-batch` | 手动发送关机提醒 |
| GET | `/api/events` | 查询事件日志 |
| GET | `/api/groups` | 查询分组列表 |
| POST | `/api/groups` | 创建分组 |
| PUT | `/api/groups/{id}` | 重命名分组 |
| DELETE | `/api/groups/{id}` | 删除分组 |
| GET | `/api/bootstrap/agent` | Agent 引导端点 |
| WS | `/ws/agent` | Agent WebSocket 连接 |

## 配置参考

### 服务端 (`src/LanAdmin.Server/appsettings.json`)

```json
{
  "Database": { "Path": "data/lanadmin.db" },
  "Agent": {
    "OfflineThresholdSeconds": 90,
    "HeartbeatSeconds": 30
  },
  "Bootstrap": { "ServerBaseUrl": "http://0.0.0.0:5000" },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:5000" } } }
}
```

### 客户端代理 (`src/LanAgent/appsettings.json`)

```json
{
  "Bootstrap": {
    "ServerBaseUrl": "http://127.0.0.1:5000",
    "EndpointPath": "/api/bootstrap/agent"
  },
  "Agent": { "HeartbeatSeconds": 30 }
}
```

### 管理客户端 (`src/LanAdmin.Console/appsettings.json`)

```json
{
  "Console": { "ServerBaseUrl": "http://localhost:5000" }
}
```

## 文档

- [产品需求文档 (PRD.md)](PRD.md)
- [部署指南 (DEPLOY.md)](DEPLOY.md)

## 许可证

本项目暂未指定开源许可证。
