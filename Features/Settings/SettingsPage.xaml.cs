using System.Windows;
using System.Windows.Controls;
using NtfyDesktop.Features.Settings.Dialogs;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace NtfyDesktop.Features.Settings;

public partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;

        // Re-read settings every time the page is shown (page is transient, VM is a
        // singleton). Snapshot reset happens inside Load(), so IsDirty starts false.
        Loaded += (_, _) => _vm.Load();
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.SaveCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unexpected error: " + ex.Message);
        }
    }

    private void OnDiscardClicked(object sender, RoutedEventArgs e) => _vm.Load();

    // ===== Server management (immediate-persist via dialog) =====

    private async void OnAddServerClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ServerEditorDialog(existing: null) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;
            await _vm.AddOrUpdateServerAsync(dialog.Result, original: null);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unexpected error: " + ex.Message);
        }
    }

    private async void OnEditServerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServerConfig server }) return;
        try
        {
            var dialog = new ServerEditorDialog(server) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;
            await _vm.AddOrUpdateServerAsync(dialog.Result, original: server);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unexpected error: " + ex.Message);
        }
    }

    private async void OnRemoveServerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServerConfig server }) return;

        // There must always be at least one server (topics need somewhere to live,
        // new topics need a default).
        if (_vm.Servers.Count <= 1)
        {
            var info = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Remove server",
                Content = "You must keep at least one server.",
                CloseButtonText = "OK",
            };
            await info.ShowDialogAsync();
            return;
        }

        var topicCount = _vm.TopicCountForServer(server.Id);

        bool deleteHistory;

        if (topicCount > 0)
        {
            // Three-way: keep history, delete history, or cancel.
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Remove server",
                Content = $"Remove “{server.DisplayLabel}” and its {topicCount} topic(s)?\n\n" +
                          "Keep their message history (still browsable under “All topics”), or delete it too?",
                PrimaryButtonText = "Keep history",
                SecondaryButtonText = "Delete history",
                CloseButtonText = "Cancel",
            };

            var result = await dialog.ShowDialogAsync();
            switch (result)
            {
                case Wpf.Ui.Controls.MessageBoxResult.Primary:   deleteHistory = false; break;
                case Wpf.Ui.Controls.MessageBoxResult.Secondary: deleteHistory = true;  break;
                default: return; // Cancel
            }
        }
        else
        {
            // No topics → no history to worry about.
            var confirm = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Remove server",
                Content = $"Remove “{server.DisplayLabel}”?",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
            };
            if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
            deleteHistory = false;
        }

        try
        {
            await _vm.RemoveServerAsync(server, deleteHistory);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unexpected error: " + ex.Message);
        }
    }
}
