global using System;
global using System.Collections.Generic;
global using System.Drawing;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Windows.Forms;

using System.Diagnostics;

namespace System.Runtime.CompilerServices
{
    // Lets C# records / init-only properties compile on .NET Framework.
    internal static class IsExternalInit { }
}

namespace BatteryBar
{
    /// <summary>Small helpers for APIs that .NET Framework 4.8 doesn't have.</summary>
    internal static class Compat
    {
        public static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        public static string Hex(byte[] b) => Hex(b, 0, b.Length);
        public static string Hex(byte[] b, int offset, int count) =>
            BitConverter.ToString(b, offset, Math.Max(0, Math.Min(count, b.Length - offset))).Replace("-", "");

        public static void Ui(this Control c, Action a)
        {
            if (!c.IsDisposed && c.IsHandleCreated) c.BeginInvoke(a);
        }
    }
}
