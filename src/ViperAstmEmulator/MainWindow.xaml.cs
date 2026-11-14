using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ViperAstmEmulator.Core.Astm;

namespace ViperAstmEmulator;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Scroll after the ListBox has processed the change (never inside the CollectionChanged notification),
        // and coalesce bursts of log lines into one scroll.
        bool scrollPending = false;
        _vm.LogEntries.CollectionChanged += (_, e) =>
        {
            if (!_vm.AutoScroll || e.Action != NotifyCollectionChangedAction.Add || scrollPending) return;
            scrollPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                scrollPending = false;
                if (_vm.LogEntries.Count > 0) LogList.ScrollIntoView(_vm.LogEntries[^1]);
            });
        };

        LogList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopyLog()));
        Loaded += async (_, _) => await _vm.ApplyStartupArgsAsync(Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopyLog();

    private void Help_Click(object sender, RoutedEventArgs e) => OpenHelp();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F1) { OpenHelp(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private static void OpenHelp()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Help", "Handleiding.html");
        if (!System.IO.File.Exists(path))
        {
            MessageBox.Show($"Handleiding niet gevonden:\n{path}", "Help", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void CopyLog()
    {
        var items = LogList.SelectedItems.Count > 0
            ? LogList.SelectedItems.Cast<LogEntry>().OrderBy(x => _vm.LogEntries.IndexOf(x))
            : _vm.LogEntries.AsEnumerable();
        var text = string.Join(Environment.NewLine, items.Select(MainViewModel.Format));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        await _vm.ShutdownAsync();
        Close();
    }
}
