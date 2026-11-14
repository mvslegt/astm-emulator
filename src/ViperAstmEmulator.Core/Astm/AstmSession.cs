namespace ViperAstmEmulator.Core.Astm;

/// <summary>
/// Keeps an ASTM link alive over a transport: connects, runs the link, and reconnects
/// (always for server/serial, optionally for TCP client).
/// </summary>
public sealed class AstmSession
{
    private CancellationTokenSource? _cts;
    private Task? _run;
    private IAstmTransport? _transport;
    private volatile AstmLink? _link;

    public event Action<LogEntry>? Log;
    public event Action<IReadOnlyList<string>>? MessageReceived;
    /// <summary>Raised with (connected, peer).</summary>
    public event Action<bool, string?>? ConnectionChanged;

    public bool IsRunning => _run is { IsCompleted: false };
    public bool IsConnected => _link != null;

    public void Start(IAstmTransport transport, AstmLinkOptions options, bool autoReconnect)
    {
        if (IsRunning) throw new InvalidOperationException("Session already running");
        _transport = transport;
        _cts = new CancellationTokenSource();
        _run = Task.Run(() => RunAsync(transport, options, autoReconnect, _cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        if (_transport != null) await _transport.DisposeAsync();
        if (_run != null)
        {
            try { await _run; } catch { /* logged */ }
        }
        _cts.Dispose();
        _cts = null;
        _run = null;
        _transport = null;
    }

    public Task<bool> SendAsync(IReadOnlyList<string> records)
    {
        var link = _link;
        if (link == null)
        {
            Write(LogDirection.Error, "Not connected - message not sent");
            return Task.FromResult(false);
        }
        return link.SendMessageAsync(records);
    }

    private async Task RunAsync(IAstmTransport transport, AstmLinkOptions options, bool autoReconnect, CancellationToken ct)
    {
        Write(LogDirection.Info, $"Starting {transport.Description}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (transport is TcpServerTransport) Write(LogDirection.Info, "Waiting for LIS to connect...");
                var (stream, peer) = await transport.ConnectAsync(ct);
                var link = new AstmLink(stream, options);
                link.Log += e => Log?.Invoke(e);
                link.MessageReceived += r => MessageReceived?.Invoke(r);
                _link = link;
                Write(LogDirection.Info, $"Connected ({peer})");
                ConnectionChanged?.Invoke(true, peer);
                try { await link.RunAsync(ct); }
                finally
                {
                    _link = null;
                    ConnectionChanged?.Invoke(false, null);
                    if (!ct.IsCancellationRequested) Write(LogDirection.Info, "Disconnected");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Write(LogDirection.Error, "Connection failed: " + ex.Message);
            }

            if (ct.IsCancellationRequested || (!autoReconnect && !transport.IsPersistent)) break;
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
            if (!transport.IsPersistent) Write(LogDirection.Info, "Reconnecting...");
        }
        Write(LogDirection.Info, "Stopped");
        Stopped?.Invoke();
    }

    /// <summary>Raised when the session loop ends (user stop, or a client connection that will not be retried).</summary>
    public event Action? Stopped;

    private void Write(LogDirection d, string text) => Log?.Invoke(new LogEntry(DateTime.Now, d, text));
}
