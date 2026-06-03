using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanAdmin.SetupWorker;

internal static class Program
{
    private const string DefaultServerServiceDisplayName = "LanAdmin Server";
    private const string DefaultAgentServiceDisplayName = "LanAdmin Agent";
    private const string LogFileName = "LanAdmin.SetupWorker.log";
    private static string? _explicitLogPath;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new InvalidOperationException("No command specified.");
            }

            var command = args[0].Trim().ToLowerInvariant();
            var options = ParseOptions(args.Skip(1).ToArray());
            _explicitLogPath = GetOptionalOption(options, "log-path");

            return command switch
            {
                "configure-server" => ConfigureServer(options),
                "configure-agent" => ConfigureAgent(options),
                "prepare-server-upgrade" => PrepareServerUpgrade(options),
                "prepare-agent-upgrade" => PrepareAgentUpgrade(options),
                "remove-service" => RemoveService(options),
                _ => throw new InvalidOperationException($"Unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            TryWriteLog(ex);
            return 1;
        }
    }

    private static int ConfigureServer(IReadOnlyDictionary<string, string> options)
    {
        var installDir = GetRequiredOption(options, "install-dir");
        var listenUrl = GetRequiredOption(options, "listen-url");
        var consoleServerBaseUrl = GetRequiredOption(options, "console-server-base-url");
        var databasePath = GetRequiredOption(options, "database-path");
        var offlineThresholdSeconds = GetRequiredIntOption(options, "offline-threshold-seconds");
        var serviceName = GetOptionalOption(options, "service-name") ?? "LanAdminServer";

        var serverDir = Path.Combine(installDir, "server");
        var consoleDir = Path.Combine(installDir, "console");
        var serverConfigPath = Path.Combine(serverDir, "appsettings.json");
        var consoleConfigPath = Path.Combine(consoleDir, "appsettings.json");
        var serverExePath = Path.Combine(serverDir, "LanAdmin.Server.exe");

        EnsureFileExists(serverConfigPath);
        EnsureFileExists(consoleConfigPath);
        EnsureFileExists(serverExePath);

        UpdateJsonFile(serverConfigPath, root =>
        {
            GetOrCreateObject(GetOrCreateObject(root, "Kestrel"), "Endpoints")
                ["Http"] = new JsonObject
                {
                    ["Url"] = listenUrl
                };

            GetOrCreateObject(root, "Database")["Path"] = databasePath;
            var agent = GetOrCreateObject(root, "Agent");
            agent["OfflineThresholdSeconds"] = offlineThresholdSeconds;
            if (agent["HeartbeatSeconds"] is null)
            {
                agent["HeartbeatSeconds"] = 30;
            }

            GetOrCreateObject(root, "Bootstrap")["ServerBaseUrl"] = consoleServerBaseUrl;
        });

        UpdateJsonFile(consoleConfigPath, root =>
        {
            GetOrCreateObject(root, "Console")["ServerBaseUrl"] = consoleServerBaseUrl;
        });

        ServiceManager.CreateOrReplaceService(
            serviceName,
            DefaultServerServiceDisplayName,
            serverExePath);

        return 0;
    }

    private static int ConfigureAgent(IReadOnlyDictionary<string, string> options)
    {
        var installDir = GetRequiredOption(options, "install-dir");
        var serviceName = GetOptionalOption(options, "service-name") ?? "LanAgent";

        var agentExePath = Path.Combine(installDir, "LanAgent.exe");

        EnsureFileExists(agentExePath);

        ServiceManager.CreateOrReplaceService(
            serviceName,
            DefaultAgentServiceDisplayName,
            agentExePath);

        return 0;
    }

    private static int PrepareAgentUpgrade(IReadOnlyDictionary<string, string> options)
    {
        var installDir = GetRequiredOption(options, "install-dir");
        var serviceName = GetOptionalOption(options, "service-name") ?? "LanAgent";
        var processName = GetOptionalOption(options, "process-name") ?? "LanAgent";
        var executablePath = Path.Combine(installDir, "LanAgent.exe");

        ServiceManager.StopServiceIfExists(serviceName);
        TerminateProcesses(processName, executablePath);

        return 0;
    }

    private static int PrepareServerUpgrade(IReadOnlyDictionary<string, string> options)
    {
        var installDir = GetRequiredOption(options, "install-dir");
        var serviceName = GetOptionalOption(options, "service-name") ?? "LanAdminServer";

        ServiceManager.StopServiceIfExists(serviceName);

        TerminateProcesses("LanAdmin.Server", Path.Combine(installDir, "server", "LanAdmin.Server.exe"));
        TerminateProcesses("LanAdmin.Console", Path.Combine(installDir, "console", "LanAdmin.Console.exe"));

        return 0;
    }

    private static int RemoveService(IReadOnlyDictionary<string, string> options)
    {
        var serviceName = GetRequiredOption(options, "service-name");
        ServiceManager.RemoveServiceIfExists(serviceName);
        return 0;
    }

    private static IReadOnlyDictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected argument: {token}");
            }

            var key = token[2..];
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Option name is required.");
            }

            if (index == args.Length - 1)
            {
                throw new InvalidOperationException($"Option '{token}' is missing a value.");
            }

            options[key] = args[++index];
        }

        return options;
    }

    private static string GetRequiredOption(IReadOnlyDictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required option --{name}.");
        }

        return value;
    }

    private static string? GetOptionalOption(IReadOnlyDictionary<string, string> options, string name)
    {
        return options.TryGetValue(name, out var value) ? value : null;
    }

    private static int GetRequiredIntOption(IReadOnlyDictionary<string, string> options, string name)
    {
        var rawValue = GetRequiredOption(options, name);
        if (!int.TryParse(rawValue, out var value))
        {
            throw new InvalidOperationException($"Option --{name} must be an integer.");
        }

        return value;
    }

    private static void UpdateJsonFile(string path, Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidOperationException($"Invalid JSON object in {path}");

        mutate(node);

        var json = node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(path, json);
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static void EnsureFileExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Required file was not found.", path);
        }
    }

    private static void TerminateProcesses(string processName, string executablePath)
    {
        var normalizedExecutablePath = Path.GetFullPath(executablePath);

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.HasExited)
                {
                    continue;
                }

                var mainModulePath = TryGetProcessPath(process);
                if (mainModulePath is not null &&
                    !string.Equals(
                        Path.GetFullPath(mainModulePath),
                        normalizedExecutablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteLog(Exception exception)
    {
        try
        {
            var path = ResolveLogPath();
            var lines = new[]
            {
                $"[{DateTimeOffset.Now:O}] {exception}",
                string.Empty
            };

            File.AppendAllLines(path, lines);
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    private static string ResolveLogPath()
    {
        if (!string.IsNullOrWhiteSpace(_explicitLogPath))
        {
            var explicitDirectory = Path.GetDirectoryName(_explicitLogPath);
            if (!string.IsNullOrWhiteSpace(explicitDirectory))
            {
                Directory.CreateDirectory(explicitDirectory);
            }

            return _explicitLogPath;
        }

        return Path.Combine(Path.GetTempPath(), LogFileName);
    }
}
