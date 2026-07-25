using System.IO;
using System.Runtime.InteropServices;

namespace T4Power.Cli;

/// <summary>
/// T4Power is a <c>WinExe</c> so the tray UI does not flash a console window. The cost is that
/// it starts with no console of its own, which would silently swallow all CLI output.
///
/// There are two distinct cases and they need handling in this order:
/// <list type="number">
///   <item>Output is <b>redirected</b> (piped to a file, or captured by a parent script). The
///   parent already handed us a valid stdout handle, so attaching to a console is unnecessary
///   and would be wrong.</item>
///   <item>Launched from an interactive console with no redirection — attach to the parent's
///   console so text lands where the user is looking.</item>
/// </list>
/// Checking for an inherited handle first is what makes piped invocations work.
/// </summary>
internal static class ConsoleAttach
{
    const int AttachParentProcess = -1;
    const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    static bool _done;

    /// <summary>Makes <see cref="Console"/> writable. Safe to call more than once.</summary>
    public static void Ensure(bool allocateIfMissing = false)
    {
        if (_done) return;
        _done = true;

        if (!HasInheritedStdout())
        {
            if (!AttachConsole(AttachParentProcess) && allocateIfMissing)
                AllocConsole();
        }

        // Reopen explicitly: the Console streams may already have been bound to Stream.Null
        // before a console existed, and AutoFlush matters because a WinExe can exit without
        // the finalizers that would otherwise flush.
        TrySet(Console.OpenStandardOutput, Console.SetOut);
        TrySet(Console.OpenStandardError, Console.SetError);
    }

    static bool HasInheritedStdout()
    {
        var handle = GetStdHandle(StdOutputHandle);
        return handle != IntPtr.Zero && handle != new IntPtr(-1);
    }

    static void TrySet(Func<Stream> open, Action<TextWriter> set)
    {
        try
        {
            var stream = open();
            if (stream == Stream.Null) return;
            set(new StreamWriter(stream) { AutoFlush = true });
        }
        catch (IOException)
        {
            // No usable console or pipe; output is discarded rather than crashing the CLI.
        }
    }
}
