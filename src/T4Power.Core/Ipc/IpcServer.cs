using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using T4Power.Core.Model;

namespace T4Power.Core.Ipc;

/// <summary>
/// The control pipe, hosted by the service as LocalSystem.
///
/// This is where the privilege boundary is crossed: an unelevated client cannot call NVML, but
/// it can ask this server to. What makes that safe is not the caller's token but (a) the pipe
/// ACL, which names exactly who may connect, and (b) the handler validating and clamping every
/// request against live NVML constraints.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    /// <summary>Concurrent listener instances. A handful is plenty: the tray UI polls at 1 Hz
    /// and CLI invocations are one-shot.</summary>
    const int ListenerCount = 4;

    readonly string _pipeName;
    readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;
    readonly IReadOnlyList<string> _allowedSids;
    readonly Action<string>? _log;

    CancellationTokenSource? _cts;
    Task? _listeners;

    public IpcServer(
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler,
        IEnumerable<string>? allowedSids = null,
        Action<string>? log = null,
        string? pipeName = null)
    {
        _handler = handler;
        _allowedSids = allowedSids?.ToList() ?? [];
        _log = log;
        _pipeName = pipeName ?? Paths.PipeName;
    }

    public void Start(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _listeners = Task.WhenAll(Enumerable.Range(0, ListenerCount).Select(_ => ListenAsync(token)));
    }

    async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServerStream();
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                await ServeAsync(server, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad connection must never take down the listener; the service has to keep
                // managing the GPU regardless of what a client does.
                _log?.Invoke($"pipe listener error: {ex.Message}");
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    async Task ServeAsync(NamedPipeServerStream server, CancellationToken token)
    {
        // Not disposed via using: disposing the reader/writer would close the pipe stream that
        // the caller still owns.
        var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line)) return;

        IpcResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options);
            response = request is null
                ? IpcResponse.Failure("malformed request", 1)
                : await _handler(request, token).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            response = IpcResponse.Failure($"malformed request: {ex.Message}", 1);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"handler error: {ex}");
            response = IpcResponse.Failure(ex.Message, 8);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, IpcJson.Options))
            .ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);

        // Let the client finish reading before the stream is torn down.
        if (server.IsConnected)
        {
            try { server.WaitForPipeDrain(); }
            catch (IOException) { /* client already gone */ }
        }
    }

    NamedPipeServerStream CreateServerStream() =>
        NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            ListenerCount,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            BuildSecurity());

    /// <summary>
    /// Full control for LocalSystem (the service itself) and Administrators; read/write for each
    /// configured SID. Note the explicit absence of any grant to Users or Everyone.
    /// </summary>
    PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        foreach (var sid in _allowedSids)
        {
            try
            {
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(sid),
                    PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                    AccessControlType.Allow));
            }
            catch (ArgumentException)
            {
                // A malformed SID in config must not stop the pipe coming up at all; the
                // affected client simply gets access denied and a clear message.
                _log?.Invoke($"ignoring malformed SID in pipeAllowedSids: '{sid}'");
            }
        }

        return security;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_listeners is not null)
        {
            // Listeners are parked in WaitForConnectionAsync; give them a moment to unwind.
            try { await _listeners.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }

        _cts.Dispose();
        _cts = null;
    }
}
