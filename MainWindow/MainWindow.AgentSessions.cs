using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Panel = System.Windows.Controls.Panel;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace alpha_island;

public partial class MainWindow
{
    private sealed class AgentSessionView(alpha_island.ViewModels.AgentSessionViewModel model,
        Grid root, TextBlock title, TextBlock detail, TextBlock value, SemanticIcon icon)
    {
        internal string Key => model.Key; internal string Owner => model.Owner; internal string Source => model.Source; internal string ArtworkKey => model.Owner;
        internal int Number => model.Number;
        internal readonly Grid Root = root;
        internal readonly TextBlock Title = title, Detail = detail, Value = value;
        internal readonly SemanticIcon Icon = icon;
        internal AgentActivity? Activity { get => model.Activity; set => model.Activity = value; }
        internal AgentAppWindow? Target;
        internal AgentRunState LastState;
        internal long LastActivityId;
        internal string ArtworkFamily { get => model.ArtworkFamily; set => model.ArtworkFamily = value; }
        internal bool Up;
    }
    private readonly Dictionary<string, AgentSessionView> _agentSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> _agentSessionArtworks = new(StringComparer.Ordinal);
    private bool _updatingAgentSessions;
    private string _agentWindowKey = AgentTickerKey;
    private static bool IsAgentIslandKey(string? key) => key == AgentTickerKey || key?.StartsWith("agentstatus:", StringComparison.Ordinal) == true;
    private string? AgentSessionKey(string owner) => _agentSessions.Values.FirstOrDefault(s => s.Owner == owner)?.Key;
    private AgentSessionView? AgentSession(string? key) => key != null && _agentSessions.TryGetValue(key, out var view) ? view : null;
    private bool PrimaryAgentClaimActive => AgentSession(AgentTickerKey) is { } view && AgentStatus.HasClaim(view.Owner);
    private bool AgentWindowSessionBusy => AgentSession(_agentWindowKey)?.Activity != null;
    private bool MultipleAgentCardIslands => _state == IslandState.Card && _agentSessions.Keys.Count(k => _layers.IsActive(k)) > 1;
    private string? ActiveAgentIslandKey => _agentSessions.Keys.FirstOrDefault(k => _layers.IsActive(k));
    private string AgentPage(string key) => _plugins?.PageForActivity(AgentSession(key)?.Owner ?? "") ?? IslandPageIds.Ai;
    private static string AgentSource(string owner) => owner.StartsWith("external:", StringComparison.Ordinal)
        ? owner[9..].Split('/')[0] : "internal";
    private static string AgentSourceName(string source) => source.ToLowerInvariant() switch
    {
        "codex" => "Codex", "antigravity" => "Antigravity", "deepseek-harness" or "dsh" => "DSH",
        "claude-code" => "Claude Code",
        "internal" => L.T("AI 助手"), _ => source
    };
    private string AgentLabel(string key) => AgentSession(key) is { } view ? AgentSourceName(view.Source) + L.T(" · 会话 ") + view.Number : L.T("AI 助手");
    private AgentRunState AgentStateFor(string? key) => AgentSession(key) is { } view ? view.Activity?.State ?? AgentRunState.Idle : key == AgentTickerKey ? AgentStatus.State : AgentRunState.Idle;
    private Brush AgentBrush(string key) => AgentStateFor(key) == AgentRunState.Idle ? ResolveAccentBrush()
        : new SolidColorBrush(ParseAgentColor(AgentStatus.ColorHexFor(AgentStateFor(key))));
    private int AgentSessionPriority(string key) => AgentStateFor(key) is AgentRunState.ToolWait or AgentRunState.InputWait ? 84 : 48;
    private string AgentGlyph(string key) => AgentStateFor(key) switch
    {
        AgentRunState.Working => "\uE713", AgentRunState.Thinking => "\uE945", AgentRunState.Streaming => "\uE895", AgentRunState.ToolWait => "\uE73E",
        AgentRunState.InputWait => "\uE897", AgentRunState.Error => "\uE783", _ => "\uE99A"
    };
    private static string AgentStateTitle(AgentRunState state) => state switch
    {
        AgentRunState.Working => L.T("正在执行"), AgentRunState.Thinking => L.T("正在思考"), AgentRunState.Streaming => L.T("正在输出"), AgentRunState.ToolWait => L.T("等待工具授权"),
        AgentRunState.InputWait => L.T("等待回复"), AgentRunState.Error => L.T("出错了"), _ => L.T("空闲")
    };
    private bool AgentHasDefaultAvatar(string key) => AgentSession(key) is { } avatar
        && AgentAvatarMotion.UsesDefaultAvatar(AvatarSource(avatar), _agentSessionArtworks.GetValueOrDefault(avatar.Owner));
    private ActionIconAnimation AgentMotion(string key) => AgentHasDefaultAvatar(key)
        ? ActionIconAnimation.None : AgentStateFor(key) switch
    {
        AgentRunState.Thinking => ActionIconAnimation.Spin, AgentRunState.ToolWait or AgentRunState.InputWait or AgentRunState.Idle => ActionIconAnimation.Tick,
        AgentRunState.Error => ActionIconAnimation.Disconnect, _ => ActionIconAnimation.Pulse
    };
    private bool AgentSessionHidden(AgentSessionView view) => MediaWindowHandoffEnabled
        && (view.Owner == _agentWindowOwner && ShouldHideForegroundAgent
            || view.Target != null && _agentNavigation.IsForeground(view.Target) && !Interop.NativeMethods.IsIconic(view.Target.Handle));

    private AgentSessionView EnsureAgentSession(AgentActivity activity)
    {
        if (AgentSessionKey(activity.Owner) is { } existing) return _agentSessions[existing];
        var model = _workspace.Agents.GetOrCreate(activity, AgentTickerKey);
        string key = model.Key;
        Grid root; TextBlock title, detail, value; SemanticIcon icon;
        if (key == AgentTickerKey)
        { root = AgentNotifyLayer; title = AgentNotifyTitle; detail = AgentNotifyDetail; value = AgentNotifyValue; icon = AgentNotifyGlyph; }
        else
        {
            root = new Grid { Width = 328, Height = 32, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Opacity = 0, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            icon = new SemanticIcon { Shape = IconShape.Agent, Width = 22, Height = 22,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0x8A, 0x5C, 0xFF)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x8A, 0x5C, 0xFF)), BorderThickness = new Thickness(1), Child = icon };
            root.Children.Add(badge);
            title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            detail = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            var text = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title); text.Children.Add(detail); Grid.SetColumn(text, 1); root.Children.Add(text);
            value = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(value, 2); root.Children.Add(value);
            AutomationProperties.SetAutomationId(root, "AgentSession_" + key[12..]);
            ((Panel)AgentNotifyLayer.Parent).Children.Add(root);
            string owner = activity.Owner;
            _layers.Register(new IslandLayerSpec(key, root, Priority: 48,
                ForceActive: () => AgentStatus.HasClaim(owner), PriorityOverride: () => AgentSessionPriority(key)));
        }
        var view = new AgentSessionView(model, root, title, detail, value, icon) { Activity = activity };
        icon.Tag = view.ArtworkKey;
        icon.Source = _agentSessionArtworks.GetValueOrDefault(view.ArtworkKey);
        _agentSessions.Add(key, view);
        return view;
    }

    private void UpdateAgentSessions()
    {
        if (_updatingAgentSessions || _options == null || BarAgentDot == null) return;
        if (!SettingsRegistry.GetBool(_options, "AgentStatusEnabled", true))
        {
            if (_agentStatusEnabled) StopAgentStatus();
            _agentStatusEnabled = false; return;
        }
        _updatingAgentSessions = true;
        try
        {
            _agentStatusEnabled = true;
            var activities = AgentStatus.Snapshot.Where(a => a.Owner != "approval").ToArray();
            foreach (var view in _agentSessions.Values.ToArray())
            {
                view.Activity = activities.FirstOrDefault(a => a.Owner == view.Owner);
                if (view.Activity == null && !_layers.IsPinned(view.Key)) RemoveAgentSession(view);
            }
            foreach (var activity in activities) EnsureAgentSession(activity).Activity = activity;
            var targets = MediaWindowHandoffEnabled
                ? _agentNavigation.ResolveActivities(_agentSessions.Values.Select(v => v.Owner)) : new Dictionary<string, AgentAppWindow>();
            foreach (var view in _agentSessions.Values) view.Target = targets.GetValueOrDefault(view.Owner);
            SyncAgentWindowTarget();
            foreach (var view in _agentSessions.Values.ToArray())
            {
                bool hidden = AgentSessionHidden(view);
                _layers.SetPresentationSuppressed(view.Key, hidden);
                bool up = view.Activity != null && !hidden;
                long activityId = view.Activity?.ActivityId ?? 0;
                var state = view.Activity?.State ?? AgentRunState.Idle;
                bool enteredWait = state is AgentRunState.ToolWait or AgentRunState.InputWait && view.LastState != state;
                bool stateChanged = view.LastState != state;
                bool newActivity = activityId != 0 && activityId != view.LastActivityId;
                WriteAgentSession(view);
                if (up)
                {
                    bool announce = (!view.Up || newActivity || enteredWait) && !_clipboardLocked && !_sessionLocked
                        && !_fileDragActive && !_exportingTransferItem;
                    var request = IslandNotification.Seconds(view.Key, 3, opensIsland: announce) with
                    { Title = view.Title.Text, Detail = view.Detail.Text, Glyph = view.Icon.Text, Value = view.Value.Text };
                    if (announce) ShowPillNotification(request);
                    else _layers.Request(request, requestSync: false);
                }
                else if (!_layers.IsPinned(view.Key) && !hidden) _layers.Hide(view.Key);
                if (stateChanged && (up || _layers.IsPinned(view.Key)))
                { ActionIconMotion.Stop(view.Icon); ActionIconMotion.SetKind(view.Icon, AgentMotion(view.Key)); ActionIconMotion.Play(view.Icon); }
                view.Up = up; view.LastState = state; view.LastActivityId = activityId;
            }
            _barAgentVisible = _agentSessions.Values.Any(v => v.Up);
            _agentLayerUp = _barAgentVisible;
            BarAgentDot.Visibility = _barAgentVisible ? Visibility.Visible : Visibility.Collapsed;
            if (_barAgentVisible)
            {
                var activity = _agentSessions.Values.Where(v => v.Up).OrderByDescending(v => v.Activity!.State == AgentRunState.ToolWait ? 5 : v.Activity.State == AgentRunState.InputWait ? 4 : v.Activity.State == AgentRunState.Error ? 3 : v.Activity.State is AgentRunState.Working or AgentRunState.Streaming ? 2 : 1).First().Activity!;
                BarAgentDot.Fill = new SolidColorBrush(ParseAgentColor(AgentStatus.ColorHexFor(activity.State)));
            }
            SyncNotificationLayers(); CollapseSuppressedAgentIsland(); RestorePinnedIdleAgentIsland();
        }
        finally { _updatingAgentSessions = false; }
    }

    private void WriteAgentSession(AgentSessionView view)
    {
        var state = view.Activity?.State ?? AgentRunState.Idle;
        string title = AgentStateTitle(state);
        view.Title.Text = _agentSessions.Count > 1 || view.Key != AgentTickerKey
            ? AgentSourceName(view.Source) + " " + view.Number + " · " + (state switch
            { AgentRunState.Working => L.T("执行中"), AgentRunState.Thinking => L.T("思考中"), AgentRunState.Streaming => L.T("输出中"), AgentRunState.ToolWait => L.T("等待授权"), AgentRunState.InputWait => L.T("等待回复"), AgentRunState.Error => L.T("出错了"), _ => L.T("空闲") })
            : title;
        view.Detail.Text = view.Activity?.Detail is { Length: > 0 } detail ? detail
            : view.Activity == null ? L.T("当前没有运行中的任务") : L.T("agent 运行中");
        if (state == AgentRunState.InputWait) view.Detail.Text = L.T("请在原对话中回复");
        view.Value.Text = view.Activity == null ? "—" : state == AgentRunState.Error ? "⚠"
            : Math.Max(0, (DateTime.UtcNow - view.Activity.SinceUtc).TotalSeconds).ToString("0.0") + "s";
        view.Icon.Text = AgentGlyph(view.Key); view.Icon.Active = view.Activity != null; view.Icon.Foreground = AgentBrush(view.Key);
        ApplyAgentArtwork(view.Key, view.Icon);
        view.Root.ToolTip = AgentLabel(view.Key) + " · " + title;
    }

    private void ApplyAgentArtwork(string key, SemanticIcon icon)
    {
        if (AgentSession(key) is not { } view) { AgentAvatarMotion.Release(icon); icon.Source = null; return; }
        if (view.Activity is { } activity && (view.Source == "internal" || activity.ArtworkFamily.Length > 0)) view.ArtworkFamily = activity.ArtworkFamily;
        var custom = _agentSessionArtworks.GetValueOrDefault(view.Owner);
        AgentAvatarMotion.Configure(icon, AvatarSource(view), AgentStateFor(key), custom);
        bool enlarged = AgentHasDefaultAvatar(key);
        if (icon == view.Icon)
        {
            icon.Width = icon.Height = icon.Source != null ? 32 : 22;
            if (icon.Parent is Border slot) slot.Width = enlarged ? 52 : 32;
        }
        else if (icon == SideBubbleIcon)
        {
            SideBubbleIconSlot.Width = enlarged ? 48 : 24;
            SideBubbleIconSlot.Height = enlarged ? 32 : 24;
            icon.Width = icon.Height = enlarged ? 32 : 24;
        }
        else if (icon.Name == "OrbGlyph") icon.Width = icon.Height = enlarged ? 32 : 26;
        icon.Tag = view.ArtworkKey;
    }
    private static string AvatarSource(AgentSessionView view) => view.Source == "internal"
        ? AlphaIsland.PluginSdk.ModelArtwork.Source(view.ArtworkFamily)
        : view.Source != "antigravity" ? view.Source : view.ArtworkFamily switch
    {
        // Older Antigravity hooks emitted "default" specifically for Claude models.
        "claude" or "default" => "claude-code",
        "deepseek" => "deepseek-harness",
        "codex" => "codex",
        _ => "antigravity"
    };

    // A stable owner identifies the artwork for this session, regardless of which island hosts it.
    private void SetAgentSessionArtwork(string owner, ImageSource? image)
    {
        if (image == null) _agentSessionArtworks.Remove(owner); else _agentSessionArtworks[owner] = image;
        if (AgentSessionKey(owner) is not { } key) return;
        ApplyAgentArtwork(key, _agentSessions[key].Icon);
        if (_satelliteOrbViews.TryGetValue(key, out var orb)) UpdateSatelliteOrbVisual(key, orb);
        if (_sideBubbleComponentKey == key) UpdateCompactIslandContent(key);
    }

    private void RemoveAgentSession(AgentSessionView view)
    {
        _agentSessions.Remove(view.Key); _workspace.Agents.Remove(view.Owner);
        AgentAvatarMotion.Release(view.Icon); ActionIconMotion.Stop(view.Icon); view.Icon.Source = null;
        if (view.Key == AgentTickerKey)
        { _layers.Hide(view.Key); _layers.SetPresentationSuppressed(view.Key, false); }
        else
        { _layers.Unregister(view.Key); ((Panel)view.Root.Parent).Children.Remove(view.Root); }
        _activeSatelliteOrder.Remove(view.Key);
        if (_satelliteOrbViews.Remove(view.Key, out var orb))
        { if (orb.Child is Grid grid) foreach (var glyph in grid.Children.OfType<SemanticIcon>()) AgentAvatarMotion.Release(glyph);
          SatelliteOrbAnimator.Hide(orb, immediate: true); SatellitesHost.Children.Remove(orb); _satellites.Unregister("orb-" + view.Key); }
    }
    private void ClearAgentSessions()
    {
        foreach (var view in _agentSessions.Values.ToArray())
        { _layers.SetPinned(view.Key, false); RemoveAgentSession(view); }
        _agentWindowKey = AgentTickerKey;
    }
}
