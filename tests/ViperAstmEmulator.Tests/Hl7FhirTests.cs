using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Hl7Emulator.Core;
using Hl7Emulator.Core.Fhir;
using Hl7Emulator.Core.Hl7v2;
using ViperAstmEmulator.Core.Viper;

namespace ViperAstmEmulator.Tests;

public class Hl7v2Tests
{
    private static Sample Hpv16(Hl7Settings s)
    {
        var smp = new SampleFactory(s, new Random(3)).Create(Scenario.Hpv16Positive);
        smp.SpecimenId = "SPEC1";
        smp.PatientId = "PAT1";
        smp.LastName = "Doe";
        smp.FirstName = "Jane";
        smp.BirthDate = "19800101";
        return smp;
    }

    [Fact]
    public void Oru_r01_has_expected_segments_and_fields()
    {
        var s = new Hl7Settings();
        var text = new Hl7MessageFactory(s).Build(Hl7MessageKind.OruR01, Hpv16(s), new DateTime(2026, 11, 15, 10, 0, 0));
        var m = Hl7Message.Parse(text);

        Assert.Equal("ORU^R01", m.MessageType);
        Assert.Equal("2.5.1", m.Version);
        Assert.Equal("HL7EMU", m.SendingApplication);
        Assert.Equal("ORU_R01", m.Get("MSH", 9, 3));
        Assert.Equal("PAT1", m.Get("PID", 3));
        Assert.Equal("Doe", m.Get("PID", 5, 1));
        Assert.Equal("Jane", m.Get("PID", 5, 2));
        Assert.Equal("SPEC1", m.Get("OBR", 3));
        Assert.Equal("F", m.Get("OBR", 25));
        Assert.Equal("SPEC1", m.Get("SPM", 2));

        var obx = m.All("OBX").ToList();
        var hpv16 = obx.First(o => m.ComponentOf(o, 3, 1) == "HPV16");
        Assert.Equal("CWE", m.FieldOf(hpv16, 2));
        Assert.Equal("POS", m.ComponentOf(hpv16, 5, 1));
        Assert.Equal("A", m.FieldOf(hpv16, 8));
        Assert.Contains(obx, o => m.ComponentOf(o, 3, 1) == "HPV16_CT" && m.FieldOf(o, 2) == "NM");
        var control = obx.First(o => m.ComponentOf(o, 3, 1) == "BC");
        Assert.Equal("N", m.FieldOf(control, 8)); // a positive internal control is normal, not abnormal
        Assert.Contains(obx, o => m.ComponentOf(o, 3, 1) == "HPV" && m.ComponentOf(o, 5, 1) == "POS");
        Assert.Equal(Enumerable.Range(1, obx.Count).Select(i => i.ToString()), obx.Select(o => o[1]));
    }

    [Fact]
    public void Version_231_uses_obr15_and_no_spm()
    {
        var s = new Hl7Settings { Hl7Version = "2.3.1", SpecimenType = "SurePath" };
        var m = Hl7Message.Parse(new Hl7MessageFactory(s).Build(Hl7MessageKind.OruR01, Hpv16(s)));
        Assert.False(m.Has("SPM"));
        Assert.Equal("SurePath", m.Get("OBR", 15));
        Assert.Equal("CE", m.FieldOf(m.All("OBX").First(), 2));
    }

    [Fact]
    public void All_message_kinds_build_with_msh_first()
    {
        var s = new Hl7Settings();
        var f = new Hl7MessageFactory(s);
        foreach (var k in Hl7MessageFactory.Kinds)
        {
            var m = Hl7Message.Parse(f.Build(k.Kind, Hpv16(s)));
            Assert.Equal("MSH", m.Segments[0][0]);
            Assert.Equal(k.Code, m.MessageType);
            Assert.NotEmpty(m.ControlId);
        }
    }

    [Fact]
    public void Escaping_roundtrips()
    {
        var escaped = Hl7Message.Escape(@"a|b^c~d\e&f");
        Assert.Equal(@"a\F\b\S\c\R\d\E\e\T\f", escaped);
        var m = Hl7Message.Parse($"MSH|^~\\&|X\rNTE|1|L|{escaped}");
        Assert.Equal(@"a|b^c~d\e&f", m.Get("NTE", 3));
    }

    [Fact]
    public void Ack_echoes_control_id_and_code()
    {
        var s = new Hl7Settings();
        var f = new Hl7MessageFactory(s);
        var incoming = Hl7Message.Parse(f.Build(Hl7MessageKind.OrmO01, Hpv16(s)));
        var ack = Hl7Message.Parse(f.Ack(incoming, AutoAckMode.Reject));
        Assert.True(ack.IsAck);
        Assert.Equal("AR", ack.AckCode);
        Assert.Equal(incoming.ControlId, ack.AckControlId);
        Assert.True(ack.Has("ERR"));
    }

    [Fact]
    public void Orders_are_parsed_from_oml()
    {
        var s = new Hl7Settings();
        var m = Hl7Message.Parse(new Hl7MessageFactory(s).Build(Hl7MessageKind.OmlO21, Hpv16(s)));
        var o = Assert.Single(IncomingOrders.FromHl7(m));
        Assert.Equal("SPEC1", o.SpecimenId);
        Assert.Equal("PAT1", o.PatientId);
        Assert.Equal("Doe", o.LastName);
        Assert.Equal("HPV", o.TestCode);
    }
}

public class MllpLoopbackTests
{
    private static async Task<(Stream a, Stream b, IDisposable cleanup)> SocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var server = await accept;
        listener.Stop();
        return (client.GetStream(), server.GetStream(), new D(client, server));
    }

    private sealed class D(params IDisposable[] x) : IDisposable { public void Dispose() { foreach (var i in x) i.Dispose(); } }

    [Theory]
    [InlineData(AutoAckMode.Accept, "AA", true)]
    [InlineData(AutoAckMode.Error, "AE", false)]
    [InlineData(AutoAckMode.Reject, "AR", false)]
    public async Task Message_is_delivered_and_acknowledged(AutoAckMode mode, string expectedCode, bool accepted)
    {
        var (a, b, cleanup) = await SocketPairAsync();
        using var _ = cleanup;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var s = new Hl7Settings();
        var f = new Hl7MessageFactory(s);

        var sender = new MllpConnection(a, new MllpOptions { AckTimeout = TimeSpan.FromSeconds(5) });
        var lis = new MllpConnection(b, new MllpOptions { AckBuilder = m => f.Ack(m, mode) });
        var received = new TaskCompletionSource<Hl7Message>();
        lis.MessageReceived += (m, _) => received.TrySetResult(m);
        var t1 = sender.RunAsync(cts.Token);
        var t2 = lis.RunAsync(cts.Token);

        var sample = new SampleFactory(s, new Random(1)).Create(Scenario.MultipleGenotypes);
        var text = f.Build(Hl7MessageKind.OruR01, sample);
        var result = await sender.SendAsync(text, waitForAck: true, cts.Token);

        Assert.True(result.Acknowledged);
        Assert.Equal(expectedCode, result.AckCode);
        Assert.Equal(accepted, result.Accepted);
        var got = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(text, got.ToString());

        cts.Cancel();
        await Task.WhenAll(t1, t2);
    }

    [Fact]
    public async Task Missing_ack_times_out()
    {
        var (a, b, cleanup) = await SocketPairAsync();
        using var _ = cleanup;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sender = new MllpConnection(a, new MllpOptions { AckTimeout = TimeSpan.FromMilliseconds(500) });
        var silent = new MllpConnection(b, new MllpOptions { AckBuilder = _ => null });
        var t1 = sender.RunAsync(cts.Token);
        var t2 = silent.RunAsync(cts.Token);

        var s = new Hl7Settings();
        var msg = new Hl7MessageFactory(s).Build(Hl7MessageKind.AdtA04, new SampleFactory(s).Create());
        var r = await sender.SendAsync(msg, waitForAck: true, cts.Token);
        Assert.False(r.Acknowledged);
        Assert.Contains("No ACK", r.Error);

        cts.Cancel();
        await Task.WhenAll(t1, t2);
    }
}

public class FhirTests
{
    private static Sample Sample(Hl7Settings s)
    {
        var smp = new SampleFactory(s, new Random(5)).Create(Scenario.Hpv18Positive);
        smp.SpecimenId = "SPEC9";
        smp.PatientId = "PAT9";
        return smp;
    }

    [Fact]
    public void Result_transaction_bundle_is_consistent()
    {
        var s = new Hl7Settings();
        var bundle = new FhirBuilder(s).TransactionBundle(Sample(s), withResults: true);
        Assert.Equal("transaction", bundle["type"]!.GetValue<string>());
        var entries = bundle["entry"]!.AsArray();
        var urls = entries.Select(e => e!["fullUrl"]!.GetValue<string>()).ToHashSet();
        Assert.All(entries, e => Assert.Equal("POST", e!["request"]!["method"]!.GetValue<string>()));

        var report = entries.Select(e => e!["resource"]!).Single(r => r["resourceType"]!.GetValue<string>() == "DiagnosticReport");
        var resultRefs = report["result"]!.AsArray().Select(r => r!["reference"]!.GetValue<string>()).ToList();
        Assert.All(resultRefs, r => Assert.Contains(r, urls));        // every result reference resolves inside the bundle
        Assert.Equal(s.Targets.Count + 1, resultRefs.Count);         // all targets + overall

        var hpv18 = entries.Select(e => e!["resource"]!).First(r => r["code"]?["coding"]?[0]?["code"]?.GetValue<string>() == "HPV18");
        Assert.Equal("10828004", hpv18["valueCodeableConcept"]!["coding"]![0]!["code"]!.GetValue<string>());
        Assert.Equal("POS", hpv18["interpretation"]![0]!["coding"]![0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void Orders_are_parsed_from_order_transaction()
    {
        var s = new Hl7Settings();
        var bundle = new FhirBuilder(s).TransactionBundle(Sample(s), withResults: false);
        var o = Assert.Single(IncomingOrders.FromFhir(bundle));
        Assert.Equal("SPEC9", o.SpecimenId);
        Assert.Equal("PAT9", o.PatientId);
        Assert.Equal("F", o.Sex);
    }

    [Fact]
    public async Task Client_and_server_roundtrip_over_http()
    {
        var s = new Hl7Settings();
        int port = FreePort();
        var server = new FhirServer();
        var exchanges = new List<FhirExchange>();
        server.RequestHandled += e => { lock (exchanges) exchanges.Add(e); };
        server.Start("localhost", port, "fhir");
        try
        {
            using var client = new FhirClient();
            var baseUrl = $"http://localhost:{port}/fhir";
            var builder = new FhirBuilder(s);

            var meta = await client.SendAsync(baseUrl, builder.Build(FhirRequestKind.Metadata, null), null, TimeSpan.FromSeconds(10));
            Assert.Equal(200, meta.StatusCode);
            Assert.Contains("CapabilityStatement", meta.Body);

            var tx = await client.SendAsync(baseUrl, builder.Build(FhirRequestKind.OrderTransaction, Sample(s)), null, TimeSpan.FromSeconds(10));
            Assert.Equal(200, tx.StatusCode);
            Assert.Contains("transaction-response", tx.Body);

            var msg = await client.SendAsync(baseUrl, builder.Build(FhirRequestKind.ResultMessage, Sample(s)), null, TimeSpan.FromSeconds(10));
            Assert.Equal(200, msg.StatusCode);
            Assert.Contains("\"code\": \"ok\"", msg.Body);

            var search = await client.SendAsync(baseUrl, builder.Build(FhirRequestKind.SearchOrders, null), null, TimeSpan.FromSeconds(10));
            Assert.Equal(200, search.StatusCode);
            var orders = IncomingOrders.FromFhir(JsonNode.Parse(search.Body));
            Assert.Contains(orders, o => o.SpecimenId == "SPEC9");

            var patient = await client.SendAsync(baseUrl, builder.Build(FhirRequestKind.PatientCreate, Sample(s)), null, TimeSpan.FromSeconds(10));
            Assert.Equal(201, patient.StatusCode);

            server.ResponseMode = FhirServerResponseMode.RejectBadRequest;
            var rejected = await client.SendAsync(baseUrl, builder.Build(FhirRequestKind.PatientCreate, Sample(s)), null, TimeSpan.FromSeconds(10));
            Assert.Equal(400, rejected.StatusCode);
            Assert.Contains("OperationOutcome", rejected.Body);

            lock (exchanges) Assert.Equal(6, exchanges.Count);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
