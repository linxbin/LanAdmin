# Deployment Guide

## Overview

This MVP currently targets Windows-only deployment:

* `LanAdmin.Server` runs as a Windows Service on an always-on machine in the LAN
* `LanAgent` runs as a Windows Service on each managed endpoint
* `LanAdmin.Console` is a desktop WPF app for administrators

Requirements:

* Windows 10 or Windows 11
* `.NET 8 SDK` for build/publish
* `Inno Setup` if you want to build the final installer `.exe` packages

## Publish

Publish the server:

```powershell
.\scripts\publish-server.ps1
```

Publish the agent:

```powershell
.\scripts\publish-agent.ps1
```

Publish the console:

```powershell
.\scripts\publish-console.ps1
```

Publish the setup worker:

```powershell
.\scripts\publish-setup-worker.ps1
```

Publish all components with one command:

```powershell
.\scripts\publish-all.ps1
```

Default outputs:

* `artifacts\server`
* `artifacts\agent`
* `artifacts\console`
* `artifacts\setup-worker`

Example with baked-in configuration:

```powershell
.\scripts\publish-all.ps1 `
  -ServerListenUrl "http://0.0.0.0:5000" `
  -ServerBaseUrl "http://192.168.1.10:5000"
```

This avoids hand-editing `appsettings.json` after publish.

## Inno Setup Installers

The repository includes two Inno Setup projects:

* `installer\inno\LanAdminServer.iss`
* `installer\inno\LanAgent.iss`

Installer shape:

* `LanAdmin Server` installer bundles both `Server` and `Console`
* `LanAgent` installer is a fixed package and no longer contains server-specific baked-in addresses
* `LanAdmin Server` installation also drops a copy of the fixed `agent-package\LanAgentSetup.exe` for redistribution

Build the server installer:

```powershell
.\scripts\build-inno-server.ps1 `
  -ServerListenUrl "http://0.0.0.0:5000" `
  -ServerBaseUrl "http://192.168.1.10:5000"
```

Build the agent installer:

```powershell
.\scripts\build-inno-agent.ps1
```

Current default:

* `LanAdmin Server` installer is built as `self-contained`
* `LanAdmin Console` bundled inside the server installer is built as `self-contained`
* `LanAgent` installer is built as `self-contained`
* `LanAdmin.SetupWorker` is bundled as a hidden `WinExe` helper for service registration, unregistration, and installer-time configuration
* default target runtime is `win-x64`
* target machines do not need a preinstalled `.NET 8 Runtime`
* the installer flow no longer depends on `PowerShell`, `cmd.exe`, `sc.exe`, or `IExpress`

Generated installer output:

* `artifacts\installer\LanAdminServerSetup.exe`
* `artifacts\installer\LanAgentSetup.exe`

## Fixed Agent Package

After `LanAdmin Server` installation completes, the install directory includes:

* `agent-package\LanAgentSetup.exe`

`LanAgentSetup.exe` is now a fixed installer. It does not need a separate `.ini` file and does not need to be regenerated for each server installation.

At runtime, `LanAgent` resolves its server address like this:

1. read `C:\ProgramData\LanAdmin\Agent\runtime.json` if it already exists
2. try the bootstrap URLs defined in `appsettings.json`
3. send UDP discovery on port `5010`
4. call `GET /api/bootstrap/agent`
5. persist the resolved runtime config into `C:\ProgramData\LanAdmin\Agent\runtime.json`

### Agent Silent Install

Example:

```powershell
LanAgentSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

## Config Files

### Server

Source template: `src\LanAdmin.Server\appsettings.json`

Published file: `artifacts\server\appsettings.json`

Important fields:

* `Database:Path`: SQLite file location, relative to the service executable directory
* `Agent:OfflineThresholdSeconds`: offline timeout threshold
* `Agent:HeartbeatSeconds`: default heartbeat interval returned by `/api/bootstrap/agent`
* `Bootstrap:ServerBaseUrl`: HTTP base URL returned by UDP discovery and the bootstrap API
* `Bootstrap:DiscoveryUdpPort`: UDP port used for agent discovery
* `Kestrel:Endpoints:Http:Url`: server listen URL
* `FileLogging:Path`: server log file path, relative to the executable directory
* `FileLogging:MinimumLevel`: minimum file log level

### Agent

Source template: `src\LanAgent\appsettings.json`

Published file: `artifacts\agent\appsettings.json`

Important fields:

* `Agent:HeartbeatSeconds`: heartbeat interval
* `Bootstrap:ServerBaseUrls`: fixed bootstrap HTTP candidates tried before UDP discovery
* `Bootstrap:EndpointPath`: bootstrap API path, default `/api/bootstrap/agent`
* `Bootstrap:DiscoveryUdpPort`: UDP port used for LAN discovery
* `FileLogging:Path`: agent log file path, relative to the executable directory
* `FileLogging:MinimumLevel`: minimum file log level

Runtime-discovered values are persisted to:

* `C:\ProgramData\LanAdmin\Agent\runtime.json`

### Console

Source template: `src\LanAdmin.Console\appsettings.json`

Published file: `artifacts\console\appsettings.json`

Important fields:

* `Console:ServerBaseUrl`: HTTP address of the server API

## Logs

Default log files:

* `logs\lanadmin-server.log`
* `logs\lanagent.log`

The paths are resolved relative to the published executable directory.

## Validation Checklist

After service deployment:

1. Confirm both services exist in `services.msc`
2. Confirm the server is listening on the configured port
3. Confirm the agent log shows successful WebSocket connection
4. Open `LanAdmin.Console` and verify the device appears as `Online`
5. Stop the agent service and confirm the device transitions to `Offline`
