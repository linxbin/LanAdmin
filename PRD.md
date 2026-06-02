# LanAdmin（智域终端）

## 项目简介

LanAdmin（智域终端）是一款面向企业、学校机房、培训机构、网吧等局域网环境的轻量级终端管理系统。

通过在局域网内的 Windows 电脑安装 Agent，实现设备注册、在线状态管理和设备分组。

目标是在不依赖云服务器的情况下，先完成局域网终端管理主流程闭环。

## 运行环境

当前 MVP 默认运行在 `Windows 10 x64` 或 `Windows 11 x64` 环境。

最低要求：

* 服务端操作系统：Windows 10 64位（建议 Windows 10 Pro / Enterprise）或 Windows 11 64位
* Agent 操作系统：Windows 10 64位 或 Windows 11 64位
* 管理端操作系统：Windows 10 64位 或 Windows 11 64位
* 运行时：.NET 8 Runtime
* 开发环境：.NET 8 SDK

说明：

* 当前版本优先适配 Windows 10 局域网环境
* 管理端使用 WPF，因此仅支持 Windows
* Agent 以 Windows Service 方式运行，因此仅支持 Windows
* 服务端当前默认部署在 Windows 主机上运行

---

# 项目架构

```text
┌─────────────────────┐
│ LanAdmin Console    │
│ 管理端（WPF）        │
└──────────┬──────────┘
           │ HTTP/WebSocket
           │
┌──────────▼──────────┐
│ LanAdmin Server     │
│ 服务端（常驻）       │
└──────────┬──────────┘
           │ WebSocket
           │
 ┌─────────┴─────────┐
 │                   │
 ▼                   ▼

LanAgent         LanAgent
（PC001）        （PC002）

Windows          Windows
```

## 组件说明

### LanAdmin Console

管理端客户端。

主要负责：

* 设备管理
* 分组管理
* 设备状态查看
* 设备事件记录查看

### LanAdmin Server

部署在局域网内一台长期在线的 Windows 主机上，作为系统常驻服务端。

主要负责：

* Agent 接入与鉴权
* 设备状态维护
* 设备事件记录
* 数据库存储

### LanAgent

部署在每台被管理电脑上的 Agent。

运行方式：

* Windows Service
* 开机自动启动
* 后台常驻运行

主要负责：

* 心跳上报
* 接收指令
* 基础信息上报

## 部署形态

系统部署在同一局域网内：

* 一台长期在线主机运行 `LanAdmin Server`
* 管理员在一台或多台电脑上使用 `LanAdmin Console`
* 每台被控 Windows 终端安装 `LanAgent`

说明：

* `LanAdmin Console` 仅负责管理界面与操作发起
* `LanAdmin Server` 持续维护连接、状态和数据
* 即使管理端界面关闭，Agent 心跳与设备状态仍由服务端持续处理

---

# MVP需求

## 1. 设备管理

### 1.1 自动注册

Agent 安装完成后自动注册到服务端。

采集信息：

* 主机名
* IP 地址
* MAC 地址
* 当前登录用户
* 操作系统版本
* Agent 版本

目标：

* 管理端可看到新设备接入

---

### 1.2 在线状态检测

Agent 每 30 秒发送一次心跳。

状态：

* 在线
* 离线

目标：

* 管理端可区分在线和离线设备

---

### 1.3 设备列表

显示字段：

| 字段      | 说明        |
| ------- | --------- |
| 主机名     | PC名称      |
| 在线状态    | 在线/离线     |
| IP地址    | 当前IP      |
| MAC地址   | 网卡地址      |
| 用户名     | 当前登录用户    |
| 操作系统    | Windows版本 |
| Agent版本 | 当前客户端版本   |

支持：

* 查看设备基础信息
* 按设备名称搜索

---

## 2. 分组管理

支持设备分组。

示例：

* 研发部
* 财务部
* 市场部
* 测试机

功能：

* 创建分组
* 编辑分组
* 将设备加入分组

---

## 3. 设备事件记录

记录设备状态相关的核心事件。

MVP 记录范围：

* Agent 注册
* 在线状态变更
* 分组变更

支持：

* 查看事件内容
* 查看发生时间

---

# 后续规划

当前版本先不纳入以下功能：

* 配置下发
* 电源管理
* 命令执行
* 文件分发
* 策略中心

待 MVP 主流程稳定后，再单独评估并逐项加入。

---

# 技术方案

## 管理端

技术栈：

* WPF
* .NET 8

兼容性：

* 仅支持 Windows 10 / 11

---

## 服务端

技术栈：

* .NET 8
* ASP.NET Core
* Background Service

运行方式：

* Windows Service
* 局域网内常驻运行

兼容性：

* 当前 MVP 默认部署在 Windows 10 / 11

---

## Agent

技术栈：

* .NET 8 Worker Service
* Windows Service

运行账户：

```text
LocalSystem
```

兼容性：

* 仅支持 Windows 10 / 11

---

## 数据库

MVP：

```text
SQLite
```

说明：

* MVP 采用单机部署，由 `LanAdmin Server` 统一读写
* 适用于中小规模局域网场景

后期扩展：

```text
MySQL
```

---

## 通讯协议

推荐：

```text
WebSocket
```

或：

```text
TCP Socket
```

建议：

* `LanAdmin Console` 与 `LanAdmin Server` 之间使用 HTTP/WebSocket
* `LanAgent` 与 `LanAdmin Server` 之间使用 WebSocket 长连接

---

# MVP交付目标

第一版仅实现以下核心能力：

* Agent 自动注册
* 在线状态管理
* 设备分组管理
* 设备事件记录

验收标准：

* 新设备安装 Agent 后可自动出现在管理端
* 管理端可看到设备在线或离线状态
* 管理员可将设备加入分组
* 管理端可查看设备注册、状态变更和分组变更记录
