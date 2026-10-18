using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ProactiveAI.Infrastructure.Logging;

/// <summary>
/// Bounded diagnostic log (two files of at most 1 MB each). Messages are metadata only by design;
/// as a defense in depth anything that looks like an API key or bearer token is redacted.
/// </summary>
public sealed partial class BoundedFileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 1024 * 1024;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, BoundedFileLogger> _loggers = new();
    private readonly LogLevel _minimum;

    public BoundedFileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "proactive-ai.log");
        _minimum = minimum;
    }

    public string LogPath => _path;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new BoundedFileLogger(this, ShortName(name)));

    public void Dispose() => _loggers.Clear();

    internal static string Redact(string message) => SecretPattern().Replace(message, "[redacted]");

    private void Write(string category, LogLevel level, string message, Exception? ex)
    {
        var sb = new StringBuilder(160);
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(' ').Append(level.ToString()[..4].ToUpperInvariant())
          .Append(' ').Append(category).Append(": ").Append(Redact(message));
        if (ex is not null)
        {
            // exception type and HRESULT only; messages may contain provider payloads
            sb.Append(" [").Append(ex.GetType().Name).Append(" 0x").Append(ex.HResult.ToString("X8")).Append(']');
        }
        sb.AppendLine();
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }
                File.AppendAllText(_path, sb.ToString());
            }
            catch (IOException)
            {
                // diagnostics must never break the app
            }
        }
    }

    private static string ShortName(string category)
    {
        int i = category.LastIndexOf('.');
        return i >= 0 ? category[(i + 1)..] : category;
    }

    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{8,})|(Bearer\s+[A-Za-z0-9_\-\.]{8,})|(ek_[A-Za-z0-9_\-]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    private sealed class BoundedFileLogger(BoundedFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            owner.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
