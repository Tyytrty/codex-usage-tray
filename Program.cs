using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

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

internal sealed class UsageTrayContext : ApplicationContext
{
    private const int RefreshIntervalMs = 30_000;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _primaryItem = new("5 小时窗口：正在读取…") { Enabled = false };
    private readonly ToolStripMenuItem _secondaryItem = new("7 天窗口：正在读取…") { Enabled = false };
    private readonly ToolStripMenuItem _updatedItem = new("最近更新：—") { Enabled = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = RefreshIntervalMs };
    private readonly UsageLogReader _reader = new();
    private UsageSnapshot? _lastSnapshot;

    public UsageTrayContext()
    {
        var menu = new ContextMenuStrip();
        var menuFont = SystemFonts.MenuFont ?? SystemFonts.DefaultFont;
        menu.Items.Add(new ToolStripMenuItem("Codex 用量") { Enabled = false, Font = new Font(menuFont, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_primaryItem);
        menu.Items.Add(_secondaryItem);
        menu.Items.Add(_updatedItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("立即刷新", null, (_, _) => RefreshUsage()));
        menu.Items.Add(new ToolStripMenuItem("打开 Codex 会话目录", null, (_, _) => OpenSessionsFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitThread()));

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = TrayIconFactory.Create(0, unknown: true),
            Text = "Codex 用量：正在读取…",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => RefreshUsage();
        _timer.Tick += (_, _) => RefreshUsage();
        _timer.Start();
        RefreshUsage();
    }

    private void RefreshUsage()
    {
        try
        {
            var snapshot = _reader.ReadLatest();
            if (snapshot is null)
            {
                ShowUnavailable("未找到含用量信息的 Codex 会话日志");
                return;
            }

            var current = snapshot.Value;
            _lastSnapshot = current;
            var primary = current.Primary;
            var secondary = current.Secondary;
            _primaryItem.Text = FormatWindow("5 小时窗口", primary);
            _secondaryItem.Text = FormatWindow("7 天窗口", secondary);
            _updatedItem.Text = $"最近更新：{current.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";

            var tooltip = $"Codex 剩余：5小时 {primary.RemainingPercent:0}% / 7天 {secondary.RemainingPercent:0}%";
            _trayIcon.Text = tooltip.Length <= 63 ? tooltip : "Codex 用量：右键查看详情";
            _trayIcon.Icon = TrayIconFactory.Create(Math.Min(primary.RemainingPercent, secondary.RemainingPercent), unknown: false);
        }
        catch (Exception ex)
        {
            ShowUnavailable($"读取失败：{ex.Message}");
        }
    }

    private void ShowUnavailable(string reason)
    {
        _primaryItem.Text = "5 小时窗口：—";
        _secondaryItem.Text = "7 天窗口：—";
        _updatedItem.Text = reason;
        _trayIcon.Text = "Codex 用量：暂无本地数据";
        _trayIcon.Icon = TrayIconFactory.Create(0, unknown: true);
    }

    private static string FormatWindow(string name, UsageWindow window)
    {
        var reset = window.ResetsAt.LocalDateTime;
        return $"{name}：剩余 {window.RemainingPercent:0}% · {reset:MM-dd HH:mm} 重置";
    }

    private static void OpenSessionsFolder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class UsageLogReader
{
    private const int MaxFilesToInspect = 12;
    private const int TailBytes = 1_048_576;
    private readonly string _sessionsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    public UsageSnapshot? ReadLatest()
    {
        if (!Directory.Exists(_sessionsPath)) return null;

        foreach (var file in Directory.EnumerateFiles(_sessionsPath, "*.jsonl", SearchOption.AllDirectories)
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Take(MaxFilesToInspect))
        {
            var snapshot = FindLatestSnapshot(file.FullName);
            if (snapshot is not null) return snapshot;
        }
        return null;
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
                if (!limits.TryGetProperty("primary", out var primary) || !limits.TryGetProperty("secondary", out var secondary)) continue;
                if (!TryReadWindow(primary, out var primaryWindow) || !TryReadWindow(secondary, out var secondaryWindow)) continue;
                var recordedAt = root.TryGetProperty("timestamp", out var timestamp) && DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed : DateTimeOffset.UtcNow;
                return new UsageSnapshot(primaryWindow, secondaryWindow, recordedAt);
            }
            catch (JsonException)
            {
                // A session can be appended while it is read; skip incomplete JSON lines.
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
internal readonly record struct UsageSnapshot(UsageWindow Primary, UsageWindow Secondary, DateTimeOffset RecordedAt);

internal static class TrayIconFactory
{
    public static Icon Create(double remainingPercent, bool unknown)
    {
        var color = unknown ? Color.DimGray : remainingPercent <= 10 ? Color.Firebrick : remainingPercent <= 30 ? Color.DarkOrange : Color.SeaGreen;
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var ring = new Pen(Color.FromArgb(70, color), 5);
            using var progress = new Pen(color, 5) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            graphics.DrawEllipse(ring, 4, 4, 24, 24);
            if (!unknown && remainingPercent > 0) graphics.DrawArc(progress, 4, 4, 24, 24, -90, (float)(remainingPercent * 3.6));
            if (unknown) graphics.DrawString("?", new Font(SystemFonts.DefaultFont.FontFamily, 16, FontStyle.Bold), Brushes.White, 9, 5);
        }
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
