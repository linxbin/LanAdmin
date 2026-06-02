using Microsoft.Extensions.Logging;

namespace LanAdmin.Server.Diagnostics;

public sealed class FileLoggerOptions
{
    public string Path { get; set; } = "logs/lanadmin-server.log";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}
