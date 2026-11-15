using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ViperAstmEmulator.Views;

namespace ViperAstmEmulator;

public partial class MainWindow : Window
{
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Any(a => a.StartsWith("--hl7", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--fhir", StringComparison.OrdinalIgnoreCase)))
                ProtocolTabs.SelectedIndex = 1;
            await Task.WhenAll(Astm.ViewModel.ApplyStartupArgsAsync(args), Hl7.ViewModel.ApplyStartupArgsAsync(args));
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F1) { HelpLauncher.Open(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        try { await Task.WhenAll(Astm.ViewModel.ShutdownAsync(), Hl7.ViewModel.ShutdownAsync()); }
        catch { /* never block closing */ }
        // The shutdown can complete synchronously (nothing connected), which would put us still inside this
        // Closing event; calling Close() there throws. Queue it so it runs after the current close has unwound.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
