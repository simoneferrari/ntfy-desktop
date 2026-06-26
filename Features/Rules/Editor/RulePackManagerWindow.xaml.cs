using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Features.Rules.Editor;

public partial class RulePackManagerWindow
{
    private readonly RulePackManagerViewModel _vm;

    public RulePackManagerWindow(RulePackManagerViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    private void OnNewBlank(object sender, RoutedEventArgs e) => _vm.NewBlankPack();

    private void OnNewWithAi(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = App.Services.GetRequiredService<DraftRulesViewModel>();
            var dialog = new DraftRulesDialog(vm, topicId: null) { Owner = this };
            dialog.ShowDialog();
            _vm.Reload(); // pick up an AI-saved pack
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show("Unexpected error: " + ex.Message);
        }
    }

    private void OnDeletePack(object sender, RoutedEventArgs e) => _vm.DeleteSelectedPack();

    private void OnAddRule(object sender, RoutedEventArgs e) =>
        _vm.AddRule((string)((FrameworkElement)sender).Tag);

    private void OnDeleteRule(object sender, RoutedEventArgs e) => _vm.DeleteSelectedRule();

    private void OnPreview(object sender, RoutedEventArgs e) => _vm.Preview();
    // Rendering is via binding: PreviewSummary + PreviewResults are populated by Preview().

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        var preview = _vm.Preview();
        if (preview is null || _vm.SelectedPack is null) return;

        var hidden = preview.Results.Count(r => r.Hidden);
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Apply to history",
            Content = $"This will hide {hidden} message(s) from the feed for “{_vm.SelectedPack.Name}”. " +
                      "This can’t be automatically undone. Apply?",
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        _vm.Apply();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.Save()) Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
