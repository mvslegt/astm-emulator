using System.Windows;
using System.Windows.Controls;

namespace ViperAstmEmulator.Views;

public partial class Hl7View : UserControl
{
    public Hl7ViewModel ViewModel { get; }

    public Hl7View()
    {
        InitializeComponent();
        ViewModel = new Hl7ViewModel();
        DataContext = ViewModel;
        LogListBehavior.Attach(LogList, ViewModel.LogEntries, () => ViewModel.AutoScroll);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => LogListBehavior.Copy(LogList, ViewModel.LogEntries);

    private void Help_Click(object sender, RoutedEventArgs e) => HelpLauncher.Open();
}
