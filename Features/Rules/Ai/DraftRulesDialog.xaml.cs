using System.Windows;
using Wpf.Ui.Controls;

namespace NtfyDesktop.Features.Rules.Ai;

public partial class DraftRulesDialog : FluentWindow
{
    private readonly DraftRulesViewModel _vm;

    public DraftRulesDialog(DraftRulesViewModel vm, Guid? topicId)
    {
        InitializeComponent();
        _vm = vm;
        _vm.Initialize(topicId);
        DataContext = _vm;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        // TrySave validates the (possibly edited) JSON and surfaces an error in the dialog
        // rather than closing it on failure.
        if (!_vm.TrySave()) return;
        DialogResult = true;
        Close();
    }
}
