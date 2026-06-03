using LanAgent;
using LanAgent.Diagnostics;
using Microsoft.Extensions.Logging;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--notifier", StringComparison.OrdinalIgnoreCase)))
        {
            AgentNotifierApplication.Run();
            return;
        }

        var builder = Host.CreateApplicationBuilder(args);
        var fileLoggerOptions = builder.Configuration.GetSection("FileLogging").Get<FileLoggerOptions>() ?? new FileLoggerOptions();

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "LanAgent";
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddProvider(new FileLoggerProvider(fileLoggerOptions));
        builder.Services.AddHostedService<Worker>();

        await builder.Build().RunAsync();
    }
}
