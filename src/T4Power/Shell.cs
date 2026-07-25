using System.Diagnostics;

namespace T4Power;

/// <summary>Handing things off to the Windows shell.</summary>
internal static class Shell
{
    /// <summary>
    /// Opens a URL in the user's browser, deliberately not inheriting our own token.
    ///
    /// The obvious implementation — <c>Process.Start(url, UseShellExecute: true)</c> — starts the
    /// browser as a child of this process, which means an *elevated* browser whenever we are
    /// elevated. Both callers here can be: the installer always runs elevated, and the tray UI
    /// does whenever someone has set a "run as administrator" compatibility flag on the exe.
    /// A browser running as administrator is a worse problem than the one being solved.
    ///
    /// Going via explorer.exe hands the URL to the already-running shell, which opens it in the
    /// logged-on user's normal context regardless of ours.
    /// </summary>
    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"")
        {
            UseShellExecute = true,
        })?.Dispose();
    }
}
