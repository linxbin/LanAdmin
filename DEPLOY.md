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

Current delivery shape:

* `LanAdminServerSetup.exe`: one installer for `Server` + bundled `Console`
* `LanAgentSetup.exe`: one fixed installer for all endpoints
* `LanAgent` does not need a baked-in server address at install time; it resolves the server at runtime

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

`ServerListenUrl` is the bind address used by Kestrel on the server machine.

`ServerBaseUrl` is the LAN address that agents and the bundled console should use to reach the server API.

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
* both installers support overwrite upgrade and will stop services / close matching installed processes before replacing files

Generated installer output:

* `artifacts\installer\LanAdminServerSetup.exe`
* `artifacts\installer\LanAgentSetup.exe`

## Recommended Deployment Flow

1. Build `LanAdminServerSetup.exe` with the correct server LAN address.
2. Install `LanAdminServerSetup.exe` on the always-on machine in the LAN.
3. Confirm `LanAdminServer` is running.
4. Redistribute `agent-package\LanAgentSetup.exe` from the server install directory, or use the same generated `artifacts\installer\LanAgentSetup.exe`.
5. Install `LanAgentSetup.exe` on endpoint machines.
6. Open `LanAdmin.Console` and verify devices appear online.

### Server Install Flow

Run `LanAdminServerSetup.exe` on the server machine.

During setup, the installer asks for:

* `Server listen URL`
* `Console server base URL`
* `Database path`
* `Offline threshold (seconds)`

The installer then:

* copies `Server`, `Console`, `SetupWorker`, and `LanAgentSetup.exe`
* writes configuration into `server\appsettings.json` and `console\appsettings.json`
* registers or updates the `LanAdminServer` Windows Service
* starts the `LanAdminServer` service

Default install directory:

* `C:\Program Files\LanAdmin`

Installed subdirectories:

* `server\`
* `console\`
* `tools\`
* `agent-package\`

### Server Overwrite Upgrade

Re-running `LanAdminServerSetup.exe` on the same machine is supported.

Before files are replaced, the installer automatically:

* stops the `LanAdminServer` service if it exists
* closes the installed `LanAdmin.Server.exe`
* closes the installed `LanAdmin.Console.exe`

This is intended to allow in-place upgrade without manually closing the application first.

## Fixed Agent Package

After `LanAdmin Server` installation completes, the install directory includes:

* `agent-package\LanAgentSetup.exe`

`LanAgentSetup.exe` is now a fixed installer. It does not need a separate `.ini` file and does not need to be regenerated for each server installation.

### Agent Install Flow

Run `LanAgentSetup.exe` on the endpoint machine.

The installer:

* installs files into `C:\Program Files\LanAdmin\Agent`
* registers or updates the `LanAgent` Windows Service
* starts the `LanAgent` service

### Agent Overwrite Upgrade

Re-running `LanAgentSetup.exe` on the same machine is supported.

Before files are replaced, the installer automatically:

* stops the `LanAgent` service if it exists
* closes the installed `LanAgent.exe`

At runtime, `LanAgent` resolves its server address like this:

1. read `C:\ProgramData\LanAdmin\Agent\runtime.json` if it already exists
2. try the bootstrap URLs defined in `appsettings.json`
3. send UDP discovery on port `5010`
4. call `GET /api/bootstrap/agent`
5. persist the resolved runtime config into `C:\ProgramData\LanAdmin\Agent\runtime.json`

### Agent Silent Install

Standard silent install command for IT deployment:

```powershell
LanAgentSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

If you need to launch it from PowerShell with elevation:

```powershell
Start-Process -FilePath .\LanAgentSetup.exe -Verb RunAs -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait
```

Recommended post-install validation:

```powershell
Get-Service LanAgent
Test-Path 'C:\Program Files\LanAdmin\Agent'
```

`runtime.json` is created after the agent successfully resolves bootstrap configuration at runtime, so it may not exist immediately at install completion.

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
3. Confirm `LanAdmin.Console` can reach the configured server API
4. Confirm the agent log shows successful bootstrap resolution and WebSocket connection
5. Open `LanAdmin.Console` and verify the device appears as `Online`
6. Stop the agent service and confirm the device transitions to `Offline`
