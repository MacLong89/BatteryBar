using System.Text;

namespace BatteryBar;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // "BatteryBar.exe --dump" writes a diagnostic report next to the exe and exits.
        if (args.Contains("--dump"))
        {
            var log = new StringBuilder();
            foreach (var h in Batteries.EnumerateHid())
                log.AppendLine($"coll {h.Vid:X4}:{h.Pid:X4} page={h.UsagePage:X4} usage={h.Usage:X4} in={h.Caps.InputReportByteLength} out={h.Caps.OutputReportByteLength} feat={h.Caps.FeatureReportByteLength}  {h.Path}");
            var list = Batteries.ReadAll(log);
            log.AppendLine().AppendLine("Result:");
            foreach (var b in list) log.AppendLine($"  {b.Kind,-8} {b.Percent,3}%{(b.Charging ? " (charging)" : "")}  {b.Name}  [{b.Source}]");
            if (list.Count == 0) log.AppendLine("  (no devices reporting battery)");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "battery-dump.txt"), log.ToString());
            return 0;
        }

        OverlayForm.Demo = args.Contains("--demo");
        using var mutex = new Mutex(true, @"Local\BatteryBar.SingleInstance", out bool first);
        if (!first) return 0;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new OverlayForm());
        return 0;
    }
}
