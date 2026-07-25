using System.Text;

namespace T4Power.Core;

/// <summary>
/// Minimal rolling file logger. The service runs as LocalSystem with no console, so a log file
/// is the only way to find out what it did overnight.
/// </summary>
public sealed class FileLog : IDisposable
{
    const long MaxBytes = 2 * 1024 * 1024;

    readonly string _path;
    readonly object _gate = new();
    readonly bool _alsoConsole;

    public FileLog(string? path = null, bool alsoConsole = false)
    {
        _path = path ?? Paths.LogFile;
        _alsoConsole = alsoConsole;
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!); }
        catch (IOException) { /* logging must never be the reason the service fails to start */ }
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";

        if (_alsoConsole) Console.WriteLine(line);

        lock (_gate)
        {
            try
            {
                Roll();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Swallow: a full or locked disk must not stop the GPU being managed.
            }
        }
    }

    void Roll()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes) return;

        var previous = _path + ".1";
        try
        {
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(_path, previous);
        }
        catch (IOException) { /* keep appending to the current file */ }
    }

    public void Dispose() { }
}
