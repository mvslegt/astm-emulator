using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Hl7Emulator.Core;
using Hl7Emulator.Core.Fhir;
using Hl7Emulator.Core.Hl7v2;
using Microsoft.Win32;
using ViperAstmEmulator.Core.Astm;
using ViperAstmEmulator.Core.Viper;

namespace ViperAstmEmulator.Views;

public sealed record ReceivedItem(DateTime Time, string Channel, string Summary, string Detail)
{
    public string Title => $"{Time:HH:mm:ss}  [{Channel}]  {Summary}";
}

public sealed record Option<T>(T Value, string Title, string Description)
{
    public string Display => $"{Title}  -  {Description}";
}

public sealed class Hl7ViewModel : ObservableObject
{
    private const int MaxLogEntries = 5000;

    private readonly System.Windows.Threading.Dispatcher _ui = Application.Current.Dispatcher;
    private readonly MllpSession _mllp = new();
    private readonly FhirServer _fhirServer = new();
    private readonly FhirClient _fhirClient = new();
    private readonly SampleFactory _factory;

    public Hl7Settings Settings { get; }
    public ObservableCollection<Sample> Samples { get; } = new();
    public ObservableCollection<LogEntry> LogEntries { get; } = new();
    public ObservableCollection<ReceivedItem> ReceivedItems { get; } = new();

    public IReadOnlyList<Hl7MessageKindInfo> Hl7Kinds { get; } = Hl7MessageFactory.Kinds;
    public IReadOnlyList<FhirRequestKindInfo> FhirKinds { get; } = FhirBuilder.Kinds;
    public IReadOnlyList<SpecimenTypeOption> SpecimenTypeOptions { get; } = SpecimenTypes.Known;
    public Scenario[] Scenarios { get; } = Enum.GetValues<Scenario>();
    public string[] ResultValueOptions { get; } = ResultValues.All;
    public string[] Hl7Versions { get; } = ["2.3.1", "2.4", "2.5", "2.5.1", "2.6"];
    public string[] CharacterSets { get; } = ["UTF-8", "ISO-8859-1"];
    public string[] HttpMethods { get; } = ["POST", "PUT", "GET", "DELETE"];
    public MllpMode[] MllpModes { get; } = Enum.GetValues<MllpMode>();

    public IReadOnlyList<Option<AutoAckMode>> AckModes { get; } =
    [
        new(AutoAckMode.Accept, "AA", "accept every message"),
        new(AutoAckMode.Error, "AE", "answer with application error"),
        new(AutoAckMode.Reject, "AR", "reject every message"),
        new(AutoAckMode.None, "None", "do not acknowledge (test LIS timeouts)"),
    ];

    public IReadOnlyList<Option<FhirServerResponseMode>> FhirResponseModes { get; } =
    [
        new(FhirServerResponseMode.Accept, "Accept", "store and answer 200/201"),
        new(FhirServerResponseMode.RejectBadRequest, "400", "reject with Bad Request + OperationOutcome"),
        new(FhirServerResponseMode.ServerError, "500", "answer with Internal Server Error"),
    ];

    public Hl7ViewModel()
    {
        Settings = Hl7Settings.Load();
        _factory = new SampleFactory(Settings);
        _fhirServer.ResponseMode = Settings.FhirServerResponseMode;

        _mllp.Log += e => _ui.BeginInvoke(() => AddLog(e));
        _mllp.MessageReceived += (m, raw) => _ui.BeginInvoke(() => OnHl7Received(m));
        _mllp.ConnectionChanged += (c, peer) => _ui.BeginInvoke(() =>
        {
            IsMllpConnected = c;
            MllpStatus = c ? $"HL7 connected - {peer}" : IsMllpRunning ? MllpWaitingText() : "HL7 disconnected";
        });
        _mllp.Stopped += () => _ui.BeginInvoke(() =>
        {
            IsMllpRunning = false;
            IsMllpConnected = false;
            MllpStatus = "HL7 disconnected";
        });
        _fhirServer.Log += e => _ui.BeginInvoke(() => AddLog(e with { Text = "FHIR server: " + e.Text }));
        _fhirServer.RequestHandled += x => _ui.BeginInvoke(() => OnFhirReceived(x));

        MllpConnectCommand = new RelayCommand(_ => MllpConnect(), _ => !IsMllpRunning);
        MllpDisconnectCommand = new RelayCommand(_ => MllpDisconnectAsync(), _ => IsMllpRunning);
        FhirServerStartCommand = new RelayCommand(_ => FhirServerStart(), _ => !IsFhirServerRunning);
        FhirServerStopCommand = new RelayCommand(_ => FhirServerStopAsync(), _ => IsFhirServerRunning);

        AddSampleCommand = new RelayCommand(_ => AddSamples(1));
        AddTenCommand = new RelayCommand(_ => AddSamples(10));
        ApplyScenarioCommand = new RelayCommand(p => { foreach (var s in Selected(p)) { _factory.Apply(s, SelectedScenario); s.Status = "Edited"; } }, p => Selected(p).Count > 0);
        RemoveSamplesCommand = new RelayCommand(p => { foreach (var s in Selected(p)) Samples.Remove(s); }, p => Selected(p).Count > 0);
        ClearSamplesCommand = new RelayCommand(_ => Samples.Clear(), _ => Samples.Count > 0);

        SendHl7SelectedCommand = new RelayCommand(p => SendHl7ForSamplesAsync(Selected(p)), p => IsMllpConnected && Selected(p).Count > 0);
        BuildHl7Command = new RelayCommand(_ =>
        {
            var s = SelectedSample ?? Samples.FirstOrDefault() ?? throw new InvalidOperationException("Add a specimen first.");
            Hl7Editor = Hl7Message.Parse(new Hl7MessageFactory(Settings).Build(SelectedHl7Kind.Kind, s)).ToDisplayText();
        });
        SendHl7EditorCommand = new RelayCommand(_ => SendHl7TextAsync(Hl7Message.FromDisplayText(Hl7Editor), null),
            _ => IsMllpConnected && !string.IsNullOrWhiteSpace(Hl7Editor));

        SendFhirSelectedCommand = new RelayCommand(p => SendFhirForSamplesAsync(Selected(p)), p => Selected(p).Count > 0 || SelectedFhirKindNeedsNoSpecimen);
        BuildFhirCommand = new RelayCommand(_ => BuildFhir());
        SendFhirEditorCommand = new RelayCommand(_ => SendFhirAsync(new FhirRequest(FhirMethod, FhirPath, FhirBody), null));

        ClearLogCommand = new RelayCommand(_ => LogEntries.Clear());
        SaveLogCommand = new RelayCommand(_ => SaveLog());
        ClearReceivedCommand = new RelayCommand(_ => ReceivedItems.Clear());

        _selectedHl7Kind = Hl7Kinds[0];
        _selectedFhirKind = FhirKinds[0];
        _fhirMethod = _selectedFhirKind.Method;
        _fhirPath = _selectedFhirKind.Path;

        AddSamples(5);
        AddLog(new LogEntry(DateTime.Now, LogDirection.Info, $"Settings: {Hl7Settings.DefaultPath}"));
    }

    // ---------------- Commands ----------------

    public RelayCommand MllpConnectCommand { get; }
    public RelayCommand MllpDisconnectCommand { get; }
    public RelayCommand FhirServerStartCommand { get; }
    public RelayCommand FhirServerStopCommand { get; }
    public RelayCommand AddSampleCommand { get; }
    public RelayCommand AddTenCommand { get; }
    public RelayCommand ApplyScenarioCommand { get; }
    public RelayCommand RemoveSamplesCommand { get; }
    public RelayCommand ClearSamplesCommand { get; }
    public RelayCommand SendHl7SelectedCommand { get; }
    public RelayCommand BuildHl7Command { get; }
    public RelayCommand SendHl7EditorCommand { get; }
    public RelayCommand SendFhirSelectedCommand { get; }
    public RelayCommand BuildFhirCommand { get; }
    public RelayCommand SendFhirEditorCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand SaveLogCommand { get; }
    public RelayCommand ClearReceivedCommand { get; }

    // ---------------- State ----------------

    public MllpMode MllpMode
    {
        get => Settings.MllpMode;
        set { Settings.MllpMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsMllpClient)); OnPropertyChanged(nameof(IsMllpServer)); }
    }
    public bool IsMllpClient => MllpMode == MllpMode.Client;
    public bool IsMllpServer => MllpMode == MllpMode.Server;

    private bool _isMllpRunning;
    public bool IsMllpRunning { get => _isMllpRunning; private set { if (Set(ref _isMllpRunning, value)) OnPropertyChanged(nameof(CanEditMllp)); } }
    public bool CanEditMllp => !IsMllpRunning;

    private bool _isMllpConnected;
    public bool IsMllpConnected { get => _isMllpConnected; private set => Set(ref _isMllpConnected, value); }

    private string _mllpStatus = "HL7 disconnected";
    public string MllpStatus { get => _mllpStatus; private set => Set(ref _mllpStatus, value); }

    private bool _isFhirServerRunning;
    public bool IsFhirServerRunning { get => _isFhirServerRunning; private set { if (Set(ref _isFhirServerRunning, value)) OnPropertyChanged(nameof(CanEditFhirServer)); } }
    public bool CanEditFhirServer => !IsFhirServerRunning;

    private string _fhirServerStatus = "FHIR server stopped";
    public string FhirServerStatus { get => _fhirServerStatus; private set => Set(ref _fhirServerStatus, value); }

    public AutoAckMode AutoAckMode
    {
        get => Settings.AutoAckMode;
        set { Settings.AutoAckMode = value; OnPropertyChanged(); }
    }

    public FhirServerResponseMode FhirServerResponseMode
    {
        get => Settings.FhirServerResponseMode;
        set { Settings.FhirServerResponseMode = value; _fhirServer.ResponseMode = value; OnPropertyChanged(); }
    }

    public string SpecimenType
    {
        get => Settings.SpecimenType;
        set { Settings.SpecimenType = value?.Trim() ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(SpecimenTypeDescription)); }
    }
    public string SpecimenTypeDescription => SpecimenTypes.Find(Settings.SpecimenType)?.Description
        ?? (string.IsNullOrWhiteSpace(Settings.SpecimenType) ? "No specimen type" : "Custom code (not in the known list)");

    private Sample? _selectedSample;
    public Sample? SelectedSample { get => _selectedSample; set => Set(ref _selectedSample, value); }

    private Scenario _selectedScenario = Scenario.Random;
    public Scenario SelectedScenario { get => _selectedScenario; set => Set(ref _selectedScenario, value); }

    private Hl7MessageKindInfo _selectedHl7Kind;
    public Hl7MessageKindInfo SelectedHl7Kind { get => _selectedHl7Kind; set => Set(ref _selectedHl7Kind, value); }

    private string _hl7Editor = "";
    public string Hl7Editor { get => _hl7Editor; set => Set(ref _hl7Editor, value); }

    private string _lastAck = "";
    public string LastAck { get => _lastAck; private set => Set(ref _lastAck, value); }

    private string _lastAckSummary = "No message sent yet";
    public string LastAckSummary { get => _lastAckSummary; private set => Set(ref _lastAckSummary, value); }

    private FhirRequestKindInfo _selectedFhirKind;
    public FhirRequestKindInfo SelectedFhirKind
    {
        get => _selectedFhirKind;
        set
        {
            if (!Set(ref _selectedFhirKind, value) || value == null) return;
            FhirMethod = value.Method;
            FhirPath = value.Path;
            OnPropertyChanged(nameof(SelectedFhirKindNeedsNoSpecimen));
        }
    }
    public bool SelectedFhirKindNeedsNoSpecimen => SelectedFhirKind.Kind is FhirRequestKind.SearchOrders or FhirRequestKind.Metadata;

    private string _fhirMethod = "POST";
    public string FhirMethod { get => _fhirMethod; set => Set(ref _fhirMethod, value); }

    private string _fhirPath = "";
    public string FhirPath { get => _fhirPath; set => Set(ref _fhirPath, value); }

    private string _fhirBody = "";
    public string FhirBody { get => _fhirBody; set => Set(ref _fhirBody, value); }

    private string _fhirResponseSummary = "No request sent yet";
    public string FhirResponseSummary { get => _fhirResponseSummary; private set => Set(ref _fhirResponseSummary, value); }

    private string _fhirResponse = "";
    public string FhirResponse { get => _fhirResponse; private set => Set(ref _fhirResponse, value); }

    private ReceivedItem? _selectedReceived;
    public ReceivedItem? SelectedReceived { get => _selectedReceived; set => Set(ref _selectedReceived, value); }

    private bool _autoScroll = true;
    public bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value); }

    private int _sent, _failed, _received;
    public string Counters => $"Sent: {_sent}   Failed: {_failed}   Received: {_received}";

    // ---------------- MLLP ----------------

    private string MllpWaitingText() => Settings.MllpMode == MllpMode.Server
        ? $"HL7 listening on port {Settings.MllpPort}..." : $"HL7 connecting to {Settings.MllpHost}:{Settings.MllpPort}...";

    private MllpOptions CreateMllpOptions() => new()
    {
        Encoding = Settings.TextEncoding,
        AckTimeout = TimeSpan.FromSeconds(Math.Max(1, Settings.AckTimeoutSeconds)),
        // Reads the current setting on every message, so the ACK mode can be changed while connected.
        AckBuilder = m => Settings.AutoAckMode == AutoAckMode.None ? null : new Hl7MessageFactory(Settings).Ack(m, Settings.AutoAckMode),
    };

    private void MllpConnect()
    {
        _mllp.Start(Settings.MllpMode, Settings.MllpHost, Settings.MllpPort, Settings.MllpBindAddress, Settings.MllpAutoReconnect, CreateMllpOptions());
        IsMllpRunning = true;
        MllpStatus = MllpWaitingText();
    }

    private async Task MllpDisconnectAsync()
    {
        await _mllp.StopAsync();
        IsMllpRunning = false;
        IsMllpConnected = false;
        MllpStatus = "HL7 disconnected";
    }

    private async Task<bool> SendHl7TextAsync(string text, Sample? sample)
    {
        if (sample != null) sample.Status = "Sending HL7...";
        var r = await _mllp.SendAsync(text, Settings.WaitForAck);
        LastAckSummary = r.Summary;
        LastAck = r.AckMessage ?? "";
        bool ok = r.Sent && (r.Accepted || !Settings.WaitForAck);
        if (ok) _sent++; else _failed++;
        OnPropertyChanged(nameof(Counters));
        if (sample != null)
        {
            sample.Status = ok ? (r.Acknowledged ? $"HL7 {r.AckCode}" : "HL7 sent") : (r.Acknowledged ? $"HL7 {r.AckCode}" : "HL7 failed");
            if (ok) sample.LastSent = DateTime.Now;
        }
        return ok;
    }

    private async Task SendHl7ForSamplesAsync(List<Sample> samples)
    {
        var f = new Hl7MessageFactory(Settings);
        foreach (var s in samples)
        {
            var text = f.Build(SelectedHl7Kind.Kind, s);
            Hl7Editor = Hl7Message.Parse(text).ToDisplayText();
            await SendHl7TextAsync(text, s);
            if (!IsMllpConnected) break;
        }
    }

    private void OnHl7Received(Hl7Message m)
    {
        _received++;
        OnPropertyChanged(nameof(Counters));
        var ackInfo = m.Has("MSA") ? $" (response {m.AckCode} to {m.AckControlId})" : "";
        AddReceived(new ReceivedItem(DateTime.Now, "HL7 v2", $"{m.MessageType} from {m.SendingApplication}{ackInfo}", m.ToDisplayText()));
        ImportOrders(IncomingOrders.FromHl7(m), "HL7");
    }

    // ---------------- FHIR ----------------

    private void FhirServerStart()
    {
        _fhirServer.ResponseMode = Settings.FhirServerResponseMode;
        _fhirServer.Start(Settings.FhirServerHost, Settings.FhirServerPort, Settings.FhirServerPath);
        IsFhirServerRunning = true;
        FhirServerStatus = $"FHIR server on {_fhirServer.Prefix}";
    }

    private async Task FhirServerStopAsync()
    {
        await _fhirServer.StopAsync();
        IsFhirServerRunning = false;
        FhirServerStatus = "FHIR server stopped";
    }

    private void BuildFhir()
    {
        var s = SelectedSample ?? Samples.FirstOrDefault();
        var req = new FhirBuilder(Settings).Build(SelectedFhirKind.Kind, s);
        FhirMethod = req.Method;
        FhirPath = req.Path;
        FhirBody = req.Body ?? "";
    }

    private async Task<bool> SendFhirAsync(FhirRequest req, Sample? sample)
    {
        if (string.IsNullOrWhiteSpace(Settings.FhirBaseUrl)) throw new InvalidOperationException("Enter the LIS FHIR base URL first (FHIR tab).");
        var url = FhirClient.Combine(Settings.FhirBaseUrl, req.Path);
        AddLog(new LogEntry(DateTime.Now, LogDirection.Tx, $"{req.Method} {url}" + (string.IsNullOrEmpty(req.Body) ? "" : $"\n{req.Body}")));
        if (sample != null) sample.Status = "Sending FHIR...";

        var r = await _fhirClient.SendAsync(Settings.FhirBaseUrl, req, Settings.FhirBearerToken, TimeSpan.FromSeconds(Math.Max(1, Settings.FhirTimeoutSeconds)));
        FhirResponseSummary = r.Summary;
        FhirResponse = r.Error ?? (r.Headers + Environment.NewLine + Environment.NewLine + r.Body);
        AddLog(new LogEntry(DateTime.Now, r.IsSuccess ? LogDirection.Rx : LogDirection.Error, r.Summary + (r.Body.Length > 0 ? "\n" + r.Body : "")));

        if (r.IsSuccess) _sent++; else _failed++;
        OnPropertyChanged(nameof(Counters));
        if (sample != null)
        {
            sample.Status = r.IsSuccess ? $"FHIR {r.StatusCode}" : (r.StatusCode > 0 ? $"FHIR {r.StatusCode}" : "FHIR failed");
            if (r.IsSuccess) sample.LastSent = DateTime.Now;
        }

        // A search (e.g. open ServiceRequests) returns orders to import.
        if (r.IsSuccess && req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            try { ImportOrders(IncomingOrders.FromFhir(JsonNode.Parse(r.Body)), "FHIR search"); }
            catch (JsonException) { }
        }
        return r.IsSuccess;
    }

    private async Task SendFhirForSamplesAsync(List<Sample> samples)
    {
        var b = new FhirBuilder(Settings);
        if (SelectedFhirKindNeedsNoSpecimen)
        {
            BuildFhir();
            await SendFhirAsync(new FhirRequest(FhirMethod, FhirPath, null), null);
            return;
        }
        foreach (var s in samples)
        {
            var req = b.Build(SelectedFhirKind.Kind, s);
            FhirMethod = req.Method;
            FhirPath = req.Path;
            FhirBody = req.Body ?? "";
            await SendFhirAsync(req, s);
        }
    }

    private void OnFhirReceived(FhirExchange x)
    {
        _received++;
        OnPropertyChanged(nameof(Counters));
        var json = x.RequestJson;
        var what = json?["resourceType"]?.ToString() ?? "";
        if (what == "Bundle") what += $" ({json?["type"]}, {json?["entry"]?.AsArray().Count ?? 0} entries)";
        var detail = $"{x.Method} {x.Url}{Environment.NewLine}{Environment.NewLine}{x.RequestBody}" +
                     $"{Environment.NewLine}{Environment.NewLine}--- Response {x.StatusCode} ---{Environment.NewLine}{x.ResponseBody}";
        AddReceived(new ReceivedItem(DateTime.Now, "FHIR", $"{x.Method} {x.Url} {what} -> {x.StatusCode}", detail));
        if (x.StatusCode < 300 && x.Method is "POST" or "PUT") ImportOrders(IncomingOrders.FromFhir(json), "FHIR");
    }

    // ---------------- Shared ----------------

    private void ImportOrders(List<IncomingOrder> orders, string source)
    {
        if (!Settings.AddReceivedOrdersToList) return;
        foreach (var o in orders)
        {
            var s = Samples.FirstOrDefault(x => x.SpecimenId == o.SpecimenId);
            bool isNew = s == null;
            if (s == null)
            {
                s = new Sample { SpecimenId = o.SpecimenId };
                s.SetTargets(Settings.Targets);
                _factory.Apply(s, SelectedScenario);
                Samples.Add(s);
            }
            if (o.PatientId.Length > 0) s.PatientId = o.PatientId;
            if (o.LastName.Length > 0) s.LastName = o.LastName;
            if (o.FirstName.Length > 0) s.FirstName = o.FirstName;
            if (o.BirthDate.Length > 0) s.BirthDate = o.BirthDate;
            if (o.Sex.Length > 0) s.Sex = o.Sex;
            s.Status = $"Ordered ({source})";
            AddLog(new LogEntry(DateTime.Now, LogDirection.Info, $"Order {(isNew ? "added" : "updated")} from {source}: specimen {o.SpecimenId}" + (o.TestCode.Length > 0 ? $", test {o.TestCode}" : "")));
        }
    }

    private void AddReceived(ReceivedItem item)
    {
        ReceivedItems.Insert(0, item);
        SelectedReceived = item;
    }

    private List<Sample> Selected(object? p)
    {
        if (p is IList list && list.Count > 0) return list.OfType<Sample>().ToList();
        return SelectedSample != null ? [SelectedSample] : [];
    }

    private void AddSamples(int count)
    {
        Sample? last = null;
        for (int i = 0; i < count; i++) Samples.Add(last = _factory.Create(SelectedScenario));
        SelectedSample = last;
    }

    private void AddLog(LogEntry e)
    {
        LogEntries.Add(e);
        while (LogEntries.Count > MaxLogEntries) LogEntries.RemoveAt(0);
    }

    private void SaveLog()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"hl7-fhir-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true) File.WriteAllLines(dlg.FileName, LogEntries.Select(LogListBehavior.Format));
    }

    /// <summary>Command line: --fhir-server (start receiving), --hl7-connect (connect MLLP), --hl7-send-all (send results of all specimens).</summary>
    public async Task<bool> ApplyStartupArgsAsync(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        bool used = false;
        if (Has("--fhir-server"))
        {
            used = true;
            try { FhirServerStart(); }
            catch (Exception ex) { AddLog(new LogEntry(DateTime.Now, LogDirection.Error, ex.Message)); }
        }
        if (Has("--hl7-connect") || Has("--hl7-send-all"))
        {
            used = true;
            MllpConnect();
            for (int i = 0; i < 100 && !IsMllpConnected; i++) await Task.Delay(100);
            if (IsMllpConnected && Has("--hl7-send-all")) await SendHl7ForSamplesAsync(Samples.ToList());
        }
        return used;
    }

    public async Task ShutdownAsync()
    {
        try { Settings.Save(); } catch { }
        var stop = Task.WhenAll(_mllp.StopAsync(), _fhirServer.StopAsync());
        await Task.WhenAny(stop, Task.Delay(2000));
        _fhirClient.Dispose();
    }
}
