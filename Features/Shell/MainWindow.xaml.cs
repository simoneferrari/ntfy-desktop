using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Feed;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Topics;
using NtfyDesktop.Features.Topics.Dialogs;
using NtfyDesktop.Features.Unread;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using FeedViewModel = NtfyDesktop.Features.Feed.FeedViewModel;
using MessageBox = System.Windows.MessageBox;
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
    private static readonly Brush BadgeBrush           = Frozen(Color.FromRgb(0x00, 0x67, 0xC0));

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly NotificationGate _gate;
    private readonly FeedViewModel _feedVm;
    private readonly SettingsViewModel _settingsVm;
    private readonly TopicsViewModel _topicsVm;
    private readonly UnreadTracker _unread;
    private readonly Dictionary<Guid, RailItem> _railItems = new();
    private readonly Dictionary<string, IconBadge> _groupBadges = new();
    private IconBadge? _allTopicsBadge;

    private sealed record RailItem(NavigationViewItem Item, Ellipse Pip, SymbolIcon PauseGlyph, IconBadge Badge);

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
        UnreadTracker unread,
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
        _unread = unread;

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

        // Rebuild the rail when a display-affecting setting changes (server rename,
        // server-label toggle).
        _settings.DisplayChanged += (_, _) => Dispatcher.Invoke(RebuildTopicItems);

        // Unread badges follow the tracker. Window focus drives "viewing" state:
        // regaining focus marks the current feed read (catches messages that
        // arrived while minimized to the tray).
        _unread.Changed += (_, _) => Dispatcher.Invoke(RefreshBadges);
        Activated   += (_, _) => _unread.SetWindowActive(true);
        Deactivated += (_, _) => _unread.SetWindowActive(false);

        // Navigation guard for the Settings page (prompt on unsaved changes).
        RootNavigation.Navigating += OnNavigationViewNavigating;
        RootNavigation.Navigated  += OnNavigationViewNavigated;

        SystemThemeWatcher.Watch(this);
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;

        _allTopicsBadge = new IconBadge(AllTopicsIcon);

        RebuildTopicItems();
        // Navigating to FeedPage marks AllTopicsItem (TargetPageType=FeedPage) as active.
        RootNavigation.Navigate(typeof(FeedPage));
        RefreshBadges();
    }

    private void OnTopicsChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RebuildTopicItems);

    private void OnConnectionChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RefreshTopicAdornments);

    private void OnGateChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RefreshTopicAdornments);

    private void OnTopicPauseChanged(object? sender, Guid topicId) =>
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
        _groupBadges.Clear();

        var topics = _settings.Topics
            .OrderBy(t => t.EffectiveDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (topics.Count == 0)
        {
            TopicsSeparator.Visibility = Visibility.Collapsed;
            return;
        }

        TopicsSeparator.Visibility = Visibility.Visible;
        var insertAt = anchorIdx + 1;

        // Server context only matters with more than one server (otherwise every
        // topic shows the same label). The toggle gates it on top of that.
        var showServer = _settings.Servers.Count > 1 && _settings.ShowServerLabel;
        string? Subtitle(TopicSettings t) =>
            showServer ? _settings.GetServer(t.ServerId)?.DisplayLabel : null;

        RailItem Register(TopicSettings t)
        {
            var item = BuildTopicNavItem(t, Subtitle(t));
            _railItems[t.Id] = item;
            ApplyEnabledStyling(item.Item, t.Enabled);
            return item;
        }

        // Ungrouped topics sit at the top level, above the folders.
        foreach (var t in topics.Where(t => string.IsNullOrWhiteSpace(t.GroupName)))
            menu.Insert(insertAt++, Register(t).Item);

        // Then one collapsible folder per group, alphabetical. (topics is already
        // sorted, so each group's children stay alphabetical.)
        var groups = topics
            .Where(t => !string.IsNullOrWhiteSpace(t.GroupName))
            .GroupBy(t => t.GroupName!.Trim())
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in groups)
        {
            var folder = BuildGroupFolder(group.Key);
            foreach (var t in group)
                folder.MenuItems.Add(Register(t).Item);
            menu.Insert(insertAt++, folder);
        }

        RefreshTopicAdornments();
        RefreshBadges();
    }

    // A collapsible group folder: a non-navigating parent nav item whose children
    // are the group's topic items. Carries an aggregate unread badge on its icon and
    // persists its expand/collapse state.
    private NavigationViewItem BuildGroupFolder(string groupName)
    {
        var icon = new SymbolIcon { Symbol = SymbolRegular.Folder24 };

        var folder = new NavigationViewItem
        {
            Content = groupName,
            Icon = icon,
            // No TargetPageType — clicking expands/collapses, never navigates.
        };

        // Restore persisted collapse state before wiring the listener, so this
        // programmatic set doesn't trigger a redundant save.
        folder.IsExpanded = !_settings.CollapsedGroups.Contains(groupName);

        _groupBadges[groupName] = new IconBadge(icon);

        // NavigationViewItem has no Expanded/Collapsed event, so watch the DP.
        // Detach on Unloaded — folders are recreated on every rebuild.
        var dpd = DependencyPropertyDescriptor.FromProperty(
            NavigationViewItem.IsExpandedProperty, typeof(NavigationViewItem));
        void OnExpandChanged(object? s, EventArgs e) => PersistGroupCollapsed(groupName, !folder.IsExpanded);
        dpd.AddValueChanged(folder, OnExpandChanged);
        folder.Unloaded += (_, _) => dpd.RemoveValueChanged(folder, OnExpandChanged);

        return folder;
    }

    private void PersistGroupCollapsed(string groupName, bool collapsed)
    {
        var alreadyCollapsed = _settings.CollapsedGroups.Contains(groupName);
        if (collapsed == alreadyCollapsed) return;

        if (collapsed) _settings.CollapsedGroups.Add(groupName);
        else           _settings.CollapsedGroups.Remove(groupName);
        _settings.Save();
    }

    // Distinct existing group names, offered as suggestions in the topic editor.
    private IReadOnlyList<string> ExistingGroupNames() =>
        _settings.Topics
            .Select(t => t.GroupName?.Trim())
            .Where(g => !string.IsNullOrEmpty(g))
            .Select(g => g!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // Pushes current unread counts onto the rail badges. Cheap (in-memory counts);
    // safe to call after any rebuild or on every tracker change.
    private void RefreshBadges()
    {
        _allTopicsBadge?.Set(_unread.Total);
        foreach (var (id, rail) in _railItems)
            rail.Badge.Set(_unread.CountFor(id));
        // Folder badge = sum of unread across the group's topics.
        foreach (var (group, badge) in _groupBadges)
            badge.Set(_settings.Topics
                .Where(t => string.Equals(t.GroupName?.Trim(), group, StringComparison.Ordinal))
                .Sum(t => _unread.CountFor(t.Id)));
    }

    private RailItem BuildTopicNavItem(TopicSettings topic, string? serverSubtitle)
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
            Text = topic.EffectiveDisplayName,
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
            Tag = topic.Id,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        moreButton.Click += OnTopicMoreClicked;

        // Label row (topic name + pause glyph), optionally stacked over a muted
        // server-name subtitle (Subtitle rail mode).
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
        labelRow.Children.Add(label);
        labelRow.Children.Add(pauseGlyph);
        
        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
        };
        textStack.Children.Add(labelRow);
        if (!string.IsNullOrEmpty(serverSubtitle))
        {
            textStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = serverSubtitle,
                FontSize = 11,
                Foreground = PausedGlyphBrush,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        var leftPart = new StackPanel { Orientation = Orientation.Horizontal };
        leftPart.Children.Add(pip);
        leftPart.Children.Add(textStack);

        // MinWidth is the workaround for WPF-UI's NavigationViewItem template:
        // its inner ContentPresenter doesn't horizontally stretch to fill the
        // rail's column, so a docked-right child otherwise hugs the label.
        // OpenPaneLength is 250; minus the rail's icon column and padding,
        // ~200 reliably pushes the more-button to the rail's right edge.
        var content = new DockPanel
        {
            LastChildFill = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 200,
        };
        
        DockPanel.SetDock(moreButton, Dock.Right);
        content.Children.Add(moreButton);
        content.Children.Add(leftPart);
        
        var icon = new SymbolIcon { Symbol = SymbolRegular.Tag24 };

        var item = new NavigationViewItem
        {
            Content = content,
            // Tag (TopicId) drives _feedVm.CurrentTopicId in OnNavigationSelectionChanged.
            Tag = topic.Id,
            TargetPageType = typeof(FeedPage),
            Icon = icon,
            // Stretch content so the right-docked more-button reaches the
            // rail's right edge instead of sitting next to the label.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        return new RailItem(item, pip, pauseGlyph, new IconBadge(icon));
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
            if (!_railItems.TryGetValue(state.TopicId, out var rail)) continue;

            rail.Pip.Fill = PipBrushFor(state.Status);
            rail.PauseGlyph.Visibility = _gate.IsTopicPaused(state.TopicId)
                ? Visibility.Visible
                : Visibility.Collapsed;

            var topic = _settings.GetTopicById(state.TopicId);
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

        if (item.TargetPageType != typeof(FeedPage))
        {
            // A real other page (Settings / Connections) means no feed is on screen,
            // so arrivals from here on count as unread. Group folders and action items
            // have no TargetPageType at all — leave the active view untouched for those
            // (expanding a folder isn't "leaving the feed").
            if (item.TargetPageType is not null)
                _unread.SetActiveView(ActiveView.None);
            return;
        }

        // Topic items carry their TopicId as Tag; "All topics" carries "" (string).
        var topicId = item.Tag is Guid id ? (Guid?)id : null;
        _feedVm.CurrentTopicId = topicId;
        // Navigating to a feed is an explicit "I'm looking at this" — mark it read.
        _unread.SetActiveView(topicId is { } gid ? ActiveView.Topic(gid) : ActiveView.AllTopics);
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
            var dialog = new TopicEditorDialog(existing: null, _settings.Servers, _settings.DefaultServerId, ExistingGroupNames()) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;

            await _topicsVm.AddOrUpdateAsync(dialog.Result, original: null);
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
        var url = _settings.DefaultServer.Url?.Trim() ?? string.Empty;
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
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not Guid topicId) return;

        var menu = BuildTopicContextMenu(topicId);
        if (menu.Items.Count == 0) return;

        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu BuildTopicContextMenu(Guid topicId)
    {
        var menu = new ContextMenu();
        var topic = _settings.GetTopicById(topicId);
        if (topic is null) return menu;

        var isTopicSpecificallyPaused = _gate.IsTopicSpecificallyPaused(topicId);
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
            if (isTopicSpecificallyPaused) _gate.ResumeTopic(topicId);
            else                           _gate.PauseTopic(topicId);
        };

        // Force-reconnect. Only meaningful while the topic is enabled (otherwise
        // there's no live socket to reconnect).
        var reconnectItem = new System.Windows.Controls.MenuItem
        {
            Header = "Reconnect",
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowSync24, FontSize = 14 },
            IsEnabled = isEnabled,
        };
        reconnectItem.Click += (_, _) => _connections.ReconnectTopic(topicId);

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
                var dialog = new TopicEditorDialog(topic, _settings.Servers, _settings.DefaultServerId, ExistingGroupNames()) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.Result is null) return;
                await _topicsVm.AddOrUpdateAsync(dialog.Result, original: topic);
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
        removeItem.Click += async (_, _) => await RemoveTopicWithPromptAsync(topic);

        menu.Items.Add(pauseItem);
        menu.Items.Add(reconnectItem);
        menu.Items.Add(enableItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(editItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(removeItem);

        return menu;
    }

    // Three-way prompt mirroring server removal: keep the topic's history (still
    // browsable under "All topics"), delete it too, or cancel.
    private async Task RemoveTopicWithPromptAsync(TopicSettings topic)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Remove topic",
            Content = $"Remove “{topic.EffectiveDisplayName}”?\n\n" +
                      "Keep its message history (still browsable under “All topics”), or delete it too?",
            PrimaryButtonText = "Keep history",
            SecondaryButtonText = "Delete history",
            CloseButtonText = "Cancel",
        };

        bool deleteHistory;
        switch (await dialog.ShowDialogAsync())
        {
            case Wpf.Ui.Controls.MessageBoxResult.Primary:   deleteHistory = false; break;
            case Wpf.Ui.Controls.MessageBoxResult.Secondary: deleteHistory = true;  break;
            default: return; // Cancel
        }

        try
        {
            await _topicsVm.RemoveAsync(topic, deleteHistory);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Unexpected error: " + ex.Message);
        }
    }

    /// <summary>
    /// Navigates to the feed for a specific topic. Called from App.HandleActivation
    /// after a toast click forwards us a ntfy-desktop:// URL. If the topic is no
    /// longer subscribed (user removed it), falls back to "All topics".
    /// </summary>
    public void NavigateToTopic(Guid topicId)
    {
        RootNavigation.Navigate(typeof(FeedPage));

        // The rail's visual selection lands on AllTopicsItem (the first item with
        // TargetPageType=FeedPage). Setting the VM's CurrentTopicId afterwards drives
        // the feed content to the requested topic. The rail won't visually highlight
        // the per-topic item — WPF-UI's NavigationView.SelectedItem setter isn't
        // public so we can't fix that without poking template internals.
        var resolved = _railItems.ContainsKey(topicId) ? (Guid?)topicId : null;
        _feedVm.CurrentTopicId = resolved;
        _unread.SetActiveView(resolved is { } id ? ActiveView.Topic(id) : ActiveView.AllTopics);
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

    // ===== Unread badge =====
    //
    // A small count bubble overlaid on a rail item's icon. Implemented as an
    // adorner so it sits on top of the icon without disturbing the rail's layout
    // or the WPF-UI NavigationViewItem template (whose Icon slot only accepts an
    // IconElement, so we can't wrap it in a Grid).

    // Binds a badge adorner to an icon's lifetime. The adorner layer only exists
    // once the icon is in the visual tree, so attachment is deferred to Loaded;
    // Unloaded (e.g. when RebuildTopicItems drops the item) detaches it.
    private sealed class IconBadge
    {
        private readonly FrameworkElement _icon;
        private BadgeAdorner? _adorner;
        private int _count;

        public IconBadge(FrameworkElement icon)
        {
            _icon = icon;
            if (icon.IsLoaded) TryAttach();
            icon.Loaded += (_, _) => TryAttach();
            icon.Unloaded += (_, _) => Detach();
        }

        public void Set(int count)
        {
            _count = count;
            _adorner?.Update(count);
        }

        private void TryAttach()
        {
            if (_adorner is not null) return;
            var layer = AdornerLayer.GetAdornerLayer(_icon);
            if (layer is null) return; // no adorner layer yet — Loaded will retry
            _adorner = new BadgeAdorner(_icon);
            layer.Add(_adorner);
            _adorner.Update(_count);
        }

        private void Detach()
        {
            if (_adorner is null) return;
            AdornerLayer.GetAdornerLayer(_icon)?.Remove(_adorner);
            _adorner = null;
        }
    }

    private sealed class BadgeAdorner : Adorner
    {
        private readonly VisualCollection _children;
        private readonly Border _badge;
        private readonly System.Windows.Controls.TextBlock _text;

        public BadgeAdorner(UIElement adornedElement) : base(adornedElement)
        {
            // Create the collection first: VisualCollection.Add wires the badge into
            // the visual tree, which makes WPF query VisualChildrenCount immediately.
            // VisualChildrenCount is also null-guarded below as belt-and-suspenders.
            _children = new VisualCollection(this);

            IsHitTestVisible = false;

            _text = new System.Windows.Controls.TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            TextOptions.SetTextFormattingMode(_text, TextFormattingMode.Display);

            _badge = new Border
            {
                Background = BadgeBrush,
                CornerRadius = new CornerRadius(7),
                MinWidth = 14,
                Height = 14,
                Padding = new Thickness(3, 0, 3, 0),
                SnapsToDevicePixels = true,
                Visibility = Visibility.Collapsed,
                Child = _text,
            };

            _children.Add(_badge);
        }

        public void Update(int count)
        {
            if (count <= 0)
            {
                _badge.Visibility = Visibility.Collapsed;
            }
            else
            {
                _text.Text = count > 99 ? "99+" : count.ToString();
                _badge.Visibility = Visibility.Visible;
            }
            InvalidateMeasure();
            InvalidateArrange();
        }

        protected override int VisualChildrenCount => _children?.Count ?? 0;
        protected override Visual GetVisualChild(int index) => _children[index];

        protected override Size MeasureOverride(Size constraint)
        {
            _badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = _badge.DesiredSize;
            // Top-right corner of the icon, overhanging slightly up and to the right.
            var x = finalSize.Width - size.Width + 5;
            var y = -5;
            _badge.Arrange(new Rect(new Point(x, y), size));
            return finalSize;
        }
    }
}
