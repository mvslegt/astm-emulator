using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ViperAstmEmulator.Core.Astm;
using ViperAstmEmulator.Core.Viper;

namespace ViperAstmEmulator;

public sealed record ReceivedMessage(DateTime Time, string Summary, string Text)
{
    public string Title => $"{Time:HH:mm:ss}  {Summary}";
}

public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 5000;

    private readonly AstmSession _session = new();
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _autoTimer = new();
    private readonly SampleFactory _factory;
    private AstmLinkOptions? _activeOptions;

    public EmulatorSettings Settings { get; }
    public ObservableCollection<Sample> Samples { get; } = new();
    public ObservableCollection<LogEntry> LogEntries { get; } = new();
    public ObservableCollection<ReceivedMessage> ReceivedMessages { get; } = new();

    public ConnectionMode[] Modes { get; } = Enum.GetValues<ConnectionMode>();
    public Parity[] Parities { get; } = Enum.GetValues<Parity>();
    public StopBits[] StopBitsValues { get; } = [StopBits.One, StopBits.OnePointFive, StopBits.Two];
    public Handshake[] Handshakes { get; } = Enum.GetValues<Handshake>();
    public int[] BaudRates { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];
    public int[] DataBitsValues { get; } = [7, 8];
    public CtReporting[] CtReportingValues { get; } = Enum.GetValues<CtReporting>();
    public Scenario[] Scenarios { get; } = Enum.GetValues<Scenario>();
    public string[] ResultValueOptions { get; } = ResultValues.All;
    public IReadOnlyList<SpecimenTypeOption> SpecimenTypeOptions { get; } = SpecimenTypes.Known;

    /// <summary>O.16 specimen descriptor: picked from the known list or typed as a custom code.</summary>
    public string SpecimenType
    {
        get => Settings.SpecimenType;
        set
        {
            Settings.SpecimenType = value?.Trim() ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpecimenTypeDescription));
        }
    }

    public string SpecimenTypeDescription => SpecimenTypes.Find(Settings.SpecimenType)?.Description
        ?? (string.IsNullOrWhiteSpace(Settings.SpecimenType) ? "No specimen type - O.16 is left empty" : "Custom code (not in the known list)");

    private string[] _comPorts = SerialPort.GetPortNames();
    public string[] ComPorts { get => _comPorts; private set => Set(ref _comPorts, value); }

    public MainViewModel()
    {
        Settings = EmulatorSettings.Load();
        _factory = new SampleFactory(Settings);

        _session.Log += e => _ui.BeginInvoke(() => AddLog(e));
        _session.MessageReceived += r => _ui.BeginInvoke(() => OnMessageReceived(r));
        _session.ConnectionChanged += (c, peer) => _ui.BeginInvoke(() =>
        {
            IsConnected = c;
            ConnectionStatus = c ? $"Connected - {peer}" : IsRunning ? WaitingText() : "Disconnected";
        });
        _session.Stopped += () => _ui.BeginInvoke(() =>
        {
            IsRunning = false;
            IsConnected = false;
            ConnectionStatus = "Disconnected";
        });

        _autoTimer.Tick += async (_, _) => await AutoSendTickAsync();

        ConnectCommand = new RelayCommand(_ => Connect(), _ => !IsRunning);
        DisconnectCommand = new RelayCommand(_ => DisconnectAsync(), _ => IsRunning);
        RefreshPortsCommand = new RelayCommand(_ => ComPorts = SerialPort.GetPortNames());

        AddSampleCommand = new RelayCommand(_ => AddSamples(1));
        AddTenCommand = new RelayCommand(_ => AddSamples(10));
        ApplyScenarioCommand = new RelayCommand(p => { foreach (var s in Selected(p)) { _factory.Apply(s, SelectedScenario); s.Status = "Edited"; } }, p => Selected(p).Count > 0);
        RemoveSamplesCommand = new RelayCommand(p => { foreach (var s in Selected(p)) Samples.Remove(s); }, p => Selected(p).Count > 0);
        ClearSamplesCommand = new RelayCommand(_ => Samples.Clear(), _ => Samples.Count > 0);

        SendSelectedCommand = new RelayCommand(p => SendSamplesAsync(Selected(p)), p => IsConnected && Selected(p).Count > 0);
        SendAllCommand = new RelayCommand(_ => SendSamplesAsync(Samples.ToList()), _ => IsConnected && Samples.Count > 0);
        HostQueryCommand = new RelayCommand(p => SendHostQueryAsync(Selected(p)), p => IsConnected && Selected(p).Count > 0);

        PreviewResultsCommand = new RelayCommand(p =>
        {
            var list = Selected(p);
            if (list.Count == 0) list = Samples.Take(1).ToList();
            MessagePreview = ViperMessageBuilder.ToDisplayText(new ViperMessageBuilder(Settings).ResultMessage(list));
        });
        PreviewQueryCommand = new RelayCommand(p =>
        {
            var ids = Selected(p).Select(s => s.SpecimenId).DefaultIfEmpty("SPECIMEN1");
            MessagePreview = ViperMessageBuilder.ToDisplayText(new ViperMessageBuilder(Settings).HostQuery(ids));
        });
        SendPreviewCommand = new RelayCommand(_ => SendRawAsync(ViperMessageBuilder.FromDisplayText(MessagePreview)),
            _ => IsConnected && !string.IsNullOrWhiteSpace(MessagePreview));

        ClearLogCommand = new RelayCommand(_ => LogEntries.Clear());
        SaveLogCommand = new RelayCommand(_ => SaveLog());
        ClearReceivedCommand = new RelayCommand(_ => ReceivedMessages.Clear());

        AddTargetCommand = new RelayCommand(_ => Settings.Targets.Add(new TargetDefinition("NEW", "New target")));
        RemoveTargetCommand = new RelayCommand(p => { if (p is TargetDefinition t) Settings.Targets.Remove(t); }, p => p is TargetDefinition);
        ResetTargetsCommand = new RelayCommand(_ =>
        {
            Settings.Targets.Clear();
            foreach (var t in TargetDefinition.OnclarityDefaults()) Settings.Targets.Add(t);
        });

        AddSamples(5);
        AddLog(new LogEntry(DateTime.Now, LogDirection.Info, $"Settings: {EmulatorSettings.DefaultPath}"));
    }

    // ---------------- Commands ----------------

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand AddSampleCommand { get; }
    public RelayCommand AddTenCommand { get; }
    public RelayCommand ApplyScenarioCommand { get; }
    public RelayCommand RemoveSamplesCommand { get; }
    public RelayCommand ClearSamplesCommand { get; }
    public RelayCommand SendSelectedCommand { get; }
    public RelayCommand SendAllCommand { get; }
    public RelayCommand HostQueryCommand { get; }
    public RelayCommand PreviewResultsCommand { get; }
    public RelayCommand PreviewQueryCommand { get; }
    public RelayCommand SendPreviewCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand SaveLogCommand { get; }
    public RelayCommand ClearReceivedCommand { get; }
    public RelayCommand AddTargetCommand { get; }
    public RelayCommand RemoveTargetCommand { get; }
    public RelayCommand ResetTargetsCommand { get; }

    // ---------------- Bindable state ----------------

    public ConnectionMode Mode
    {
        get => Settings.Mode;
        set
        {
            Settings.Mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSerial));
            OnPropertyChanged(nameof(IsTcp));
            OnPropertyChanged(nameof(IsTcpClient));
            OnPropertyChanged(nameof(IsTcpServer));
        }
    }

    public bool IsSerial => Mode == ConnectionMode.Serial;
    public bool IsTcp => !IsSerial;
    public bool IsTcpClient => Mode == ConnectionMode.TcpClient;
    public bool IsTcpServer => Mode == ConnectionMode.TcpServer;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(CanEditConnection)); }
    }
    public bool CanEditConnection => !IsRunning;

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }

    private string _connectionStatus = "Disconnected";
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }

    private Sample? _selectedSample;
    public Sample? SelectedSample { get => _selectedSample; set => Set(ref _selectedSample, value); }

    private Scenario _selectedScenario = Scenario.Random;
    public Scenario SelectedScenario { get => _selectedScenario; set => Set(ref _selectedScenario, value); }

    private string _messagePreview = "";
    public string MessagePreview { get => _messagePreview; set => Set(ref _messagePreview, value); }

    private ReceivedMessage? _selectedReceived;
    public ReceivedMessage? SelectedReceived { get => _selectedReceived; set => Set(ref _selectedReceived, value); }

    private bool _autoScroll = true;
    public bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value); }

    private int _sentCount, _failedCount, _receivedCount;
    public string Counters => $"Sent: {_sentCount}   Failed: {_failedCount}   Received: {_receivedCount}";

    public bool CorruptFirstFrameChecksum
    {
        get => Settings.CorruptFirstFrameChecksum;
        set { Settings.CorruptFirstFrameChecksum = value; if (_activeOptions != null) _activeOptions.CorruptFirstFrameChecksum = value; OnPropertyChanged(); }
    }

    public bool NakFirstReceivedFrame
    {
        get => Settings.NakFirstReceivedFrame;
        set { Settings.NakFirstReceivedFrame = value; if (_activeOptions != null) _activeOptions.NakFirstReceivedFrame = value; OnPropertyChanged(); }
    }

    private bool _autoSend;
    public bool AutoSend
    {
        get => _autoSend;
        set
        {
            if (!Set(ref _autoSend, value)) return;
            if (value)
            {
                _autoTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.AutoSendIntervalSeconds));
                _autoTimer.Start();
                AddLog(new LogEntry(DateTime.Now, LogDirection.Info, $"Auto-send started: one random specimen every {_autoTimer.Interval.TotalSeconds:0} s"));
            }
            else
            {
                _autoTimer.Stop();
                AddLog(new LogEntry(DateTime.Now, LogDirection.Info, "Auto-send stopped"));
            }
        }
    }

    // ---------------- Connection ----------------

    private string WaitingText() => Settings.Mode switch
    {
        ConnectionMode.TcpServer => $"Listening on port {Settings.Port}...",
        ConnectionMode.Serial => $"Opening {Settings.ComPort}...",
        _ => $"Connecting to {Settings.Host}:{Settings.Port}...",
    };

    private void Connect()
    {
        _activeOptions = Settings.ToLinkOptions();
        _session.Start(Settings.CreateTransport(), _activeOptions, Settings.AutoReconnect);
        IsRunning = true;
        ConnectionStatus = WaitingText();
    }

    private async Task DisconnectAsync()
    {
        AutoSend = false;
        await _session.StopAsync();
        _activeOptions = null;
        IsRunning = false;
        IsConnected = false;
        ConnectionStatus = "Disconnected";
    }

    /// <summary>Command line: --connect, --send-all (after connecting), --auto-send.</summary>
    public async Task ApplyStartupArgsAsync(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (!Has("--connect")) return;
        Connect();
        for (int i = 0; i < 100 && !IsConnected; i++) await Task.Delay(100);
        if (!IsConnected) return;
        if (Has("--send-all")) await SendSamplesAsync(Samples.ToList());
        if (Has("--auto-send")) AutoSend = true;
    }

    public async Task ShutdownAsync()
    {
        _autoTimer.Stop();
        try { Settings.Save(); } catch { }
        var stop = _session.StopAsync();
        await Task.WhenAny(stop, Task.Delay(2000));
    }

    // ---------------- Samples & sending ----------------

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

    private async Task SendSamplesAsync(List<Sample> samples)
    {
        var builder = new ViperMessageBuilder(Settings);
        if (Settings.BatchSamples)
        {
            var records = builder.ResultMessage(samples);
            MessagePreview = ViperMessageBuilder.ToDisplayText(records);
            foreach (var s in samples) s.Status = "Sending...";
            bool ok = await _session.SendAsync(records);
            foreach (var s in samples) Mark(s, ok);
        }
        else
        {
            foreach (var s in samples)
            {
                var records = builder.ResultMessage([s]);
                MessagePreview = ViperMessageBuilder.ToDisplayText(records);
                s.Status = "Sending...";
                bool ok = await _session.SendAsync(records);
                Mark(s, ok);
                if (!IsConnected) break;
            }
        }
    }

    private void Mark(Sample s, bool ok)
    {
        if (ok) { s.Status = "Sent"; s.LastSent = DateTime.Now; _sentCount++; }
        else { s.Status = "Failed"; _failedCount++; }
        OnPropertyChanged(nameof(Counters));
    }

    private async Task SendHostQueryAsync(List<Sample> samples)
    {
        var records = new ViperMessageBuilder(Settings).HostQuery(samples.Select(s => s.SpecimenId));
        MessagePreview = ViperMessageBuilder.ToDisplayText(records);
        foreach (var s in samples) s.Status = "Querying...";
        bool ok = await _session.SendAsync(records);
        foreach (var s in samples) s.Status = ok ? "Query sent" : "Query failed";
    }

    private async Task SendRawAsync(List<string> records)
    {
        bool ok = await _session.SendAsync(records);
        if (ok) _sentCount++; else _failedCount++;
        OnPropertyChanged(nameof(Counters));
    }

    private async Task AutoSendTickAsync()
    {
        if (!IsConnected) return;
        _autoTimer.Stop();
        try
        {
            var s = _factory.Create(Scenario.Random);
            Samples.Add(s);
            await SendSamplesAsync([s]);
        }
        finally
        {
            if (AutoSend)
            {
                _autoTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.AutoSendIntervalSeconds));
                _autoTimer.Start();
            }
        }
    }

    // ---------------- Incoming ----------------

    private void OnMessageReceived(IReadOnlyList<string> records)
    {
        _receivedCount++;
        OnPropertyChanged(nameof(Counters));
        var msg = new ReceivedMessage(DateTime.Now, $"{records.Count} records: {IncomingMessageParser.Describe(records)}", string.Join(Environment.NewLine, records));
        ReceivedMessages.Insert(0, msg);
        SelectedReceived = msg;

        if (!Settings.AddReceivedOrdersToList) return;
        foreach (var o in IncomingMessageParser.ParseOrders(records))
        {
            var s = Samples.FirstOrDefault(x => x.SpecimenId == o.SpecimenId);
            bool isNew = s == null;
            if (s == null)
            {
                s = new Sample { SpecimenId = o.SpecimenId };
                s.SetTargets(Settings.Targets);
                _factory.Apply(s, SelectedScenario);
                s.Rack = "R99";
                s.Position = (Samples.Count % 24 + 1).ToString();
                Samples.Add(s);
            }
            if (o.PatientId.Length > 0) s.PatientId = o.PatientId;
            if (o.LastName.Length > 0) s.LastName = o.LastName;
            if (o.FirstName.Length > 0) s.FirstName = o.FirstName;
            if (o.BirthDate.Length > 0) s.BirthDate = o.BirthDate;
            if (o.Sex.Length > 0) s.Sex = o.Sex;
            s.Status = "Ordered by LIS";
            AddLog(new LogEntry(DateTime.Now, LogDirection.Info,
                $"Order {(isNew ? "added" : "updated")}: specimen {o.SpecimenId}, test {o.TestCode}" + (o.ActionCode.Length > 0 ? $", action {o.ActionCode}" : "")));
        }
    }

    // ---------------- Log ----------------

    private void AddLog(LogEntry e)
    {
        LogEntries.Add(e);
        while (LogEntries.Count > MaxLogEntries) LogEntries.RemoveAt(0);
    }

    public static string Format(LogEntry e) => $"{e.Time:HH:mm:ss.fff}  {e.Direction,-5}  {e.Text}";

    private void SaveLog()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"viper-astm-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            File.WriteAllLines(dlg.FileName, LogEntries.Select(Format));
    }
}
