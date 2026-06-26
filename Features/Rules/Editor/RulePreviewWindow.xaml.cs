using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace NtfyDesktop.Features.Rules.Editor;

public partial class RulePreviewWindow
{
    private sealed record PreviewRow(string Effect, Brush AccentBrush, string Title, string Detail);

    public RulePreviewWindow(SimReport report, string packName, string scopeLabel)
    {
        InitializeComponent();

        var rows = BuildRows(report);
        RowList.ItemsSource = rows;

        var hidden = report.Results.Count(r => r.Hidden);
        var folded = report.Results.Count(r => r.DismissMessageId is not null);
        HeaderText.Text = rows.Count == 0
            ? $"“{packName}” wouldn’t change anything here."
            : $"“{packName}” would affect {rows.Count} message(s): {hidden} hidden" +
              (folded > 0 ? $", {folded} folded with their problem" : "") +
              (report.Absences.Count > 0 ? $", {report.Absences.Count} heartbeat gap(s)" : "") + ".";
        ScopeText.Text = $"Tested against {report.Results.Count} stored message(s) — {scopeLabel}.";

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RowList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (report.Absences.Count > 0)
        {
            AbsencePanel.Visibility = Visibility.Visible;
            AbsenceList.ItemsSource = report.Absences
                .Select(a => $"“{a.RuleTitle}” — {Humanize(a.Gap)} gap ({a.Start:g} → {a.End:g})")
                .ToList();
        }
    }

    private List<PreviewRow> BuildRows(SimReport report)
    {
        var rows = new List<PreviewRow>();
        foreach (var r in report.Results)
        {
            string? effect = null;
            Brush brush = Resolve("SystemFillColorNeutralBrush", Brushes.Gray);

            if (r.DismissMessageId is not null)
            {
                effect = "Folds (resolved)";
                brush = Resolve("SystemFillColorCautionBrush", Brushes.DarkOrange);
            }
            else if (r.Hidden)
            {
                effect = "Hidden";
                brush = Resolve("SystemFillColorCautionBrush", Brushes.DarkOrange);
            }
            else if (r.OpensIncident)
            {
                effect = "Opens incident";
                brush = Resolve("AccentFillColorDefaultBrush", Brushes.SteelBlue);
            }
            else if (r.Tags.Count > 0)
            {
                effect = "Tagged: " + string.Join(", ", r.Tags);
                brush = Resolve("SystemFillColorSuccessBrush", Brushes.SeaGreen);
            }

            if (effect is null) continue; // unaffected
            rows.Add(new PreviewRow(effect, brush, r.Message.DisplayTitle, r.Message.Topic));
        }
        return rows;
    }

    private static Brush Resolve(string key, Brush fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback;

    private static string Humanize(System.TimeSpan t) =>
        t.TotalDays >= 1 ? $"{t.TotalDays:0.#}d" : t.TotalHours >= 1 ? $"{t.TotalHours:0.#}h" : $"{t.TotalMinutes:0}m";

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
