using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Rules.Ai;
using NtfyDesktop.Features.Settings;

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

    private void OnAddRuleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && Resources["AddRuleMenu"] is ContextMenu menu)
        {
            menu.PlacementTarget = fe;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnAddRuleMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (tag.StartsWith("template:")) _vm.AddTemplate(tag["template:".Length..]);
        else if (tag.StartsWith("blank:")) _vm.AddRule(tag["blank:".Length..]);
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e) => _vm.DeleteSelectedRule();

    private void OnPickSample(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MatcherViewModel matcher, Tag: string field }) return;

        var history = App.Services.GetRequiredService<HistoryRepository>();
        var msgs = history.Query(topicId: ResolveTopicId(matcher.Topic), limit: 50, includeSuppressed: true);
        if (msgs.Count == 0)
        {
            System.Windows.MessageBox.Show("No stored messages to pick from for this topic yet.");
            return;
        }

        var picker = new SampleMessagePickerWindow(msgs) { Owner = this };
        if (picker.ShowDialog() != true || picker.Picked is not { } m) return;

        if (field == "Body")
        {
            matcher.BodyMode = MatchMode.Contains;
            matcher.BodyRegex = m.Body ?? "";
        }
        else
        {
            matcher.TitleMode = MatchMode.Contains;
            matcher.TitleRegex = string.IsNullOrEmpty(m.Title) ? m.Topic : m.Title!;
        }
    }

    private static System.Guid? ResolveTopicId(string rawTopic)
    {
        if (string.IsNullOrEmpty(rawTopic)) return null;
        var settings = App.Services.GetRequiredService<AppSettings>();
        return settings.Topics.FirstOrDefault(t =>
            string.Equals(t.Name, rawTopic, System.StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        var report = _vm.Preview();
        if (report is null || _vm.SelectedPack is null) return;
        var scope = $"{_vm.SelectedScopeTopic?.Name ?? "All topics"}, last {_vm.ScopeCount}";
        new RulePreviewWindow(report, _vm.SelectedPack.Name, scope) { Owner = this }.ShowDialog();
    }

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
