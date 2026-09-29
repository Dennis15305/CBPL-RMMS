using Cbpl.Rmms.Collector.Configuration;
using System.Text;

namespace Cbpl.Rmms.Collector.Diagnostics;

public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly LoggingOptions _options;
    private readonly LogLevel _minimumLevel;

    public AppLog(LoggingOptions options)
    {
        _options = options;
        _minimumLevel = Enum.TryParse(options.Level, ignoreCase: true, out LogLevel level)
            ? level
            : LogLevel.Information;
        Directory.CreateDirectory(Path.GetDirectoryName(options.FilePath)!);
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Information(string message) => Write(LogLevel.Information, message);
    public void Warning(string message) => Write(LogLevel.Warning, message);
    public void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(LogLevel level, string message)
    {
        if (level < _minimumLevel)
            return;

        string line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {level.ToString().ToUpperInvariant()} {message}";
        lock (_gate)
        {
            Console.WriteLine(line);
            RotateIfRequired();
            File.AppendAllText(_options.FilePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    private void RotateIfRequired()
    {
        var file = new FileInfo(_options.FilePath);
        if (!file.Exists || file.Length < _options.MaxBytes || _options.BackupCount == 0)
            return;

        string oldest = $"{_options.FilePath}.{_options.BackupCount}";
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (int index = _options.BackupCount - 1; index >= 1; index--)
        {
            string source = $"{_options.FilePath}.{index}";
            string target = $"{_options.FilePath}.{index + 1}";
            if (File.Exists(source))
                File.Move(source, target, overwrite: true);
        }
        File.Move(_options.FilePath, $"{_options.FilePath}.1", overwrite: true);
    }

    private enum LogLevel
    {
        Debug,
        Information,
        Warning,
        Error
    }
}

