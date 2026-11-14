using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ftx1.App.ViewModels;
namespace Ftx1.App;

// Station settings and the CAT console share the main view model, so the
// connection and the log stay the same whichever window is used.
public partial class SettingsWindow : Window
{
    public const int ConnectionTab = 0, ConsoleTab = 1;
    readonly MainViewModel? model;
    public SettingsWindow() { AvaloniaXamlLoader.Load(this); }
    public SettingsWindow(MainViewModel model) : this()
    {
        this.model = model; DataContext = model;
        model.CatLog.CollectionChanged += ScrollToEnd;
        Closed += (_, _) => model.CatLog.CollectionChanged -= ScrollToEnd;
    }
    public void ShowTab(int tab)
    {
        this.FindControl<TabControl>("Tabs")!.SelectedIndex = tab;
        if (tab == ConsoleTab) Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("CommandBox")?.Focus());
    }
    void ScrollToEnd(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || model == null || model.CatLog.Count == 0) return;
        Dispatcher.UIThread.Post(() => { if (model.CatLog.Count > 0) this.FindControl<ListBox>("Log")?.ScrollIntoView(model.CatLog.Count - 1); });
    }
    void CommandKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && model != null) { model.SendRawCommand.Execute(null); e.Handled = true; }
    }
}
