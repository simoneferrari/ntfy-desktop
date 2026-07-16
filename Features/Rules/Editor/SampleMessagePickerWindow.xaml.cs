using System.Collections.Generic;
using System.Windows;
using NtfyDesktop.Features.History;

namespace NtfyDesktop.Features.Rules.Editor;

/// <summary>Lets the user pick one stored message; <see cref="Picked"/> holds the choice
/// after a successful close. Which field (title vs body) gets used is decided by the caller.</summary>
public partial class SampleMessagePickerWindow
{
    public HistoryMessage? Picked { get; private set; }

    public SampleMessagePickerWindow(IReadOnlyList<HistoryMessage> messages)
    {
        InitializeComponent();
        MessageList.ItemsSource = messages;
        if (messages.Count > 0) MessageList.SelectedIndex = 0;
    }

    private void OnMessageDoubleClick(object sender, RoutedEventArgs e) => Accept();

    private void OnUse(object sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Accept()
    {
        if (MessageList.SelectedItem is not HistoryMessage m) return;
        Picked = m;
        DialogResult = true;
        Close();
    }
}
