using System.Net;
using System.Net.Sockets;
using ViperAstmEmulator.Core.Astm;
using ViperAstmEmulator.Core.Viper;
using static ViperAstmEmulator.Core.Astm.AstmChars;

namespace ViperAstmEmulator.Tests;

public class FramerTests
{
    [Fact]
    public void Checksum_matches_known_frame()
    {
        // "1H|\^&<CR><ETX>" -> sum of '1','H','|','\','^','&',CR,ETX
        var frame = AstmFramer.BuildFrame(1, "H|\\^&\r", final: true);
        int sum = "1H|\\^&\r".Sum(c => c) + ETX;
        var expected = (sum % 256).ToString("X2");
        Assert.Equal(expected, Latin1.GetString(frame, frame.Length - 4, 2));
        Assert.Equal(STX, frame[0]);
        Assert.Equal(new byte[] { CR, LF }, frame[^2..]);
    }

    [Fact]
    public void Build_then_parse_roundtrips()
    {
        var f = AstmFramer.BuildFrame(5, "R|1|^^^HPV16|POS\r", final: true);
        var p = AstmFramer.Parse(f);
        Assert.True(p.IsValid);
        Assert.Equal(5, p.FrameNumber);
        Assert.True(p.IsFinal);
        Assert.Equal("R|1|^^^HPV16|POS\r", p.Text);
    }

    [Fact]
    public void Parse_detects_bad_checksum()
    {
        var f = AstmFramer.BuildFrame(1, "L|1|N\r", final: true, corruptChecksum: true);
        var p = AstmFramer.Parse(f);
        Assert.False(p.IsValid);
        Assert.Contains("checksum", p.Error);
    }

    [Fact]
    public void Long_record_is_split_with_ETB_and_frame_numbers_wrap_mod_8()
    {
        var longRecord = "C|1|I|" + new string('x', 1000) + "|G";
        var frames = AstmFramer.BuildFrames([longRecord], 240, recordPerFrame: true);
        Assert.Equal(5, frames.Count); // 1008 chars + CR = 1009 -> 5 frames
        Assert.All(frames.Take(4), f => Assert.Equal(ETB, f[^5]));
        Assert.Equal(ETX, frames[^1][^5]);
        Assert.All(frames, f => Assert.True(f.Length <= 247));

        var many = AstmFramer.BuildFrames(Enumerable.Range(0, 10).Select(i => $"R|{i}"));
        var numbers = many.Select(f => f[1] - '0').ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 0, 1, 2 }, numbers);
    }
}

public class BuilderTests
{
    private static Sample Hpv16Sample(EmulatorSettings s)
    {
        var smp = new Sample { SpecimenId = "HPV000042", PatientId = "P1", LastName = "Doe", FirstName = "Jane", BirthDate = "19800101", Sex = "F", Rack = "R01", Position = "3" };
        smp.SetTargets(s.Targets);
        foreach (var r in smp.Results) { r.Result = ResultValues.Negative; r.Ct = ""; }
        smp.Results.First(r => r.Code == "HPV16").Result = ResultValues.Positive;
        smp.Results.First(r => r.Code == "HPV16").Ct = "27.3";
        smp.Results.First(r => r.IsControl).Result = ResultValues.Positive;
        smp.Results.First(r => r.IsControl).Ct = "24.0";
        return smp;
    }

    [Fact]
    public void Result_message_has_expected_structure()
    {
        var s = new EmulatorSettings();
        var ts = new DateTime(2026, 11, 14, 10, 30, 0);
        var recs = new ViperMessageBuilder(s).ResultMessage([Hpv16Sample(s)], ts);

        Assert.Equal(@"H|\^&|||BD Viper LT^VLT0001^3.0|||||LIS||P|LIS2-A2|20261114103000", recs[0]);
        Assert.Equal("P|1|P1|||Doe^Jane||19800101|F", recs[1]);
        Assert.Equal("O|1|HPV000042|R01^3|^^^HPV|R||||||||||Cervical|||||||20261114103000|||F", recs[2]);
        Assert.Contains(recs, r => r.StartsWith("R|") && r.Contains("|^^^HPV16|POS|"));
        Assert.Contains(recs, r => r.Contains("|^^^HPV16^CT|27.3|"));
        Assert.Contains(recs, r => r.Contains("|^^^HPV18|NEG|"));
        Assert.Contains(recs, r => r.Contains("|^^^BC|POS|"));
        Assert.Contains(recs, r => r.Contains("|^^^HPV|POS|"));
        Assert.Equal("L|1|N", recs[^1]);

        // R sequence numbers are consecutive within the order
        var seqs = recs.Where(r => r.StartsWith("R|")).Select(r => int.Parse(r.Split('|')[1])).ToList();
        Assert.Equal(Enumerable.Range(1, seqs.Count), seqs);
    }

    [Fact]
    public void Delimiters_in_data_are_escaped()
    {
        Assert.Equal("a&F&b&S&c&R&d&E&e", ViperMessageBuilder.Escape("a|b^c\\d&e"));
    }

    [Fact]
    public void Overall_is_invalid_when_control_fails_and_all_negative()
    {
        var s = new EmulatorSettings();
        var smp = new SampleFactory(s, new Random(1)).Create(Scenario.InvalidControlFailure);
        Assert.Equal(ResultValues.Invalid, smp.OverallResult);
        Assert.Equal(ResultValues.Negative, new SampleFactory(s, new Random(1)).Create(Scenario.Negative).OverallResult);
    }

    [Fact]
    public void Parses_orders_sent_by_lis()
    {
        var orders = IncomingMessageParser.ParseOrders(
        [
            @"H|\^&|||LIS",
            "P|1|PAT9|||Smit^Eva||19750505|F",
            "O|1|SPEC77||^^^HPV|R||||||N",
            "L|1|N",
        ]);
        var o = Assert.Single(orders);
        Assert.Equal("SPEC77", o.SpecimenId);
        Assert.Equal("PAT9", o.PatientId);
        Assert.Equal("Smit", o.LastName);
        Assert.Equal("HPV", o.TestCode);
        Assert.Equal("N", o.ActionCode);
    }
}

public class LinkLoopbackTests
{
    private static async Task<(Stream a, Stream b, IDisposable cleanup)> SocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var server = await acceptTask;
        listener.Stop();
        return (client.GetStream(), server.GetStream(), new Disposer(client, server));
    }

    private sealed class Disposer(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) i.Dispose(); }
    }

    private static AstmLinkOptions Fast() => new()
    {
        ReplyTimeout = TimeSpan.FromSeconds(3),
        ReceiveTimeout = TimeSpan.FromSeconds(3),
        EnqNakDelay = TimeSpan.FromMilliseconds(200),
        ContentionDelay = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task Instrument_message_is_received_intact_by_peer()
    {
        var (a, b, cleanup) = await SocketPairAsync();
        using var _ = cleanup;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var instrument = new AstmLink(a, Fast());
        var lis = new AstmLink(b, Fast());
        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        lis.MessageReceived += r => received.TrySetResult(r);

        var t1 = instrument.RunAsync(cts.Token);
        var t2 = lis.RunAsync(cts.Token);

        var s = new EmulatorSettings();
        var samples = Enumerable.Range(0, 3).Select(_ => new SampleFactory(s, new Random(7)).Create(Scenario.MultipleGenotypes)).ToList();
        var records = new ViperMessageBuilder(s).ResultMessage(samples);
        records.Insert(1, "C|1|I|" + new string('z', 600) + "|G"); // force ETB continuation frames

        Assert.True(await instrument.SendMessageAsync(records));
        var got = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(records, got);

        cts.Cancel();
        await Task.WhenAll(t1, t2);
    }

    [Fact]
    public async Task Corrupted_frame_is_NAKed_and_retransmitted()
    {
        var (a, b, cleanup) = await SocketPairAsync();
        using var _ = cleanup;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var opt = Fast();
        opt.CorruptFirstFrameChecksum = true;
        var instrument = new AstmLink(a, opt);
        var lis = new AstmLink(b, Fast());
        var log = new List<LogEntry>();
        instrument.Log += e => { lock (log) log.Add(e); };
        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        lis.MessageReceived += r => received.TrySetResult(r);

        var t1 = instrument.RunAsync(cts.Token);
        var t2 = lis.RunAsync(cts.Token);

        var records = new List<string> { @"H|\^&|||BD Viper LT", "L|1|N" };
        Assert.True(await instrument.SendMessageAsync(records));
        Assert.Equal(records, await received.Task.WaitAsync(cts.Token));
        lock (log) Assert.Contains(log, e => e.Direction == LogDirection.Rx && e.Text.StartsWith("<NAK>"));

        cts.Cancel();
        await Task.WhenAll(t1, t2);
    }

    [Fact]
    public async Task Both_sides_can_send_and_contention_resolves()
    {
        var (a, b, cleanup) = await SocketPairAsync();
        using var _ = cleanup;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var instrument = new AstmLink(a, Fast());
        var lisOpt = Fast();
        lisOpt.YieldOnContention = true;
        var lis = new AstmLink(b, lisOpt);
        var atLis = new TaskCompletionSource<IReadOnlyList<string>>();
        var atInstrument = new TaskCompletionSource<IReadOnlyList<string>>();
        lis.MessageReceived += r => atLis.TrySetResult(r);
        instrument.MessageReceived += r => atInstrument.TrySetResult(r);

        var t1 = instrument.RunAsync(cts.Token);
        var t2 = lis.RunAsync(cts.Token);

        var m1 = new List<string> { "H|\\^&|||VIPER", "L|1|N" };
        var m2 = new List<string> { "H|\\^&|||LIS", "P|1", "O|1|S1||^^^HPV", "L|1|N" };
        var s1 = instrument.SendMessageAsync(m1);
        var s2 = lis.SendMessageAsync(m2);

        Assert.True(await s1.WaitAsync(cts.Token));
        Assert.True(await s2.WaitAsync(cts.Token));
        Assert.Equal(m1, await atLis.Task.WaitAsync(cts.Token));
        Assert.Equal(m2, await atInstrument.Task.WaitAsync(cts.Token));

        cts.Cancel();
        await Task.WhenAll(t1, t2);
    }
}
