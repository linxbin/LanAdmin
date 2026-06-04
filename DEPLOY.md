# Deployment Guide

## Overview

This project targets Windows deployment:

* `LanAdmin.Server` runs as a Windows Service on an always-on machine in the LAN.
* `LanAdmin.Console` is a WPF desktop client for administrators.
* `LanAgent` runs as a Windows Service on each managed endpoint.

Requirements:

* Windows 10 or Windows 11
* `.NET 8 SDK` for build and publish
* `Inno Setup` for generating installer `.exe` packages

Current installer shape:

* `LanAdminServerSetup.exe`: installs `Server` and bundled `Console`
* `LanAgentSetup.exe`: installs `LanAgent`

`Server` and `Agent` packaging are independent. `LanAgent` uses the packaged `Bootstrap:ServerBaseUrl` directly. There is no UDP discovery and no `runtime.json` cache.

## Publish

Publish individual components:

```powershell
.\scripts\publish-server.ps1
.\scripts\publish-console.ps1
.\scripts\publish-agent.ps1
.\scripts\publish-setup-worker.ps1
```

Publish all artifacts:

```powershell
.\scripts\publish-all.ps1 `
  -ServerListenUrl "http://0.0.0.0:5000" `
  -ServerBaseUrl "http://192.168.1.10:5000"
```

Default publish output:

* `artifacts\server`
* `artifacts\console`
* `artifacts\agent`
* `artifacts\setup-worker`

## Build Installers

Build the server installer:

```powershell
.\scripts\build-inno-server.ps1 `
  -ServerListenUrl "http://0.0.0.0:5000" `
  -ServerBaseUrl "http://192.168.1.10:5000"
```

Build the agent installer:

```powershell
.\scripts\build-inno-agent.ps1 `
  -ServerBaseUrl "http://192.168.1.10:5000"
```

If `-ServerBaseUrl` is omitted when building or publishing the agent, it defaults to `http://127.0.0.1:5000`.

Generated installer output:

* `artifacts\installer\LanAdminServerSetup.exe`
* `artifacts\installer\LanAgentSetup.exe`

Current defaults:

* all installers are `self-contained`
* default runtime is `win-x64`
* target machines do not need a preinstalled `.NET 8 Runtime`
* overwrite upgrade is supported for both installers

## Recommended Deployment Flow

1. Build `LanAdminServerSetup.exe` with the correct LAN-facing `ServerBaseUrl`.
2. Install `LanAdminServerSetup.exe` on the server machine.
3. Confirm the `LanAdminServer` service is running.
4. Build `LanAgentSetup.exe` with the same `ServerBaseUrl`.
5. Install `LanAgentSetup.exe` on endpoint machines.
6. Open `LanAdmin.Console` and verify devices appear online.

## Server Install Flow

Run `LanAdminServerSetup.exe` on the server machine.

During setup, the installer asks for:

* `Server listen URL`
* `Console server base URL`
* `Database path`
* `Offline threshold (seconds)`

The installer then:

* copies `Server`, `Console`, and `SetupWorker`
* writes configuration into `server\appsettings.json` and `console\appsettings.json`
* registers or updates the `LanAdminServer` Windows Service
* starts the `LanAdminServer` service

Default install directory:

* `C:\Program Files\LanAdmin`

Installed subdirectories:

* `server\`
* `console\`
* `tools\`

## Agent Install Flow

Run `LanAgentSetup.exe` on the endpoint machine.

The installer:

* installs files into `C:\Program Files\LanAdmin\Agent`
* registers or updates the `LanAgent` Windows Service
* starts the `LanAgent` service

At runtime, `LanAgent` does this:

1. reads `Bootstrap:ServerBaseUrl` from its packaged `appsettings.json`
2. calls `GET /api/bootstrap/agent`
3. connects to the returned WebSocket URL
4. sends `register` and periodic `heartbeat` messages

There is no UDP broadcast discovery and no runtime address cache file.

### Agent Silent Install

```powershell
LanAgentSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

```powershell
Start-Process -FilePath .\LanAgentSetup.exe -Verb RunAs -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait
```

Recommended post-install validation:

```powershell
Get-Service LanAgent
Test-Path 'C:\Program Files\LanAdmin\Agent'
```

## Config Files

### Server

Source template: `src\LanAdmin.Server\appsettings.json`

Published file: `artifacts\server\appsettings.json`

Important fields:

* `Database:Path`: SQLite file location, relative to the service executable directory
* `Agent:OfflineThresholdSeconds`: offline timeout threshold
* `Agent:HeartbeatSeconds`: heartbeat interval returned by `/api/bootstrap/agent`
* `Bootstrap:ServerBaseUrl`: HTTP base URL used by the bundled console and expected by packaged agents
* `Kestrel:Endpoints:Http:Url`: server listen URL
* `FileLogging:Path`: server log file path
* `FileLogging:MinimumLevel`: minimum file log level

### Agent

Source template: `src\LanAgent\appsettings.json`

Published file: `artifacts\agent\appsettings.json`

Important fields:

* `Agent:HeartbeatSeconds`: default heartbeat fallback
* `Bootstrap:ServerBaseUrl`: configured server API base URL
* `Bootstrap:EndpointPath`: bootstrap API path, default `/api/bootstrap/agent`
* `FileLogging:Path`: agent log file path
* `FileLogging:MinimumLevel`: minimum file log level

### Console

Source template: `src\LanAdmin.Console\appsettings.json`

Published file: `artifacts\console\appsettings.json`

Important fields:

* `Console:ServerBaseUrl`: server API base URL

## Logs

Default log files:

* `logs\lanadmin-server.log`
* `logs\lanagent.log`

Paths are resolved relative to the published executable directory.

## Validation Checklist

1. Confirm both services exist in `services.msc`.
2. Confirm the server is listening on the configured port.
3. Confirm `LanAdmin.Console` can reach the configured server API.
4. Confirm the agent log shows a successful bootstrap request and WebSocket connection.
5. Confirm the device appears as `Online` in `LanAdmin.Console`.
6. Stop the agent service and confirm the device transitions to `Offline`.
