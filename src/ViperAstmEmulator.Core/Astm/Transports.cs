using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace ViperAstmEmulator.Core.Astm;

public enum ConnectionMode { TcpClient, TcpServer, Serial }

/// <summary>Opens the byte stream an <see cref="AstmLink"/> runs on.</summary>
public interface IAstmTransport : IAsyncDisposable
{
    string Description { get; }
    /// <summary>True if the transport should keep accepting/reopening after a connection ends.</summary>
    bool IsPersistent { get; }
    Task<(Stream Stream, string Peer)> ConnectAsync(CancellationToken ct);
}

public sealed class TcpClientTransport(string host, int port) : IAstmTransport
{
    public string Description => $"TCP client -> {host}:{port}";
    public bool IsPersistent => false;

    public async Task<(Stream, string)> ConnectAsync(CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct);
            return (new NetworkStream(socket, ownsSocket: true), socket.RemoteEndPoint?.ToString() ?? $"{host}:{port}");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class TcpServerTransport(int port, string? bindAddress = null) : IAstmTransport
{
    private TcpListener? _listener;
    public string Description => $"TCP server on {(string.IsNullOrWhiteSpace(bindAddress) ? "0.0.0.0" : bindAddress)}:{port}";
    public bool IsPersistent => true;

    public async Task<(Stream, string)> ConnectAsync(CancellationToken ct)
    {
        if (_listener == null)
        {
            var ip = string.IsNullOrWhiteSpace(bindAddress) ? IPAddress.Any : IPAddress.Parse(bindAddress);
            _listener = new TcpListener(ip, port);
            _listener.Start(1);
        }
        var socket = await _listener.AcceptSocketAsync(ct);
        socket.NoDelay = true;
        return (new NetworkStream(socket, ownsSocket: true), socket.RemoteEndPoint?.ToString() ?? "?");
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _listener = null;
        return ValueTask.CompletedTask;
    }
}

public sealed class SerialTransport(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Handshake handshake = Handshake.None) : IAstmTransport
{
    private SerialPort? _port;
    public string Description => $"Serial {portName} {baudRate} {dataBits}{parity.ToString()[0]}{(stopBits == StopBits.One ? "1" : stopBits == StopBits.Two ? "2" : "1.5")}";
    public bool IsPersistent => true;

    public Task<(Stream, string)> ConnectAsync(CancellationToken ct)
    {
        _port?.Dispose();
        _port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
        {
            Handshake = handshake,
            Encoding = AstmChars.Latin1,
            DtrEnable = true,
            RtsEnable = handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff),
        };
        _port.Open();
        // Closing the port is the only reliable way to cancel a pending serial read.
        var port = _port;
        ct.Register(() => { try { port.Close(); } catch { } });
        return Task.FromResult<(Stream, string)>((port.BaseStream, portName));
    }

    public ValueTask DisposeAsync()
    {
        try { _port?.Close(); } catch { }
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}
