using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Connections.Events;
using NtfyDesktop.Features.Feed;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Notifications.Events;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Settings.Events;
using NtfyDesktop.Features.Topics;
using NtfyDesktop.Features.Topics.Dialogs;
using NtfyDesktop.Features.Topics.Events;
using NtfyDesktop.Features.Unread;
using NtfyDesktop.Features.Unread.Events;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using FeedViewModel = NtfyDesktop.Features.Feed.FeedViewModel;
using MessageBox = System.Windows.MessageBox;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using SettingsViewModel = NtfyDesktop.Features.Settings.SettingsViewModel;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace NtfyDesktop.Features.Shell;

public partial class MainWindow
{
    private static readonly Brush _pipConnectedBrush    = Frozen(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush _pipConnectingBrush   = Frozen(Color.FromRgb(0xEA, 0x58, 0x0C));
    private static readonly Brush _pipDisconnectedBrush = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush _pausedGlyphBrush     = Frozen(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush _badgeBrush           = Frozen(Color.FromRgb(0x00, 0x67, 0xC0));

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly NotificationGate _gate;
    private readonly FeedViewModel _feedVm;
    private readonly SettingsViewModel _settingsVm;
    private readonly TopicManager _topics;
    private readonly UnreadTracker _unread;
    private readonly Dictionary<Guid, RailItem> _railItems = new();
    private readonly Dictionary<string, IconBadge> _groupBadges = new();
    private readonly Dictionary<string, NavigationViewItem> _groupFolders = new();
    
    private IconBadge? _allTopicsBadge;

    // Rail drag-and-drop state. Payload is a Guid (topic id) or a string (group name).
    private Point _dragStartPoint;
    private object? _dragCandidate;   // armed on mouse-down, promoted past the threshold
    private object? _dragging;        // payload of the in-flight drag
    private Adorner? _dropAdorner;    // current drop indicator (line or highlight)
    private (UIElement Target, string Mode, bool Flag)? _dropKey; // dedupe re-renders

    // Group is the section the item currently lives in within the rail (""=ungrouped),
    // captured at build time. Needed to locate the item for removal even after the
    // topic is gone from settings (TopicDeleted carries only the id).
    private sealed record RailItem(NavigationViewItem Item, Ellipse Pip, SymbolIcon PauseGlyph, IconBadge Badge,
        System.Windows.Controls.TextBlock Label, System.Windows.Controls.TextBlock? Subtitle, string Group);

    // Last page type the user landed on; updated by the Navigated event.
    private Type? _currentPageType;

    // Set true while we re-issue a Navigate after the user resolved the
    // unsaved-changes dialog (or after dismissing the AddTopic action item),
    // so we don't re-prompt or recurse.
    private bool _bypassDirtyGuard;

    // The window state to return to when un-minimizing from the tray. Tracked so
    // restoring a maximized-then-minimized window comes back maximized rather than
    // collapsing to Normal. Seeded from persisted placement in the constructor.
    private WindowState _restoreWindowState = WindowState.Maximized;

    // Last non-minimized state, used by App.ShowMainWindow when re-showing from the tray.
    public WindowState RestoreWindowState => _restoreWindowState;

    public MainWindow(
        MainWindowViewModel viewModel,
        AppSettings settings,
        ConnectionManager connections,
        NotificationGate gate,
        FeedViewModel feedVm,
        SettingsViewModel settingsVm,
        TopicManager topics,
        UnreadTracker unread,
        EventBus eventBus,
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
        _topics = topics;
        _unread = unread;

        RootNavigation.SetServiceProvider(services);

        // NavigationView's template isn't applied yet inside the constructor,
        // so initial Navigate would NRE. Defer to Loaded.
        Loaded += OnFirstLoaded;

        // ===== Rail event subscriptions (bus; marshaled to the UI thread) =====
        // Structural — incremental single-item updates.
        eventBus.Subscribe<TopicAdded>(this, _ => OnTopicAdded(), ThreadOption.UIThread);
        eventBus.Subscribe<TopicUpdated>(this, e => OnTopicUpdated(e.Topic), ThreadOption.UIThread);
        eventBus.Subscribe<TopicDeleted>(this, e => OnTopicDeleted(e.TopicId), ThreadOption.UIThread);
        eventBus.Subscribe<TopicMoved>(this, e => OnTopicMoved(e.TopicId), ThreadOption.UIThread);
        eventBus.Subscribe<GroupMoved>(this, e => RepositionFolder(e.GroupName), ThreadOption.UIThread);

        // Server display / removal — rare; a full rebuild handles server-label
        // appear/disappear cleanly.
        eventBus.Subscribe<ServerDisplayChanged>(this, _ => RebuildTopicItems(), ThreadOption.UIThread);
        eventBus.Subscribe<ServerDeleted>(this, OnServerDeleted, ThreadOption.UIThread);

        // Status / pause / unread — targeted adornment updates, never a rebuild.
        eventBus.Subscribe<TopicConnectionStatusChanged>(this,
            e => RefreshTopicAdornment(e.TopicId, e.Status), ThreadOption.UIThread);
        eventBus.Subscribe<NotificationsStatusChanged>(this, _ => RefreshAllPauseGlyphs(), ThreadOption.UIThread);
        eventBus.Subscribe<TopicNotificationsStatusChanged>(this, OnTopicPauseChanged, ThreadOption.UIThread);
        eventBus.Subscribe<UnreadCountChanged>(this, _ => RefreshBadges(), ThreadOption.UIThread);

        // Window focus drives "viewing" state: regaining focus marks the current feed
        // read (catches messages that arrived while minimized to the tray).
        Activated   += (_, _) => _unread.SetWindowActive(true);
        Deactivated += (_, _) => _unread.SetWindowActive(false);

        // Remember the last non-minimized state so re-showing from the tray restores
        // maximized-vs-normal rather than always dropping to Normal, and keep the
        // persisted placement current as the user moves/resizes/maximizes the window.
        // Capture is in-memory only; it's written to disk on hide and on exit.
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _restoreWindowState = WindowState;
            CaptureWindowPlacement();
        };
        SizeChanged     += (_, _) => CaptureWindowPlacement();
        LocationChanged += (_, _) => CaptureWindowPlacement();

        ApplyPersistedPlacement();

        // Right-click "All topics" → Mark all read. Populated on open so it can disable
        // itself when there's nothing unread.
        var allTopicsMenu = new ContextMenu();
        AllTopicsItem.ContextMenu = allTopicsMenu;
        AllTopicsItem.ContextMenuOpening += (_, _) => PopulateAllTopicsContextMenu(allTopicsMenu);

        // Navigation guard for the Settings page (prompt on unsaved changes).
        RootNavigation.Navigating += OnNavigationViewNavigating;
        RootNavigation.Navigated  += OnNavigationViewNavigated;

        SystemThemeWatcher.Watch(this);
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;

        _allTopicsBadge = new IconBadge(AllTopicsIcon);

        // "All topics" is a drop target for ungrouping: drag a topic onto it to pull
        // it out of its group. (It's a drop target only — never a drag source.)
        AllTopicsItem.AllowDrop = true;
        AllTopicsItem.DragOver += (s, e1) => OnRailDragOver((NavigationViewItem)s, e1);
        AllTopicsItem.Drop += (s, e1) => OnRailDrop((NavigationViewItem)s, e1);

        RebuildTopicItems();
        // Navigating to FeedPage marks AllTopicsItem (TargetPageType=FeedPage) as active.
        RootNavigation.Navigate(typeof(FeedPage));
        RefreshBadges();
    }

    // Rebuild rather than incrementally insert the one new item. WPF-UI's NavigationView
    // doesn't reliably register a dynamically-inserted item for selection — most visibly a
    // topic dropped into a brand-new group folder — so the first click on it never raised
    // SelectionChanged and the feed wouldn't switch to that topic (it took an app restart,
    // i.e. a clean rail build, to start working). A rebuild reproduces that clean-build
    // state, the same reason OnTopicMoved rebuilds. Adds are rare, user-initiated actions,
    // so the extra work is imperceptible.
    private void OnTopicAdded() => RebuildTopicItems();

    private void OnTopicUpdated(TopicSettings topic)
    {
        // An edit can change label, enabled, server (subtitle) and group — rebuild the
        // single item in its (possibly new) place. Cheap; only on a deliberate edit.
        RepositionTopicItem(topic);
    }

    private void OnTopicDeleted(Guid topicId)
    {
        RemoveTopicItem(topicId);

        // Viewing the deleted topic? Fall back to All topics.
        if (_feedVm.CurrentTopicId == topicId)
            NavigateToAllTopics();
    }

    private void OnTopicMoved(Guid topicId)
    {
        // Reorder via rebuild. Incremental same-container reposition works for drag-drop,
        // but leaves WPF-UI selection stuck when triggered from a closing context menu
        // (Move up/down); a rebuild re-syncs selection reliably. Reorders are rare.
        RebuildTopicItems();
    }

    private void OnServerDeleted(ServerDeleted ev)
    {
        // The server's topics are gone from settings — a full rebuild drops their rail
        // items and refreshes remaining server-label visibility. Rare action.
        RebuildTopicItems();

        if (_feedVm.CurrentTopicId is { } id && ev.RemovedTopicIds.Contains(id))
            NavigateToAllTopics();
    }

    private void OnTopicPauseChanged(TopicNotificationsStatusChanged ev)
    {
        if (_railItems.TryGetValue(ev.TopicId, out var rail))
            rail.PauseGlyph.Visibility =
                ev.IsPaused || _gate.IsGloballyPaused ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshAllPauseGlyphs()
    {
        foreach (var (id, rail) in _railItems)
            rail.PauseGlyph.Visibility = _gate.IsTopicPaused(id) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavigateToAllTopics()
    {
        RootNavigation.Navigate(typeof(FeedPage));
        _feedVm.CurrentTopicId = null;
        _unread.SetActiveView(ActiveView.AllTopics);
    }

    private NavigationViewItem EnsureGroupFolder(string groupName)
    {
        if (_groupFolders.TryGetValue(groupName, out var existingFolder))
            return existingFolder;

        var folder = BuildGroupFolder(groupName);
        _groupFolders[groupName] = folder;
        RootNavigation.MenuItems.Insert(FolderInsertIndex(groupName), folder);

        return folder;
    }

    // Folders live after the ungrouped topics, ordered by OrderedGroupNames. Computed
    // from current menu state, so it's valid both when creating a folder and when
    // re-inserting one after a GroupMoved (the caller removes it first).
    private int FolderInsertIndex(string groupName)
    {
        var order = _topics.Arrangement.OrderedGroupNames;
        var groupIndex = order.IndexOf(groupName);
        var menu = RootNavigation.MenuItems;

        if (groupIndex <= 0)
        {
            var ungroupedCount = _topics.Arrangement.GetTopicsInGroup(null).Count;
            return menu.IndexOf(AddTopicItem) + 1 + ungroupedCount;
        }

        // The preceding group always has a folder: OrderedGroupNames only lists groups
        // in use, and any in-use group created its folder via this path.
        return menu.IndexOf(_groupFolders[order[groupIndex - 1]]) + 1;
    }

    private void RepositionFolder(string groupName)
    {
        if (!_groupFolders.TryGetValue(groupName, out var folder)) return;

        var menu = RootNavigation.MenuItems;
        menu.Remove(folder);
        menu.Insert(FolderInsertIndex(groupName), folder);
    }

    private void InsertTopicItem(TopicSettings topic)
    {
        TopicsSeparator.Visibility = Visibility.Visible;

        var rail = BuildRailItem(topic);
        _railItems[topic.Id] = rail;
        ApplyEnabledStyling(rail.Item, topic.Enabled);

        var groupName = TopicArrangement.GetTopicGroupName(topic);

        if (groupName.Length == 0)
        {
            // Ungrouped: a direct child of the menu, contiguous after AddTopicItem.
            var section = _topics.Arrangement.GetTopicsInGroup(null);
            var k = section.FindIndex(t => t.Id == topic.Id);

            var menu = RootNavigation.MenuItems;
            menu.Insert(menu.IndexOf(AddTopicItem) + 1 + k, rail.Item);
        }
        else
        {
            // Grouped: into the folder, whose children are exactly that group's topics
            // in order — so the section index is the folder index directly.
            var folder = EnsureGroupFolder(groupName);
            var section = _topics.Arrangement.GetTopicsInGroup(groupName);
            var k = section.FindIndex(t => t.Id == topic.Id);

            folder.MenuItems.Insert(k, rail.Item);
        }

        RefreshTopicAdornment(topic.Id);
        RefreshBadges();
    }

    // Removes by id (using the item's captured Group) so it works even after the topic
    // is gone from settings — TopicDeleted carries only the id.
    private void RemoveTopicItem(Guid topicId)
    {
        if (!_railItems.Remove(topicId, out var rail)) return;

        rail.Badge.Detach();

        if (rail.Group.Length == 0)
        {
            RootNavigation.MenuItems.Remove(rail.Item);
        }
        else if (_groupFolders.TryGetValue(rail.Group, out var folder))
        {
            folder.MenuItems.Remove(rail.Item);

            // Drop the folder (and its badge) once its last topic leaves.
            if (folder.MenuItems.Count == 0)
            {
                RootNavigation.MenuItems.Remove(folder);
                _groupFolders.Remove(rail.Group);
                _groupBadges.Remove(rail.Group);
            }
        }

        RefreshBadges();
    }

    // Moves/refreshes an existing rail item in place, PRESERVING the NavigationViewItem
    // instance. Replacing it with a fresh instance breaks WPF-UI NavigationView
    // selection for that item (it keeps tracking the removed instance), so we reuse it.
    private void RepositionTopicItem(TopicSettings topic)
    {
        if (!_railItems.TryGetValue(topic.Id, out var rail))
        {
            InsertTopicItem(topic); // not tracked yet (e.g. an update racing the add)
            return;
        }

        var newGroup = TopicArrangement.GetTopicGroupName(topic);

        // Cross-group move: WPF-UI doesn't reliably reparent a live NavigationViewItem
        // between the root and a folder (the item lands in limbo). Rebuild instead — a
        // rare, deliberate action, and a rebuild gives correct structure + selection.
        if (newGroup != rail.Group)
        {
            RebuildTopicItems();
            return;
        }

        // Same group — update content (rename / enable / server) and reorder in place,
        // PRESERVING the instance (a fresh one would break WPF-UI selection for it).
        rail.Label.Text = topic.EffectiveDisplayName;
        ApplyEnabledStyling(rail.Item, topic.Enabled);
        if (rail.Subtitle is not null)
            rail.Subtitle.Text = _settings.GetServer(topic.ServerId)?.DisplayLabel ?? string.Empty;

        RemoveItemFromContainer(rail);

        if (newGroup.Length == 0)
        {
            var section = _topics.Arrangement.GetTopicsInGroup(null);
            var k = section.FindIndex(t => t.Id == topic.Id);
            var menu = RootNavigation.MenuItems;
            menu.Insert(menu.IndexOf(AddTopicItem) + 1 + k, rail.Item);
        }
        else
        {
            var folder = _groupFolders[newGroup];
            var section = _topics.Arrangement.GetTopicsInGroup(newGroup);
            var k = section.FindIndex(t => t.Id == topic.Id);
            folder.MenuItems.Insert(k, rail.Item);
        }

        RefreshTopicAdornment(topic.Id);
        RefreshBadges();
    }

    // Detaches an item from its current rail container without untracking it.
    private void RemoveItemFromContainer(RailItem rail)
    {
        if (rail.Group.Length == 0)
            RootNavigation.MenuItems.Remove(rail.Item);
        else if (_groupFolders.TryGetValue(rail.Group, out var folder))
            folder.MenuItems.Remove(rail.Item);
    }

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
        _groupFolders.Clear();

        if (_settings.Topics.Count == 0)
        {
            TopicsSeparator.Visibility = Visibility.Collapsed;
            return;
        }

        TopicsSeparator.Visibility = Visibility.Visible;
        var insertAt = anchorIdx + 1;

        // Order is the Topics list order within a section, and GroupOrder for the
        // folders themselves (both user-arranged). Ungrouped topics sit at the top
        // level, above the folders.
        _topics.Arrangement.GetTopicsInGroup(null).ForEach(t => menu.Insert(insertAt++, Register(t).Item));

        foreach (var groupName in _topics.Arrangement.OrderedGroupNames)
        {
            var folder = BuildGroupFolder(groupName);
            _groupFolders[groupName] = folder;

            foreach (var t in _topics.Arrangement.GetTopicsInGroup(groupName))
                folder.MenuItems.Add(Register(t).Item);

            menu.Insert(insertAt++, folder);
        }

        RefreshTopicAdornments();
        RefreshBadges();

        return;

        RailItem Register(TopicSettings t)
        {
            var item = BuildRailItem(t);
            _railItems[t.Id] = item;
            ApplyEnabledStyling(item.Item, t.Enabled);
            return item;
        }
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
            // Tag = group name identifies it as a folder drop target.
            Tag = groupName,
            ContextMenu = BuildGroupContextMenu(groupName),
        };

        WireDragDrop(folder, groupName);

        // Restore persisted collapse state before wiring the listener, so this
        // programmatic set doesn't trigger a redundant save.
        folder.IsExpanded = !_topics.Arrangement.IsGroupCollapsed(groupName);

        _groupBadges[groupName] = new IconBadge(icon);

        // NavigationViewItem has no Expanded/Collapsed event, so watch the DP.
        // Detach on Unloaded — folders are recreated on every rebuild.
        var dpd = DependencyPropertyDescriptor.FromProperty(
            NavigationViewItem.IsExpandedProperty, typeof(NavigationViewItem));
        dpd.AddValueChanged(folder, OnExpandChanged);
        folder.Unloaded += (_, _) => dpd.RemoveValueChanged(folder, OnExpandChanged);

        return folder;
        
        void OnExpandChanged(object? s, EventArgs e) => _topics.Arrangement.SetGroupCollapsed(groupName, !folder.IsExpanded);
    }

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
    
    private RailItem BuildRailItem(TopicSettings topic)
    {
        var showServer = _settings.Servers.Count > 1 && _settings.ShowServerLabel;
        var serverSubtitle = showServer ? _settings.GetServer(topic.ServerId)?.DisplayLabel : null;
        
        var pip = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = _pipDisconnectedBrush,
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
            Foreground = _pausedGlyphBrush,
            Visibility = Visibility.Collapsed,
        };

        // Label row (topic name + pause glyph), optionally stacked over a muted
        // server-name subtitle (when server labels are shown).
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
        labelRow.Children.Add(label);
        labelRow.Children.Add(pauseGlyph);

        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
        };
        textStack.Children.Add(labelRow);

        System.Windows.Controls.TextBlock? subtitle = null;
        
        if (!string.IsNullOrEmpty(serverSubtitle)) {

            subtitle = new() {
                Text = serverSubtitle,
                FontSize = 11,
                Foreground = _pausedGlyphBrush,
                Margin = new Thickness(0, 1, 0, 0),
            };
            
            textStack.Children.Add(subtitle);
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(pip);
        content.Children.Add(textStack);

        var icon = new SymbolIcon { Symbol = SymbolRegular.Tag24 };

        // Topic actions live on a right-click menu, rebuilt each time it opens so its
        // labels (Pause/Resume, Enable/Disable, move-edge enablement) reflect current
        // state. Each item carrying its own menu also stops a right-click inside a
        // folder from bubbling up to the folder's (group) menu.
        var contextMenu = new ContextMenu();

        var item = new NavigationViewItem
        {
            Content = content,
            // Tag (TopicId) drives _feedVm.CurrentTopicId in OnNavigationSelectionChanged.
            Tag = topic.Id,
            TargetPageType = typeof(FeedPage),
            Icon = icon,
            ContextMenu = contextMenu,
        };
        // ContextMenuOpening fires before the menu is shown, so populating here sizes
        // it correctly on the first open.
        item.ContextMenuOpening += (_, _) => PopulateTopicContextMenu(contextMenu, topic.Id);

        WireDragDrop(item, topic.Id);

        return new RailItem(item, pip, pauseGlyph, new IconBadge(icon), label, subtitle,
            TopicArrangement.GetTopicGroupName(topic));
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
            RefreshTopicAdornment(state.TopicId, state.Status);
    }

    private void RefreshTopicAdornment(Guid topicId, TopicConnectionStatus? connectionStatus = null)
    {
        if (!_railItems.TryGetValue(topicId, out var rail)) return;
        
        rail.Pip.Fill = PipBrushFor(connectionStatus ?? _connections.GetTopicConnectionStatus(topicId));
        rail.PauseGlyph.Visibility = _gate.IsTopicPaused(topicId)
            ? Visibility.Visible :  Visibility.Collapsed;
        
        var topic = _settings.GetTopicById(topicId);
        if (topic is not null)
            ApplyEnabledStyling(rail.Item, topic.Enabled);
    }

    private static Brush PipBrushFor(TopicConnectionStatus status) => status switch
    {
        TopicConnectionStatus.Connected    => _pipConnectedBrush,
        TopicConnectionStatus.Connecting   => _pipConnectingBrush,
        _                                  => _pipDisconnectedBrush,
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
        try
        {
            e.Handled = true;

            if (!await EnsureServerConfiguredAsync()) return;

            var dialog = new TopicEditorDialog(existing: null, _settings.Servers, _settings.DefaultServerId, _topics.Arrangement.GroupNames) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;

            _topics.AddOrUpdate(dialog.Result, original: null);
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
        if (_settings.IsDefaultServerUsable) return true;
        
        await ShowServerNotConfiguredWarning();
        return false;
    }

    // ===== Topic right-click menu =====
    //
    // Repopulated each time it opens so labels reflect current state (Pause vs
    // Resume, Enable vs Disable, move-edge enablement) without extra subscriptions.
    private void PopulateAllTopicsContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        var markAllRead = new System.Windows.Controls.MenuItem
        {
            Header = "Mark all read",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark24, FontSize = 14 },
            IsEnabled = _unread.Total > 0,
        };
        markAllRead.Click += (_, _) => _unread.MarkAllRead();
        menu.Items.Add(markAllRead);
    }

    private void PopulateTopicContextMenu(ContextMenu menu, Guid topicId)
    {
        menu.Items.Clear();
        var topic = _settings.GetTopicById(topicId);
        if (topic is null) return;

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
        enableItem.Click += async (_, _) => _topics.ToggleEnabled(topic);

        // Mark this topic's messages read. Disabled when it has no unread.
        var markReadItem = new System.Windows.Controls.MenuItem
        {
            Header = "Mark as read",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark24, FontSize = 14 },
            IsEnabled = _unread.CountFor(topicId) > 0,
        };
        markReadItem.Click += (_, _) => _unread.MarkTopicRead(topicId);

        var editItem = new System.Windows.Controls.MenuItem
        {
            Header = "Edit",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Edit24, FontSize = 14 },
        };
        editItem.Click += (_, _) =>
        {
            try
            {
                var dialog = new TopicEditorDialog(topic, _settings.Servers, _settings.DefaultServerId, _topics.Arrangement.GroupNames) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.Result is null) return;
                _topics.AddOrUpdate(dialog.Result, original: topic);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unexpected error: " + ex.Message);
            }
        };

        var moveUp = new System.Windows.Controls.MenuItem
        {
            Header = "Move up",
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowUp24, FontSize = 14 },
            IsEnabled = _topics.Arrangement.CanMoveTopicWithinGroup(topic, -1),
        };
        moveUp.Click += (_, _) => _topics.Arrangement.MoveTopicWithinGroup(topic, -1);

        var moveDown = new System.Windows.Controls.MenuItem
        {
            Header = "Move down",
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowDown24, FontSize = 14 },
            IsEnabled = _topics.Arrangement.CanMoveTopicWithinGroup(topic, +1),
        };
        moveDown.Click += (_, _) => _topics.Arrangement.MoveTopicWithinGroup(topic, +1);

        var moveToGroup = BuildMoveToGroupMenu(topic);

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
        menu.Items.Add(markReadItem);
        menu.Items.Add(editItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        if (moveToGroup is not null) menu.Items.Add(moveToGroup);
        menu.Items.Add(new Separator());
        menu.Items.Add(removeItem);
    }

    // "Move to group ▸" submenu: existing groups + Ungrouped. New groups are created
    // via the editor dialog's group combo, not here. Returns null when there's
    // nothing to offer (no groups and the topic is already ungrouped).
    private System.Windows.Controls.MenuItem? BuildMoveToGroupMenu(TopicSettings topic)
    {
        var groupNames = _topics.Arrangement.OrderedGroupNames;
        var current = topic.GroupName?.Trim() ?? string.Empty;

        if (groupNames.Count == 0 && current.Length == 0) return null;

        var root = new System.Windows.Controls.MenuItem
        {
            Header = "Move to group",
            Icon = new SymbolIcon { Symbol = SymbolRegular.FolderArrowRight24, FontSize = 14 },
        };

        foreach (var g in groupNames)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = g,
                IsChecked = string.Equals(g, current, StringComparison.Ordinal),
            };
            var target = g;
            item.Click += (_, _) => _topics.Arrangement.MoveTopicToGroup(topic, target);
            root.Items.Add(item);
        }

        if (groupNames.Count > 0) root.Items.Add(new Separator());

        var ungrouped = new System.Windows.Controls.MenuItem
        {
            Header = "Ungrouped",
            IsChecked = current.Length == 0,
        };
        ungrouped.Click += (_, _) => _topics.Arrangement.MoveTopicToGroup(topic, null);
        root.Items.Add(ungrouped);

        return root;
    }

    // Right-click menu on a group folder: reorder it among the other folders.
    private ContextMenu BuildGroupContextMenu(string groupName)
    {
        var menu = new ContextMenu();

        var up = new System.Windows.Controls.MenuItem
        {
            Header = "Move up",
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowUp24, FontSize = 14 },
            IsEnabled = _topics.Arrangement.CanMoveGroup(groupName, -1),
        };
        up.Click += (_, _) => _topics.Arrangement.MoveGroup(groupName, -1);

        var down = new System.Windows.Controls.MenuItem
        {
            Header = "Move down",
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowDown24, FontSize = 14 },
            IsEnabled = _topics.Arrangement.CanMoveGroup(groupName, +1),
        };
        down.Click += (_, _) => _topics.Arrangement.MoveGroup(groupName, +1);

        menu.Items.Add(up);
        menu.Items.Add(down);
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

        _topics.Remove(topic, deleteHistory);
    }

    /// <summary>
    /// Navigates to the feed for a specific topic. Called from App.HandleActivation
    /// after a toast click forwards us a ntfy-desktop:// URL. If the topic is no
    /// longer subscribed (user removed it), falls back to "All topics".
    /// </summary>
    public void NavigateToTopic(Guid topicId)
    {
        // Navigate by the specific rail item's Id, NOT by typeof(FeedPage): every rail
        // item shares TargetPageType=FeedPage, so a type-based navigate always resolves
        // to the first match ("All topics") and leaves the rail highlighting the wrong
        // row. Navigating by id activates that exact NavigationViewItem (deactivating the
        // previously-selected one) and raises SelectionChanged, which drives the feed and
        // unread active-view through the same path as a user click. Falls back to "All
        // topics" if the topic is no longer subscribed (user removed it).
        if (!_railItems.TryGetValue(topicId, out var rail))
        {
            RootNavigation.Navigate(AllTopicsItem.Id);
            return;
        }

        // If the topic lives in a group folder, expand it first. NavigationViewItem.Activate
        // would normally expand the parent, but WPF-UI nulls a child's parent link when it's
        // unloaded — which a collapsed folder does — so it can't, and the activated topic
        // would stay hidden inside the collapsed folder. Expanding here also persists the
        // open state via the folder's IsExpanded watcher.
        if (_groupFolders.TryGetValue(rail.Group, out var folder))
            folder.IsExpanded = true;

        RootNavigation.Navigate(rail.Item.Id);
    }

    // Restore the size/position/maximized state the user last left the window in. Runs in
    // the constructor (before the window is first shown from the tray) so there's no
    // reposition flicker. Normal-state bounds are applied even when opening maximized, so
    // a later un-maximize lands on the saved size rather than the XAML default. Saved
    // bounds that no longer fall on any screen (e.g. a monitor was unplugged) are ignored.
    private void ApplyPersistedPlacement()
    {
        var p = _settings.Window;

        if (p is { Left: { } left, Top: { } top, Width: { } width, Height: { } height }
            && IsOnScreen(left, top, width, height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left; Top = top; Width = width; Height = height;
        }

        _restoreWindowState = p.Maximized ? WindowState.Maximized : WindowState.Normal;
        WindowState = _restoreWindowState;
    }

    // Snapshot the current placement into settings (in-memory only; persisted on hide/exit).
    // The normal-state bounds come from RestoreBounds while maximized, so we store the size
    // the window un-maximizes to — not the maximized rect.
    private void CaptureWindowPlacement()
    {
        if (!IsLoaded) return; // bounds aren't meaningful before the first show

        var p = _settings.Window;
        p.Maximized = _restoreWindowState == WindowState.Maximized;

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds is { Width: > 0, Height: > 0 })
        {
            p.Left = bounds.Left; p.Top = bounds.Top;
            p.Width = bounds.Width; p.Height = bounds.Height;
        }
    }

    // True if a meaningful portion of the rect overlaps the virtual desktop, so a window
    // restored to it has a grabbable title bar (guards against off-screen saved bounds).
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(left, top, width, height);
        rect.Intersect(virtualScreen);
        return rect is { Width: >= 100, Height: >= 50 };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        CaptureWindowPlacement();
        _settings.Save();
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

        public void Detach()
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
                Background = _badgeBrush,
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

        protected override int VisualChildrenCount => _children.Count;
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

    // ===== Rail drag-and-drop =====
    //
    // Drag a topic to reorder it within its section or drop it onto a folder to move
    // it into that group; drag a folder to reorder it among the other folders. Each
    // rail item is both a drag source and a drop target; the payload is the topic id
    // (Guid) or the group name (string), which also matches how items carry their Tag.
    // The moves themselves live in TopicsViewModel.

    private void WireDragDrop(NavigationViewItem item, object payload)
    {
        item.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStartPoint = e.GetPosition(null);
            _dragCandidate = payload;
        };
        item.PreviewMouseMove += (s, e) => OnRailMouseMove((NavigationViewItem)s, e);
        item.AllowDrop = true;
        item.DragOver += (s, e) => OnRailDragOver((NavigationViewItem)s, e);
        item.Drop += (s, e) => OnRailDrop((NavigationViewItem)s, e);
    }

    private void OnRailMouseMove(NavigationViewItem item, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;

        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var payload = _dragCandidate;
        _dragCandidate = null;
        _dragging = payload;
        try { DragDrop.DoDragDrop(item, new DataObject("ntfyRail", payload), DragDropEffects.Move); }
        finally { _dragging = null; ClearDrop(); }
    }

    private void OnRailDragOver(NavigationViewItem target, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (_dragging is null) return;

        var targetIsTopic = target.Tag is Guid;
        var targetIsFolder = target.Tag is string {Length: > 0};
        var targetIsAllTopics = ReferenceEquals(target, AllTopicsItem);

        if (_dragging is Guid)
        {
            if (targetIsTopic)
            {
                // Reorder relative to a topic: insertion line by vertical midpoint.
                var before = e.GetPosition(target).Y < target.ActualHeight / 2;
                ShowLine(target, before);
                e.Effects = DragDropEffects.Move;
            }
            else if (targetIsFolder || targetIsAllTopics)
            {
                // Into a group (folder) or out to ungrouped (All topics): highlight the
                // whole target — there's no before/after, the topic just joins it.
                ShowHighlight(target);
                e.Effects = DragDropEffects.Move;
            }
            else ClearDrop();
        }
        else if (_dragging is string draggedGroup)
        {
            // A folder reorders only among folders. Decide before/after by the groups'
            // relative order, not pixels: an expanded folder's height includes its
            // children, so a midpoint test would always read as "before".
            if (targetIsFolder)
            {
                ShowLine(target, _topics.Arrangement.GroupGoesBefore(draggedGroup, (string)target.Tag));
                e.Effects = DragDropEffects.Move;
            }
            else ClearDrop();
        }
    }

    private void OnRailDrop(NavigationViewItem target, DragEventArgs e)
    {
        e.Handled = true;
        var payload = _dragging;
        ClearDrop();
        if (payload is null) return;

        if (payload is Guid draggedId)
        {
            var dragged = _settings.GetTopicById(draggedId);
            if (dragged is null) return;

            if (target.Tag is Guid anchorId)
            {
                var anchor = _settings.GetTopicById(anchorId);
                var before = e.GetPosition(target).Y < target.ActualHeight / 2;
                if (anchor is not null) _topics.Arrangement.MoveTopicRelativeTo(dragged, anchor, before);
            }
            else if (ReferenceEquals(target, AllTopicsItem))
            {
                _topics.Arrangement.MoveTopicToGroup(dragged, null); // ungroup
            }
            else if (target.Tag is string {Length: > 0} group)
            {
                // Drop onto a folder → into that group, at the top.
                var first = _topics.Arrangement.FirstTopicInGroup(group);
                if (first is not null) _topics.Arrangement.MoveTopicRelativeTo(dragged, first, before: true);
                else                    _topics.Arrangement.MoveTopicToGroup(dragged, group);
            }
        }
        else if (payload is string draggedGroup && target.Tag is string {Length: > 0} anchorGroup)
            _topics.Arrangement.MoveGroupRelativeTo(draggedGroup, anchorGroup);
    }

    private void ShowLine(UIElement target, bool atTop) =>
        SetDrop(target, "line", atTop, () => new InsertionAdorner(target, atTop));

    private void ShowHighlight(UIElement target) =>
        SetDrop(target, "highlight", false, () => new HighlightAdorner(target));

    private void SetDrop(UIElement target, string mode, bool flag, Func<Adorner> make)
    {
        var key = (target, mode, flag);
        if (_dropAdorner is not null && _dropKey == key) return;
        ClearDrop();

        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null) return;
        _dropAdorner = make();
        _dropKey = key;
        layer.Add(_dropAdorner);
    }

    private void ClearDrop()
    {
        if (_dropAdorner is null) return;
        AdornerLayer.GetAdornerLayer(_dropAdorner.AdornedElement)?.Remove(_dropAdorner);
        _dropAdorner = null;
        _dropKey = null;
    }

    // A horizontal insertion line at the top or bottom edge of the hovered item.
    private sealed class InsertionAdorner : Adorner
    {
        private static readonly Pen _pen = CreatePen();
        private readonly bool _atTop;

        public InsertionAdorner(UIElement adornedElement, bool atTop) : base(adornedElement)
        {
            _atTop = atTop;
            IsHitTestVisible = false;
        }

        private static Pen CreatePen()
        {
            var pen = new Pen(_badgeBrush, 2);
            pen.Freeze();
            return pen;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var y = _atTop ? 1 : AdornedElement.RenderSize.Height - 1;
            drawingContext.DrawLine(_pen, new Point(0, y), new Point(AdornedElement.RenderSize.Width, y));
        }
    }

    // A rounded highlight over the whole item — "drop into this group" / "ungroup".
    private sealed class HighlightAdorner : Adorner
    {
        private static readonly Brush _fill = FrozenFill();
        private static readonly Pen _pen = CreatePen();

        public HighlightAdorner(UIElement adornedElement) : base(adornedElement) => IsHitTestVisible = false;

        private static Brush FrozenFill() { var b = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x67, 0xC0)); b.Freeze(); return b; }
        private static Pen CreatePen() { var p = new Pen(_badgeBrush, 1.5); p.Freeze(); return p; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var w = Math.Max(0, AdornedElement.RenderSize.Width - 2);
            var h = Math.Max(0, AdornedElement.RenderSize.Height - 2);
            drawingContext.DrawRoundedRectangle(_fill, _pen, new Rect(new Point(1, 1), new Size(w, h)), 4, 4);
        }
    }
}
