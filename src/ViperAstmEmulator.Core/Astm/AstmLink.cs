using System.Text;
using System.Threading.Channels;
using static ViperAstmEmulator.Core.Astm.AstmChars;

namespace ViperAstmEmulator.Core.Astm;

public enum LogDirection { Tx, Rx, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogDirection Direction, string Text);

public sealed class AstmLinkOptions
{
    public int MaxFrameText { get; set; } = AstmFramer.MaxFrameText;
    public bool RecordPerFrame { get; set; } = true;

    /// <summary>Sender waits this long for a reply to ENQ or a frame (LIS1-A: 15 s).</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Receiver waits this long for the next frame or EOT (LIS1-A: 30 s).</summary>
    public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Delay after a NAK to ENQ before retrying (LIS1-A: 10 s).</summary>
    public TimeSpan EnqNakDelay { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Instrument waits this long after line contention before re-sending ENQ (LIS1-A: ≥ 1 s).</summary>
    public TimeSpan ContentionDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Max transmissions of one frame before aborting (LIS1-A: 6).</summary>
    public int MaxFrameRetries { get; set; } = 6;
    public int MaxEnqAttempts { get; set; } = 6;

    /// <summary>
    /// Computer-system behaviour on line contention: yield and receive first (true).
    /// The instrument side (false, default) keeps priority and re-bids after <see cref="ContentionDelay"/>.
    /// </summary>
    public bool YieldOnContention { get; set; }

    /// <summary>Error injection: send a bad checksum on the first attempt of the first frame of each message.</summary>
    public bool CorruptFirstFrameChecksum { get; set; }
    /// <summary>Error injection: answer the first received frame of each message with NAK.</summary>
    public bool NakFirstReceivedFrame { get; set; }
}

public sealed class ConnectionClosedException() : Exception("Connection closed by remote side");

/// <summary>
/// One ASTM E1381 / LIS1-A link over a byte stream, emulating the instrument side.
/// Runs the establishment / transfer / termination phases for sending and receiving.
/// </summary>
public sealed class AstmLink
{
    private readonly Stream _stream;
    private readonly AstmLinkOptions _opt;
    private readonly Channel<byte> _rx = Channel.CreateUnbounded<byte>(new() { SingleReader = true, SingleWriter = true });
    private readonly Channel<(IReadOnlyList<string> Records, TaskCompletionSource<bool> Done)> _tx =
        Channel.CreateUnbounded<(IReadOnlyList<string>, TaskCompletionSource<bool>)>();

    public event Action<LogEntry>? Log;
    public event Action<IReadOnlyList<string>>? MessageReceived;

    public AstmLink(Stream stream, AstmLinkOptions options)
    {
        _stream = stream;
        _opt = options;
    }

    /// <summary>Queues a message (list of records, without CR) for transmission. Completes true when the receiver accepted all frames.</summary>
    public Task<bool> SendMessageAsync(IReadOnlyList<string> records)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_tx.Writer.TryWrite((records, tcs))) tcs.TrySetResult(false);
        return tcs.Task;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = Task.Run(() => ReadLoopAsync(cts.Token), CancellationToken.None);
        try
        {
            await ProcessLoopAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopped by caller
        }
        catch (ConnectionClosedException)
        {
            Write(LogDirection.Warn, "Connection closed by remote side");
        }
        catch (IOException ex)
        {
            Write(LogDirection.Warn, "Connection lost: " + ex.Message);
        }
        finally
        {
            cts.Cancel();
            _tx.Writer.TryComplete();
            while (_tx.Reader.TryRead(out var pending)) pending.Done.TrySetResult(false);
            try { await _stream.DisposeAsync(); } catch { /* ignore */ }
            try { await reader; } catch { /* ignore */ }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buf, ct);
                if (n <= 0) break;
                for (int i = 0; i < n; i++) _rx.Writer.TryWrite(buf[i]);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Write(LogDirection.Warn, "Read error: " + ex.Message); }
        finally { _rx.Writer.TryComplete(); }
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Incoming data first: if the host already bid for the line, let it have it.
            if (_rx.Reader.TryRead(out var early))
            {
                await HandleIdleByteAsync(early, ct);
                continue;
            }
            if (_tx.Reader.TryRead(out var job))
            {
                bool ok;
                try { ok = await SendInternalAsync(job.Records, ct); }
                catch { job.Done.TrySetResult(false); throw; }
                job.Done.TrySetResult(ok);
                continue;
            }
            int b = await ReadByteAsync(TimeSpan.FromMilliseconds(100), ct);
            if (b >= 0) await HandleIdleByteAsync((byte)b, ct);
        }
    }

    private async Task HandleIdleByteAsync(byte b, CancellationToken ct)
    {
        if (b == ENQ)
        {
            Write(LogDirection.Rx, Name(ENQ));
            await ReceiveInternalAsync(ct);
        }
        else
        {
            Write(LogDirection.Rx, $"{Name(b)} (ignored while idle)");
        }
    }

    // ---------------- Sending ----------------

    private async Task<bool> SendInternalAsync(IReadOnlyList<string> records, CancellationToken ct)
    {
        var frames = AstmFramer.BuildFrames(records, _opt.MaxFrameText, _opt.RecordPerFrame);
        Write(LogDirection.Info, $"Sending message: {records.Count} record(s), {frames.Count} frame(s)");

        // Establishment phase
        int attempts = 0;
        while (true)
        {
            if (++attempts > _opt.MaxEnqAttempts)
            {
                Write(LogDirection.Error, "Could not establish link (too many attempts); message not sent");
                return false;
            }
            await SendBytesAsync([ENQ], ct);
            int reply = await ReadByteAsync(_opt.ReplyTimeout, ct);
            if (reply == ACK) { Write(LogDirection.Rx, Name(ACK)); break; }
            if (reply < 0)
            {
                Write(LogDirection.Error, $"No reply to ENQ within {_opt.ReplyTimeout.TotalSeconds:0} s; sending EOT");
                await SendBytesAsync([EOT], ct);
                return false;
            }
            if (reply == NAK)
            {
                Write(LogDirection.Rx, $"{Name(NAK)} (receiver busy) - retry in {_opt.EnqNakDelay.TotalSeconds:0} s");
                await DelayHandlingIncomingAsync(_opt.EnqNakDelay, ct);
                continue;
            }
            if (reply == ENQ && _opt.YieldOnContention)
            {
                Write(LogDirection.Rx, $"{Name(ENQ)} (line contention - yielding to instrument)");
                attempts--;
                int next = await ReadByteAsync(TimeSpan.FromSeconds(20), ct);
                if (next >= 0) await HandleIdleByteAsync((byte)next, ct);
                continue;
            }
            if (reply == ENQ)
            {
                // Contention: the instrument has priority. Computer must yield; we wait >= 1 s and bid again.
                Write(LogDirection.Rx, $"{Name(ENQ)} (line contention - instrument has priority, re-bidding)");
                await Task.Delay(_opt.ContentionDelay, ct);
                continue;
            }
            Write(LogDirection.Rx, $"{Name((byte)reply)} (unexpected reply to ENQ, ignored)");
            attempts--; // garbage does not count as an attempt
        }

        // Transfer phase
        bool interruptRequested = false;
        for (int i = 0; i < frames.Count; i++)
        {
            int tries = 0;
            while (true)
            {
                tries++;
                var frame = frames[i];
                if (_opt.CorruptFirstFrameChecksum && i == 0 && tries == 1)
                {
                    frame = (byte[])frame.Clone();
                    frame[^4] = frame[^4] == (byte)'0' ? (byte)'1' : (byte)'0';
                    Write(LogDirection.Warn, "Error injection: corrupting checksum of first frame");
                }
                await SendBytesAsync(frame, ct);
                int reply = await ReadByteAsync(_opt.ReplyTimeout, ct);
                if (reply == ACK) { Write(LogDirection.Rx, Name(ACK)); break; }
                if (reply == EOT)
                {
                    // Receiver interrupt request: treated as ACK; sender may finish or stop. We finish the message.
                    Write(LogDirection.Rx, $"{Name(EOT)} (receiver interrupt request, treated as ACK)");
                    interruptRequested = true;
                    break;
                }
                if (reply < 0)
                {
                    Write(LogDirection.Error, $"No reply to frame within {_opt.ReplyTimeout.TotalSeconds:0} s; aborting with EOT");
                    await SendBytesAsync([EOT], ct);
                    return false;
                }
                Write(LogDirection.Rx, reply == NAK ? $"{Name(NAK)} (frame rejected, attempt {tries}/{_opt.MaxFrameRetries})" : $"{Name((byte)reply)} (unexpected, treated as NAK)");
                if (tries >= _opt.MaxFrameRetries)
                {
                    Write(LogDirection.Error, "Frame rejected too many times; aborting with EOT");
                    await SendBytesAsync([EOT], ct);
                    return false;
                }
            }
        }

        // Termination phase
        await SendBytesAsync([EOT], ct);
        Write(LogDirection.Info, interruptRequested ? "Message sent (receiver requested interrupt)" : "Message sent and acknowledged");
        return true;
    }

    /// <summary>Waits, but still services an ENQ from the host so the line is not blocked.</summary>
    private async Task DelayHandlingIncomingAsync(TimeSpan delay, CancellationToken ct)
    {
        var until = DateTime.UtcNow + delay;
        while (true)
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return;
            int b = await ReadByteAsync(left, ct);
            if (b >= 0) await HandleIdleByteAsync((byte)b, ct);
        }
    }

    // ---------------- Receiving ----------------

    private async Task ReceiveInternalAsync(CancellationToken ct)
    {
        await SendBytesAsync([ACK], ct);
        int expectedFn = 1;
        bool nakInjected = false;
        var text = new StringBuilder();
        var frame = new List<byte>(256);

        while (true)
        {
            int b = await ReadByteAsync(_opt.ReceiveTimeout, ct);
            if (b < 0)
            {
                Write(LogDirection.Error, $"Receive timeout ({_opt.ReceiveTimeout.TotalSeconds:0} s); message discarded, link back to idle");
                return;
            }
            if (b == EOT)
            {
                Write(LogDirection.Rx, Name(EOT));
                break;
            }
            if (b != STX)
            {
                Write(LogDirection.Rx, $"{Name((byte)b)} (ignored, waiting for STX)");
                continue;
            }

            frame.Clear();
            frame.Add(STX);
            while (true)
            {
                int c = await ReadByteAsync(_opt.ReceiveTimeout, ct);
                if (c < 0)
                {
                    Write(LogDirection.Error, "Receive timeout inside frame; message discarded");
                    return;
                }
                frame.Add((byte)c);
                if (c == LF || frame.Count > 1024) break;
            }

            var raw = frame.ToArray();
            Write(LogDirection.Rx, Visualize(raw));
            var parsed = AstmFramer.Parse(raw);

            if (_opt.NakFirstReceivedFrame && !nakInjected)
            {
                nakInjected = true;
                Write(LogDirection.Warn, "Error injection: rejecting first received frame");
                await SendBytesAsync([NAK], ct);
                continue;
            }

            if (!parsed.IsValid)
            {
                Write(LogDirection.Warn, "Bad frame: " + parsed.Error);
                await SendBytesAsync([NAK], ct);
                continue;
            }
            if (parsed.FrameNumber == expectedFn)
            {
                text.Append(parsed.Text);
                expectedFn = (expectedFn + 1) % 8;
                await SendBytesAsync([ACK], ct);
            }
            else if (parsed.FrameNumber == (expectedFn + 7) % 8)
            {
                Write(LogDirection.Warn, $"Duplicate frame {parsed.FrameNumber} - acknowledged and ignored");
                await SendBytesAsync([ACK], ct);
            }
            else
            {
                Write(LogDirection.Warn, $"Unexpected frame number {parsed.FrameNumber} (expected {expectedFn})");
                await SendBytesAsync([NAK], ct);
            }
        }

        var records = AstmFramer.SplitRecords(text.ToString());
        Write(LogDirection.Info, $"Message received: {records.Count} record(s)");
        if (records.Count > 0) MessageReceived?.Invoke(records);
    }

    // ---------------- I/O helpers ----------------

    private async Task<int> ReadByteAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_rx.Reader.TryRead(out var b)) return b;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (await _rx.Reader.WaitToReadAsync(cts.Token))
                if (_rx.Reader.TryRead(out b)) return b;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return -1; // timeout
        }
        ct.ThrowIfCancellationRequested();
        throw new ConnectionClosedException();
    }

    private async Task SendBytesAsync(byte[] data, CancellationToken ct)
    {
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
        Write(LogDirection.Tx, Visualize(data));
    }

    private void Write(LogDirection d, string text) => Log?.Invoke(new LogEntry(DateTime.Now, d, text));
}
