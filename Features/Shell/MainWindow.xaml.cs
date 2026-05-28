using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Feed;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Topics;
using NtfyDesktop.Features.Topics.Dialogs;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using FeedViewModel = NtfyDesktop.Features.Feed.FeedViewModel;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using SettingsViewModel = NtfyDesktop.Features.Settings.SettingsViewModel;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;

namespace NtfyDesktop.Features.Shell;

public partial class MainWindow : FluentWindow
{
    private static readonly Brush PipConnectedBrush    = Frozen(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush PipConnectingBrush   = Frozen(Color.FromRgb(0xEA, 0x58, 0x0C));
    private static readonly Brush PipDisconnectedBrush = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush PausedGlyphBrush     = Frozen(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly NotificationGate _gate;
    private readonly FeedViewModel _feedVm;
    private readonly SettingsViewModel _settingsVm;
    private readonly TopicsViewModel _topicsVm;
    private readonly Dictionary<string, RailItem> _railItems = new();

    private sealed record RailItem(NavigationViewItem Item, Ellipse Pip, SymbolIcon PauseGlyph);

    // Last page type the user landed on; updated by the Navigated event.
    private Type? _currentPageType;

    // Set true while we re-issue a Navigate after the user resolved the
    // unsaved-changes dialog (or after dismissing the AddTopic action item),
    // so we don't re-prompt or recurse.
    private bool _bypassDirtyGuard;

    public MainWindow(
        MainWindowViewModel viewModel,
        AppSettings settings,
        ConnectionManager connections,
        NotificationGate gate,
        FeedViewModel feedVm,
        SettingsViewModel settingsVm,
        TopicsViewModel topicsVm,
        IServiceProvider services)
    {
        InitializeComponent();

        Title = App.NAME;
        DataContext = viewModel;

        _settings = settings;
        _connections = connections;
        _gate = gate;
        _feedVm = feedVm;
        _settingsVm = settingsVm;
        _topicsVm = topicsVm;

        RootNavigation.SetServiceProvider(services);

        // NavigationView's template isn't applied yet inside the constructor,
        // so initial Navigate would NRE. Defer to Loaded.
        Loaded += OnFirstLoaded;

        // Keep the rail's per-topic items in sync with settings.
        _connections.TopicsChanged += OnTopicsChanged;
        // Pip colour follows connection status.
        _connections.ConnectionStatusChanged += OnConnectionChanged;
        // Pause glyph follows the gate (both global and per-topic flips).
        _gate.GlobalStatusChanged += OnGateChanged;
        _gate.TopicPauseChanged += OnTopicPauseChanged;

        // Navigation guard for the Settings page (prompt on unsaved changes).
        RootNavigation.Navigating += OnNavigationViewNavigating;
        RootNavigation.Navigated  += OnNavigationViewNavigated;

        SystemThemeWatcher.Watch(this);
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;

        RebuildTopicItems();
        // Navigating to FeedPage marks AllTopicsItem (TargetPageType=FeedPage) as active.
        RootNavigation.Navigate(typeof(FeedPage));
    }

    private void OnTopicsChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RebuildTopicItems);

    private void OnConnectionChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RefreshTopicAdornments);

    private void OnGateChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RefreshTopicAdornments);

    private void OnTopicPauseChanged(object? sender, string topicName) =>
        Dispatcher.Invoke(RefreshTopicAdornments);

    private void RebuildTopicItems()
    {
        // Remove old dynamic items (everything between AddTopicItem and the
        // separator is dynamic; the separator marks the end of the topics block).
        var menu = RootNavigation.MenuItems;
        var separatorIdx = menu.IndexOf(TopicsSeparator);
        var anchorIdx = menu.IndexOf(AddTopicItem);

        for (var i = separatorIdx - 1; i > anchorIdx; i--)
            menu.RemoveAt(i);
        _railItems.Clear();

        var topics = _settings.Topics;
        if (topics.Count == 0)
        {
            TopicsSeparator.Visibility = Visibility.Collapsed;
            return;
        }

        TopicsSeparator.Visibility = Visibility.Visible;
        var insertAt = anchorIdx + 1;
        foreach (var t in topics)
        {
            var item = BuildTopicNavItem(t.Name);
            _railItems[t.Name] = item;
            menu.Insert(insertAt++, item.Item);
            ApplyEnabledStyling(item.Item, t.Enabled);
        }

        RefreshTopicAdornments();
    }

    private RailItem BuildTopicNavItem(string topicName)
    {
        var pip = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = PipDisconnectedBrush,
        };
        var label = new System.Windows.Controls.TextBlock
        {
            Text = topicName,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Pause glyph: appears after the label when the topic is effectively
        // paused (global or per-topic). Filled variant renders sharper at this
        // small size than the regular outline.
        var pauseGlyph = new SymbolIcon
        {
            Symbol = SymbolRegular.Pause24,
            Filled = true,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PausedGlyphBrush,
            Visibility = Visibility.Collapsed,
        };

        // Right-docked three-dot button (shown on hover/selected by the
        // TopicMoreButton style in MainWindow.xaml).
        var moreButton = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("TopicMoreButton"),
            Content = new SymbolIcon { Symbol = SymbolRegular.MoreHorizontal24, FontSize = 14 },
            Tag = topicName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        moreButton.Click += OnTopicMoreClicked;

        var leftPart = new StackPanel { Orientation = Orientation.Horizontal };
        leftPart.Children.Add(pip);
        leftPart.Children.Add(label);
        leftPart.Children.Add(pauseGlyph);

        // MinWidth is the workaround for WPF-UI's NavigationViewItem template:
        // its inner ContentPresenter doesn't horizontally stretch to fill the
        // rail's column, so a docked-right child otherwise hugs the label.
        // OpenPaneLength is 220; minus the rail's icon column and padding,
        // ~160 reliably pushes the more-button to the rail's right edge.
        var content = new DockPanel
        {
            LastChildFill = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 160,
        };
        
        DockPanel.SetDock(moreButton, Dock.Right);
        content.Children.Add(moreButton);
        content.Children.Add(leftPart);

        var item = new NavigationViewItem
        {
            Content = content,
            // Tag drives _feedVm.CurrentTopic in OnNavigationSelectionChanged.
            Tag = topicName,
            TargetPageType = typeof(FeedPage),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Tag24 },
            // Stretch content so the right-docked more-button reaches the
            // rail's right edge instead of sitting next to the label.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        return new RailItem(item, pip, pauseGlyph);
    }

    private static void ApplyEnabledStyling(NavigationViewItem item, bool enabled)
    {
        // Disabled topics still appear in the rail (the user can still browse
        // their history) but are dimmed so the disabled state reads at a glance.
        item.Opacity = enabled ? 1.0 : 0.5;
    }

    private void RefreshTopicAdornments()
    {
        foreach (var state in _connections.GetTopicStates())
        {
            if (!_railItems.TryGetValue(state.TopicName, out var rail)) continue;

            rail.Pip.Fill = PipBrushFor(state.Status);
            rail.PauseGlyph.Visibility = _gate.IsTopicPaused(state.TopicName)
                ? Visibility.Visible
                : Visibility.Collapsed;

            var topic = _settings.GetTopicSettings(state.TopicName);
            if (topic is not null)
                ApplyEnabledStyling(rail.Item, topic.Enabled);
        }
    }

    private static Brush PipBrushFor(TopicConnectionStatus status) => status switch
    {
        TopicConnectionStatus.Connected    => PipConnectedBrush,
        TopicConnectionStatus.Connecting   => PipConnectingBrush,
        TopicConnectionStatus.Disconnected => PipDisconnectedBrush,
        _                                  => PipDisconnectedBrush,
    };

    private void OnNavigationSelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is not NavigationViewItem item) return;
        if (item.TargetPageType != typeof(FeedPage)) return;

        // Tag is "" for All topics; otherwise the topic name.
        var tag = item.Tag as string;
        _feedVm.CurrentTopic = string.IsNullOrEmpty(tag) ? null : tag;
    }

    // ===== Add topic action =====
    //
    // AddTopicItem has no TargetPageType and Handled=true on Click, so the
    // NavigationView leaves the current rail selection alone — no restore
    // logic needed.
    private async void OnAddTopicNavClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (!await EnsureServerConfiguredAsync()) return;

        try
        {
            var dialog = new TopicEditorDialog(existing: null) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;

            await _topicsVm.AddOrUpdateAsync(dialog.Result, newTopicSettings: null);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unexpected error: " + ex.Message);
        }
    }

    private async Task ShowServerNotConfiguredWarning()
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Server not configured",
            Content = "Set a valid server URL in Settings before adding topics.",
            PrimaryButtonText = "Open Settings",
            CloseButtonText = "Cancel"
        };

        var result = await box.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
            NavigateBypassingGuard(typeof(SettingsPage));
    } 

    // Adding a topic without a usable server URL produces a topic that can't
    // connect — surface that up front instead of letting the user create
    // something that silently fails. Edit/Pause/Remove don't need this check
    // (they operate on already-saved topics).
    private async Task<bool> EnsureServerConfiguredAsync()
    {
        var url = _settings.ServerUrl?.Trim() ?? string.Empty;
        var valid = !string.IsNullOrEmpty(url)
                    && Uri.TryCreate(url, UriKind.Absolute, out var u)
                    && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        if (valid) return true;

        await ShowServerNotConfiguredWarning();

        return false;
    }

    // ===== Topic three-dot menu =====
    //
    // Built lazily each time the user clicks so labels reflect current state
    // (Pause vs Resume, Enable vs Disable) without needing extra subscriptions.
    private void OnTopicMoreClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string topicName) return;

        var menu = BuildTopicContextMenu(topicName);
        if (menu.Items.Count == 0) return;

        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu BuildTopicContextMenu(string topicName)
    {
        var menu = new ContextMenu();
        var topic = _settings.GetTopicSettings(topicName);
        if (topic is null) return menu;

        var isTopicSpecificallyPaused = _gate.IsTopicSpecificallyPaused(topicName);
        var isEnabled = topic.Enabled;
        var globallyPaused = _gate.IsGloballyPaused;

        // Pause / resume notifications for this topic. Disabled while globally
        // paused — the global flag overrides per-topic state.
        var pauseItem = new System.Windows.Controls.MenuItem
        {
            Header = isTopicSpecificallyPaused ? "Resume notifications" : "Pause notifications",
            Icon = new SymbolIcon
            {
                Symbol = isTopicSpecificallyPaused ? SymbolRegular.Play24 : SymbolRegular.Pause24,
                Filled = true,
                FontSize = 14,
            },
            IsEnabled = !globallyPaused,
            ToolTip = globallyPaused
                ? "Notifications are paused globally — use the title-bar Resume button."
                : null,
        };
        pauseItem.Click += (_, _) =>
        {
            if (isTopicSpecificallyPaused) _gate.ResumeTopic(topicName);
            else                           _gate.PauseTopic(topicName);
        };

        // Force-reconnect. Only meaningful while the topic is enabled (otherwise
        // there's no live socket to reconnect).
        var reconnectItem = new System.Windows.Controls.MenuItem
        {
            Header = "Reconnect",
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowSync24, FontSize = 14 },
            IsEnabled = isEnabled,
        };
        reconnectItem.Click += (_, _) => _connections.ReconnectTopic(topicName);

        // Disable tears down the socket (no messages received); enable starts
        // it back up. Same persistence path as the editor dialog.
        var enableItem = new System.Windows.Controls.MenuItem
        {
            Header = isEnabled ? "Disable" : "Enable",
            Icon = new SymbolIcon
            {
                Symbol = isEnabled ? SymbolRegular.Prohibited24 : SymbolRegular.CheckmarkCircle24,
                FontSize = 14,
            },
        };
        enableItem.Click += async (_, _) => await _topicsVm.ToggleEnabledAsync(topic);

        var editItem = new System.Windows.Controls.MenuItem
        {
            Header = "Edit",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Edit24, FontSize = 14 },
        };
        editItem.Click += async (_, _) =>
        {
            try
            {
                var dialog = new TopicEditorDialog(topic) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.Result is null) return;
                await _topicsVm.AddOrUpdateAsync(dialog.Result, newTopicSettings: topic);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Unexpected error: " + ex.Message);
            }
        };

        var removeItem = new System.Windows.Controls.MenuItem
        {
            Header = "Remove",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24, FontSize = 14 },
        };
        removeItem.Click += (_, _) => _topicsVm.RemoveCommand.Execute(topic);

        menu.Items.Add(pauseItem);
        menu.Items.Add(reconnectItem);
        menu.Items.Add(enableItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(editItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(removeItem);

        return menu;
    }

    /// <summary>
    /// Navigates to the feed for a specific topic. Called from App.HandleActivation
    /// after a toast click forwards us a ntfy-desktop:// URL. If the topic is no
    /// longer subscribed (user removed it), falls back to "All topics".
    /// </summary>
    public void NavigateToTopic(string? topicName)
    {
        RootNavigation.Navigate(typeof(FeedPage));

        // The rail's visual selection lands on AllTopicsItem (the first item with
        // TargetPageType=FeedPage). Setting the VM's CurrentTopic afterwards drives
        // the feed content to the requested topic. The rail won't visually highlight
        // the per-topic item — WPF-UI's NavigationView.SelectedItem setter isn't
        // public so we can't fix that without poking template internals.
        _feedVm.CurrentTopic =
            !string.IsNullOrEmpty(topicName) && _railItems.ContainsKey(topicName)
                ? topicName
                : null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    // Navigating fires before the new page is shown. If we're leaving the Settings
    // page with unsaved edits, cancel the nav, prompt the user, and either commit
    // (Save/Discard then re-issue the nav) or stay (restore the rail selection).
    private void OnNavigationViewNavigating(NavigationView sender, NavigatingCancelEventArgs e)
    {
        if (_bypassDirtyGuard) return;
        if (_currentPageType != typeof(SettingsPage)) return;
        if (!_settingsVm.IsDirty) return;

        e.Cancel = true;
        var targetType = (e.Page as FrameworkElement)?.GetType();
        if (targetType is null) return;

        // Defer out of the event handler so we can await the dialog.
        _ = Dispatcher.BeginInvoke(async () => await PromptForUnsavedChangesAsync(targetType));
    }

    private void OnNavigationViewNavigated(NavigationView sender, NavigatedEventArgs e)
    {
        if (e.Page is FrameworkElement fe)
            _currentPageType = fe.GetType();
    }

    private async Task PromptForUnsavedChangesAsync(Type targetType)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Unsaved changes",
            Content = "You have unsaved changes in Settings. Save them, discard them, or stay on the page?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
        };

        var result = await dialog.ShowDialogAsync();

        switch (result)
        {
            case Wpf.Ui.Controls.MessageBoxResult.Primary:
                await _settingsVm.SaveCommand.ExecuteAsync(null);
                NavigateBypassingGuard(targetType);
                break;

            case Wpf.Ui.Controls.MessageBoxResult.Secondary:
                _settingsVm.DiscardCommand.Execute(null);
                NavigateBypassingGuard(targetType);
                break;

            default:
                // Cancel: stay on Settings. The rail's visual selection had already
                // moved to the target item, so restore it to the Settings item.
                RestoreSelectionToSettings();
                break;
        }
    }

    private void NavigateBypassingGuard(Type pageType)
    {
        _bypassDirtyGuard = true;
        try { RootNavigation.Navigate(pageType); }
        finally { _bypassDirtyGuard = false; }
    }

    private void RestoreSelectionToSettings()
    {
        // Navigate back to Settings rather than poking SelectedItem directly
        // (the setter on NavigationView.SelectedItem isn't public). NavigationView
        // updates the rail's visual selection as a side effect of Navigate.
        NavigateBypassingGuard(typeof(SettingsPage));
    }
}
