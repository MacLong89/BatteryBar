using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using Microsoft.Win32;
using static BatteryBar.Native;

namespace BatteryBar;

/// <summary>
/// A borderless, per-pixel-alpha window that sits on top of the taskbar just left of the
/// notification area and shows "icon  NN%" for each device.
/// </summary>
sealed class OverlayForm : Form
{
    const string RegKey = @"Software\BatteryBar";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const int WM_DEVICECHANGE = 0x0219;

    List<BatteryInfo> _devices = new();
    bool _polling, _hasData, _shown;
    double _offsetDip;                 // user drag offset, in 96-dpi pixels (negative = further left)
    bool _dragging; int _dragStartX; double _dragStartOffset;
    string _lastKey = "";

    readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 30_000 };
    readonly System.Windows.Forms.Timer _layoutTimer = new() { Interval = 500 };
    readonly System.Windows.Forms.Timer _deviceChangeTimer = new() { Interval = 2500 };
    readonly ToolTip _tip = new() { InitialDelay = 300, ShowAlways = true };
    readonly ContextMenuStrip _menu = new();
    readonly WinEventProc _fgProc;
    IntPtr _fgHook;
    readonly string _iconFont;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(1, 1);
        Text = "BatteryBar";

        using (var fonts = new InstalledFontCollection())
            _iconFont = fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        _offsetDip = LoadOffset();
        if (!Demo) LoadLastKnown();

        _pollTimer.Tick += (_, _) => Poll();
        _layoutTimer.Tick += (_, _) => Render();
        _deviceChangeTimer.Tick += (_, _) => { _deviceChangeTimer.Stop(); Poll(); };
        _fgProc = (_, _, _, _, _, _, _) => Render(force: false, reassertTopmost: true);

        _menu.Opening += (_, _) => BuildMenu();
        SystemEvents.UserPreferenceChanged += (_, _) => this.Ui(() => Render(force: true));
        SystemEvents.DisplaySettingsChanged += (_, _) => this.Ui(() => Render(force: true));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = WS_POPUP;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _pollTimer.Start();
        _layoutTimer.Start();
        Render(force: true);
        Poll();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook);
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE) { _deviceChangeTimer.Stop(); _deviceChangeTimer.Start(); }
        base.WndProc(ref m);
    }

    // ------------------------------------------------------------------ polling

    // Wireless mice/keyboards go to sleep and stop answering; keep showing their last reading for a while.
    readonly Dictionary<string, (BatteryInfo info, DateTime at)> _lastKnown = new();
    static readonly TimeSpan KeepAsleepFor = TimeSpan.FromHours(12);
    public static bool Demo;
    static readonly string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryBar", "last-known.txt");

    // One device per line: kind <TAB> percent <TAB> UTC ticks <TAB> name
    void LoadLastKnown()
    {
        try
        {
            foreach (var line in File.ReadAllLines(StatePath))
            {
                var p = line.Split(new[] { '\t' }, 4);
                if (p.Length < 4) continue;
                var at = new DateTime(long.Parse(p[2]), DateTimeKind.Utc).ToLocalTime();
                _lastKnown[p[3]] = (new BatteryInfo(p[3], (DeviceKind)int.Parse(p[0]), int.Parse(p[1]), false, "saved"), at);
            }
        }
        catch { }
    }

    void SaveLastKnown()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllLines(StatePath, _lastKnown.Values.Select(v =>
                $"{(int)v.info.Kind}\t{v.info.Percent}\t{v.at.ToUniversalTime().Ticks}\t{v.info.Name.Replace('\t', ' ')}"));
        }
        catch { }
    }

    void Poll()
    {
        if (_polling) return;
        _polling = true;
        Task.Run(() =>
            {
                if (Demo) return new List<BatteryInfo> {
                    new("PRO X SUPERLIGHT 2", DeviceKind.Mouse, 93, false, "demo"),
                    new("G915 TKL", DeviceKind.Keyboard, 12, false, "demo"),
                    new("Arctis Nova 7", DeviceKind.Headset, 30, true, "demo") };
                try { return Batteries.ReadAll(); } catch { return new List<BatteryInfo>(); }
            })
            .ContinueWith(t =>
            {
                if (IsDisposed) return;
                this.Ui(() =>
                {
                    _polling = false;
                    var now = DateTime.Now;
                    var fresh = t.Result;
                    foreach (var d in fresh) _lastKnown[d.Name] = (d, now);
                    if (fresh.Count > 0 && !Demo) SaveLastKnown();
                    var sleeping = _lastKnown.Values
                        .Where(v => !fresh.Any(f => f.Name == v.info.Name) && now - v.at < KeepAsleepFor)
                        .Select(v => v.info with { LastSeen = v.at, Charging = false });
                    _devices = fresh.Concat(sleeping)
                        .OrderBy(b => b.Kind switch { DeviceKind.Mouse => 0, DeviceKind.Keyboard => 1, DeviceKind.Headset => 2, DeviceKind.Gamepad => 3, _ => 4 })
                        .ThenBy(b => b.Name).ToList();
                    _hasData = true;
                    _tip.SetToolTip(this, TooltipText());
                    Render(force: true);
                });
            });
    }

    string TooltipText()
    {
        if (_devices.Count == 0) return "BatteryBar — no devices reporting battery.\nRight-click for options.";
        var sb = new StringBuilder();
        foreach (var d in _devices) sb.AppendLine($"{d.Name}: {d.Percent}%{(d.Charging ? " (charging)" : "")}{Asleep(d)}");
        return sb.ToString().TrimEnd();
    }

    static string Asleep(BatteryInfo d)
    {
        if (d.LastSeen is not DateTime t) return "";
        var m = (int)(DateTime.Now - t).TotalMinutes;
        return $" — asleep, last read {(m < 1 ? "just now" : m < 60 ? $"{m} min ago" : $"{m / 60} h ago")}";
    }

    // ------------------------------------------------------------------ rendering / placement

    void Render(bool force = false, bool reassertTopmost = true)
    {
        if (!IsHandleCreated || IsDisposed) return;

        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var tb) || !IsWindowVisible(tray)) { SetShown(false); return; }

        // Hide when the taskbar is auto-hidden or a fullscreen app/game covers its monitor.
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(tray, 2), ref mi);
        var mon = mi.rcMonitor;
        bool taskbarOnScreen = tb.Top >= mon.Top && tb.Bottom <= mon.Bottom && tb.Height > 8 && tb.Width > tb.Height;
        if (!taskbarOnScreen || ForegroundIsFullscreen(mon)) { SetShown(false); return; }

        float s = Math.Max(1, GetDpiForWindow(tray)) / 96f;
        bool light = IsLightTheme();

        // Anchor to the left edge of the notification area (the "^" chevron / tray icons).
        int anchor = tb.Right - (int)(260 * s);
        var notify = FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out var nr) && nr.Width > 0 && nr.Left > tb.Left + tb.Width / 2 && nr.Left < tb.Right)
            anchor = nr.Left;

        string key = $"{s}|{light}|{tb.Left},{tb.Top},{tb.Right},{tb.Bottom}|{anchor}|{_offsetDip}|{string.Join(";", _devices)}|{_hasData}";
        if (force || key != _lastKey)
        {
            _lastKey = key;
            using var bmp = Draw(s, light, tb.Height);
            int x = anchor - bmp.Width - (int)(6 * s) + (int)Math.Round(_offsetDip * s);
            x = Math.Max(tb.Left, Math.Min(x, tb.Right - bmp.Width));
            Present(bmp, x, tb.Top);
        }
        SetShown(true);
        if (reassertTopmost)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void SetShown(bool show)
    {
        if (show == _shown) return;
        _shown = show;
        ShowWindow(Handle, show ? SW_SHOWNOACTIVATE : SW_HIDE);
    }

    static bool ForegroundIsFullscreen(RECT mon)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var cls = new StringBuilder(64);
        GetClassNameW(fg, cls, 64);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(fg, out var r)) return false;
        return r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom;
    }

    static bool IsLightTheme()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    static string Glyph(DeviceKind k) => k switch
    {
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Headset => "",
        DeviceKind.Gamepad => "",
        _ => "",
    };

    Bitmap Draw(float s, bool light, int height)
    {
        var fg = light ? Color.FromArgb(255, 26, 26, 26) : Color.FromArgb(255, 255, 255, 255);
        var dim = light ? Color.FromArgb(150, 26, 26, 26) : Color.FromArgb(150, 255, 255, 255);
        var low = light ? Color.FromArgb(255, 196, 43, 28) : Color.FromArgb(255, 255, 110, 90);

        var items = new List<(string glyph, string text, Color color, bool charging)>();
        if (!_hasData) items.Add(("", "…", dim, false));
        else if (_devices.Count == 0) items.Add(("", "--", dim, false));
        else foreach (var d in _devices)
                items.Add((Glyph(d.Kind), $"{d.Percent}%", d.LastSeen != null ? dim : d.Percent <= 15 && !d.Charging ? low : fg, d.Charging));

        using var iconFont = new Font(_iconFont, 15f * s, GraphicsUnit.Pixel);
        using var boltFont = new Font(_iconFont, 10f * s, GraphicsUnit.Pixel);
        using var textFont = new Font("Segoe UI", 13.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap };

        float pad = 8 * s, iconGap = 4 * s, itemGap = 14 * s;
        var widths = new List<(float icon, float text, float bolt)>();
        float total = pad * 2;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
        {
            pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            foreach (var it in items)
            {
                float iw = pg.MeasureString(it.glyph, iconFont, PointF.Empty, fmt).Width;
                float tw = pg.MeasureString(it.text, textFont, PointF.Empty, fmt).Width;
                float bw = it.charging ? pg.MeasureString("", boltFont, PointF.Empty, fmt).Width + 1 * s : 0;
                widths.Add((iw, tw, bw));
                total += iw + iconGap + tw + bw;
            }
            total += itemGap * (items.Count - 1);
        }

        var bmp = new Bitmap((int)Math.Ceiling(total), height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        // Alpha 1 everywhere so the whole strip catches the mouse (for drag/right-click/tooltip).
        g.Clear(Color.FromArgb(1, 0, 0, 0));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float x = pad, cy = height / 2f;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i]; var w = widths[i];
            using var brush = new SolidBrush(it.color);
            g.DrawString(it.glyph, iconFont, brush, new RectangleF(x, 0, w.icon + 2, height), fmt);
            x += w.icon + iconGap;
            g.DrawString(it.text, textFont, brush, new RectangleF(x, -0.5f * s, w.text + 2, height), fmt);
            x += w.text;
            if (it.charging)
            {
                using var bolt = new SolidBrush(light ? Color.FromArgb(255, 16, 124, 16) : Color.FromArgb(255, 108, 203, 95));
                g.DrawString("", boltFont, bolt, new RectangleF(x + 1 * s, 0, w.bolt + 2, height), fmt);
                x += w.bolt;
            }
            x += itemGap;
        }
        return bmp;
    }

    void Present(Bitmap bmp, int x, int y)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hb = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hb);
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var pos = new POINT { X = x, Y = y };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (hb != IntPtr.Zero) DeleteObject(hb);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ------------------------------------------------------------------ mouse: drag to move, right-click menu

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _dragging = true; _dragStartX = Cursor.Position.X; _dragStartOffset = _offsetDip; Capture = true; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var tray = FindWindowW("Shell_TrayWnd", null);
        float s = Math.Max(1, GetDpiForWindow(tray)) / 96f;
        _offsetDip = _dragStartOffset + (Cursor.Position.X - _dragStartX) / s;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; Capture = false; SaveOffset(); }
        if (e.Button == MouseButtons.Right) _menu.Show(Cursor.Position);
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        if (_devices.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("No devices reporting battery") { Enabled = false });
        foreach (var d in _devices)
            _menu.Items.Add(new ToolStripMenuItem($"{d.Name} — {d.Percent}%{(d.Charging ? " ⚡" : "")}{Asleep(d)}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Refresh now", null, (_, _) => Poll());
        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = IsStartupEnabled() });
        _menu.Items.Add("Reset position", null, (_, _) => { _offsetDip = 0; SaveOffset(); Render(force: true); });
        _menu.Items.Add(new ToolStripSeparator());
        var version = typeof(OverlayForm).Assembly.GetName().Version;
        _menu.Items.Add(new ToolStripMenuItem($"BatteryBar {version.Major}.{version.Minor}.{version.Build}") { Enabled = false });
        if (ProjectUrl.Length > 0) _menu.Items.Add("Website / report a device…", null, (_, _) => OpenUrl(ProjectUrl));
        if (TipUrl.Length > 0) _menu.Items.Add("Support development ♥", null, (_, _) => OpenUrl(TipUrl));
        _menu.Items.Add("Exit", null, (_, _) => Close());
    }

    // Fill these in before publishing; empty strings hide the menu items.
    const string ProjectUrl = "https://github.com/MacLong89/BatteryBar";
    const string TipUrl = "";       // e.g. https://ko-fi.com/<you>

    static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------------ settings

    static double LoadOffset()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RegKey);
        return k?.GetValue("OffsetX") is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    void SaveOffset()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RegKey);
        k.SetValue("OffsetX", _offsetDip.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    static bool IsStartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("BatteryBar") != null;
    }

    static void ToggleStartup()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (k.GetValue("BatteryBar") != null) k.DeleteValue("BatteryBar");
        else k.SetValue("BatteryBar", $"\"{Application.ExecutablePath}\"");
    }
}
