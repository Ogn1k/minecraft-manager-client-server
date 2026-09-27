using Microsoft.Extensions.Logging;
using MinecraftManager.Core.Persistence;

namespace MinecraftManager.Infrastructure.Diagnostics;

public sealed class SafeFileLoggerProvider(IApplicationPaths paths) : ILoggerProvider
{
    private readonly object gate = new();
    public ILogger CreateLogger(string categoryName) => new SafeFileLogger(categoryName, paths.LogDirectory, gate);
    public void Dispose() { }

    private sealed class SafeFileLogger(string category, string directory, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var message = Redact(formatter(state, exception));
            if (exception is not null) message += " | " + exception.GetType().Name;
            lock (gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "client.log");
                if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024) File.Move(path, Path.Combine(directory, "client.previous.log"), true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {level} {category} {message}{Environment.NewLine}");
            }
        }
        private static string Redact(string value)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) value = value.Replace(home, "<user-home>", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            foreach (var marker in new[] { "access_token=", "token=", "registrationCode=", "refreshCredential=" })
            {
                var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0) value = value[..(index + marker.Length)] + "<redacted>";
            }
            return value;
        }
    }
}
