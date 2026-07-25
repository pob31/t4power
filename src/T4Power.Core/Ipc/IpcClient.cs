using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using T4Power.Core.Model;

namespace T4Power.Core.Ipc;

/// <summary>
/// Talks to the service's control pipe. Used by both the tray UI and the CLI, neither of which
/// is elevated — connecting needs only to be on the pipe ACL, not to hold an admin token.
/// </summary>
public sealed class IpcClient
{
    /// <summary>Distinguishes "the service isn't running" from "the service said no", which the
    /// CLI needs in order to return the right exit code.</summary>
    public sealed class UnavailableException(string message, Exception? inner = null)
        : Exception(message, inner);

    /// <summary>The caller is not on the pipe ACL.</summary>
    public sealed class AccessDeniedException(string message, Exception? inner = null)
        : Exception(message, inner);

    readonly string _pipeName;
    readonly int _timeoutMs;

    public IpcClient(string? pipeName = null, int timeoutMs = 3000)
    {
        _pipeName = pipeName ?? Paths.PipeName;
        _timeoutMs = timeoutMs;
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken token = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(_timeoutMs, token).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new UnavailableException(
                "the T4Power service is not running (start it, or run --install-service)", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new AccessDeniedException(
                "access to the T4Power control pipe was denied for this account", ex);
        }

        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, IpcJson.Options))
            .ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);

        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
            throw new UnavailableException("the service closed the connection without responding");

        try
        {
            return JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options)
                   ?? throw new UnavailableException("the service returned an unreadable response");
        }
        catch (JsonException ex)
        {
            // Most likely a version mismatch between a running service and a newer client.
            throw new UnavailableException(
                $"could not read the service's response ({ex.Message}). " +
                "If T4Power was just updated, restart the T4Power service.", ex);
        }
    }

    /// <summary>True when the service is up and answering. Used by the UI to show its status.</summary>
    public async Task<bool> IsServiceRunningAsync(CancellationToken token = default)
    {
        try
        {
            var response = await SendAsync(new IpcRequest { Command = IpcCommands.Ping }, token)
                .ConfigureAwait(false);
            return response.Ok;
        }
        catch (Exception ex) when (ex is UnavailableException or AccessDeniedException or IOException)
        {
            return false;
        }
    }
}
