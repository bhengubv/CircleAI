// RollingFileSink.cs
//
// The log as a file in the app's own storage, readable after the fact: logcat on the
// P30 keeps about two minutes, and a failure is usually noticed later than that. One
// file plus one predecessor, rolled at a size cap, so a busy day cannot fill a phone -
// the rule the Windows host's service.log already follows (4 MB, one rollover).

using System.Globalization;
using System.Text;

namespace CircleAI.Core.Logging;

/// <summary>Writes log lines to a file that rolls over at a size cap.</summary>
public sealed class RollingFileSink : ICircleLogSink, IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private StreamWriter? _writer;

    /// <summary>Logs to <paramref name="path"/>, keeping one older file as <c>path.1</c>.</summary>
    public RollingFileSink(string path, long maxBytes = 4L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "At least 1 KB.");
        _path = Path.GetFullPath(path);
        _maxBytes = maxBytes;
    }

    /// <summary>The file being written.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public void Write(in CircleLogLine line)
    {
        var text = Format(line) + "\n";
        lock (_gate)
        {
            var writer = _writer ??= Open();
            if (writer.BaseStream.Length + Encoding.UTF8.GetByteCount(text) > _maxBytes && writer.BaseStream.Length > 0)
            {
                writer.Dispose();
                File.Move(_path, _path + ".1", overwrite: true);
                writer = _writer = Open();
            }
            writer.Write(text);
        }
    }

    /// <summary>One line as it is written: time, level letter, area, message, then any exception.</summary>
    public static string Format(in CircleLogLine line)
    {
        var head = string.Create(CultureInfo.InvariantCulture,
            $"{line.At.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} {Letter(line.Level)} {line.Area}: {line.Message}");
        return line.Exception is null ? head : head + "\n" + line.Exception;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    private static char Letter(CircleLogLevel level) => level switch
    {
        CircleLogLevel.Verbose => 'V',
        CircleLogLevel.Debug => 'D',
        CircleLogLevel.Info => 'I',
        CircleLogLevel.Warn => 'W',
        CircleLogLevel.Error => 'E',
        _ => '?',
    };
}
