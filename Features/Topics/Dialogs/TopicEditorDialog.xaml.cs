using System.Globalization;
using System.Windows;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Settings;
using Wpf.Ui.Controls;

namespace NtfyDesktop.Features.Topics.Dialogs;

public partial class TopicEditorDialog : FluentWindow
{
    private readonly TopicEditorViewModel _vm;

    public TopicSettings? Result { get; private set; }

    public TopicEditorDialog(TopicSettings? existing, IReadOnlyList<ServerConfig> servers, Guid defaultServerId,
        IReadOnlyList<string> groups)
    {
        InitializeComponent();
        _vm = TopicEditorViewModel.FromTopic(existing, servers, defaultServerId, groups);
        DataContext = _vm;
        Title = existing is null ? "Add topic" : "Edit topic";

        StartBox.Text = _vm.ActiveHoursStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        EndBox.Text   = _vm.ActiveHoursEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.Name))
        {
            System.Windows.MessageBox.Show(this, "Topic name is required.", "Validation",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (_vm.SelectedServer is null)
        {
            System.Windows.MessageBox.Show(this, "Select a server for this topic.", "Validation",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (TimeOnly.TryParseExact(StartBox.Text, "HH:mm", out var start))
            _vm.ActiveHoursStart = start;
        if (TimeOnly.TryParseExact(EndBox.Text, "HH:mm", out var end))
            _vm.ActiveHoursEnd = end;

        Result = _vm.ToTopic();
        DialogResult = true;
        Close();
    }
}
