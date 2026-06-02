# Deployment Guide

## Overview

This MVP currently targets Windows-only deployment:

* `LanAdmin.Server` runs as a Windows Service on an always-on machine in the LAN
* `LanAgent` runs as a Windows Service on each managed endpoint
* `LanAdmin.Console` is a desktop WPF app for administrators

Requirements:

* Windows 10 or Windows 11
* `.NET 8 SDK` for build/publish
* `.NET 8 Runtime` on target machines if you deploy framework-dependent builds

## Publish

Publish the server:

```powershell
.\scripts\publish-server.ps1
```

Publish the agent:

```powershell
.\scripts\publish-agent.ps1
```

Default outputs:

* `artifacts\server`
* `artifacts\agent`

## Install Services

Install the server service:

```powershell
.\scripts\install-server-service.ps1
```

Install the agent service:

```powershell
.\scripts\install-agent-service.ps1
```

Both scripts:

* create the Windows Service
* set startup type to `Automatic`
* configure simple restart-on-failure behavior
* start the service immediately

## Uninstall Services

Remove the server service:

```powershell
.\scripts\uninstall-server-service.ps1
```

Remove the agent service:

```powershell
.\scripts\uninstall-agent-service.ps1
```

## Config Files

### Server

File: `src\LanAdmin.Server\appsettings.json`

Important fields:

* `Database:Path`: SQLite file location, relative to the service executable directory
* `Agent:OfflineThresholdSeconds`: offline timeout threshold
* `Kestrel:Endpoints:Http:Url`: server listen URL
* `FileLogging:Path`: server log file path, relative to the executable directory
* `FileLogging:MinimumLevel`: minimum file log level

### Agent

File: `src\LanAgent\appsettings.json`

Important fields:

* `Agent:ServerUrl`: WebSocket address of the server
* `Agent:HeartbeatSeconds`: heartbeat interval
* `FileLogging:Path`: agent log file path, relative to the executable directory
* `FileLogging:MinimumLevel`: minimum file log level

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
