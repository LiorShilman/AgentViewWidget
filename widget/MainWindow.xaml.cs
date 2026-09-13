using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Ellipse = System.Windows.Shapes.Ellipse;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace AgentLiveWidget;

public partial class MainWindow : Window
{
    private const string ServerUrl = "ws://localhost:4577/live";

    private readonly WebSocketService _ws = new(ServerUrl);
    private readonly ObservableCollection<EventItem> _events = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ServerLauncher _serverLauncher = new();

    private TrayIconService? _tray;
    private AgentState _state = new();
    private bool _connected;
    private bool _positioned;
    private string _lastStatusKey = "";

    private readonly Dictionary<string, AgentState> _projects = new();
    private List<AgentState> _lastProjects = [];
    private string? _selectedProjectKey;
    private string? _lastRenderedProjectKey;
    private long? _lastTopEventTimestamp;
    private long? _lastShownMemorySaveAt;
    private bool? _memShimmerHigh;

    private Storyboard _pulse = null!;
    private Storyboard _compactPulse = null!;
    private Storyboard _memPulse = null!;
    private bool _pulseRunning;
    private bool _memPulseRunning;

    // Below the server's 5-minute hard watchdog (which force-resets to idle),
    // this just flags "no update in a while" on a project that should still
    // be active — an early, non-destructive warning, not a state change.
    private const long StaleThresholdMs = 90_000;

    private static bool IsStale(AgentState project)
    {
        if (project.Status is not ("thinking" or "running_tool" or "waiting_approval")) return false;
        if (project.RecentEvents.Count == 0) return false;
        var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - project.RecentEvents[0].Timestamp;
        return ageMs > StaleThresholdMs;
    }

    private static string StaleAge(AgentState project)
    {
        var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - project.RecentEvents[0].Timestamp;
        var minutes = (int)(ageMs / 60_000);
        return minutes >= 1 ? $"{minutes}m" : $"{ageMs / 1000}s";
    }

    private sealed record StatusStyle(Color Color, string Label);

    private static readonly Dictionary<string, StatusStyle> StatusStyles = new()
    {
        ["idle"] = new(Color.FromRgb(0x34, 0xD3, 0x99), "Idle"),
        ["thinking"] = new(Color.FromRgb(0xA7, 0x8B, 0xFA), "Thinking…"),
        ["running_tool"] = new(Color.FromRgb(0x60, 0xA5, 0xFA), "Running tool"),
        ["waiting_approval"] = new(Color.FromRgb(0xFB, 0xBF, 0x24), "Waiting for approval"),
        ["offline"] = new(Color.FromRgb(0xF8, 0x71, 0x71), "Offline"),
    };

    public MainWindow()
    {
        InitializeComponent();

        _pulse = (Storyboard)FindResource("PulseStoryboard");
        _compactPulse = (Storyboard)FindResource("CompactPulseStoryboard");
        _memPulse = (Storyboard)FindResource("MemPulseStoryboard");

        EventsList.ItemsSource = _events;

        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        _ws.SnapshotReceived += snapshot => Dispatcher.BeginInvoke(() => OnSnapshot(snapshot));
        _ws.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => OnConnectionChanged(connected));

        Loaded += OnLoaded;
        SizeChanged += OnWindowSizeChanged;
    }

    // ===== window setup =====

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Top + 12;
        _positioned = true;

        _tray = new TrayIconService(ToggleVisibility, ExitApplication);
        ApplyStatusVisuals("offline");

        _ = _serverLauncher.EnsureRunningAsync();
        _ws.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Tool window (no alt-tab entry) that never steals focus from the editor.
        var handle = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        _ = SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Keep the right edge anchored when switching expanded/compact.</summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_positioned && e.PreviousSize.Width > 0)
        {
            Left += e.PreviousSize.Width - e.NewSize.Width;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* released mid-gesture */ }
        }
    }

    // ===== state handling =====

    private void OnConnectionChanged(bool connected)
    {
        _connected = connected;
        if (!connected)
        {
            ApplyStatusVisuals("offline");
            _ = _serverLauncher.EnsureRunningAsync();
        }
    }

    private void OnSnapshot(WidgetSnapshot snapshot)
    {
        if (!_connected) return;

        _lastProjects = snapshot.Projects;
        _projects.Clear();
        foreach (var project in snapshot.Projects)
        {
            _projects[project.Key] = project;
        }

        // Auto-follow whichever project last had activity, unless the user has
        // manually pinned a tab that's still alive.
        if (_selectedProjectKey is null || !_projects.ContainsKey(_selectedProjectKey))
        {
            _selectedProjectKey = snapshot.ActiveProjectKey ?? snapshot.Projects.FirstOrDefault()?.Key;
        }

        RenderTabs();

        if (_selectedProjectKey is not null && _projects.TryGetValue(_selectedProjectKey, out var selected))
        {
            ApplyProjectState(selected);
        }
    }

    private void SelectProject(string key)
    {
        if (key == _selectedProjectKey) return;
        _selectedProjectKey = key;
        RenderTabs();
        if (_projects.TryGetValue(key, out var project))
        {
            ApplyProjectState(project);
        }
    }

    private void RenderTabs()
    {
        TabsPanel.Children.Clear();
        TabsPanel.Visibility = _lastProjects.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_lastProjects.Count <= 1) return;

        foreach (var project in _lastProjects)
        {
            var style = StatusStyles.TryGetValue(project.Status, out var s) ? s : StatusStyles["offline"];
            bool isSelected = project.Key == _selectedProjectKey;
            var accent = style.Color;

            var name = string.IsNullOrEmpty(project.ProjectPath)
                ? "…"
                : Path.GetFileName(project.ProjectPath.TrimEnd('\\', '/'));

            bool stale = IsStale(project);

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(isSelected ? "TextPrimaryBrush" : "TextSecondaryBrush"),
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = name,
            });
            if (stale)
            {
                nameRow.Children.Add(new TextBlock
                {
                    Text = "⚠",
                    Margin = new Thickness(5, 0, 0, 0),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                    ToolTip = $"No update in {StaleAge(project)} — might be stuck",
                });
            }

            // Live one-line activity summary, so every project's status reads at a
            // glance from the tab bar itself — no need to select each tab in turn.
            var activity = GetActivitySummary(project, style);

            var textStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(nameRow);
            textStack.Children.Add(new TextBlock
            {
                Text = activity,
                FontSize = 9,
                Margin = new Thickness(0, 1, 0, 0),
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = activity,
            });

            // A Grid — not a WrapPanel chip — so the text column stretches to the
            // whole card width instead of being capped to fit several tabs per row.
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 0, 8, 0),
                Fill = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(textStack, 1);
            row.Children.Add(dot);
            row.Children.Add(textStack);

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                BorderBrush = isSelected
                    ? new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B))
                    : (Brush)FindResource("CardBorderBrush"),
                Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B))
                    : (Brush)FindResource("CardBrush"),
                Tag = project.Key,
                ToolTip = project.ProjectPath,
                Child = row,
            };
            border.MouseLeftButtonDown += TabBorder_MouseLeftButtonDown;

            TabsPanel.Children.Add(border);
        }
    }

    /// <summary>One-line "what's this project doing right now" summary for the tab bar.</summary>
    private static string GetActivitySummary(AgentState project, StatusStyle style)
    {
        // currentTool stays populated through "thinking"/"waiting_approval" too
        // (the last tool used, not cleared just because the turn moved on), so
        // show it whenever we have it — the tab's status dot already carries
        // the thinking/running/waiting distinction via its color.
        if (project.CurrentTool is { } tool)
        {
            var file = string.IsNullOrEmpty(tool.FilePath) ? null : Path.GetFileName(tool.FilePath);
            return file is null ? tool.Name : $"{tool.Name} · {file}";
        }
        return style.Label;
    }

    private void TabBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // don't let this bubble into the window-drag handler
        if (sender is Border { Tag: string key })
        {
            SelectProject(key);
        }
    }

    private const int MaxFileChipsShown = 6;

    private void RenderFilesChanged(List<string> files)
    {
        FilesChangedPanel.Children.Clear();

        if (files.Count == 0)
        {
            FilesChangedSection.Visibility = Visibility.Collapsed;
            return;
        }

        FilesChangedSection.Visibility = Visibility.Visible;
        FilesChangedLabel.Text = $"FILES CHANGED ({files.Count})";

        foreach (var path in files.Take(MaxFileChipsShown))
        {
            FilesChangedPanel.Children.Add(FileChip(Path.GetFileName(path.TrimEnd('\\', '/')), path));
        }

        if (files.Count > MaxFileChipsShown)
        {
            FilesChangedPanel.Children.Add(FileChip($"+{files.Count - MaxFileChipsShown}", null));
        }
    }

    private Border FileChip(string text, string? tooltip) => new()
    {
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 3, 7, 3),
        Margin = new Thickness(0, 0, 6, 6),
        Background = (Brush)FindResource("CardBrush"),
        BorderBrush = (Brush)FindResource("CardBorderBrush"),
        BorderThickness = new Thickness(1),
        ToolTip = tooltip,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = (Brush)FindResource(tooltip is null ? "TextTertiaryBrush" : "TextSecondaryBrush"),
        },
    };

    private void ApplyProjectState(AgentState state)
    {
        _state = state;

        // A tab switch shows a different project's history — prime the "what
        // have we already shown" baselines silently instead of firing effects
        // for events/saves that happened before we were looking at this tab.
        bool projectChanged = state.Key != _lastRenderedProjectKey;
        _lastRenderedProjectKey = state.Key;

        ApplyStatusVisuals(state.Status);

        // Project name from path
        ProjectNameText.Text = string.IsNullOrEmpty(state.ProjectPath)
            ? "Agent Live"
            : Path.GetFileName(state.ProjectPath.TrimEnd('\\', '/'));

        // Git branch + dirty count (best-effort; absent for non-repos). A
        // colored dot carries the clean/dirty signal — green vs. amber reads
        // instantly, the same language used by every other status indicator
        // in this widget, rather than relying on subtle text/border shifts.
        if (state.Git is { } git)
        {
            bool dirty = git.ChangedCount > 0;
            GitBadgeText.Text = dirty ? $"{git.Branch} · {git.ChangedCount}" : git.Branch;
            var dotBrush = new SolidColorBrush(dirty
                ? Color.FromRgb(0xFB, 0xBF, 0x24)
                : Color.FromRgb(0x34, 0xD3, 0x99));
            GitBadgeDot.Fill = dotBrush;
            GitBadge.BorderBrush = dirty
                ? new SolidColorBrush(Color.FromArgb(0x55, 0xFB, 0xBF, 0x24))
                : (Brush)FindResource("CardBorderBrush");
            GitBadge.Visibility = Visibility.Visible;
        }
        else
        {
            GitBadge.Visibility = Visibility.Collapsed;
        }

        // Current activity
        if (state.CurrentTool is { } tool)
        {
            ToolNameText.Text = tool.Name;
            ToolFileText.Text = ShortPath(tool.FilePath) ?? "…";
        }
        else
        {
            ToolNameText.Text = state.Status switch
            {
                "thinking" => "Thinking…",
                "waiting_approval" => "Waiting for approval",
                "offline" => "Offline",
                _ => "Idle",
            };
            ToolFileText.Text = "No active tool";
        }

        // Last prompt
        PromptText.Text = string.IsNullOrWhiteSpace(state.LastPrompt) ? "No prompt yet" : state.LastPrompt;
        PromptText.ToolTip = string.IsNullOrWhiteSpace(state.LastPrompt) ? null : state.LastPrompt;

        // Last response
        ResponseText.Text = string.IsNullOrWhiteSpace(state.LastResponse) ? "No response yet" : state.LastResponse;
        ResponseText.ToolTip = string.IsNullOrWhiteSpace(state.LastResponse) ? null : state.LastResponse;

        // Last command output (Bash only) — hidden until there's something to show
        if (string.IsNullOrWhiteSpace(state.LastOutput))
        {
            OutputSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            OutputText.Text = state.LastOutput;
            OutputText.ToolTip = state.LastOutput;
            OutputSection.Visibility = Visibility.Visible;
        }

        // Files changed this session
        RenderFilesChanged(state.FilesTouched);

        // Memory pressure
        ApplyMemoryPressure(state.MemoryPressure == "high");

        // "Saved to memory" flash — only for a genuinely new save while this
        // tab is being watched, not a stale one inherited from a tab switch.
        if (projectChanged)
        {
            _lastShownMemorySaveAt = state.LastMemorySaveAt;
        }
        else if (state.LastMemorySaveAt is { } savedAt && savedAt != _lastShownMemorySaveAt)
        {
            _lastShownMemorySaveAt = savedAt;
            FlashMemorySaveIndicator();
        }

        // Event log — for a genuinely new top event, the row must not appear
        // until the flow particle actually lands: cause (particle leaves the
        // core) before effect (row appears), not the other way around.
        var newestEvent = state.RecentEvents.Count > 0 ? state.RecentEvents[0] : null;
        bool spawnParticle = !projectChanged && newestEvent is not null && newestEvent.Timestamp != _lastTopEventTimestamp;
        _lastTopEventTimestamp = newestEvent?.Timestamp;

        if (spawnParticle)
        {
            _events.Clear();
            foreach (var ev in state.RecentEvents.Skip(1))
            {
                _events.Add(new EventItem(ev.Type, ev.Icon, ev.Label, ev.Timestamp));
            }
            EmptyLogText.Visibility = _events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var arriving = newestEvent!;
            Dispatcher.BeginInvoke(new Action(() =>
                SpawnFlowParticle(() =>
                {
                    // If events are arriving faster than a particle's flight time,
                    // a later rebuild may already have shown this one — guard
                    // against inserting it a second time.
                    bool alreadyShown = _events.Count > 0 && _events[0].Timestamp == arriving.Timestamp;
                    if (!alreadyShown)
                    {
                        _events.Insert(0, new EventItem(arriving.Type, arriving.Icon, arriving.Label, arriving.Timestamp));
                    }
                    EmptyLogText.Visibility = Visibility.Collapsed;
                })), DispatcherPriority.Loaded);
        }
        else
        {
            _events.Clear();
            foreach (var ev in state.RecentEvents)
            {
                _events.Add(new EventItem(ev.Type, ev.Icon, ev.Label, ev.Timestamp));
            }
            EmptyLogText.Visibility = _events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        Tick();
    }

    private void ApplyStatusVisuals(string statusKey)
    {
        if (!StatusStyles.TryGetValue(statusKey, out var style))
        {
            style = StatusStyles["offline"];
            statusKey = "offline";
        }

        var brush = new SolidColorBrush(style.Color);
        DotCore.Fill = brush;
        DotGlow.Fill = brush;
        CompactDotCore.Fill = brush;
        CompactDotGlow.Fill = brush;
        StatusText.Text = style.Label;
        CompactStatusText.Text = style.Label;

        ActivityCore.Apply(style.Color, statusKey);

        _tray?.SetStatusColor(System.Drawing.Color.FromArgb(style.Color.R, style.Color.G, style.Color.B));

        var active = statusKey is "thinking" or "running_tool" or "waiting_approval";
        if (active && !_pulseRunning)
        {
            _pulse.Begin(this, true);
            _compactPulse.Begin(this, true);
            _pulseRunning = true;
        }
        else if (!active && _pulseRunning)
        {
            _pulse.Stop(this);
            _compactPulse.Stop(this);
            DotGlow.Opacity = 0;
            CompactDotGlow.Opacity = 0;
            _pulseRunning = false;
        }

        _lastStatusKey = statusKey;
    }

    private void ApplyMemoryPressure(bool high)
    {
        MemLabel.Text = high ? "HIGH" : "NORMAL";
        MemLabel.Foreground = new SolidColorBrush(high
            ? Color.FromRgb(0xEF, 0x44, 0x44)
            : Color.FromRgb(0x34, 0xD3, 0x99));
        MemFill.Background = (Brush)FindResource(high ? "MemHighBrush" : "MemNormalBrush");

        var trackWidth = MemTrack.ActualWidth > 0 ? MemTrack.ActualWidth : 464;
        var target = trackWidth * (high ? 0.94 : 0.28);
        MemFill.BeginAnimation(WidthProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase(),
            });

        if (high && !_memPulseRunning)
        {
            _memPulse.Begin(this, true);
            _memPulseRunning = true;
        }
        else if (!high && _memPulseRunning)
        {
            _memPulse.Stop(this);
            MemFill.Opacity = 1;
            _memPulseRunning = false;
        }

        if (_memShimmerHigh != high)
        {
            _memShimmerHigh = high;
            var shimmer = new DoubleAnimation(-26, 300, TimeSpan.FromSeconds(high ? 0.8 : 2.0))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            MemShimmerTranslate.BeginAnimation(TranslateTransform.XProperty, shimmer);
        }
    }

    /// <summary>Brief pulse on the header's brain icon when a memory file gets written.</summary>
    private void FlashMemorySaveIndicator()
    {
        var opacity = new DoubleAnimationUsingKeyFrames();
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800))));
        MemorySaveIndicator.BeginAnimation(OpacityProperty, opacity);

        var scale = new DoubleAnimationUsingKeyFrames();
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
        });
        scale.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))));
        MemorySaveScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        MemorySaveScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    /// <summary>
    /// Sends a small glowing particle from the energy core to the top of the
    /// event log. <paramref name="onArrived"/> fires when the particle lands
    /// (or immediately, if it couldn't be drawn) — callers that gate the
    /// actual log row on this callback get cause-then-effect ordering: the
    /// particle leaves the core before the row appears, never the reverse.
    /// </summary>
    private void SpawnFlowParticle(Action? onArrived = null)
    {
        if (ExpandedRoot.Visibility != Visibility.Visible || ActivityCore.ActualWidth <= 0 || EventsList.ActualWidth <= 0)
        {
            onArrived?.Invoke();
            return;
        }

        Point start, end;
        try
        {
            start = ActivityCore.TranslatePoint(new Point(ActivityCore.ActualWidth / 2, ActivityCore.ActualHeight / 2), EffectsOverlay);
            end = EventsList.TranslatePoint(new Point(12, 8), EffectsOverlay);
        }
        catch (InvalidOperationException)
        {
            onArrived?.Invoke(); // elements not connected to the same visual tree yet
            return;
        }

        var accent = StatusStyles.TryGetValue(_state.Status, out var style)
            ? style.Color
            : StatusStyles["running_tool"].Color;

        var particle = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(accent),
            Effect = new DropShadowEffect { Color = accent, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 },
        };
        Canvas.SetLeft(particle, start.X - 3);
        Canvas.SetTop(particle, start.Y - 3);
        EffectsOverlay.Children.Add(particle);

        var duration = TimeSpan.FromMilliseconds(550);
        var moveX = new DoubleAnimation(start.X - 3, end.X - 3, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        var moveY = new DoubleAnimation(start.Y - 3, end.Y - 3, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        var fade = new DoubleAnimation(1, 0, duration) { BeginTime = TimeSpan.FromMilliseconds(350) };
        moveX.Completed += (_, _) =>
        {
            EffectsOverlay.Children.Remove(particle);
            onArrived?.Invoke();
        };

        particle.BeginAnimation(Canvas.LeftProperty, moveX);
        particle.BeginAnimation(Canvas.TopProperty, moveY);
        particle.BeginAnimation(OpacityProperty, fade);
    }

    // ===== per-second refresh =====

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Session duration
        if (_connected && _state.SessionStartedAt is { } startedAt && _lastStatusKey != "offline")
        {
            var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, now - startedAt));
            SessionTimeText.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
        }
        else
        {
            SessionTimeText.Text = "--:--";
        }

        // Live tool elapsed time — only while the tool is actually running.
        // currentTool now stays populated through "thinking" too (the last
        // tool used, kept as context instead of being blanked out), so this
        // must gate on status too or it'd render as a still-ticking timer
        // for a tool that already finished.
        if (_connected && _state.Status == "running_tool" && _state.CurrentTool is { } tool)
        {
            var seconds = Math.Max(0, (now - tool.StartedAt) / 1000.0);
            ToolNameText.Text = seconds >= 1 ? $"{tool.Name} · {seconds:0}s" : tool.Name;
        }

        foreach (var item in _events)
        {
            item.UpdateTimeAgo();
        }

        // Staleness crosses its threshold purely with the passage of time, so
        // it's re-evaluated every tick rather than only on new snapshots.
        if (_connected && IsStale(_state))
        {
            var baseLabel = StatusStyles.TryGetValue(_state.Status, out var s) ? s.Label : _state.Status;
            StatusText.Text = $"{baseLabel} · stale {StaleAge(_state)}";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        }
        else if (_connected)
        {
            var style = StatusStyles.TryGetValue(_state.Status, out var s) ? s : StatusStyles["offline"];
            StatusText.Text = style.Label;
            StatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }

        RenderTabs();
    }

    // ===== helpers =====

    private static string? ShortPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Split('\\', '/');
        return parts.Length <= 2 ? path : string.Join("/", parts[^2..]);
    }

    // ===== UI actions =====

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => SetExpanded(false);

    private void ExpandButton_Click(object sender, RoutedEventArgs e) => SetExpanded(true);

    private void SetExpanded(bool expanded)
    {
        var show = expanded ? (UIElement)ExpandedRoot : CompactRoot;
        var hide = expanded ? (UIElement)CompactRoot : ExpandedRoot;

        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        show.Opacity = 0;
        show.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void ToggleVisibility()
    {
        if (IsVisible) Hide(); else Show();
    }

    private void ExitApplication()
    {
        _timer.Stop();
        _ws.Dispose();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _ws.Dispose();
        _tray?.Dispose();
        base.OnClosed(e);
    }
}
