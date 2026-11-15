using System.Windows;
using System.Windows.Controls;

namespace ViperAstmEmulator.Views;

public partial class AstmView : UserControl
{
    public AstmViewModel ViewModel { get; }

    public AstmView()
    {
        InitializeComponent();
        ViewModel = new AstmViewModel();
        DataContext = ViewModel;
        LogListBehavior.Attach(LogList, ViewModel.LogEntries, () => ViewModel.AutoScroll);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => LogListBehavior.Copy(LogList, ViewModel.LogEntries);

    private void Help_Click(object sender, RoutedEventArgs e) => HelpLauncher.Open();
}
