using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LanAgent.Diagnostics;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerOptions _options;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options;
        var logPath = System.IO.Path.GetFullPath(options.Path, AppContext.BaseDirectory);
        var directory = System.IO.Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _options, WriteLine));
    }

    public void Dispose()
    {
        _writer.Dispose();
    }

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly FileLoggerOptions _options;
        private readonly Action<string> _writeLine;

        public FileLogger(string categoryName, FileLoggerOptions options, Action<string> writeLine)
        {
            _categoryName = categoryName;
            _options = options;
            _writeLine = writeLine;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None && logLevel >= _options.MinimumLevel;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_categoryName}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _writeLine(line);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
