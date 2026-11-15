using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ViperAstmEmulator.Core.Astm;

namespace Hl7Emulator.Core.Hl7v2;

public sealed record Hl7SendResult(bool Sent, bool Acknowledged, string AckCode, string AckText, string? AckMessage, string? Error)
{
    public bool Accepted => Sent && (AckCode is "AA" or "CA" || (!Acknowledged && Error == null));
    public string Summary =>
        Error != null ? Error :
        !Acknowledged ? "Sent (no ACK expected)" :
        $"{AckCode} {DescribeAck(AckCode)}" + (AckText.Length > 0 ? $" - {AckText}" : "");

    public static string DescribeAck(string code) => code switch
    {
        "AA" or "CA" => "accepted",
        "AE" or "CE" => "application error",
        "AR" or "CR" => "rejected",
        _ => "",
    };
}

public sealed class MllpOptions
{
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Builds the ACK for an incoming non-ACK message; return null to not answer.</summary>
    public Func<Hl7Message, string?>? AckBuilder { get; set; }
}

/// <summary>
/// HL7 v2 over MLLP: each message is wrapped as &lt;VT&gt; message &lt;FS&gt;&lt;CR&gt;.
/// Outgoing messages are matched to their ACK by MSH-10 / MSA-2; incoming messages are raised and auto-acknowledged.
/// </summary>
public sealed class MllpConnection
{
    public const byte VT = 0x0B, FS = 0x1C, CR = 0x0D;

    private readonly Stream _stream;
    private readonly MllpOptions _opt;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Hl7Message>> _pending = new();

    public event Action<LogEntry>? Log;
    public event Action<Hl7Message, string>? MessageReceived;

    public MllpConnection(Stream stream, MllpOptions options)
    {
        _stream = stream;
        _opt = options;
    }

    public static byte[] Wrap(string message, Encoding enc)
    {
        var body = enc.GetBytes(message);
        var frame = new byte[body.Length + 3];
        frame[0] = VT;
        body.CopyTo(frame, 1);
        frame[^2] = FS;
        frame[^1] = CR;
        return frame;
    }

    public static string Visualize(string message) =>
        "<VT>" + message.Replace("\r", "<CR>\n") + "<FS><CR>";

    public async Task<Hl7SendResult> SendAsync(string message, bool waitForAck, CancellationToken ct = default)
    {
        message = message.Replace("\r\n", "\r").Replace('\n', '\r').Trim('\r');
        var parsed = Hl7Message.Parse(message);
        var controlId = parsed.ControlId;
        TaskCompletionSource<Hl7Message>? tcs = null;
        if (waitForAck && controlId.Length > 0)
        {
            tcs = new TaskCompletionSource<Hl7Message>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[controlId] = tcs;
        }

        try
        {
            await WriteAsync(message, ct);
            Write(LogDirection.Info, $"Sent {parsed.MessageType} (control ID {controlId})");
            if (tcs == null) return new(true, false, "", "", null, null);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opt.AckTimeout);
            try
            {
                var ack = await tcs.Task.WaitAsync(cts.Token);
                var r = new Hl7SendResult(true, true, ack.AckCode, ack.AckText, ack.ToDisplayText(), null);
                Write(r.Accepted ? LogDirection.Info : LogDirection.Warn, $"ACK for {controlId}: {r.Summary}");
                return r;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Write(LogDirection.Error, $"No ACK for {controlId} within {_opt.AckTimeout.TotalSeconds:0} s");
                return new(true, false, "", "", null, $"No ACK within {_opt.AckTimeout.TotalSeconds:0} s");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Write(LogDirection.Error, "Send failed: " + ex.Message);
            return new(false, false, "", "", null, "Send failed: " + ex.Message);
        }
        finally
        {
            if (controlId.Length > 0) _pending.TryRemove(controlId, out _);
        }
    }

    private async Task WriteAsync(string message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(Wrap(message, _opt.Encoding), ct);
            await _stream.FlushAsync(ct);
            Write(LogDirection.Tx, Visualize(message));
        }
        finally { _writeLock.Release(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        var msg = new MemoryStream();
        bool inMessage = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buf, ct);
                if (n <= 0) { Write(LogDirection.Warn, "Connection closed by remote side"); break; }
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (!inMessage)
                    {
                        if (b == VT) { inMessage = true; msg.SetLength(0); }
                        else if (b is not (CR or 0x0A)) Write(LogDirection.Warn, $"Ignored byte 0x{b:X2} outside MLLP frame");
                        continue; // the CR that follows FS lands here and is skipped
                    }
                    if (b == FS)
                    {
                        inMessage = false;
                        await HandleAsync(_opt.Encoding.GetString(msg.ToArray()), ct);
                        continue;
                    }
                    if (b == VT) { Write(LogDirection.Warn, "Start block inside message - previous data discarded"); msg.SetLength(0); continue; }
                    msg.WriteByte(b);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Write(LogDirection.Warn, "Connection lost: " + ex.Message);
        }
        finally
        {
            foreach (var p in _pending.Values) p.TrySetCanceled();
            try { await _stream.DisposeAsync(); } catch { }
        }
    }

    private async Task HandleAsync(string text, CancellationToken ct)
    {
        Write(LogDirection.Rx, Visualize(text.TrimEnd('\r')));
        var m = Hl7Message.Parse(text);
        if (!m.Has("MSH"))
        {
            Write(LogDirection.Warn, "Received data without MSH segment - ignored");
            return;
        }

        // A message with MSA answering one of ours (ACK, or a response such as RSP^K11 / ORL^O22).
        if (m.Has("MSA") && _pending.TryGetValue(m.AckControlId, out var tcs))
        {
            tcs.TrySetResult(m);
            if (m.MessageCode != "ACK") MessageReceived?.Invoke(m, text); // show/import responses; responses are not ACKed
            return;
        }
        if (m.MessageCode == "ACK")
        {
            Write(LogDirection.Warn, $"ACK for unknown or timed-out control ID {m.AckControlId}");
            return;
        }

        Write(LogDirection.Info, $"Received {m.MessageType} (control ID {m.ControlId})");
        MessageReceived?.Invoke(m, text);
        var ack = _opt.AckBuilder?.Invoke(m);
        if (ack != null)
        {
            try { await WriteAsync(ack, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Write(LogDirection.Error, "Could not send ACK: " + ex.Message); }
        }
    }

    private void Write(LogDirection d, string text) => Log?.Invoke(new LogEntry(DateTime.Now, d, text));
}

/// <summary>Keeps an MLLP connection alive as TCP client or server, reconnecting as configured.</summary>
public sealed class MllpSession
{
    private CancellationTokenSource? _cts;
    private Task? _run;
    private TcpListener? _listener;
    private volatile MllpConnection? _conn;

    public event Action<LogEntry>? Log;
    public event Action<Hl7Message, string>? MessageReceived;
    public event Action<bool, string?>? ConnectionChanged;
    public event Action? Stopped;

    public bool IsRunning => _run is { IsCompleted: false };
    public bool IsConnected => _conn != null;

    public void Start(MllpMode mode, string host, int port, string bindAddress, bool autoReconnect, MllpOptions options)
    {
        if (IsRunning) throw new InvalidOperationException("Already running");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _run = Task.Run(() => RunAsync(mode, host, port, bindAddress, autoReconnect, options, ct));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _listener?.Stop();
        if (_run != null) { try { await _run; } catch { } }
        _cts.Dispose();
        _cts = null;
        _run = null;
    }

    public Task<Hl7SendResult> SendAsync(string message, bool waitForAck)
    {
        var c = _conn;
        if (c == null)
        {
            Write(LogDirection.Error, "Not connected - message not sent");
            return Task.FromResult(new Hl7SendResult(false, false, "", "", null, "Not connected"));
        }
        return c.SendAsync(message, waitForAck);
    }

    private async Task RunAsync(MllpMode mode, string host, int port, string bindAddress, bool autoReconnect, MllpOptions options, CancellationToken ct)
    {
        Write(LogDirection.Info, mode == MllpMode.Client ? $"MLLP client -> {host}:{port}" : $"MLLP server on port {port}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Socket socket;
                if (mode == MllpMode.Client)
                {
                    socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(host, port, ct); }
                    catch { socket.Dispose(); throw; }
                }
                else
                {
                    if (_listener == null)
                    {
                        var ip = string.IsNullOrWhiteSpace(bindAddress) ? IPAddress.Any : IPAddress.Parse(bindAddress);
                        _listener = new TcpListener(ip, port);
                        _listener.Start(1);
                    }
                    Write(LogDirection.Info, "Waiting for LIS to connect...");
                    socket = await _listener.AcceptSocketAsync(ct);
                    socket.NoDelay = true;
                }

                var peer = socket.RemoteEndPoint?.ToString() ?? "?";
                var conn = new MllpConnection(new NetworkStream(socket, ownsSocket: true), options);
                conn.Log += e => Log?.Invoke(e);
                conn.MessageReceived += (m, raw) => MessageReceived?.Invoke(m, raw);
                _conn = conn;
                Write(LogDirection.Info, $"Connected ({peer})");
                ConnectionChanged?.Invoke(true, peer);
                try { await conn.RunAsync(ct); }
                finally
                {
                    _conn = null;
                    ConnectionChanged?.Invoke(false, null);
                    if (!ct.IsCancellationRequested) Write(LogDirection.Info, "Disconnected");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Write(LogDirection.Error, "Connection failed: " + ex.Message);
                if (mode == MllpMode.Server) { _listener?.Stop(); _listener = null; }
            }

            if (ct.IsCancellationRequested || (mode == MllpMode.Client && !autoReconnect)) break;
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
        }
        _listener?.Stop();
        _listener = null;
        Write(LogDirection.Info, "MLLP stopped");
        Stopped?.Invoke();
    }

    private void Write(LogDirection d, string text) => Log?.Invoke(new LogEntry(DateTime.Now, d, text));
}
