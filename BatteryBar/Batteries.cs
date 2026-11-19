using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static BatteryBar.Native;

namespace BatteryBar;

public enum DeviceKind { Mouse, Keyboard, Headset, Gamepad, Other }

public sealed record BatteryInfo(string Name, DeviceKind Kind, int Percent, bool Charging, string Source)
{
    /// <summary>When set, the device isn't answering right now (asleep/off) and this is its last reading.</summary>
    public DateTime? LastSeen { get; init; }
}

/// <summary>Collects battery levels from every source we know how to read.</summary>
public static class Batteries
{
    public static List<BatteryInfo> ReadAll(StringBuilder? log = null)
    {
        var all = new List<BatteryInfo>();
        // Order = priority when de-duplicating (vendor protocol is most accurate).
        Try(() => all.AddRange(Logitech.Read(log)), "Logitech", log);
        Try(() => all.AddRange(HyperX.Read(log)), "HyperX", log);
        Try(() => all.AddRange(WindowsBluetooth.Read(log)), "Bluetooth", log);
        Try(() => all.AddRange(GenericHid.Read(log)), "HID", log);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BatteryInfo>();
        foreach (var b in all)
            if (seen.Add(Normalize(b.Name))) result.Add(b);

        // Mice first, then keyboards, then the rest â€” matches what you usually care about.
        return result.OrderBy(b => b.Kind switch { DeviceKind.Mouse => 0, DeviceKind.Keyboard => 1, DeviceKind.Headset => 2, DeviceKind.Gamepad => 3, _ => 4 })
                     .ThenBy(b => b.Name).ToList();
    }

    static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());

    static void Try(Action a, string what, StringBuilder? log)
    {
        try { a(); } catch (Exception e) { log?.AppendLine($"[{what}] failed: {e.Message}"); }
    }

    public static DeviceKind GuessKind(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("mx anywhere") || n.Contains("trackball") || n.Contains("superlight") || n.Contains("viper") || n.Contains("deathadder")) return DeviceKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("keys") || n.Contains("kbd")) return DeviceKind.Keyboard;
        if (n.Contains("headset") || n.Contains("headphone") || n.Contains("buds") || n.Contains("airpods") || n.Contains("earbuds") || n.Contains("wh-") || n.Contains("wf-")) return DeviceKind.Headset;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("xbox")) return DeviceKind.Gamepad;
        return DeviceKind.Other;
    }

    // ------------------------------------------------------------------
    // Shared HID enumeration helpers
    // ------------------------------------------------------------------
    public sealed record HidPath(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage, HIDP_CAPS Caps);

    public static List<HidPath> EnumerateHid()
    {
        var list = new List<HidPath>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return list;
        try
        {
            for (int i = 0; ; i++)
            {
                var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifd)) break;
                SetupDiGetDeviceInterfaceDetailW(set, ref ifd, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                if (req <= 0) continue;
                var buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifd, buf, req, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buf + 4)!;
                    // Access 0 = query-only; works even for mouse/keyboard collections Windows locks.
                    using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(h, ref attr)) continue;
                    if (!HidD_GetPreparsedData(h, out var pp)) continue;
                    try
                    {
                        if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS) continue;
                        list.Add(new HidPath(path, attr.VendorID, attr.ProductID, caps.UsagePage, caps.Usage, caps));
                    }
                    finally { HidD_FreePreparsedData(pp); }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }
}

// ======================================================================
// 1) Windows-reported battery (Bluetooth Classic + Bluetooth LE devices)
// ======================================================================
static class WindowsBluetooth
{
    static DEVPROPKEY BatteryKey = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static DEVPROPKEY FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    static DEVPROPKEY DevName = new("b725f130-47ef-101a-a5f1-02608c9eebac", 10);
    static DEVPROPKEY ClassOfDevice = new("2bd67d8b-8beb-48d5-87e0-6cda3428040a", 10);

    public static IEnumerable<BatteryInfo> Read(StringBuilder? log)
    {
        var results = new List<BatteryInfo>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (set == INVALID_HANDLE_VALUE) return results;
        try
        {
            var buf = new byte[512];
            for (int i = 0; ; i++)
            {
                var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref dev)) break;
                if (!SetupDiGetDevicePropertyW(set, ref dev, ref BatteryKey, out var t, buf, buf.Length, out _, 0) || t != DEVPROP_TYPE_BYTE) continue;
                int pct = buf[0];
                if (pct > 100) continue;
                string name = GetString(set, ref dev, ref FriendlyName) ?? GetString(set, ref dev, ref DevName) ?? "Bluetooth device";

                var kind = DeviceKind.Other;
                if (SetupDiGetDevicePropertyW(set, ref dev, ref ClassOfDevice, out t, buf, buf.Length, out _, 0) && t == DEVPROP_TYPE_UINT32)
                    kind = FromClassOfDevice(BitConverter.ToUInt32(buf, 0));
                if (kind == DeviceKind.Other) kind = Batteries.GuessKind(name);

                log?.AppendLine($"[Bluetooth] {name}: {pct}% ({kind})");
                results.Add(new BatteryInfo(name, kind, pct, false, "Bluetooth"));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return results;
    }

    static DeviceKind FromClassOfDevice(uint cod)
    {
        uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F;
        if (major == 0x04) return DeviceKind.Headset;              // Audio/Video
        if (major == 0x05)                                           // Peripheral
        {
            if ((minor & 0x20) != 0) return DeviceKind.Mouse;       // pointing
            if ((minor & 0x10) != 0) return DeviceKind.Keyboard;
            if ((minor & 0x0F) is 1 or 2) return DeviceKind.Gamepad;
        }
        return DeviceKind.Other;
    }

    static string? GetString(IntPtr set, ref SP_DEVINFO_DATA dev, ref DEVPROPKEY key)
    {
        var buf = new byte[1024];
        if (!SetupDiGetDevicePropertyW(set, ref dev, ref key, out var t, buf, buf.Length, out int req, 0) || t != DEVPROP_TYPE_STRING) return null;
        var s = Encoding.Unicode.GetString(buf, 0, Math.Max(0, req - 2)).TrimEnd('\0').Trim();
        return s.Length > 0 ? s : null;
    }
}

// ======================================================================
// 2) Standard HID "Battery Strength" usage (USB dongles that follow the spec)
// ======================================================================
static class GenericHid
{
    const ushort UP_GENERIC_DEVICE = 0x06, U_BATTERY_STRENGTH = 0x20;

    public static IEnumerable<BatteryInfo> Read(StringBuilder? log)
    {
        var results = new List<BatteryInfo>();
        var all = Batteries.EnumerateHid();

        // Work out what each physical device is from its sibling collections.
        var kinds = new Dictionary<string, DeviceKind>();
        foreach (var h in all)
        {
            var key = $"{h.Vid:x4}:{h.Pid:x4}";
            if (h.UsagePage == 1 && h.Usage == 2) kinds[key] = DeviceKind.Mouse;
            else if (h.UsagePage == 1 && h.Usage == 6 && !kinds.ContainsKey(key)) kinds[key] = DeviceKind.Keyboard;
            else if (h.UsagePage == 1 && h.Usage is 4 or 5 && !kinds.ContainsKey(key)) kinds[key] = DeviceKind.Gamepad;
        }

        foreach (var h in all)
        {
            if (h.UsagePage >= 0xFF00) continue; // vendor channels are handled by their own readers
            var r = ReadOne(h);
            if (r is null) continue;
            var (pct, name) = r.Value;
            var kind = kinds.TryGetValue($"{h.Vid:x4}:{h.Pid:x4}", out var k) ? k : Batteries.GuessKind(name);
            log?.AppendLine($"[HID] {name} ({h.Vid:X4}:{h.Pid:X4}): {pct}%");
            results.Add(new BatteryInfo(name, kind, pct, false, "HID"));
        }
        return results;
    }

    static (int pct, string name)? ReadOne(Batteries.HidPath h)
    {
        using var q = CreateFileW(h.Path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (q.IsInvalid || !HidD_GetPreparsedData(q, out var pp)) return null;
        try
        {
            foreach (var type in new[] { HidP_Feature, HidP_Input })
            {
                ushort n = 8;
                var caps = new byte[HIDP_VALUE_CAPS_SIZE * n];
                if (HidP_GetSpecificValueCaps(type, UP_GENERIC_DEVICE, 0, U_BATTERY_STRENGTH, caps, ref n, pp) != HIDP_STATUS_SUCCESS || n == 0) continue;
                byte reportId = caps[2];
                int logMax = BitConverter.ToInt32(caps, 44);
                int len = type == HidP_Feature ? h.Caps.FeatureReportByteLength : h.Caps.InputReportByteLength;
                if (len == 0) continue;

                using var rw = CreateFileW(h.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (rw.IsInvalid) continue;
                var report = new byte[len];
                report[0] = reportId;
                // These calls can block on flaky devices; give up after 1.5s.
                var task = Task.Run(() => type == HidP_Feature ? HidD_GetFeature(rw, report, len) : HidD_GetInputReport(rw, report, len));
                if (!task.Wait(1500) || !task.Result) continue;
                if (HidP_GetUsageValue(type, UP_GENERIC_DEVICE, 0, U_BATTERY_STRENGTH, out uint v, pp, report, len) != HIDP_STATUS_SUCCESS) continue;

                int pct = logMax > 0 && logMax != 100 ? (int)Math.Round(v * 100.0 / logMax) : (int)v;
                if (pct is < 0 or > 100) continue;
                return (pct, ProductName(q) ?? $"Device {h.Vid:X4}:{h.Pid:X4}");
            }
        }
        catch { }
        finally { HidD_FreePreparsedData(pp); }
        return null;
    }

    static string? ProductName(SafeFileHandle h)
    {
        var buf = new byte[256];
        if (!HidD_GetProductString(h, buf, buf.Length)) return null;
        var s = Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        return s.Length > 0 ? s : null;
    }
}

// ======================================================================
// 3) Logitech HID++ 2.0 (Lightspeed / Unifying / Bolt receivers, wired & BT)
// ======================================================================
static class Logitech
{
    const ushort F_ROOT = 0x0000, F_NAME = 0x0005, F_BATTERY_STATUS = 0x1000, F_BATTERY_VOLTAGE = 0x1001, F_UNIFIED_BATTERY = 0x1004;

    public static IEnumerable<BatteryInfo> Read(StringBuilder? log)
    {
        var results = new List<BatteryInfo>();
        var hid = Batteries.EnumerateHid().Where(h => h.Vid == 0x046D && h.UsagePage == 0xFF00).ToList();
        foreach (var p in hid.Where(h => h.Usage == 0x0002))
        {
            log?.AppendLine($"[Logitech] HID++ interface {p.Vid:X4}:{p.Pid:X4}");
            // The short-report collection is the sibling on the same interface (same path up to "&col").
            string Iface(string path) { int c = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase); return c > 0 ? path.Substring(0, c) : path; }
            var shortColl = hid.FirstOrDefault(h => h.Usage == 0x0001 && Iface(h.Path).Equals(Iface(p.Path), StringComparison.OrdinalIgnoreCase));
            try
            {
                using var dev = HidppChannel.Open(p.Path, p.Caps.InputReportByteLength, shortColl?.Path, shortColl?.Caps.InputReportByteLength ?? 7);
                if (dev is null) { log?.AppendLine("  could not open"); continue; }
                if (log != null)
                {
                    var conn = dev.ReadRegister(0x02);
                    log.AppendLine($"  receiver connection reg: {(conn is null ? "n/a" : Compat.Hex(conn))}");
                    for (byte i = 0; i < 6; i++)
                    {
                        var pi = dev.ReadRegister(0xB5, (byte)(0x20 + i), longReg: true);
                        if (pi != null) log.AppendLine($"  pairing slot {i + 1}: {Compat.Hex(pi)}");
                    }
                }
                foreach (byte idx in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                {
                    var info = Query(dev, idx, log);
                    if (info != null) results.Add(info);
                }
            }
            catch (Exception e) { log?.AppendLine("  error: " + e.Message); }
        }
        return results;
    }

    static BatteryInfo? Query(HidppChannel d, byte idx, StringBuilder? log)
    {
        // Ping (root.GetProtocolVersion). Fails/times out if nothing is at this index or it's asleep/off.
        var ping = d.Request(idx, 0, 1, 0, 0, 0xAA);
        if (log != null) log.AppendLine($"  ping {idx:X2}: {(ping is null ? "no reply" : Compat.Hex(ping))} {d.LastNote}");
        if (ping is null || ping[2] != 0xAA || ping[0] < 2) return null;

        string name = "Logitech device";
        DeviceKind kind = DeviceKind.Other;
        int fName = Feature(d, idx, F_NAME);
        if (fName > 0)
        {
            var t = d.Request(idx, (byte)fName, 2);
            if (t != null)
            {
                if (t[0] == 7) return null; // that's the receiver itself
                kind = t[0] switch { 0 or 2 => DeviceKind.Keyboard, 3 or 4 or 5 => DeviceKind.Mouse, 8 => DeviceKind.Headset, 11 or 12 => DeviceKind.Gamepad, _ => DeviceKind.Other };
            }
            var c = d.Request(idx, (byte)fName, 0);
            if (c != null && c[0] > 0)
            {
                var sb = new List<byte>();
                while (sb.Count < c[0])
                {
                    var part = d.Request(idx, (byte)fName, 1, (byte)sb.Count);
                    if (part is null) break;
                    var chunk = part.TakeWhile(b => b != 0).Take(c[0] - sb.Count).ToArray();
                    if (chunk.Length == 0) break;
                    sb.AddRange(chunk);
                }
                if (sb.Count > 0) name = Encoding.UTF8.GetString(sb.ToArray()).Trim();
            }
        }
        if (kind == DeviceKind.Other) kind = Batteries.GuessKind(name);

        int pct = -1; bool charging = false;
        int f;
        if ((f = Feature(d, idx, F_UNIFIED_BATTERY)) > 0)
        {
            var caps = d.Request(idx, (byte)f, 0);
            var s = d.Request(idx, (byte)f, 1);
            if (s != null)
            {
                bool socSupported = caps == null || (caps[1] & 0x02) != 0;
                pct = socSupported ? s[0] : (s[1] & 8) != 0 ? 90 : (s[1] & 4) != 0 ? 50 : (s[1] & 2) != 0 ? 20 : 5;
                charging = s[2] is 1 or 2 or 3;
            }
        }
        else if ((f = Feature(d, idx, F_BATTERY_STATUS)) > 0)
        {
            var s = d.Request(idx, (byte)f, 0);
            if (s != null) { pct = s[0]; charging = s[2] is >= 1 and <= 4; }
        }
        else if ((f = Feature(d, idx, F_BATTERY_VOLTAGE)) > 0)
        {
            var s = d.Request(idx, (byte)f, 0);
            if (s != null) { pct = VoltageToPercent((s[0] << 8) | s[1]); charging = (s[2] & 0x80) != 0; }
        }

        log?.AppendLine($"  index {idx:X2}: {name} ({kind}) battery={pct}% charging={charging}");
        if (pct is < 0 or > 100) return null;
        return new BatteryInfo(name, kind, pct, charging, "Logitech HID++");
    }

    static int Feature(HidppChannel d, byte idx, ushort id)
    {
        var r = d.Request(idx, 0, 0, (byte)(id >> 8), (byte)id);
        return r is null ? -1 : r[0];
    }

    // Typical Li-ion discharge curve used by Logitech mice (mV -> %).
    static readonly (int mv, int pct)[] Curve =
    {
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
        (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    };

    static int VoltageToPercent(int mv)
    {
        if (mv >= Curve[0].mv) return 100;
        for (int i = 1; i < Curve.Length; i++)
            if (mv >= Curve[i].mv)
            {
                var (hv, hp) = Curve[i - 1]; var (lv, lp) = Curve[i];
                return lp + (int)Math.Round((mv - lv) * (double)(hp - lp) / (hv - lv));
            }
        return 0;
    }
}

/// <summary>One HID collection opened for overlapped I/O, with a read that can stay pending.</summary>
sealed class HidPipe : IDisposable
{
    public readonly SafeFileHandle H;
    public readonly IntPtr Buf, Ov, Evt;
    public readonly int InLen;
    public bool Pending;

    HidPipe(SafeFileHandle h, int inLen)
    {
        H = h; InLen = Math.Max(inLen, 7);
        Buf = Marshal.AllocHGlobal(64);
        Ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        Evt = CreateEventW(IntPtr.Zero, true, false, null);
    }

    public static HidPipe? Open(string path, int inLen)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        return h.IsInvalid ? null : new HidPipe(h, inLen);
    }

    void Prep() { Marshal.StructureToPtr(new NativeOverlapped { EventHandle = Evt }, Ov, false); ResetEvent(Evt); }

    /// <summary>Makes sure a read is outstanding. Returns false if the read could not be started.</summary>
    public bool EnsureRead()
    {
        if (Pending) return true;
        Prep();
        if (!ReadFile(H, Buf, InLen, IntPtr.Zero, Ov) && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
        Pending = true; // completes via Evt (even if it finished synchronously, the event is set)
        return true;
    }

    /// <summary>Collects a completed read into dst; returns length or -1.</summary>
    public int Complete(byte[] dst)
    {
        Pending = false;
        if (!GetOverlappedResult(H, Ov, out int n, false)) return -1;
        Marshal.Copy(Buf, dst, 0, Math.Min(n, dst.Length));
        return n;
    }

    public bool Write(byte[] data, int len)
    {
        // Writes use their own OVERLAPPED so a pending read isn't disturbed.
        var ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        var buf = Marshal.AllocHGlobal(len);
        var evt = CreateEventW(IntPtr.Zero, true, false, null);
        try
        {
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = evt }, ov, false);
            Marshal.Copy(data, 0, buf, len);
            if (!WriteFile(H, buf, len, IntPtr.Zero, ov) && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
            if (WaitForSingleObject(evt, 1000) != 0) { CancelIoEx(H, ov); GetOverlappedResult(H, ov, out _, true); return false; }
            return GetOverlappedResult(H, ov, out _, false);
        }
        finally { CloseHandle(evt); Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(ov); }
    }

    public void Dispose()
    {
        if (Pending) { CancelIoEx(H, Ov); GetOverlappedResult(H, Ov, out _, true); }
        H.Dispose();
        CloseHandle(Evt);
        Marshal.FreeHGlobal(Buf);
        Marshal.FreeHGlobal(Ov);
    }
}

/// <summary>
/// Logitech HID++ over the receiver/device's vendor collections. Requests go out as long (0x11)
/// reports; replies can come back on either the long or the short (0x10) collection.
/// </summary>
sealed class HidppChannel : IDisposable
{
    const byte SW_ID = 0x0B;
    readonly HidPipe _long;
    readonly HidPipe? _short;
    public string LastNote = "";

    HidppChannel(HidPipe l, HidPipe? s) { _long = l; _short = s; }

    public static HidppChannel? Open(string longPath, int longIn, string? shortPath, int shortIn)
    {
        var l = HidPipe.Open(longPath, longIn);
        if (l is null) return null;
        var s = shortPath is null ? null : HidPipe.Open(shortPath, shortIn);
        return new HidppChannel(l, s);
    }

    /// <summary>Sends a HID++ request and returns the 16 parameter bytes of the reply, or null on error/timeout.</summary>
    public byte[]? Request(byte device, byte feature, byte function, params byte[] args)
    {
        var msg = new byte[20];
        msg[0] = 0x11; msg[1] = device; msg[2] = feature; msg[3] = (byte)((function << 4) | SW_ID);
        Array.Copy(args, 0, msg, 4, Math.Min(args.Length, 16));
        LastNote = "";

        // Start listening before writing so a fast reply isn't missed.
        _long.EnsureRead(); _short?.EnsureRead();
        if (!_long.Write(msg, 20)) { LastNote = "write failed"; return null; }

        var deadline = Compat.NowMs + 1000;
        var buf = new byte[64];
        var pipes = _short is null ? new[] { _long } : new[] { _long, _short };
        var events = pipes.Select(p => p.Evt).ToArray();
        while (true)
        {
            long left = deadline - Compat.NowMs;
            if (left <= 0) { LastNote += " timeout"; return null; }
            foreach (var p in pipes) p.EnsureRead();
            uint w = WaitForMultipleObjects((uint)events.Length, events, false, (uint)left);
            if (w >= events.Length) { LastNote += " timeout"; return null; }
            int n = pipes[w].Complete(buf);
            if (n < 7 || buf[1] != device || buf[0] is not (0x10 or 0x11)) continue;
            if ((buf[2] == 0xFF || buf[2] == 0x8F) && buf[3] == feature && buf[4] == msg[3]) { LastNote += $" error 0x{buf[5]:X2}"; return null; }
            if (buf[2] == feature && buf[3] == msg[3])
            {
                var r = new byte[16];
                Array.Copy(buf, 4, r, 0, Math.Min(16, n - 4));
                return r;
            }
        }
    }

    /// <summary>HID++ 1.0 receiver register read (short report on the short collection). Returns the 3 value bytes.</summary>
    public byte[]? ReadRegister(byte register, byte p0 = 0, bool longReg = false)
    {
        if (_short is null) return null;
        byte sub = (byte)(longReg ? 0x83 : 0x81);
        var msg = new byte[] { 0x10, 0xFF, sub, register, p0, 0, 0 };
        _long.EnsureRead(); _short.EnsureRead();
        if (!_short.Write(msg, 7)) return null;
        var deadline = Compat.NowMs + 500;
        var buf = new byte[64];
        var pipes = new[] { _long, _short };
        var events = pipes.Select(p => p.Evt).ToArray();
        while (Compat.NowMs < deadline)
        {
            foreach (var p in pipes) p.EnsureRead();
            uint w = WaitForMultipleObjects(2, events, false, (uint)Math.Max(1, deadline - Compat.NowMs));
            if (w >= 2) return null;
            int n = pipes[w].Complete(buf);
            if (n < 7 || buf[1] != 0xFF) continue;
            if (buf[2] == 0x8F && buf[3] == sub && buf[4] == register) return null;
            if (buf[2] == sub && buf[3] == register) return buf.Skip(4).Take(n - 4).ToArray();
        }
        return null;
    }

    public void Dispose() { _long.Dispose(); _short?.Dispose(); }
}

// ======================================================================
// 4) HyperX Cloud Stinger Core Wireless (Kingston 0951:170B / 0951:1713 PS, HP 03F0:0A93)
//    Read-only queries over feature report 0xFF on the FF00:0001 collection.
//    Protocol credit: krisgfx9/Wireless-Device-Battery-Monitor PR #295 / HyperBatteryMonitor (MIT).
// ======================================================================
static class HyperX
{
    static readonly byte[] ConnectionQuery = { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x00, 0x00, 0x02, 0x82, 0x03, 0x05, 0x29, 0x0F, 0x00, 0x00, 0x03, 0x68, 0x10, 0x00, 0x00 };
    static readonly byte[] BatteryQuery = { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x0C, 0xF1, 0x02, 0x01, 0x04, 0xF0, 0x0C };

    static readonly int[] Pct = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0 };
    static readonly int[] ChargingMv = { 4130, 4090, 4050, 4000, 3967, 3946, 3930, 3916, 3871, 3831, 3400 };
    static readonly int[] DischargingMv = { 4121, 4042, 3954, 3905, 3838, 3785, 3763, 3738, 3700, 3651, 3517 };

    static bool Supported(ushort vid, ushort pid) =>
        (vid == 0x0951 && pid is 0x170B or 0x1713) || (vid == 0x03F0 && pid == 0x0A93);

    public static IEnumerable<BatteryInfo> Read(StringBuilder? log)
    {
        var results = new List<BatteryInfo>();
        foreach (var h in Batteries.EnumerateHid().Where(h => Supported(h.Vid, h.Pid) && h.UsagePage == 0xFF00 && h.Usage == 0x0001 && h.Caps.FeatureReportByteLength >= 32))
        {
            using var f = CreateFileW(h.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (f.IsInvalid) continue;
            int len = h.Caps.FeatureReportByteLength;

            var conn = Exchange(f, len, ConnectionQuery, r => !(r[1] == 0x01 && r[2] == 0x00));
            log?.AppendLine($"[HyperX] {h.Vid:X4}:{h.Pid:X4} connection: {(conn is null ? "no reply" : Compat.Hex(conn, 0, 16))}");
            if (conn is null || conn[11] != 0x01) continue; // headset off / out of range

            var bat = Exchange(f, len, BatteryQuery, r => r[0] == 0xFF && r[1] == 0x12 && r[2] == 0x05);
            log?.AppendLine($"[HyperX] battery: {(bat is null ? "no reply" : Compat.Hex(bat, 0, 16))}");
            if (bat is null || bat[9] is not (0x03 or 0x05 or 0x06)) continue;

            int mv = bat[11] | (bat[12] << 8);
            int pct = bat[9] == 0x06 ? 100 : Interpolate(mv, bat[9] == 0x05 ? ChargingMv : DischargingMv);
            bool charging = bat[9] == 0x05;
            log?.AppendLine($"[HyperX] {mv} mV, code {bat[9]:X2} -> {pct}%{(charging ? " charging" : "")}");
            results.Add(new BatteryInfo("HyperX Cloud Stinger Core Wireless", DeviceKind.Headset, pct, charging, "HyperX"));
        }
        return results;
    }

    static byte[]? Exchange(SafeFileHandle f, int len, byte[] query, Func<byte[], bool> isReply)
    {
        var q = new byte[len];
        Array.Copy(query, q, query.Length);
        if (!HidD_SetFeature(f, q, len)) return null;
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(20);
            var r = new byte[len];
            r[0] = 0xFF;
            if (!HidD_GetFeature(f, r, len)) return null;
            if (isReply(r)) return r;
        }
        return null;
    }

    static int Interpolate(int mv, int[] curve)
    {
        if (mv >= curve[0]) return 100;
        for (int i = 1; i < curve.Length; i++)
            if (mv >= curve[i])
                return (int)Math.Round(Pct[i] + (mv - curve[i]) * (double)(Pct[i - 1] - Pct[i]) / (curve[i - 1] - curve[i]));
        return 0;
    }
}
