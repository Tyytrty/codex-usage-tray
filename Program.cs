using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new UsageTrayContext());
    }
}

internal enum IconDisplayMode
{
    Ring,
    Numbers,
    DualNumbers,
    Battery,
    CustomAssets,
}

internal enum NumericContent
{
    FiveHour,
    SevenDay,
    Both,
}

internal enum TooltipMode
{
    Short,
    Detailed,
}

internal sealed class UsageTrayContext : ApplicationContext
{
    private const int RefreshIntervalMs = 30_000;
    private const string StartupName = "CodexUsageTray";
    private static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromHours(6);
    private static readonly string DiagnosticsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageTray",
        "diagnostics.log");

    private readonly NotifyIcon _primaryIcon;
    private readonly NotifyIcon _secondaryIcon;
    private readonly ToolStripMenuItem _primaryItem = new("5h: loading") { Enabled = false };
    private readonly ToolStripMenuItem _secondaryItem = new("7d: loading") { Enabled = false };
    private readonly ToolStripMenuItem _updatedItem = new("Last updated: loading") { Enabled = false };
    private readonly ToolStripMenuItem _ringItem = new("圆环");
    private readonly ToolStripMenuItem _numbersItem = new("单图标数字");
    private readonly ToolStripMenuItem _dualNumbersItem = new("双托盘大数字");
    private readonly ToolStripMenuItem _batteryItem = new("电池条");
    private readonly ToolStripMenuItem _customAssetsItem = new("自定义图标 assets");
    private readonly ToolStripMenuItem _fiveHourItem = new("5 小时");
    private readonly ToolStripMenuItem _sevenDayItem = new("7 天");
    private readonly ToolStripMenuItem _bothItem = new("都显示");
    private readonly ToolStripMenuItem _shortTooltipItem = new("短 tooltip");
    private readonly ToolStripMenuItem _detailedTooltipItem = new("详细 tooltip");
    private readonly ToolStripMenuItem _notificationItem = new("低用量通知");
    private readonly ToolStripMenuItem _startupItem = new("开机自启动");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = RefreshIntervalMs };
    private readonly System.Windows.Forms.Timer _refreshDebounceTimer = new() { Interval = 1_000 };
    private readonly SynchronizationContext _uiContext;
    private readonly UsageLogReader _reader = new();
    private readonly TrayDisplaySettings _settings;
    private FileSystemWatcher? _sessionWatcher;
    private UsageSnapshot? _lastSnapshot;
    private DateTimeOffset? _lastLoggedRecordedAt;
    private DateTimeOffset? _notifiedPrimaryReset;
    private DateTimeOffset? _notifiedSecondaryReset;

    public UsageTrayContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = TrayDisplaySettings.Load();
        var menu = BuildMenu();

        _primaryIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = TrayIconFactory.CreateRing(0, unknown: true, _settings),
            Text = "Codex usage: loading",
            Visible = true,
        };
        _secondaryIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = TrayIconFactory.CreateRing(0, unknown: true, _settings),
            Text = "Codex 7d: loading",
            Visible = false,
        };

        _primaryIcon.DoubleClick += (_, _) => RefreshUsage();
        _secondaryIcon.DoubleClick += (_, _) => RefreshUsage();
        _timer.Tick += (_, _) => RefreshUsage();
        _refreshDebounceTimer.Tick += (_, _) =>
        {
            _refreshDebounceTimer.Stop();
            RefreshUsage();
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        InitializeSessionWatcher();
        _timer.Start();
        LogDiagnostic("started");
        RefreshUsage();
    }

    private ContextMenuStrip BuildMenu()
    {
        foreach (var item in new[]
                 {
                     _ringItem, _numbersItem, _dualNumbersItem, _batteryItem, _customAssetsItem,
                     _fiveHourItem, _sevenDayItem, _bothItem, _shortTooltipItem, _detailedTooltipItem,
                     _notificationItem, _startupItem,
                 })
        {
            item.CheckOnClick = false;
        }

        _ringItem.Click += (_, _) => SetDisplayMode(IconDisplayMode.Ring);
        _numbersItem.Click += (_, _) => SetDisplayMode(IconDisplayMode.Numbers);
        _dualNumbersItem.Click += (_, _) => SetDisplayMode(IconDisplayMode.DualNumbers);
        _batteryItem.Click += (_, _) => SetDisplayMode(IconDisplayMode.Battery);
        _customAssetsItem.Click += (_, _) => SetDisplayMode(IconDisplayMode.CustomAssets);
        _fiveHourItem.Click += (_, _) => SetNumericContent(NumericContent.FiveHour);
        _sevenDayItem.Click += (_, _) => SetNumericContent(NumericContent.SevenDay);
        _bothItem.Click += (_, _) => SetNumericContent(NumericContent.Both);
        _shortTooltipItem.Click += (_, _) => SetTooltipMode(TooltipMode.Short);
        _detailedTooltipItem.Click += (_, _) => SetTooltipMode(TooltipMode.Detailed);
        _notificationItem.Click += (_, _) => ToggleNotifications();
        _startupItem.Click += (_, _) => ToggleStartup();

        var menu = new ContextMenuStrip();
        var menuFont = SystemFonts.MenuFont ?? SystemFonts.DefaultFont;
        menu.Items.Add(new ToolStripMenuItem("Codex Usage Tray") { Enabled = false, Font = new Font(menuFont, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_primaryItem);
        menu.Items.Add(_secondaryItem);
        menu.Items.Add(_updatedItem);
        menu.Items.Add(new ToolStripSeparator());

        var displayMenu = new ToolStripMenuItem("显示方式");
        displayMenu.DropDownItems.AddRange([_ringItem, _numbersItem, _dualNumbersItem, _batteryItem, _customAssetsItem]);
        var numberMenu = new ToolStripMenuItem("单图标数字内容");
        numberMenu.DropDownItems.AddRange([_fiveHourItem, _sevenDayItem, _bothItem]);
        var tooltipMenu = new ToolStripMenuItem("Tooltip");
        tooltipMenu.DropDownItems.AddRange([_shortTooltipItem, _detailedTooltipItem]);
        var fontMenu = new ToolStripMenuItem("数字字体");
        foreach (var fontName in new[] { "Arial", "Microsoft YaHei UI", "Segoe UI", "Tahoma" })
        {
            var captured = fontName;
            fontMenu.DropDownItems.Add(new ToolStripMenuItem(captured, null, (_, _) => SetFont(captured))
            {
                Checked = string.Equals(_settings.FontName, captured, StringComparison.OrdinalIgnoreCase),
            });
        }

        menu.Items.Add(displayMenu);
        menu.Items.Add(numberMenu);
        menu.Items.Add(fontMenu);
        menu.Items.Add(tooltipMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_notificationItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("立即刷新", null, (_, _) => RefreshUsage()));
        menu.Items.Add(new ToolStripMenuItem("重新加载配置", null, (_, _) => ReloadSettingsAndRefresh()));
        menu.Items.Add(new ToolStripMenuItem("打开配置文件", null, (_, _) => OpenConfigFile()));
        menu.Items.Add(new ToolStripMenuItem("打开诊断日志", null, (_, _) => OpenDiagnosticsLog()));
        menu.Items.Add(new ToolStripMenuItem("打开自定义图标目录", null, (_, _) => OpenAssetsFolder()));
        menu.Items.Add(new ToolStripMenuItem("打开 Codex 会话目录", null, (_, _) => OpenSessionsFolder()));
        menu.Items.Add(new ToolStripMenuItem("重置设置", null, (_, _) => ResetSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitThread()));

        UpdateMenuChecks(menu);
        return menu;
    }

    private void SetDisplayMode(IconDisplayMode mode)
    {
        _settings.IconMode = mode;
        SaveSettingsAndRefresh();
    }

    private void SetNumericContent(NumericContent content)
    {
        _settings.NumericContent = content;
        _settings.IconMode = IconDisplayMode.Numbers;
        SaveSettingsAndRefresh();
    }

    private void SetTooltipMode(TooltipMode mode)
    {
        _settings.TooltipMode = mode;
        SaveSettingsAndRefresh();
    }

    private void SetFont(string fontName)
    {
        _settings.FontName = fontName;
        SaveSettingsAndRefresh();
    }

    private void ToggleNotifications()
    {
        _settings.EnableLowUsageNotifications = !_settings.EnableLowUsageNotifications;
        SaveSettingsAndRefresh();
    }

    private void ToggleStartup()
    {
        if (IsStartupEnabled())
            Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)?.DeleteValue(StartupName, throwOnMissingValue: false);
        else
            SetStartupEnabled();
        UpdateMenuChecks(_primaryIcon.ContextMenuStrip);
    }

    private void ReloadSettingsAndRefresh()
    {
        var fresh = TrayDisplaySettings.Load();
        _settings.CopyFrom(fresh);
        UpdateMenuChecks(_primaryIcon.ContextMenuStrip);
        RefreshUsage();
    }

    private void ResetSettings()
    {
        _settings.CopyFrom(new TrayDisplaySettings());
        _settings.Save();
        UpdateMenuChecks(_primaryIcon.ContextMenuStrip);
        RefreshUsage();
    }

    private void SaveSettingsAndRefresh()
    {
        _settings.Save();
        UpdateMenuChecks(_primaryIcon.ContextMenuStrip);
        if (_lastSnapshot is { } snapshot)
            UpdateIcon(snapshot);
    }

    private void UpdateMenuChecks(ContextMenuStrip? menu)
    {
        _ringItem.Checked = _settings.IconMode == IconDisplayMode.Ring;
        _numbersItem.Checked = _settings.IconMode == IconDisplayMode.Numbers;
        _dualNumbersItem.Checked = _settings.IconMode == IconDisplayMode.DualNumbers;
        _batteryItem.Checked = _settings.IconMode == IconDisplayMode.Battery;
        _customAssetsItem.Checked = _settings.IconMode == IconDisplayMode.CustomAssets;
        _fiveHourItem.Checked = _settings.NumericContent == NumericContent.FiveHour;
        _sevenDayItem.Checked = _settings.NumericContent == NumericContent.SevenDay;
        _bothItem.Checked = _settings.NumericContent == NumericContent.Both;
        _shortTooltipItem.Checked = _settings.TooltipMode == TooltipMode.Short;
        _detailedTooltipItem.Checked = _settings.TooltipMode == TooltipMode.Detailed;
        _notificationItem.Checked = _settings.EnableLowUsageNotifications;
        _startupItem.Checked = IsStartupEnabled();

        if (menu is null) return;
        foreach (var fontItem in menu.Items.OfType<ToolStripMenuItem>().SelectMany(FlattenMenuItems).Where(i => i.OwnerItem?.Text == "数字字体"))
            fontItem.Checked = string.Equals(fontItem.Text, _settings.FontName, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ToolStripMenuItem> FlattenMenuItems(ToolStripMenuItem item)
    {
        foreach (ToolStripItem child in item.DropDownItems)
            if (child is ToolStripMenuItem menuItem)
                yield return menuItem;
    }

    private void RefreshUsage()
    {
        try
        {
            if (_sessionWatcher is null)
                InitializeSessionWatcher();

            var snapshot = _reader.ReadLatest();
            if (snapshot is null)
            {
                ShowUnavailable("No Codex usage snapshot found");
                return;
            }

            var current = snapshot.Value;
            if (IsStaleSnapshot(current, out var staleReason))
            {
                LogDiagnostic($"stale snapshot ignored: {staleReason}; recorded={current.RecordedAt:O}; has5h={current.HasFiveHour}; 5hReset={(current.HasFiveHour ? current.Primary.ResetsAt.ToString("O") : "n/a")}; 7dReset={current.Secondary.ResetsAt:O}");
                ShowUnavailable(staleReason);
                return;
            }

            _lastSnapshot = current;
            _primaryItem.Text = current.HasFiveHour ? FormatWindow("5h", current.Primary) : "5h: unavailable in current Codex data";
            _secondaryItem.Text = FormatWindow("7d", current.Secondary);
            _updatedItem.Text = $"Last updated: {current.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
            UpdateTooltips(current);
            UpdateIcon(current);
            MaybeNotifyLowUsage(current);
            if (_lastLoggedRecordedAt != current.RecordedAt)
            {
                _lastLoggedRecordedAt = current.RecordedAt;
                LogDiagnostic(current.HasFiveHour
                    ? $"snapshot {current.RecordedAt:O}; 5h={current.Primary.RemainingPercent:0}%; 7d={current.Secondary.RemainingPercent:0}%"
                    : $"snapshot {current.RecordedAt:O}; 5h=unavailable; 7d={current.Secondary.RemainingPercent:0}% (primary-only format)");
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic($"read failed: {ex}");
            ShowUnavailable($"Read failed: {ex.Message}");
        }
    }

    private static bool IsStaleSnapshot(UsageSnapshot snapshot, out string reason)
    {
        var now = DateTimeOffset.Now;
        var age = now - snapshot.RecordedAt.ToLocalTime();
        var freshnessWindow = snapshot.HasFiveHour ? snapshot.Primary : snapshot.Secondary;
        if (freshnessWindow.ResetsAt <= now)
        {
            var label = snapshot.HasFiveHour ? "5h" : "weekly";
            reason = $"Latest local {label} snapshot expired at {freshnessWindow.ResetsAt.LocalDateTime:MM-dd HH:mm}";
            return true;
        }

        if (age > MaxSnapshotAge)
        {
            reason = $"Latest local usage snapshot is stale ({snapshot.RecordedAt.LocalDateTime:MM-dd HH:mm})";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private void InitializeSessionWatcher()
    {
        try
        {
            if (!Directory.Exists(_reader.SessionsPath))
            {
                LogDiagnostic($"sessions directory not found: {_reader.SessionsPath}");
                return;
            }

            _sessionWatcher?.Dispose();
            _sessionWatcher = new FileSystemWatcher(_reader.SessionsPath, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _sessionWatcher.Created += (_, _) => QueueRefresh("session file created");
            _sessionWatcher.Changed += (_, _) => QueueRefresh("session file changed");
            _sessionWatcher.Renamed += (_, _) => QueueRefresh("session file renamed");
            _sessionWatcher.Error += (_, args) =>
            {
                LogDiagnostic($"watcher error: {args.GetException()}");
                _uiContext.Post(_ =>
                {
                    _sessionWatcher?.Dispose();
                    _sessionWatcher = null;
                    InitializeSessionWatcher();
                    QueueRefresh("watcher restarted");
                }, null);
            };
            LogDiagnostic($"watching sessions: {_reader.SessionsPath}");
        }
        catch (Exception ex)
        {
            LogDiagnostic($"watcher init failed: {ex}");
        }
    }

    private void QueueRefresh(string reason)
    {
        _uiContext.Post(_ =>
        {
            LogDiagnostic($"queued refresh: {reason}");
            _refreshDebounceTimer.Stop();
            _refreshDebounceTimer.Start();
        }, null);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            QueueRefresh("power resume");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect)
            QueueRefresh($"session switch: {e.Reason}");
    }

    private void UpdateIcon(UsageSnapshot snapshot)
    {
        _secondaryIcon.Visible = _settings.IconMode == IconDisplayMode.DualNumbers && snapshot.HasFiveHour;
        var limitingRemaining = snapshot.HasFiveHour
            ? Math.Min(snapshot.Primary.RemainingPercent, snapshot.Secondary.RemainingPercent)
            : snapshot.Secondary.RemainingPercent;

        switch (_settings.IconMode)
        {
            case IconDisplayMode.Ring:
                ReplaceIcon(_primaryIcon, TrayIconFactory.CreateRing(limitingRemaining, unknown: false, _settings));
                break;
            case IconDisplayMode.Battery:
                ReplaceIcon(_primaryIcon, TrayIconFactory.CreateBattery(limitingRemaining, unknown: false, _settings));
                break;
            case IconDisplayMode.DualNumbers:
                if (snapshot.HasFiveHour)
                {
                    var dualIcons = TrayIconFactory.CreateDualNumbers(snapshot, _settings);
                    ReplaceIcon(_primaryIcon, dualIcons.Primary);
                    ReplaceIcon(_secondaryIcon, dualIcons.Secondary);
                }
                else
                {
                    ReplaceIcon(_primaryIcon, TrayIconFactory.CreateSingleNumber(snapshot.Secondary.RemainingPercent, _settings, "7d"));
                }
                break;
            case IconDisplayMode.CustomAssets:
                ReplaceIcon(_primaryIcon, TrayIconFactory.CreateCustomOrFallback(snapshot, _settings));
                break;
            default:
                ReplaceIcon(_primaryIcon, TrayIconFactory.CreateNumbers(snapshot, _settings.NumericContent, _settings));
                break;
        }
    }

    private static void ReplaceIcon(NotifyIcon notifyIcon, Icon icon)
    {
        var previous = notifyIcon.Icon;
        notifyIcon.Icon = icon;
        previous?.Dispose();
    }

    private void UpdateTooltips(UsageSnapshot snapshot)
    {
        var p = snapshot.Primary;
        var s = snapshot.Secondary;
        var text = snapshot.HasFiveHour
            ? (_settings.TooltipMode == TooltipMode.Short
                ? $"Codex 5h {p.RemainingPercent:0}% | 7d {s.RemainingPercent:0}%"
                : $"5h {p.RemainingPercent:0}% -> {p.ResetsAt.LocalDateTime:MM-dd HH:mm}; 7d {s.RemainingPercent:0}% -> {s.ResetsAt.LocalDateTime:MM-dd HH:mm}")
            : (_settings.TooltipMode == TooltipMode.Short
                ? $"Codex 7d {s.RemainingPercent:0}% (5h unavailable)"
                : $"7d {s.RemainingPercent:0}% -> {s.ResetsAt.LocalDateTime:MM-dd HH:mm}; 5h unavailable");
        SetNotifyText(_primaryIcon, text);
        SetNotifyText(_secondaryIcon, $"Codex 7d {s.RemainingPercent:0}% -> {s.ResetsAt.LocalDateTime:MM-dd HH:mm}");
    }

    private void MaybeNotifyLowUsage(UsageSnapshot snapshot)
    {
        if (!_settings.EnableLowUsageNotifications) return;
        var threshold = _settings.NotificationThreshold;
        if (snapshot.HasFiveHour && snapshot.Primary.RemainingPercent <= threshold && _notifiedPrimaryReset != snapshot.Primary.ResetsAt)
        {
            _notifiedPrimaryReset = snapshot.Primary.ResetsAt;
            _primaryIcon.ShowBalloonTip(4000, "Codex 5h 用量偏低", $"剩余 {snapshot.Primary.RemainingPercent:0}%，重置时间 {snapshot.Primary.ResetsAt.LocalDateTime:MM-dd HH:mm}", ToolTipIcon.Warning);
        }
        if (snapshot.Secondary.RemainingPercent <= threshold && _notifiedSecondaryReset != snapshot.Secondary.ResetsAt)
        {
            _notifiedSecondaryReset = snapshot.Secondary.ResetsAt;
            _primaryIcon.ShowBalloonTip(4000, "Codex 7d 用量偏低", $"剩余 {snapshot.Secondary.RemainingPercent:0}%，重置时间 {snapshot.Secondary.ResetsAt.LocalDateTime:MM-dd HH:mm}", ToolTipIcon.Warning);
        }
    }

    private void ShowUnavailable(string reason)
    {
        LogDiagnostic($"unavailable: {reason}");
        _primaryItem.Text = "5h: --";
        _secondaryItem.Text = "7d: --";
        _updatedItem.Text = reason;
        SetNotifyText(_primaryIcon, "Codex usage: no local data");
        SetNotifyText(_secondaryIcon, "Codex 7d: no local data");
        _secondaryIcon.Visible = false;
        ReplaceIcon(_primaryIcon, TrayIconFactory.CreateRing(0, unknown: true, _settings));
    }

    private static void SetNotifyText(NotifyIcon notifyIcon, string text)
    {
        notifyIcon.Text = text.Length <= 63 ? text : text[..60] + "...";
    }

    private static string FormatWindow(string name, UsageWindow window)
    {
        var reset = window.ResetsAt.LocalDateTime;
        return $"{name}: {window.RemainingPercent:0}% remaining - resets {reset:MM-dd HH:mm}";
    }

    private static void OpenSessionsFolder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (Directory.Exists(path)) OpenExplorer(path);
    }

    private void OpenConfigFile()
    {
        _settings.Save();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{TrayDisplaySettings.SettingsPath}\"") { UseShellExecute = true });
    }

    private static void OpenDiagnosticsLog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticsPath)!);
        if (!File.Exists(DiagnosticsPath)) File.WriteAllText(DiagnosticsPath, string.Empty);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{DiagnosticsPath}\"") { UseShellExecute = true });
    }

    private void OpenAssetsFolder()
    {
        Directory.CreateDirectory(_settings.EffectiveAssetsDirectory);
        foreach (var name in new[] { "5h", "7d", "both" })
            Directory.CreateDirectory(Path.Combine(_settings.EffectiveAssetsDirectory, name));
        OpenExplorer(_settings.EffectiveAssetsDirectory);
    }

    private static void OpenExplorer(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        return key?.GetValue(StartupName) is string value && value.Contains("CodexUsageTray.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetStartupEnabled()
    {
        var target = Environment.ProcessPath ?? Application.ExecutablePath;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                       ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.SetValue(StartupName, $"\"{target}\"");
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _refreshDebounceTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _sessionWatcher?.Dispose();
        _primaryIcon.Visible = false;
        _secondaryIcon.Visible = false;
        _primaryIcon.Dispose();
        _secondaryIcon.Dispose();
        LogDiagnostic("exited");
        base.ExitThreadCore();
    }

    private static void LogDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticsPath)!);
            File.AppendAllText(DiagnosticsPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break tray updates.
        }
    }
}

internal sealed class TrayDisplaySettings
{
    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageTray",
        "settings.json");

    public IconDisplayMode IconMode { get; set; } = IconDisplayMode.DualNumbers;
    public NumericContent NumericContent { get; set; } = NumericContent.Both;
    public TooltipMode TooltipMode { get; set; } = TooltipMode.Detailed;
    public bool EnableLowUsageNotifications { get; set; } = true;
    public double LowThreshold { get; set; } = 10;
    public double MediumThreshold { get; set; } = 30;
    public double NotificationThreshold { get; set; } = 20;
    public string FontName { get; set; } = "Arial";
    public string LowColor { get; set; } = "#AA0000";
    public string MediumColor { get; set; } = "#B45C00";
    public string GoodColor { get; set; } = "#00692D";
    public string UnknownColor { get; set; } = "#696969";
    public string? AssetsDirectory { get; set; }

    [JsonIgnore]
    public string EffectiveAssetsDirectory => string.IsNullOrWhiteSpace(AssetsDirectory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray", "assets")
        : Environment.ExpandEnvironmentVariables(AssetsDirectory);

    public static TrayDisplaySettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var created = new TrayDisplaySettings();
                created.Save();
                return created;
            }
            var settings = JsonSerializer.Deserialize<TrayDisplaySettings>(File.ReadAllText(SettingsPath)) ?? new TrayDisplaySettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception)
        {
            return new TrayDisplaySettings();
        }
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void CopyFrom(TrayDisplaySettings other)
    {
        IconMode = other.IconMode;
        NumericContent = other.NumericContent;
        TooltipMode = other.TooltipMode;
        EnableLowUsageNotifications = other.EnableLowUsageNotifications;
        LowThreshold = other.LowThreshold;
        MediumThreshold = other.MediumThreshold;
        NotificationThreshold = other.NotificationThreshold;
        FontName = other.FontName;
        LowColor = other.LowColor;
        MediumColor = other.MediumColor;
        GoodColor = other.GoodColor;
        UnknownColor = other.UnknownColor;
        AssetsDirectory = other.AssetsDirectory;
    }

    private void Normalize()
    {
        if (!Enum.IsDefined(IconMode)) IconMode = IconDisplayMode.DualNumbers;
        if (!Enum.IsDefined(NumericContent)) NumericContent = NumericContent.Both;
        if (!Enum.IsDefined(TooltipMode)) TooltipMode = TooltipMode.Detailed;
        LowThreshold = Math.Clamp(LowThreshold, 0, 100);
        MediumThreshold = Math.Clamp(MediumThreshold, LowThreshold, 100);
        NotificationThreshold = Math.Clamp(NotificationThreshold, 0, 100);
        if (string.IsNullOrWhiteSpace(FontName)) FontName = "Arial";
    }
}

internal sealed class UsageLogReader
{
    private const int MaxFilesToInspect = 12;
    private const int TailBytes = 1_048_576;
    private readonly string _sessionsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
    public string SessionsPath => _sessionsPath;

    public UsageSnapshot? ReadLatest()
    {
        if (!Directory.Exists(_sessionsPath)) return null;

        UsageSnapshot? latest = null;
        foreach (var file in Directory.EnumerateFiles(_sessionsPath, "*.jsonl", SearchOption.AllDirectories)
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Take(MaxFilesToInspect))
        {
            var snapshot = FindLatestSnapshot(file.FullName);
            if (snapshot is not null && (latest is null || snapshot.Value.RecordedAt > latest.Value.RecordedAt))
                latest = snapshot;
        }
        return latest;
    }

    private static UsageSnapshot? FindLatestSnapshot(string path)
    {
        foreach (var line in ReadTailLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "event_msg") continue;
                if (!root.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("rate_limits", out var limits)) continue;
                if (!limits.TryGetProperty("primary", out var primary) || primary.ValueKind != JsonValueKind.Object) continue;
                if (!TryReadWindow(primary, out var primaryWindow)) continue;
                var recordedAt = root.TryGetProperty("timestamp", out var timestamp) && DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed : DateTimeOffset.UtcNow;
                if (limits.TryGetProperty("secondary", out var secondary)
                    && secondary.ValueKind == JsonValueKind.Object
                    && TryReadWindow(secondary, out var secondaryWindow))
                    return new UsageSnapshot(primaryWindow, secondaryWindow, recordedAt, HasFiveHour: true);

                // Newer Codex builds can emit only `primary`; its reset matches the weekly
                // allowance shown by the official Usage page. Keep 5h explicitly unavailable.
                return new UsageSnapshot(primaryWindow, primaryWindow, recordedAt, HasFiveHour: false);
            }
            catch (JsonException)
            {
                // A session can be appended while it is read; skip incomplete JSON lines.
            }
            catch (InvalidOperationException)
            {
                // Codex may emit transient/null rate_limit entries; skip malformed snapshots.
            }
        }
        return null;
    }

    private static bool TryReadWindow(JsonElement element, out UsageWindow window)
    {
        window = default;
        if (!element.TryGetProperty("used_percent", out var percent) || !element.TryGetProperty("resets_at", out var resetsAt)) return false;
        if (!percent.TryGetDouble(out var usedPercent) || !resetsAt.TryGetInt64(out var unixSeconds)) return false;
        window = new UsageWindow(Math.Clamp(usedPercent, 0, 100), DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        return true;
    }

    private static IEnumerable<string> ReadTailLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var byteCount = (int)Math.Min(stream.Length, TailBytes);
        var buffer = new byte[byteCount];
        stream.Seek(-byteCount, SeekOrigin.End);
        _ = stream.Read(buffer, 0, byteCount);
        var text = System.Text.Encoding.UTF8.GetString(buffer);
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse();
    }
}

internal readonly record struct UsageWindow(double UsedPercent, DateTimeOffset ResetsAt)
{
    public double RemainingPercent => 100 - UsedPercent;
}

internal readonly record struct UsageSnapshot(UsageWindow Primary, UsageWindow Secondary, DateTimeOffset RecordedAt, bool HasFiveHour = true);

internal static class TrayIconFactory
{
    public static Icon CreateRing(double remainingPercent, bool unknown, TrayDisplaySettings settings)
    {
        var color = GetColor(remainingPercent, unknown, settings);
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var ring = new Pen(Color.FromArgb(70, color), 5);
            using var progress = new Pen(color, 5) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            graphics.DrawEllipse(ring, 4, 4, 24, 24);
            if (!unknown && remainingPercent > 0) graphics.DrawArc(progress, 4, 4, 24, 24, -90, (float)(remainingPercent * 3.6));
            if (unknown)
            {
                using var font = new Font(FontFamily.GenericSansSerif, 16, FontStyle.Bold, GraphicsUnit.Pixel);
                graphics.DrawString("?", font, Brushes.White, 10, 8);
            }
        }
        return ToIcon(bitmap);
    }

    public static Icon CreateBattery(double remainingPercent, bool unknown, TrayDisplaySettings settings)
    {
        var color = GetColor(remainingPercent, unknown, settings);
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var outline = new Pen(color, 3);
            using var outlinePath = CreateRoundedRectanglePath(new RectangleF(3, 8, 23, 16), 4);
            graphics.DrawPath(outline, outlinePath);
            using var knob = new SolidBrush(Color.FromArgb(180, color));
            graphics.FillRectangle(knob, 27, 13, 3, 6);
            if (unknown)
            {
                using var font = new Font(FontFamily.GenericSansSerif, 14, FontStyle.Bold, GraphicsUnit.Pixel);
                graphics.DrawString("?", font, Brushes.White, 11, 8);
            }
            else
            {
                var width = (int)Math.Round(Math.Clamp(remainingPercent, 0, 100) / 100.0 * 17);
                using var fill = new SolidBrush(color);
                graphics.FillRectangle(fill, 6, 11, width, 10);
            }
        }
        return ToIcon(bitmap);
    }

    public static Icon CreateNumbers(UsageSnapshot snapshot, NumericContent content, TrayDisplaySettings settings)
    {
        if (!snapshot.HasFiveHour && content == NumericContent.FiveHour)
            return CreateRing(0, unknown: true, settings);

        var primary = snapshot.Primary.RemainingPercent;
        var secondary = snapshot.Secondary.RemainingPercent;
        using var bitmap = content switch
        {
            NumericContent.FiveHour => CreateTightNumberBitmap(primary, 36f, settings),
            NumericContent.SevenDay => CreateTightNumberBitmap(secondary, 36f, settings),
            _ => snapshot.HasFiveHour
                ? CreateStackedTightNumberBitmap(primary, secondary, settings)
                : CreateTightNumberBitmap(secondary, 36f, settings),
        };
        return ToIcon(bitmap);
    }

    public static Icon CreateSingleNumber(double remainingPercent, TrayDisplaySettings settings, string bucket = "single")
    {
        using var bitmap = TryLoadCustomNumber(bucket, remainingPercent, settings)
                           ?? TryLoadCustomNumber("single", remainingPercent, settings)
                           ?? CreateTightNumberBitmap(remainingPercent, 36f, settings);
        return ToIcon(bitmap);
    }

    public static Icon CreateCustomOrFallback(UsageSnapshot snapshot, TrayDisplaySettings settings)
    {
        if (!snapshot.HasFiveHour && settings.NumericContent == NumericContent.FiveHour)
            return CreateRing(0, unknown: true, settings);

        var primary = snapshot.Primary.RemainingPercent;
        var secondary = snapshot.Secondary.RemainingPercent;
        using var bitmap = settings.NumericContent switch
        {
            NumericContent.FiveHour => TryLoadCustomNumber("5h", primary, settings) ?? TryLoadCustomNumber("single", primary, settings) ?? CreateTightNumberBitmap(primary, 36f, settings),
            NumericContent.SevenDay => TryLoadCustomNumber("7d", secondary, settings) ?? TryLoadCustomNumber("single", secondary, settings) ?? CreateTightNumberBitmap(secondary, 36f, settings),
            _ => snapshot.HasFiveHour
                ? TryLoadCustomNumber("both", Math.Min(primary, secondary), settings) ?? CreateStackedTightNumberBitmap(primary, secondary, settings)
                : TryLoadCustomNumber("7d", secondary, settings) ?? TryLoadCustomNumber("single", secondary, settings) ?? CreateTightNumberBitmap(secondary, 36f, settings),
        };
        return ToIcon(bitmap);
    }

    public static (Icon Primary, Icon Secondary) CreateDualNumbers(UsageSnapshot snapshot, TrayDisplaySettings settings)
    {
        var primary = (int)Math.Round(Math.Clamp(snapshot.Primary.RemainingPercent, 0, 100));
        var secondary = (int)Math.Round(Math.Clamp(snapshot.Secondary.RemainingPercent, 0, 100));
        var primaryText = primary.ToString(CultureInfo.InvariantCulture);
        var secondaryText = secondary.ToString(CultureInfo.InvariantCulture);
        var area = new RectangleF(-1, -2, 50, 52);
        var fontSize = FindSharedSingleFontSize(primaryText, secondaryText, area, settings);

        using var primaryBitmap = CreateFixedNumberBitmap(primaryText, GetColor(primary, unknown: false, settings), fontSize, area, settings);
        using var secondaryBitmap = CreateFixedNumberBitmap(secondaryText, GetColor(secondary, unknown: false, settings), fontSize, area, settings);
        return (ToIcon(primaryBitmap), ToIcon(secondaryBitmap));
    }

    private static Bitmap CreateNumbersBitmap(UsageSnapshot snapshot, TrayDisplaySettings settings) =>
        settings.NumericContent switch
        {
            NumericContent.FiveHour when snapshot.HasFiveHour => CreateTightNumberBitmap(snapshot.Primary.RemainingPercent, 36f, settings),
            NumericContent.FiveHour => CreateTightNumberBitmap(snapshot.Secondary.RemainingPercent, 36f, settings),
            NumericContent.SevenDay => CreateTightNumberBitmap(snapshot.Secondary.RemainingPercent, 36f, settings),
            _ => snapshot.HasFiveHour
                ? CreateStackedTightNumberBitmap(snapshot.Primary.RemainingPercent, snapshot.Secondary.RemainingPercent, settings)
                : CreateTightNumberBitmap(snapshot.Secondary.RemainingPercent, 36f, settings),
        };

    private static Bitmap? TryLoadCustomNumber(string bucket, double remainingPercent, TrayDisplaySettings settings)
    {
        var percent = (int)Math.Round(Math.Clamp(remainingPercent, 0, 100));
        foreach (var path in new[]
                 {
                     Path.Combine(settings.EffectiveAssetsDirectory, bucket, $"{percent}.png"),
                     Path.Combine(settings.EffectiveAssetsDirectory, $"{percent}.png"),
                 })
        {
            if (!File.Exists(path)) continue;
            using var image = Image.FromFile(path);
            return new Bitmap(image, new Size(32, 32));
        }
        return null;
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRectanglePath(RectangleF rectangle, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Bitmap CreateTightNumberBitmap(double remainingPercent, float fontSize, TrayDisplaySettings settings)
    {
        var percent = (int)Math.Round(Math.Clamp(remainingPercent, 0, 100));
        var text = percent.ToString(CultureInfo.InvariantCulture);
        var color = GetColor(percent, unknown: false, settings);
        return CreateTightTextBitmap(text, color, fontSize, settings);
    }

    private static Bitmap CreateFixedNumberBitmap(string text, Color color, float fontSize, RectangleF area, TrayDisplaySettings settings)
    {
        const int side = 48;
        var bitmap = new Bitmap(side, side);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        DrawTextPathInto(graphics, text, color, fontSize, area, settings);
        return bitmap;
    }

    private static Bitmap CreateStackedTightNumberBitmap(double primaryPercent, double secondaryPercent, TrayDisplaySettings settings)
    {
        const int width = 48;
        const int height = 48;
        var primary = (int)Math.Round(Math.Clamp(primaryPercent, 0, 100));
        var secondary = (int)Math.Round(Math.Clamp(secondaryPercent, 0, 100));
        var primaryText = primary.ToString(CultureInfo.InvariantCulture);
        var secondaryText = secondary.ToString(CultureInfo.InvariantCulture);
        var topArea = new RectangleF(-1, -3, width + 2, 24);
        var bottomArea = new RectangleF(-1, 27, width + 2, 24);
        var fontSize = FindSharedStackedFontSize(primaryText, secondaryText, topArea, settings);
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.Clear(Color.Transparent);
        DrawTextPathInto(graphics, primaryText, GetColor(primary, unknown: false, settings), fontSize, topArea, settings);
        DrawTextPathInto(graphics, secondaryText, GetColor(secondary, unknown: false, settings), fontSize, bottomArea, settings);
        return bitmap;
    }

    private static float FindSharedStackedFontSize(string primaryText, string secondaryText, RectangleF area, TrayDisplaySettings settings)
    {
        for (var size = 28f; size >= 12f; size -= 0.5f)
        {
            using var primaryPath = CreateTextPath(primaryText, size, settings);
            using var secondaryPath = CreateTextPath(secondaryText, size, settings);
            var primaryBounds = primaryPath.GetBounds();
            var secondaryBounds = secondaryPath.GetBounds();
            if (primaryBounds.Width <= area.Width && secondaryBounds.Width <= area.Width
                && primaryBounds.Height <= area.Height && secondaryBounds.Height <= area.Height)
                return size;
        }
        return 12f;
    }

    private static float FindSharedSingleFontSize(string primaryText, string secondaryText, RectangleF area, TrayDisplaySettings settings)
    {
        for (var size = 40f; size >= 12f; size -= 0.5f)
        {
            using var primaryPath = CreateTextPath(primaryText, size, settings);
            using var secondaryPath = CreateTextPath(secondaryText, size, settings);
            var primaryBounds = primaryPath.GetBounds();
            var secondaryBounds = secondaryPath.GetBounds();
            if (primaryBounds.Width <= area.Width && secondaryBounds.Width <= area.Width
                && primaryBounds.Height <= area.Height && secondaryBounds.Height <= area.Height)
                return size;
        }
        return 12f;
    }

    private static void DrawTextPathInto(Graphics graphics, string text, Color color, float fontSize, RectangleF area, TrayDisplaySettings settings)
    {
        using var path = CreateTextPath(text, fontSize, settings);
        var bounds = path.GetBounds();
        using var matrix = new System.Drawing.Drawing2D.Matrix();
        matrix.Translate(
            area.X + ((area.Width - bounds.Width) / 2f) - bounds.X,
            area.Y + ((area.Height - bounds.Height) / 2f) - bounds.Y);
        path.Transform(matrix);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    private static Bitmap CreateTightTextBitmap(string text, Color color, float fontSize, TrayDisplaySettings settings)
    {
        using var path = CreateTextPath(text, fontSize, settings);
        var bounds = path.GetBounds();
        var side = Math.Max(1, (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height)) + 2);
        var bitmap = new Bitmap(side, side);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        using var matrix = new System.Drawing.Drawing2D.Matrix();
        matrix.Translate(((side - bounds.Width) / 2f) - bounds.X, ((side - bounds.Height) / 2f) - bounds.Y);
        path.Transform(matrix);
        graphics.FillPath(brush, path);
        return bitmap;
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateTextPath(string text, float fontSize, TrayDisplaySettings settings)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        using var family = ResolveFontFamily(settings.FontName);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoClip;
        path.AddString(text, family, (int)FontStyle.Bold, fontSize, Point.Empty, format);
        return path;
    }

    private static FontFamily ResolveFontFamily(string fontName)
    {
        try { return new FontFamily(fontName); }
        catch { return new FontFamily("Arial"); }
    }

    private static Color GetColor(double remainingPercent, bool unknown, TrayDisplaySettings settings)
    {
        if (unknown) return ParseColor(settings.UnknownColor, Color.DimGray);
        if (remainingPercent <= settings.LowThreshold) return ParseColor(settings.LowColor, Color.FromArgb(170, 0, 0));
        if (remainingPercent <= settings.MediumThreshold) return ParseColor(settings.MediumColor, Color.FromArgb(180, 92, 0));
        return ParseColor(settings.GoodColor, Color.FromArgb(0, 105, 45));
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try { return ColorTranslator.FromHtml(value); }
        catch { return fallback; }
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var temporaryIcon = Icon.FromHandle(handle);
            return (Icon)temporaryIcon.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
