using System.Net;
using System.Net.Sockets;
using ViperAstmEmulator.Core.Astm;

// Minimal LIS (host computer) simulator for testing the Viper emulator.
//   LisSimulator [port]                 listen on port (default 5000)
//   LisSimulator --connect host:port    connect to an emulator running in TCP server mode
// Replies to host queries (Q records) with an order for each queried specimen.

string? connect = null;
int port = 5000;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--connect" && i + 1 < args.Length) connect = args[++i];
    else if (int.TryParse(args[i], out var p)) port = p;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var options = new AstmLinkOptions { YieldOnContention = true };

while (!cts.IsCancellationRequested)
{
    Stream stream;
    try
    {
        if (connect != null)
        {
            var parts = connect.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], int.Parse(parts[1]), cts.Token);
            stream = client.GetStream();
            Print(ConsoleColor.Gray, $"Connected to {connect}");
        }
        else
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Print(ConsoleColor.Gray, $"LIS simulator listening on port {port} (Ctrl+C to quit)");
            var client = await listener.AcceptTcpClientAsync(cts.Token);
            listener.Stop();
            stream = client.GetStream();
            Print(ConsoleColor.Gray, $"Instrument connected from {client.Client.RemoteEndPoint}");
        }
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Print(ConsoleColor.Red, ex.Message);
        await Task.Delay(2000);
        continue;
    }

    var link = new AstmLink(stream, options);
    link.Log += e => Print(e.Direction switch
    {
        LogDirection.Tx => ConsoleColor.Cyan,
        LogDirection.Rx => ConsoleColor.Green,
        LogDirection.Warn => ConsoleColor.Yellow,
        LogDirection.Error => ConsoleColor.Red,
        _ => ConsoleColor.Gray,
    }, $"{e.Direction,-5} {e.Text}");

    link.MessageReceived += records =>
    {
        Print(ConsoleColor.White, "---- message ----");
        foreach (var r in records) Print(ConsoleColor.White, "  " + r);
        var queried = records.Where(r => r.StartsWith('Q'))
            .Select(r => r.Split('|').ElementAtOrDefault(2)?.Split('^').ElementAtOrDefault(1) ?? "")
            .Where(id => id.Length > 0).ToList();
        if (queried.Count > 0)
        {
            var reply = new List<string> { $@"H|\^&|||LIS SIMULATOR|||||VIPER||P|LIS2-A2|{DateTime.Now:yyyyMMddHHmmss}" };
            int seq = 1;
            foreach (var id in queried)
            {
                reply.Add($"P|{seq}|PAT{id}|||Query^Patient||19800101|F");
                reply.Add($"O|1|{id}||^^^HPV|R||||||N");
                seq++;
            }
            reply.Add("L|1|N");
            Print(ConsoleColor.Magenta, $"Answering host query with {queried.Count} order(s)");
            _ = link.SendMessageAsync(reply);
        }
    };

    await link.RunAsync(cts.Token);
    Print(ConsoleColor.Gray, "Connection ended");
    if (connect != null) await Task.Delay(2000);
}

static void Print(ConsoleColor color, string text)
{
    lock (Console.Out)
    {
        Console.ForegroundColor = color;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
        Console.ResetColor();
    }
}
