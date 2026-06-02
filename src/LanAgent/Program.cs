using LanAgent;
using LanAgent.Diagnostics;
using Microsoft.Extensions.Logging;

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
