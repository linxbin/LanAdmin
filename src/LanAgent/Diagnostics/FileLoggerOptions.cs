using Microsoft.Extensions.Logging;

namespace LanAgent.Diagnostics;

public sealed class FileLoggerOptions
{
    public string Path { get; set; } = "logs/lanagent.log";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}
