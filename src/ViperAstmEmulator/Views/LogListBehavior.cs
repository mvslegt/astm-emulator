using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ViperAstmEmulator.Core.Astm;

namespace ViperAstmEmulator.Views;

/// <summary>Shared behaviour for the communication log lists: auto-scroll and copy to clipboard.</summary>
public static class LogListBehavior
{
    public static string Format(LogEntry e) => $"{e.Time:HH:mm:ss.fff}  {e.Direction,-5}  {e.Text}";

    public static void Attach(ListBox list, ObservableCollection<LogEntry> entries, Func<bool> autoScroll)
    {
        // Scroll after the ListBox has processed the change (never inside the CollectionChanged notification)
        // and coalesce bursts of log lines into one scroll.
        bool pending = false;
        entries.CollectionChanged += (_, e) =>
        {
            if (!autoScroll() || e.Action != NotifyCollectionChangedAction.Add || pending) return;
            pending = true;
            list.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                pending = false;
                if (entries.Count > 0) list.ScrollIntoView(entries[^1]);
            });
        };
        list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => Copy(list, entries)));
    }

    public static void Copy(ListBox list, ObservableCollection<LogEntry> entries)
    {
        var items = list.SelectedItems.Count > 0
            ? list.SelectedItems.Cast<LogEntry>().OrderBy(entries.IndexOf)
            : entries.AsEnumerable();
        var text = string.Join(Environment.NewLine, items.Select(Format));
        if (text.Length > 0) Clipboard.SetText(text);
    }
}

public static class HelpLauncher
{
    public static void Open()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Help", "Handleiding.html");
        if (!System.IO.File.Exists(path))
        {
            MessageBox.Show($"Handleiding niet gevonden:\n{path}", "Help", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}
