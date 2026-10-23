// monitor-power.exe - DDC/CI monitor power controller (VCP 0xD6)
//   no arguments  -> GUI (clickable per-monitor power buttons)
//   with arguments -> same command line interface as before
// Build: src\build.cmd   (Framework csc, C# 5 / .NET 4.x, /target:winexe /codepage:65001)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Monitor Power")]
[assembly: AssemblyDescription("DDC/CI monitor power controller (GUI + CLI)")]
[assembly: AssemblyProduct("monitor-power")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

static class AppInfo
{
    public const string Version = "1.1.1";
    public const string RepoUrl = "https://github.com/IDShallow/monitor-power";
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string LatestUrl = RepoUrl + "/releases/latest/download/latest.json";
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MonitorPower";

    public static bool IsEnabled()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                if (k == null) return false;
                return k.GetValue(ValueName) != null;
            }
        }
        catch { return false; }
    }

    public static bool Set(bool on)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k == null) return false;
                if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(ValueName, false);
            }
            return true;
        }
        catch { return false; }
    }
}

// Dark, flat theme in the spirit of the ai-traffic-light app.
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(30, 30, 30);
    public static readonly Color Bg2 = Color.FromArgb(38, 38, 38);
    public static readonly Color Bg3 = Color.FromArgb(46, 46, 46);
    public static readonly Color Line = Color.FromArgb(60, 60, 60);
    public static readonly Color Text = Color.FromArgb(235, 235, 235);
    public static readonly Color Dim = Color.FromArgb(155, 155, 155);
    public static readonly Color Green = Color.FromArgb(76, 196, 106);
    public static readonly Color Blue = Color.FromArgb(90, 169, 230);
    public static readonly Color Red = Color.FromArgb(224, 93, 93);
    public static readonly Color Amber = Color.FromArgb(240, 180, 60);

    public static void Button(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Bg3;
        b.ForeColor = Text;
        b.FlatAppearance.BorderColor = Line;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 60);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(72, 72, 72);
    }

    public static void Link(LinkLabel l)
    {
        l.LinkColor = Blue;
        l.ActiveLinkColor = Color.FromArgb(140, 200, 245);
        l.VisitedLinkColor = Blue;
        l.LinkBehavior = LinkBehavior.HoverUnderline;
    }
}

// Dark title bar (Windows 10 1809+): same look as the ai-traffic-light app.
static class DarkTitle
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Form f)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)
                DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
        }
        catch { }
    }
}

class UpdateInfo
{    public string Version;
    public string Url;
    public string Sha256;
    public string Notes;
    public bool Mandatory;
}

// Update manifest = latest.json attached to the newest GitHub release:
//   { "version":"1.1.0", "url":".../monitor-power.exe", "sha256":"...",
//     "notes":"...", "mandatory":false }
static class UpdateCheck
{
    public static UpdateInfo Fetch()
    {
        Exception last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return FetchOnce(); }
            catch (Exception ex) { last = ex; Thread.Sleep(700); }
        }
        throw last;
    }

    static UpdateInfo FetchOnce()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(AppInfo.LatestUrl);
        req.UserAgent = "monitor-power/" + AppInfo.Version;
        req.Timeout = 12000;
        req.ReadWriteTimeout = 12000;
        req.Proxy = null;                       // go direct; a stale WinINET proxy breaks this
        req.AutomaticDecompression = DecompressionMethods.GZip;
        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        using (Stream s = resp.GetResponseStream())
        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
        {
            string json = sr.ReadToEnd();
            UpdateInfo info = new UpdateInfo();
            info.Version = StrField(json, "version");
            info.Url = StrField(json, "url");
            info.Sha256 = StrField(json, "sha256");
            info.Notes = StrField(json, "notes");
            info.Mandatory = BoolField(json, "mandatory");
            if (string.IsNullOrEmpty(info.Version) || string.IsNullOrEmpty(info.Url)) return null;
            return info;
        }
    }

    static string StrField(string json, string key)
    {
        string pat = "\"" + key + "\"";
        int i = json.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i = json.IndexOf(':', i + pat.Length);
        if (i < 0) return null;
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return null;
        i++;
        StringBuilder sb = new StringBuilder();
        while (i < json.Length)
        {
            char c = json[i++];
            if (c == '"') break;
            if (c == '\\' && i < json.Length)
            {
                char e = json[i++];
                if (e == 'n') sb.Append('\n');
                else if (e == 't') sb.Append('\t');
                else if (e == 'r') sb.Append('\r');
                else if (e == 'u' && i + 4 <= json.Length)
                {
                    int code;
                    if (int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out code)) { sb.Append((char)code); i += 4; }
                }
                else sb.Append(e);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static bool BoolField(string json, string key)
    {
        string pat = "\"" + key + "\"";
        int i = json.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        i = json.IndexOf(':', i + pat.Length);
        if (i < 0) return false;
        string rest = json.Substring(i + 1, Math.Min(10, json.Length - i - 1)).Trim();
        return rest.StartsWith("true", StringComparison.OrdinalIgnoreCase);
    }

    public static int CompareVersions(string a, string b)
    {
        string[] pa = (a ?? "").TrimStart('v', 'V').Split('.');
        string[] pb = (b ?? "").TrimStart('v', 'V').Split('.');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            int va = 0, vb = 0;
            if (i < pa.Length) int.TryParse(pa[i], out va);
            if (i < pb.Length) int.TryParse(pb[i], out vb);
            if (va != vb) return va < vb ? -1 : 1;
        }
        return 0;
    }

    // Downloads the new exe to %TEMP%, verifies sha256, returns the local path.
    public static string Download(UpdateInfo info, out string error)
    {
        error = null;
        string path = Path.Combine(Path.GetTempPath(), "monitor-power-" + info.Version + ".exe");
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(info.Url);
            req.UserAgent = "monitor-power/" + AppInfo.Version;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
            }
            if (!string.IsNullOrEmpty(info.Sha256))
            {
                string actual;
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder();
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    actual = sb.ToString();
                }
                if (!string.Equals(actual, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    error = "SHA256 不匹配（下载被篡改或文件损坏）";
                    try { File.Delete(path); } catch { }
                    return null;
                }
            }
            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // The downloaded exe replaces this one: it runs with --apply-update <pid> <dst>,
    // waits for this process to exit, copies itself over dst and starts it.
    public static void ApplyAndExit(string newExe)
    {
        ProcessStartInfo psi = new ProcessStartInfo(newExe,
            "--apply-update " + Process.GetCurrentProcess().Id + " \"" + Application.ExecutablePath + "\"");
        psi.UseShellExecute = false;
        Process.Start(psi);
        Application.Exit();
    }
}

class MonInfo
{
    public int Index;
    public IntPtr Handle;
    public string Model;
    public string Device;      // \\.\DISPLAYn
    public string EdidModel;   // model string from the EDID (works even when DDC/CI is dead)
    public string Vendor;      // 3 letter PnP id, e.g. DEL
    public int Left, Top, Right, Bottom;
    public string RectText { get { return Left + "," + Top + "-" + Right + "," + Bottom; } }
}

static class Ddc
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetCapabilitiesStringLength(IntPtr h, out uint len);
    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Ansi)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, StringBuilder s, uint len);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] static extern bool LockWorkStation();

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);

    static List<MonInfo> enumTarget;
    static readonly object enumLock = new object();

    // The EDID sits in HKLM\...\Enum\DISPLAY\<vendor>\<instance>\Device Parameters
    // and is readable even when the panel does not answer DDC/CI at all, so the
    // model name still comes out right for a monitor we cannot talk to.
    static string EdidModel(string deviceName, out string vendor)
    {
        vendor = "";
        DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(dd);
        if (string.IsNullOrEmpty(deviceName) || !EnumDisplayDevices(deviceName, 0, ref dd, 1)) return null;
        string id = dd.DeviceID;
        if (string.IsNullOrEmpty(id)) return null;
        int k = id.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
        if (k < 0) return null;
        string[] parts = id.Substring(k + 8).Split('#');
        if (parts.Length < 2) return null;

        byte[] edid = null;
        string key = "SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\" + parts[0] + "\\" + parts[1] + "\\Device Parameters";
        try
        {
            using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key))
            {
                if (rk != null) edid = rk.GetValue("EDID") as byte[];
            }
        }
        catch { }
        if (edid == null || edid.Length < 128) return null;
        if (!(edid[0] == 0x00 && edid[1] == 0xFF && edid[7] == 0x00)) return null;

        int code = (edid[8] << 8) | edid[9];
        vendor = "" + (char)(((code >> 10) & 0x1F) + 'A' - 1)
                    + (char)(((code >> 5) & 0x1F) + 'A' - 1)
                    + (char)((code & 0x1F) + 'A' - 1);

        string name = null;
        for (int i = 0; i < 4; i++)
        {
            int off = 54 + i * 18;
            if (off + 17 >= edid.Length) break;
            if (edid[off] == 0 && edid[off + 1] == 0 && edid[off + 2] == 0 && edid[off + 4] == 0 && edid[off + 3] == 0xFC)
            {
                StringBuilder sb = new StringBuilder();
                for (int j = 5; j < 18; j++)
                {
                    byte b = edid[off + j];
                    if (b == 0x0A || b == 0x00) break;
                    sb.Append((char)b);
                }
                name = sb.ToString().Trim();
                break;
            }
        }
        if (string.IsNullOrEmpty(name)) name = vendor + " " + (edid[10] | (edid[11] << 8)).ToString("X4");
        return name;
    }

    static string ModelOf(IntPtr h, string fallback)
    {
        uint len = 0;
        if (!GetCapabilitiesStringLength(h, out len) || len == 0 || len > 200000) return fallback;
        StringBuilder sb = new StringBuilder((int)len);
        if (!CapabilitiesRequestAndCapabilitiesReply(h, sb, len)) return fallback;
        string caps = sb.ToString();
        int i = caps.IndexOf("model(", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return fallback;
        i += 6;
        int j = caps.IndexOf(')', i);
        if (j <= i) return fallback;
        return caps.Substring(i, j - i).Trim();
    }

    static bool OnMonitor(IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data)
    {
        uint n = 0;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out n) || n == 0) return true;
        PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[n];
        if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;

        MONITORINFOEX mi = new MONITORINFOEX();
        mi.cbSize = Marshal.SizeOf(mi);
        string devName = GetMonitorInfo(hMon, ref mi) ? mi.szDevice : null;

        string vendor;
        string edidName = EdidModel(devName, out vendor);

        for (int i = 0; i < arr.Length; i++)
        {
            MonInfo m = new MonInfo();
            m.Handle = arr[i].hPhysicalMonitor;
            m.Device = devName;
            m.Vendor = vendor;
            m.EdidModel = edidName;
            string ddcName = ModelOf(m.Handle, null);
            m.Model = !string.IsNullOrEmpty(ddcName) ? ddcName
                    : (!string.IsNullOrEmpty(edidName) ? edidName : arr[i].szPhysicalMonitorDescription);
            m.Left = rect.left; m.Top = rect.top; m.Right = rect.right; m.Bottom = rect.bottom;

            // drivers occasionally report the same panel twice in one pass - keep the first
            bool dup = false;
            foreach (MonInfo e in enumTarget)
            {
                if (e.Device == m.Device && e.Left == m.Left && e.Top == m.Top &&
                    e.Right == m.Right && e.Bottom == m.Bottom && e.EdidModel == m.EdidModel)
                { dup = true; break; }
            }
            if (dup) { DestroyPhysicalMonitor(m.Handle); continue; }

            m.Index = enumTarget.Count;
            enumTarget.Add(m);
        }
        return true;
    }

    // Each call owns its own list (the main window and the board enumerate
    // independently); the previous shared static list let one window destroy
    // the other window's monitor handles.
    public static List<MonInfo> Enumerate()
    {
        lock (enumLock)
        {
            List<MonInfo> result = new List<MonInfo>();
            enumTarget = result;
            try
            {
                MonitorEnumProc proc = new MonitorEnumProc(OnMonitor);
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
                GC.KeepAlive(proc);
            }
            finally { enumTarget = null; }
            return result;
        }
    }

    // DDC calls run on a worker thread: SetVCPFeature against a panel whose I2C
    // path is dead blocks inside the driver for a long time.
    public static string Read(IntPtr h, int timeoutMs)
    {
        string result = "TIMEOUT";
        Thread t = new Thread(delegate()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                uint type = 0, cur = 0, max = 0;
                if (GetVCPFeatureAndVCPFeatureReply(h, 0xD6, out type, out cur, out max)) { result = cur.ToString(); return; }
                result = "ERR 0x" + Marshal.GetLastWin32Error().ToString("X8");
                Thread.Sleep(250);
            }
        });
        t.IsBackground = true;
        t.Start();
        if (!t.Join(timeoutMs)) return "TIMEOUT";
        return result;
    }

    public static string Write(IntPtr h, uint value, int timeoutMs)
    {
        string result = "TIMEOUT";
        Thread t = new Thread(delegate()
        {
            if (SetVCPFeature(h, 0xD6, value)) result = "OK";
            else result = "ERR 0x" + Marshal.GetLastWin32Error().ToString("X8");
        });
        t.IsBackground = true;
        t.Start();
        if (!t.Join(timeoutMs)) return result;
        return result;
    }

    public static bool IsNumeric(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
        return true;
    }

    public static string WaitState(IntPtr h, string want, int timeoutSec)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            last = Read(h, 2500);
            if (last == want) return last;
            Thread.Sleep(400);
        }
        return last;
    }

    public static string StateName(string raw)
    {
        if (raw == "1") return "On";
        if (raw == "2") return "Standby";
        if (raw == "3") return "Suspend";
        if (raw == "4") return "Off(soft)";
        if (raw == "5") return "Off(hard)";
        return "unreachable/unknown";
    }

    public static string StateTextCn(string raw)
    {
        if (raw == "1") return "开 (D6=1)";
        if (raw == "2") return "待机 (D6=2)";
        if (raw == "3") return "挂起 (D6=3)";
        if (raw == "4") return "软关 (D6=4)";
        if (raw == "5") return "关机 (D6=5)";
        return "读不到 (" + (raw == "" ? "DDC 无应答" : raw) + ")";
    }

    // A panel that never answers DDC/CI cannot be switched on from software; the
    // only thing that brings a DPMS-off display back is real user input, so nudge
    // the cursor one pixel and put it back.
    public static void NudgeInput()
    {
        POINT p;
        if (!GetCursorPos(out p)) return;
        SetCursorPos(p.x + 1, p.y);
        Thread.Sleep(60);
        SetCursorPos(p.x, p.y);
    }

    public static bool LockDesktop()
    {
        try { return LockWorkStation(); } catch { return false; }
    }

    public static void ForceOff()    {
        // HWND_BROADCAST / WM_SYSCOMMAND / SC_MONITORPOWER=2, timeout instead of SendMessage
        IntPtr res;
        SendMessageTimeout(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2), 0x0003, 1500, out res);
    }

    public static void Destroy(IntPtr h) { try { DestroyPhysicalMonitor(h); } catch { } }

    public static bool AttachToParentConsole()
    {
        try { return AttachConsole(0xFFFFFFFF); } catch { return false; }
    }
}

// ---------------------------------------------------------------- GUI

class Card : Panel
{
    public MonInfo M;
    public Label StateLabel;
    public Label DetailLabel;
    public Button BOn, BOff, BSoft;
    public bool Hinted;      // DDC warning already written to the log for this card
    public int FailStreak;   // consecutive unreadable polls
    public string LastGood;  // last readable D6 value

    public Card(MonInfo m, EventHandler onOn, EventHandler onOff, EventHandler onSoft)
    {
        M = m;
        Height = 96;
        Margin = new Padding(6);
        BackColor = Theme.Bg2;
        BorderStyle = BorderStyle.None;

        Label title = new Label();
        title.Text = "[" + m.Index + "] " + m.Model;
        title.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        title.ForeColor = Theme.Text;
        title.AutoSize = true;
        title.Location = new Point(14, 10);

        DetailLabel = new Label();
        DetailLabel.Text = BaseDetail(m);
        DetailLabel.AutoSize = false;
        DetailLabel.Height = 18;
        DetailLabel.AutoEllipsis = true;
        DetailLabel.ForeColor = Theme.Dim;
        DetailLabel.Location = new Point(14, 36);

        StateLabel = new Label();
        StateLabel.Text = "状态: 读取中…";
        StateLabel.AutoSize = true;
        StateLabel.ForeColor = Theme.Dim;
        StateLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        StateLabel.Location = new Point(14, 62);

        BOn = MakeButton("开机", 62);
        BOn.Name = "on_" + m.Index;
        BOn.Click += onOn;
        BOff = MakeButton("关机(电源键)", 108);
        BOff.Name = "off_" + m.Index;
        BOff.Click += onOff;
        BSoft = MakeButton("软关(DPMS)", 100);
        BSoft.Name = "soft_" + m.Index;
        BSoft.Click += onSoft;

        Controls.Add(title);
        Controls.Add(DetailLabel);
        Controls.Add(StateLabel);
        Controls.Add(BOn);
        Controls.Add(BOff);
        Controls.Add(BSoft);
        LayoutButtons();
    }

    static Button MakeButton(string text, int width)
    {
        Button b = new Button();
        b.Text = text;
        b.Width = width;
        b.Height = 30;
        b.Top = 30;
        Theme.Button(b);
        return b;
    }

    void LayoutButtons()
    {
        if (BOn == null || BOff == null || BSoft == null) return;   // fires from the ctor via Height
        BSoft.Left = Width - BSoft.Width - 14;
        BOff.Left = BSoft.Left - BOff.Width - 8;
        BOn.Left = BOff.Left - BOn.Width - 8;
        if (DetailLabel != null) DetailLabel.Width = Math.Max(80, BOn.Left - DetailLabel.Left - 10);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutButtons();
    }

    public void SetState(string raw, bool busy)
    {
        if (busy) { StateLabel.Text = "状态: 执行中…"; StateLabel.ForeColor = Theme.Amber; return; }
        StateLabel.Text = "状态: " + Ddc.StateTextCn(raw);
        if (raw == "1") StateLabel.ForeColor = Theme.Green;
        else if (raw == "5") StateLabel.ForeColor = Theme.Dim;
        else if (raw == "4") StateLabel.ForeColor = Theme.Blue;
        else if (Ddc.IsNumeric(raw)) StateLabel.ForeColor = Theme.Blue;
        else StateLabel.ForeColor = Theme.Red;
    }

    public void SetDetail(string text)
    {
        DetailLabel.Text = BaseDetail(M) + (string.IsNullOrEmpty(text) ? "" : "   " + text);
    }

    static string BaseDetail(MonInfo m)
    {
        string s = m.RectText;
        if (!string.IsNullOrEmpty(m.Device)) s += "   " + m.Device;
        if (!string.IsNullOrEmpty(m.Vendor)) s += "   " + m.Vendor;
        return s;
    }
}

class MainForm : Form
{
    FlowLayoutPanel cards;
    Label header;
    TextBox logBox;
    Button btnRefresh, btnAllOn, btnAllOff, btnBlank, btnLock, btnBoard;
    LinkLabel lnkUpdate, lnkStar;
    Label lblDot, lblVersion;
    CheckBox chkStartup;
    bool syncingStartup;
    BoardForm board;
    bool updateKnown;
    string updateVersion;
    System.Windows.Forms.Timer pollTimer;
    List<MonInfo> monitors = new List<MonInfo>();
    List<Card> cardList = new List<Card>();
    readonly object cardLock = new object();
    string currentSig = "";
    string pendingSig;
    int pendingCount;
    int tick;
    bool polling;
    bool acting;

    public MainForm()
    {
        Text = "显示器电源控制器 (DDC/CI VCP 0xD6)";
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        ClientSize = new Size(900, 600);
        MinimumSize = new Size(780, 460);
        StartPosition = FormStartPosition.CenterScreen;

        Panel top = new Panel();
        top.Dock = DockStyle.Top;
        top.Height = 52;
        top.BackColor = Theme.Bg;
        top.Padding = new Padding(12, 10, 12, 10);

        header = new Label();
        header.ForeColor = Theme.Text;
        header.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        header.Text = "正在枚举显示器…";
        header.AutoSize = true;
        header.Location = new Point(12, 18);

        FlowLayoutPanel actions = new FlowLayoutPanel();
        actions.Dock = DockStyle.Right;
        actions.FlowDirection = FlowDirection.RightToLeft;
        actions.WrapContents = false;
        actions.AutoSize = true;
        actions.Padding = new Padding(0, 2, 0, 0);

        btnRefresh = ActionButton("刷新", delegate { RefreshCycle(true, true); });
        btnRefresh.Name = "btnRefresh";
        btnBlank = ActionButton("系统熄灭", delegate { RunAll("系统熄灭", 0); });
        btnBlank.Name = "btnBlank";
        btnAllOff = ActionButton("全部关机", delegate { RunAll("全部关机", 5); });
        btnAllOff.Name = "btnAllOff";
        btnAllOn = ActionButton("全部开机", delegate { RunAll("全部开机", 1); });
        btnAllOn.Name = "btnAllOn";

        btnLock = ActionButton("锁定并关屏", delegate { LockAndBlank(); });
        btnLock.Name = "btnLockOff";
        btnBoard = ActionButton("状态看板", delegate { ShowBoard(); });
        btnBoard.Name = "btnBoard";

        actions.Controls.Add(btnBoard);
        actions.Controls.Add(btnLock);
        actions.Controls.Add(btnRefresh);
        actions.Controls.Add(btnBlank);
        actions.Controls.Add(btnAllOff);
        actions.Controls.Add(btnAllOn);
        top.Controls.Add(actions);
        top.Controls.Add(header);

        cards = new FlowLayoutPanel();
        cards.Dock = DockStyle.Fill;
        cards.FlowDirection = FlowDirection.TopDown;
        cards.WrapContents = false;
        cards.AutoScroll = true;
        cards.Padding = new Padding(8, 4, 8, 8);
        cards.BackColor = Theme.Bg;

        logBox = new TextBox();
        logBox.Dock = DockStyle.Bottom;
        logBox.Height = 120;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.BackColor = Color.FromArgb(20, 20, 20);
        logBox.Font = new Font("Consolas", 9F);
        logBox.ForeColor = Color.FromArgb(190, 190, 190);
        logBox.BorderStyle = BorderStyle.None;

        Panel footer = new Panel();
        footer.Dock = DockStyle.Bottom;
        footer.Height = 28;
        footer.BackColor = Color.FromArgb(24, 24, 24);

        lblVersion = new Label();
        lblVersion.Text = "v" + AppInfo.Version;
        lblVersion.AutoSize = true;
        lblVersion.ForeColor = Theme.Dim;
        lblVersion.Location = new Point(12, 6);

        lnkUpdate = new LinkLabel();
        lnkUpdate.Text = "检查更新";
        lnkUpdate.AutoSize = true;
        lnkUpdate.LinkColor = Theme.Blue;
        lnkUpdate.ActiveLinkColor = Color.FromArgb(140, 200, 245);
        lnkUpdate.VisitedLinkColor = Theme.Blue;
        lnkUpdate.Location = new Point(66, 5);
        lnkUpdate.LinkClicked += delegate { CheckUpdates(true); };

        lblDot = new Label();
        lblDot.Text = "●";
        lblDot.AutoSize = true;
        lblDot.ForeColor = Theme.Red;
        lblDot.Location = new Point(126, 4);
        lblDot.Visible = false;

        lnkStar = new LinkLabel();
        lnkStar.Text = "★ 给个 Star";
        lnkStar.AutoSize = true;
        lnkStar.LinkColor = Theme.Blue;
        lnkStar.ActiveLinkColor = Color.FromArgb(140, 200, 245);
        lnkStar.VisitedLinkColor = Theme.Blue;
        lnkStar.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        lnkStar.Location = new Point(760, 5);
        lnkStar.LinkClicked += delegate { OpenUrl(AppInfo.RepoUrl); };

        chkStartup = new CheckBox();
        chkStartup.Text = "开机自启";
        chkStartup.AutoSize = true;
        chkStartup.ForeColor = Theme.Dim;
        chkStartup.Location = new Point(148, 4);
        chkStartup.Checked = Autostart.IsEnabled();
        chkStartup.CheckedChanged += delegate
        {
            if (syncingStartup) return;
            bool ok = Autostart.Set(chkStartup.Checked);
            if (!ok)
            {
                syncingStartup = true;
                chkStartup.Checked = !chkStartup.Checked;
                syncingStartup = false;
                MessageBox.Show("写入开机自启注册表失败（HKCU\\...\\Run）", "monitor-power", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Log("开机自启：" + (chkStartup.Checked ? "已开启" : "已关闭"));
        };

        footer.Controls.Add(lblVersion);
        footer.Controls.Add(lnkUpdate);
        footer.Controls.Add(lblDot);
        footer.Controls.Add(chkStartup);
        footer.Controls.Add(lnkStar);
        footer.Resize += delegate { lnkStar.Left = footer.Width - lnkStar.Width - 12; };

        Controls.Add(cards);
        Controls.Add(logBox);
        Controls.Add(footer);
        Controls.Add(top);

        pollTimer = new System.Windows.Forms.Timer();
        pollTimer.Interval = 4000;
        pollTimer.Tick += delegate { tick++; RefreshCycle(tick % 3 == 0, false); };   // every 3rd tick = full re-enumeration (12s)

        Load += delegate
        {
            DarkTitle.Apply(this);
            try { Reload(); }
            catch (Exception ex) { Entry.CrashLog(ex, "Load/Reload"); MessageBox.Show(ex.ToString(), "monitor-power"); }
            pollTimer.Start();
            lnkStar.Left = footer.Width - lnkStar.Width - 12;

            // quiet update check on startup (does not block the UI)
            ThreadPool.QueueUserWorkItem(delegate(object st)
            {
                Thread.Sleep(1500);
                UpdateInfo info = null;
                try { info = UpdateCheck.Fetch(); } catch { }
                if (info == null) return;
                bool newer = UpdateCheck.CompareVersions(info.Version, AppInfo.Version) > 0;
                UI(delegate
                {
                    updateKnown = newer;
                    updateVersion = info.Version;
                    lblDot.Visible = newer;
                    if (newer) Log("发现新版本 v" + info.Version + "（点左下角「检查更新」查看/安装）");
                });
            });
        };
        FormClosing += delegate
        {
            pollTimer.Stop();
            if (board != null && !board.IsDisposed) board.Close();
            foreach (MonInfo m in monitors) Ddc.Destroy(m.Handle);
        };
    }

    static Button ActionButton(string text, EventHandler h)
    {
        Button b = new Button();
        b.Text = text;
        b.Width = 92;
        b.Height = 30;
        b.Margin = new Padding(6, 0, 0, 0);
        b.Click += h;
        Theme.Button(b);
        return b;
    }

    void Log(string line)
    {
        if (logBox.IsDisposed) return;
        if (logBox.InvokeRequired)
        {
            logBox.BeginInvoke((MethodInvoker)delegate { Log(line); });
            return;
        }
        logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine);
    }

    static void OpenUrl(string url)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(url);
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch { }
    }

    void ShowBoard()
    {
        if (board == null || board.IsDisposed)
        {
            board = new BoardForm();
            board.FormClosed += delegate { board = null; };
            // no owner window: an owned form is hidden together with the main window
            board.Show();
        }
        else
        {
            if (board.WindowState == FormWindowState.Minimized) board.WindowState = FormWindowState.Normal;
            board.Activate();
            board.BringToFront();
        }
    }

    void CheckUpdates(bool manual)
    {
        Log("正在检查更新…");
        ThreadPool.QueueUserWorkItem(delegate(object st)
        {
            UpdateInfo info = null;
            string err = null;
            try { info = UpdateCheck.Fetch(); }
            catch (Exception ex) { err = ex.Message; }
            UI(delegate
            {
                if (info == null)
                {
                    string msg = err ?? "无法读取 latest.json";
                    Log("检查更新失败：" + msg);
                    if (manual) ShowUpdateError(msg);
                    return;
                }
                bool newer = UpdateCheck.CompareVersions(info.Version, AppInfo.Version) > 0;
                updateKnown = newer;
                updateVersion = info.Version;
                lblDot.Visible = newer;
                if (newer)
                {
                    Log("发现新版本 v" + info.Version + "（当前 v" + AppInfo.Version + "）");
                    ShowUpdateDialog(info);
                }
                else
                {
                    Log("已是最新版本 v" + AppInfo.Version);
                    if (manual) MessageBox.Show("已是最新版本 v" + AppInfo.Version + "。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            });
        });
    }

    void ShowUpdateError(string msg)
    {
        using (Form dlg = new Form())
        {
            dlg.Text = "检查更新失败";
            dlg.ClientSize = new Size(460, 176);
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false;
            dlg.MinimizeBox = false;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.Font = Font;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;

            Label tip = new Label();
            tip.AutoSize = false;
            tip.Bounds = new Rectangle(14, 14, 432, 78);
            tip.ForeColor = Theme.Text;
            tip.Text = "连不上 GitHub（网络原因）：\r\n" + msg;

            Button retry = new Button();
            retry.Text = "重试";
            retry.Bounds = new Rectangle(160, 120, 90, 30);
            retry.DialogResult = DialogResult.Retry;
            Theme.Button(retry);

            Button page = new Button();
            page.Text = "打开发布页";
            page.Bounds = new Rectangle(258, 120, 112, 30);
            page.Click += delegate { OpenUrl(AppInfo.ReleasesUrl); };
            Theme.Button(page);

            Button close = new Button();
            close.Text = "关闭";
            close.Bounds = new Rectangle(378, 120, 68, 30);
            close.DialogResult = DialogResult.Cancel;
            Theme.Button(close);

            dlg.Controls.Add(tip);
            dlg.Controls.Add(retry);
            dlg.Controls.Add(page);
            dlg.Controls.Add(close);
            dlg.CancelButton = close;

            if (dlg.ShowDialog(this) == DialogResult.Retry) CheckUpdates(true);
        }
    }

    void ShowUpdateDialog(UpdateInfo info)
    {
        using (Form dlg = new Form())
        {
            dlg.Text = "发现新版本 v" + info.Version;
            dlg.ClientSize = new Size(480, 300);
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false;
            dlg.MinimizeBox = false;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.Font = Font;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Load += delegate { DarkTitle.Apply(dlg); };

            Label tip = new Label();
            tip.Text = "当前 v" + AppInfo.Version + "  →  最新 v" + info.Version + (info.Mandatory ? "（重要更新）" : "");
            tip.ForeColor = Theme.Text;
            tip.AutoSize = true;
            tip.Location = new Point(14, 12);

            TextBox notes = new TextBox();
            notes.Multiline = true;
            notes.ReadOnly = true;
            notes.ScrollBars = ScrollBars.Vertical;
            notes.Bounds = new Rectangle(14, 38, 452, 190);
            notes.BackColor = Color.FromArgb(20, 20, 20);
            notes.ForeColor = Color.FromArgb(200, 200, 200);
            notes.BorderStyle = BorderStyle.None;
            notes.Text = string.IsNullOrEmpty(info.Notes) ? "（该版本没有附更新说明）" : info.Notes;

            Button ok = new Button();
            ok.Text = "立即更新";
            ok.Bounds = new Rectangle(232, 244, 100, 30);
            ok.DialogResult = DialogResult.OK;
            Theme.Button(ok);

            Button page = new Button();
            page.Text = "打开发布页";
            page.Bounds = new Rectangle(340, 244, 126, 30);
            page.Click += delegate { OpenUrl(AppInfo.ReleasesUrl); };
            Theme.Button(page);

            Button cancel = new Button();
            cancel.Text = "稍后";
            cancel.Bounds = new Rectangle(140, 244, 84, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Theme.Button(cancel);

            dlg.Controls.Add(tip);
            dlg.Controls.Add(notes);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(page);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

            if (dlg.ShowDialog(this) == DialogResult.OK) DoUpdate(info);
        }
    }

    void DoUpdate(UpdateInfo info)
    {
        Log("开始下载 v" + info.Version + " …");
        ThreadPool.QueueUserWorkItem(delegate(object st)
        {
            string err;
            string file = UpdateCheck.Download(info, out err);
            if (file == null)
            {
                UI(delegate
                {
                    Log("下载失败：" + err);
                    MessageBox.Show("下载失败：" + err, "更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
                return;
            }
            UI(delegate
            {
                Log("下载完成并通过校验：" + file);
                if (MessageBox.Show("将关闭本程序，替换为 v" + info.Version + " 并重新启动。继续？", "更新",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                UpdateCheck.ApplyAndExit(file);
            });
        });
    }

    void Reload()
    {
        foreach (MonInfo m in monitors) Ddc.Destroy(m.Handle);
        monitors = Ddc.Enumerate();
        BuildCards();
        currentSig = Signature(monitors);
        header.Text = "共 " + monitors.Count + " 块显示器";
        Log("已枚举 " + monitors.Count + " 块显示器");
        if (monitors.Count == 0) Log("没有找到显示器");
        RefreshCycle(false, false);
    }

    // Cards are rebuilt only when the monitor set / layout / EDID actually changed,
    // so a periodic re-enumeration does not make the window flicker.
    static string Signature(List<MonInfo> list)
    {
        StringBuilder sb = new StringBuilder();
        foreach (MonInfo m in list)
        {
            sb.Append(m.Device).Append('|')
              .Append(m.Left).Append(',').Append(m.Top).Append(',').Append(m.Right).Append(',').Append(m.Bottom).Append('|')
              .Append(m.Vendor).Append('|').Append(m.EdidModel).Append(';');
        }
        return sb.ToString();
    }

    void BuildCards()
    {
        cards.SuspendLayout();
        List<Card> old = new List<Card>(cardList);
        cards.Controls.Clear();
        foreach (Card c in old) c.Dispose();
        lock (cardLock)
        {
            cardList.Clear();
            foreach (MonInfo m in monitors)
            {
                Card c = null;
                MonInfo mm = m;
                c = new Card(mm,
                    delegate { DoAction(c, 1); },
                    delegate { DoAction(c, 5); },
                    delegate { DoAction(c, 4); });
                cards.Controls.Add(c);
                cardList.Add(c);
            }
        }
        cards.ResumeLayout();
        UpdateWidths();
    }

    void ApplyMonitors(List<MonInfo> fresh)
    {
        foreach (MonInfo m in monitors) Ddc.Destroy(m.Handle);
        monitors = fresh;
        BuildCards();
        currentSig = Signature(monitors);
        header.Text = "共 " + monitors.Count + " 块显示器";
        Log("显示器列表已更新：共 " + monitors.Count + " 块");
        if (monitors.Count == 0) Log("没有找到显示器");
    }

    void UpdateWidths()
    {
        if (cards == null || cardList == null) return;   // called from OnResize during construction
        int w = cards.ClientSize.Width - 30;
        foreach (Card c in cardList) if (w > 320) c.Width = w;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateWidths(); }

    void SetBusy(bool busy)
    {
        acting = busy;
        btnAllOn.Enabled = !busy;
        btnAllOff.Enabled = !busy;
        btnBlank.Enabled = !busy;
        btnRefresh.Enabled = !busy;
        if (btnLock != null) btnLock.Enabled = !busy;
        foreach (Card c in cardList)
        {
            c.BOn.Enabled = !busy;
            c.BOff.Enabled = !busy;
            c.BSoft.Enabled = !busy;
        }
    }

    void DoAction(Card c, uint value)
    {
        if (acting) { Log("正在执行上一个操作，请稍候"); return; }
        SetBusy(true);
        c.SetState("", true);
        string name = value == 1 ? "开机" : (value == 5 ? "关机(电源键)" : "软关(DPMS)");
        Log("[" + c.M.Index + "] " + name + " → 写 D6=" + value);
        ThreadPool.QueueUserWorkItem(delegate(object state)
        {
            string r = Ddc.Write(c.M.Handle, value, 5000);
            bool fallback = (r != "OK");
            string after = fallback ? Ddc.Read(c.M.Handle, 3000)
                                    : Ddc.WaitState(c.M.Handle, value.ToString(), value == 1 ? 8 : 5);
            string note = "";
            if (fallback)
            {
                // nobody answers DDC/CI on this panel
                if (value == 1) { Ddc.NudgeInput(); note = "这块屏 DDC/CI 不通，已发送唤醒输入（不保证点亮）"; }
                else if (value == 4) { Ddc.ForceOff(); note = "这块屏 DDC/CI 不通，已改用系统级熄灭：所有显示器一起灭，动鼠标通常能亮回来"; }
                else { note = "这块屏 DDC/CI 不通，未执行关机（电源键语义不会退回系统级熄灭）"; }
            }
            else if (value == 4) Ddc.ForceOff();   // soft-off only sticks with the OS blank
            UI(delegate
            {
                c.SetState(after, false);
                c.SetDetail(fallback ? "DDC/CI 无应答" : "");
                Log("[" + c.M.Index + "] " + name + " → 写入 " + r + "，当前 " + Ddc.StateTextCn(after));
                if (note != "") Log("        " + note);
                SetBusy(false);
            });
        });
    }

    void RunAll(string name, uint value)
    {
        if (acting) { Log("正在执行上一个操作，请稍候"); return; }
        if (monitors.Count == 0) return;
        SetBusy(true);
        Log("全部显示器: " + name);
        ThreadPool.QueueUserWorkItem(delegate(object state)
        {
            if (value == 0) { Ddc.ForceOff(); UI(delegate { Log("系统熄灭已发送（动鼠标通常能亮回来；熄灭很久后按「开机」或物理电源键）"); SetBusy(false); RefreshCycle(false, true); }); return; }
            int failed = 0;
            foreach (Card c in cardList.ToArray())
            {
                string r = Ddc.Write(c.M.Handle, value, 5000);
                string after = r == "OK" ? Ddc.WaitState(c.M.Handle, value.ToString(), value == 1 ? 8 : 5) : Ddc.Read(c.M.Handle, 3000);
                if (r != "OK") failed++;
                Card cc = c; string rr = r; string aa = after;
                UI(delegate { cc.SetState(aa, false); cc.SetDetail(rr == "OK" ? "" : ("写入失败: " + rr)); });
                Log("[" + c.M.Index + "] " + name + " → 写入 " + r + "，当前 " + Ddc.StateTextCn(after));
            }
            // 电源键语义（D6=5）绝不退回 SC_MONITORPOWER：那属于软关，动鼠标就会亮
            if (value == 4) Ddc.ForceOff();
            if (failed > 0 && value == 1) Ddc.NudgeInput();
            UI(delegate
            {
                if (failed > 0 && value == 5)
                    Log("有 " + failed + " 块屏 DDC/CI 不通，未执行关机（不会退回系统级熄灭）；可在显示器 OSD 里打开 DDC/CI 后重试");
                else if (failed > 0 && value == 4)
                    Log("有 " + failed + " 块屏 DDC/CI 不通，已追加系统级熄灭（所有屏一起灭，动鼠标通常能亮回来）；可在显示器 OSD 里打开 DDC/CI 后重试");
                else if (failed > 0)
                    Log("有 " + failed + " 块屏 DDC/CI 不通，已发送唤醒输入；可在显示器 OSD 里打开 DDC/CI 后重试");
                SetBusy(false);
                RefreshCycle(false, true);
            });
        });
    }

    // One click: lock the session, then (after 1s) switch the panels off with the
    // power-button semantic (D6=5). Deliberately NOT D6=4 + SC_MONITORPOWER - that
    // is a soft off that the next mouse move undoes.
    void LockAndBlank()
    {
        if (acting) { Log("正在执行上一个操作，请稍候"); return; }
        SetBusy(true);
        bool locked = Ddc.LockDesktop();
        Log((locked ? "已锁定桌面" : "锁定桌面失败（可能不是交互式会话）") + "，1 秒后关闭显示器电源…");
        ThreadPool.QueueUserWorkItem(delegate(object state)
        {
            Thread.Sleep(1000);
            Card[] snap;
            lock (cardLock) { snap = cardList.ToArray(); }
            int failed = 0;
            foreach (Card c in snap)
            {
                string r = Ddc.Write(c.M.Handle, 5, 5000);
                if (r != "OK") failed++;
            }
            // no SC_MONITORPOWER fallback: a soft off is undone by the next input
            UI(delegate
            {
                Log("显示器已按电源键语义关闭" + (failed > 0 ? "（" + failed + " 块屏 DDC/CI 不通，未处理）" : "") +
                    "；唤醒请按显示器电源键，或解锁后运行 monitor-power.exe poweron");
                SetBusy(false);
                RefreshCycle(false, true);
            });
        });
    }

    // reenumerate=true: also re-scan the monitors (picks up a re-plugged cable, a
    // swapped port, a new/removed display) before reading the power state.
    void RefreshCycle(bool reenumerate, bool verbose)
    {
        if (polling || acting) return;
        polling = true;
        ThreadPool.QueueUserWorkItem(delegate(object state)
        {
            try
            {
                if (reenumerate)
                {
                    List<MonInfo> fresh = Ddc.Enumerate();
                    string sig = Signature(fresh);
                    if (sig != currentSig)
                    {
                        // debounce: a new monitor set only replaces the cards after it
                        // survived two scans, so a momentarily attached virtual display
                        // (or a re-plug blip) does not rebuild the whole window
                        if (sig == pendingSig) pendingCount++;
                        else { pendingSig = sig; pendingCount = 1; }
                        if (pendingCount >= 2)
                        {
                            List<MonInfo> give = fresh;
                            InvokeSync(delegate { ApplyMonitors(give); });
                            pendingSig = null;
                            pendingCount = 0;
                        }
                        else
                        {
                            foreach (MonInfo m in fresh) Ddc.Destroy(m.Handle);
                        }
                    }
                    else
                    {
                        pendingSig = null;
                        pendingCount = 0;
                        foreach (MonInfo m in fresh) Ddc.Destroy(m.Handle);
                    }
                }
                Card[] snapshot;
                lock (cardLock) { snapshot = cardList.ToArray(); }
                foreach (Card c in snapshot)
                {
                    string raw = Ddc.Read(c.M.Handle, 2500);
                    Card cc = c; string rr = raw;
                    InvokeSync(delegate
                    {
                        bool ok = Ddc.IsNumeric(rr);
                        if (ok) { cc.LastGood = rr; cc.FailStreak = 0; }
                        else cc.FailStreak++;
                        // keep showing the last good state for the first couple of misses;
                        // DDC occasionally drops a single reply, that is not "unreachable"
                        bool unknown = !ok && (cc.FailStreak >= 3 || cc.LastGood == null);
                        string show = unknown ? rr : (ok ? rr : cc.LastGood);
                        cc.SetState(show, false);
                        cc.SetDetail(unknown ? ("DDC/CI: " + rr) : "");
                        if (unknown && !cc.Hinted)
                        {
                            cc.Hinted = true;
                            Log("[" + cc.M.Index + "] " + cc.M.Model + " 的 DDC/CI 无应答：无法精确开关这块屏，按钮会退化为「系统级熄灭 / 唤醒输入」。常见原因是走 VGA/转接头（多数只转发 EDID、不转发 DDC/CI），或 OSD 里关了 DDC/CI；改接 HDMI/DP/DVI 直连后点刷新即可。");
                        }
                    });
                }
                if (verbose) Log(reenumerate ? "已刷新：重新枚举显示器 + 读取状态" : "状态已刷新");
            }
            catch (Exception ex) { Entry.CrashLog(ex, "RefreshCycle"); }
            finally { polling = false; }
        });
    }

    void InvokeSync(MethodInvoker act)
    {
        if (IsDisposed) return;
        try { if (InvokeRequired) Invoke(act); else act(); }
        catch (Exception) { }
    }

    void UI(MethodInvoker act)
    {
        if (IsDisposed) return;
        try { if (InvokeRequired) BeginInvoke(act); else act(); }
        catch (Exception) { }
    }
}

// ---------------------------------------------------------------- board (dashboard)

class BoardRow : Panel
{
    public MonInfo M;
    public Label Dot;
    public Label NameLabel;
    public Label State;
    public int FailStreak;
    public string LastGood;

    public BoardRow(MonInfo m)
    {
        M = m;
        Height = 26;
        BackColor = Theme.Bg2;

        Dot = new Label();
        Dot.Text = "●";
        Dot.AutoSize = true;
        Dot.Font = new Font("Microsoft YaHei UI", 9F);
        Dot.Location = new Point(4, 4);
        Dot.ForeColor = Theme.Dim;

        NameLabel = new Label();
        NameLabel.Text = m.Model;
        NameLabel.AutoSize = false;
        NameLabel.Height = 18;
        NameLabel.Location = new Point(20, 5);
        NameLabel.ForeColor = Theme.Text;
        NameLabel.AutoEllipsis = true;

        State = new Label();
        State.AutoSize = false;
        State.Height = 18;
        State.Width = 56;
        State.TextAlign = ContentAlignment.MiddleRight;
        State.Location = new Point(150, 5);
        State.ForeColor = Theme.Dim;
        State.Font = new Font("Microsoft YaHei UI", 8.5F);
        State.Text = "…";

        Controls.Add(Dot);
        Controls.Add(NameLabel);
        Controls.Add(State);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (State == null || NameLabel == null) return;
        State.Left = Width - State.Width - 6;
        NameLabel.Width = Math.Max(30, State.Left - NameLabel.Left - 4);
    }
}

class BoardForm : Form
{
    static readonly int[] INTERVALS = new int[] { 1, 2, 5, 10 };

    FlowLayoutPanel rowsPanel;
    FlowLayoutPanel checksPanel;
    Panel settingsPanel;
    Panel bar;
    ComboBox intervalBox;
    CheckBox topBox;
    Button btnPick, btnClose;
    System.Windows.Forms.Timer timer;
    List<MonInfo> mons = new List<MonInfo>();
    List<BoardRow> rows = new List<BoardRow>();
    HashSet<string> shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    bool busy;
    string cfgPath;

    public BoardForm()
    {
        Text = "显示器看板";
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        ClientSize = new Size(258, 128);
        MinimumSize = new Size(220, 90);
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(80, 80);

        cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               "monitor-power", "board.ini");

        bar = new Panel();
        bar.Dock = DockStyle.Bottom;
        bar.Height = 26;
        bar.BackColor = Color.FromArgb(24, 24, 24);

        intervalBox = new ComboBox();
        intervalBox.DropDownStyle = ComboBoxStyle.DropDownList;
        intervalBox.Items.AddRange(new object[] { "1s", "2s", "5s", "10s" });
        intervalBox.SelectedIndex = 1;
        intervalBox.Bounds = new Rectangle(4, 2, 50, 22);
        intervalBox.SelectedIndexChanged += delegate { ApplyInterval(); };
        intervalBox.FlatStyle = FlatStyle.Flat;
        intervalBox.BackColor = Theme.Bg3;
        intervalBox.ForeColor = Theme.Text;
        bar.Controls.Add(intervalBox);

        topBox = new CheckBox();
        topBox.Text = "顶";
        topBox.AutoSize = true;
        topBox.ForeColor = Theme.Dim;
        topBox.Checked = true;
        topBox.Location = new Point(58, 4);
        topBox.CheckedChanged += delegate { TopMost = topBox.Checked; SaveConfig(); };
        bar.Controls.Add(topBox);

        btnPick = new Button();
        btnPick.Text = "屏";
        btnPick.Bounds = new Rectangle(92, 2, 30, 22);
        Theme.Button(btnPick);
        btnPick.Click += delegate { ToggleSettings(); };
        bar.Controls.Add(btnPick);

        btnClose = new Button();
        btnClose.Text = "×";
        btnClose.Bounds = new Rectangle(126, 2, 24, 22);
        Theme.Button(btnClose);
        btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnClose.Click += delegate { Close(); };
        bar.Controls.Add(btnClose);
        bar.Resize += delegate { btnClose.Left = bar.Width - btnClose.Width - 4; };

        settingsPanel = new Panel();
        settingsPanel.Dock = DockStyle.Top;
        settingsPanel.Height = 0;
        settingsPanel.Visible = false;
        settingsPanel.BackColor = Theme.Bg2;

        checksPanel = new FlowLayoutPanel();
        checksPanel.Dock = DockStyle.Fill;
        checksPanel.FlowDirection = FlowDirection.TopDown;
        checksPanel.WrapContents = false;
        checksPanel.AutoScroll = true;
        checksPanel.Padding = new Padding(8, 4, 8, 4);
        checksPanel.BackColor = Theme.Bg2;
        settingsPanel.Controls.Add(checksPanel);

        rowsPanel = new FlowLayoutPanel();
        rowsPanel.Dock = DockStyle.Fill;
        rowsPanel.FlowDirection = FlowDirection.TopDown;
        rowsPanel.WrapContents = false;
        rowsPanel.AutoScroll = true;
        rowsPanel.BackColor = Theme.Bg;
        rowsPanel.Padding = new Padding(4);

        Controls.Add(rowsPanel);
        Controls.Add(settingsPanel);
        Controls.Add(bar);

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 2000;
        timer.Tick += delegate { ReadStates(); };

        Load += delegate
        {
            DarkTitle.Apply(this);
            LoadConfig();
            Rebuild();
            timer.Start();
        };
        FormClosing += delegate
        {
            timer.Stop();
            SaveConfig();
            foreach (MonInfo m in mons) Ddc.Destroy(m.Handle);
        };
        rowsPanel.Resize += delegate
        {
            foreach (BoardRow r in rows) r.Width = Math.Max(160, rowsPanel.ClientSize.Width - 12);
        };
    }

    void ToggleSettings()
    {
        settingsPanel.Visible = !settingsPanel.Visible;
        settingsPanel.Height = settingsPanel.Visible ? 104 : 0;
    }

    void ApplyInterval()
    {
        int idx = intervalBox.SelectedIndex;
        if (idx < 0 || idx >= INTERVALS.Length) idx = 1;
        timer.Interval = INTERVALS[idx] * 1000;
        SaveConfig();
    }

    void Rebuild()
    {
        foreach (MonInfo m in mons) Ddc.Destroy(m.Handle);
        mons = Ddc.Enumerate();
        checksPanel.Controls.Clear();
        foreach (MonInfo m in mons)
        {
            CheckBox cb = new CheckBox();
            cb.Text = m.Model + "   " + (m.Device ?? "");
            cb.AutoSize = true;
            cb.ForeColor = Theme.Text;
            cb.BackColor = Theme.Bg2;
            cb.Tag = m;
            cb.Checked = shown.Count == 0 || shown.Contains(m.Device ?? "");
            cb.CheckedChanged += delegate { Recollect(); };
            checksPanel.Controls.Add(cb);
        }
        Recollect();
    }

    void Recollect()
    {
        shown.Clear();
        rows.Clear();
        rowsPanel.Controls.Clear();
        foreach (Control c in checksPanel.Controls)
        {
            CheckBox cb = c as CheckBox;
            if (cb == null) continue;
            MonInfo m = (MonInfo)cb.Tag;
            if (!cb.Checked) continue;
            shown.Add(m.Device ?? "");
            BoardRow r = new BoardRow(m);
            r.Width = Math.Max(160, rowsPanel.ClientSize.Width - 12);
            rows.Add(r);
            rowsPanel.Controls.Add(r);
        }
        SaveConfig();
        ReadStates();
    }

    void ReadStates()
    {
        if (busy) return;
        busy = true;
        BoardRow[] snap = rows.ToArray();
        ThreadPool.QueueUserWorkItem(delegate(object st)
        {
            try
            {
                foreach (BoardRow r in snap)
                {
                    string raw = Ddc.Read(r.M.Handle, 2000);
                    BoardRow rr = r;
                    string v = raw;
                    try
                    {
                        if (!IsDisposed) Invoke((MethodInvoker)delegate { UpdateRow(rr, v); });
                    }
                    catch { }
                }
            }
            finally { busy = false; }
        });
    }

    void UpdateRow(BoardRow r, string raw)
    {
        if (r.IsDisposed) return;
        bool ok = Ddc.IsNumeric(raw);
        if (ok) { r.LastGood = raw; r.FailStreak = 0; }
        else r.FailStreak++;
        bool unknown = !ok && (r.FailStreak >= 3 || r.LastGood == null);
        string v = unknown ? raw : (ok ? raw : r.LastGood);
        if (v == "1") { r.Dot.ForeColor = Theme.Green; r.State.ForeColor = Theme.Green; r.State.Text = "开"; }
        else if (v == "4") { r.Dot.ForeColor = Theme.Blue; r.State.ForeColor = Theme.Blue; r.State.Text = "软关"; }
        else if (v == "5") { r.Dot.ForeColor = Theme.Dim; r.State.ForeColor = Theme.Dim; r.State.Text = "关"; }
        else { r.Dot.ForeColor = Theme.Red; r.State.ForeColor = Theme.Red; r.State.Text = "读不到"; }
    }

    void LoadConfig()
    {
        try
        {
            if (!File.Exists(cfgPath)) return;
            foreach (string line in File.ReadAllLines(cfgPath, Encoding.UTF8))
            {
                string[] kv = line.Split(new char[] { '=' }, 2);
                if (kv.Length != 2) continue;
                string k = kv[0].Trim();
                string v = kv[1].Trim();
                if (k == "interval")
                {
                    int sec;
                    if (int.TryParse(v, out sec))
                    {
                        int idx = Array.IndexOf(INTERVALS, sec);
                        if (idx >= 0) intervalBox.SelectedIndex = idx;
                    }
                }
                else if (k == "topmost") topBox.Checked = (v == "1");
                else if (k == "show")
                {
                    shown.Clear();
                    foreach (string d in v.Split(';')) if (d.Trim().Length > 0) shown.Add(d.Trim());
                }
            }
        }
        catch { }
    }

    void SaveConfig()
    {
        try
        {
            string dir = Path.GetDirectoryName(cfgPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            int idx = intervalBox.SelectedIndex;
            int sec = (idx >= 0 && idx < INTERVALS.Length) ? INTERVALS[idx] : 2;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("interval=" + sec);
            sb.AppendLine("topmost=" + (topBox.Checked ? "1" : "0"));
            sb.AppendLine("show=" + string.Join(";", new List<string>(shown).ToArray()));
            File.WriteAllText(cfgPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}

// ---------------------------------------------------------------- CLI + entry

static class Cli
{


    public static void Usage()
    {
        Console.WriteLine("monitor-power " + AppInfo.Version + " - DDC/CI monitor power controller (VCP 0xD6)");
        Console.WriteLine();
        Console.WriteLine("  no arguments          open the GUI controller");
        Console.WriteLine();
        Console.WriteLine("usage: monitor-power.exe [command] [options]");
        Console.WriteLine();
        Console.WriteLine("commands:");
        Console.WriteLine("  status              show every monitor and its power state (default)");
        Console.WriteLine("  list                show every monitor (no DDC read)");
        Console.WriteLine("  poweron   | on      D6=1 On        - same as pressing the power button on");
        Console.WriteLine("  poweroff            D6=5 Off(hard) - same as pressing the power button off");
        Console.WriteLine("  powercycle          poweroff, wait --wait seconds, poweron, verify");
        Console.WriteLine("  off                 D6=4 Off(soft) + SC_MONITORPOWER; any input wakes it");
        Console.WriteLine("  offddc              D6=4 only (no OS-level blank)");
        Console.WriteLine("  forceoff            only the OS-level blank (SC_MONITORPOWER)");
        Console.WriteLine("  lockoff             lock the desktop, wait 1s, then switch the displays off (D6=5)");
        Console.WriteLine("  help                this text");
        Console.WriteLine();
        Console.WriteLine("options: --index 0,1  --wait N  --json  --force  --no-force  --version  --check-update");
        Console.WriteLine("states: 1=On 2=Standby 3=Suspend 4=Off(soft) 5=Off(hard)");
        Console.WriteLine("exit codes: 0 all good, 1 partial/failed, 2 usage error");
    }

    public static int UsageError(string msg)
    {
        if (msg != null) Console.WriteLine("error: " + msg);
        Usage();
        return 2;
    }

    public static int Run(string[] args)
    {
        string cmd = "status";
        List<int> index = new List<int>();
        int waitSec = 5;
        bool force = false, noForce = false, json = false;

        for (int i = 0; i < args.Length; i++)
        {
            string al = args[i].ToLowerInvariant();
            if (al == "--index" || al == "-index" || al == "/index")
            {
                if (i + 1 >= args.Length) return UsageError("--index needs a value, e.g. --index 0,1");
                string[] parts = args[++i].Split(',');
                foreach (string p in parts)
                {
                    string t = p.Trim();
                    if (t.Length == 0) continue;
                    int v;
                    if (!int.TryParse(t, out v) || v < 0) return UsageError("bad index: " + t);
                    index.Add(v);
                }
            }
            else if (al == "--wait" || al == "-wait" || al == "/wait")
            {
                if (i + 1 >= args.Length) return UsageError("--wait needs a number of seconds");
                if (!int.TryParse(args[++i], out waitSec) || waitSec < 0) return UsageError("bad --wait value");
            }
            else if (al == "--force" || al == "-force") force = true;
            else if (al == "--no-force" || al == "-no-force") noForce = true;
            else if (al == "--json" || al == "-json") json = true;
            else if (al == "--version" || al == "-version" || al == "/version") { Console.WriteLine("monitor-power " + AppInfo.Version); return 0; }
            else if (al == "--check-update") cmd = "check-update";
            else if (al == "--help" || al == "-h" || al == "/?") cmd = "help";
            else if (al.StartsWith("-")) return UsageError("unknown option: " + args[i]);
            else cmd = al;
        }

        if (cmd == "help") { Usage(); return 0; }
        if (cmd == "check-update") return CheckUpdateCli();

        List<MonInfo> mons = Ddc.Enumerate();
        if (mons.Count == 0) { Console.WriteLine("no physical monitors found"); return 1; }

        if (index.Count > 0)
        {
            List<MonInfo> pick = new List<MonInfo>();
            foreach (int i in index)
            {
                if (i < 0 || i >= mons.Count) return UsageError("no monitor with index " + i);
                pick.Add(mons[i]);
            }
            for (int i = 0; i < pick.Count; i++) pick[i].Index = i;
            mons = pick;
        }

        switch (cmd)
        {
            case "status": return Status(mons, json);
            case "list":
                for (int i = 0; i < mons.Count; i++)
                    Console.WriteLine("  [" + i + "] " + Info(mons[i]));
                return 0;
            case "on":
            case "poweron": return Write(mons, 1, force);
            case "off": return Write(mons, 4, !noForce);
            case "offddc": return Write(mons, 4, false);
            case "offhard":
            case "poweroff": return Write(mons, 5, false);
            case "forceoff": Ddc.ForceOff(); Console.WriteLine("SC_MONITORPOWER sent to all displays (wakes on next input)."); return 0;
            case "lockoff": return LockOff(mons);
            case "powercycle": return Cycle(mons, waitSec);
            default: return UsageError("unknown command: " + cmd);
        }
    }

    static string Info(MonInfo m)
    {
        string s = "idx=" + m.Index + " rect=" + m.RectText + " dev=" + (m.Device ?? "-") +
                   " vendor=" + (m.Vendor ?? "-") + " model=" + m.Model;
        if (!string.IsNullOrEmpty(m.EdidModel) && m.EdidModel != m.Model) s += " edid=" + m.EdidModel;
        return s;
    }

    static int CheckUpdateCli()
    {
        Console.WriteLine("current: " + AppInfo.Version);
        UpdateInfo info = null;
        try { info = UpdateCheck.Fetch(); }
        catch (Exception ex) { Console.WriteLine("check failed: " + ex.Message); return 2; }
        if (info == null) { Console.WriteLine("check failed: bad manifest"); return 2; }
        Console.WriteLine("latest:  " + info.Version);
        if (UpdateCheck.CompareVersions(info.Version, AppInfo.Version) > 0)
        {
            Console.WriteLine("update available: " + info.Url);
            return 1;
        }
        Console.WriteLine("up to date");
        return 0;
    }

    static int Status(List<MonInfo> mons, bool json)
    {
        if (json)
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < mons.Count; i++)
            {
                string raw = Ddc.Read(mons[i].Handle, 3000);
                if (i > 0) sb.Append(",");
                sb.Append("{\"index\":").Append(i)
                  .Append(",\"model\":\"").Append(mons[i].Model.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"")
                  .Append(",\"vendor\":\"").Append(mons[i].Vendor ?? "").Append("\"")
                  .Append(",\"device\":\"").Append((mons[i].Device ?? "").Replace("\\", "\\\\")).Append("\"")
                  .Append(",\"rect\":[").Append(mons[i].Left).Append(",").Append(mons[i].Top).Append(",")
                  .Append(mons[i].Right).Append(",").Append(mons[i].Bottom).Append("]")
                  .Append(",\"vcp_d6\":\"").Append(Ddc.IsNumeric(raw) ? raw : "").Append("\"")
                  .Append(",\"state\":\"").Append(Ddc.StateName(raw)).Append("\"")
                  .Append(",\"readable\":").Append(Ddc.IsNumeric(raw) ? "true" : "false").Append("}");
            }
            sb.Append("]");
            Console.WriteLine(sb.ToString());
            return 0;
        }
        Console.WriteLine("monitors: " + mons.Count);
        for (int i = 0; i < mons.Count; i++)
        {
            string raw = Ddc.Read(mons[i].Handle, 3000);
            Console.WriteLine("  [" + i + "] " + Info(mons[i]) + "  D6=" + raw + " (" + Ddc.StateName(raw) + ")");
        }
        return 0;
    }

    static int Write(List<MonInfo> mons, uint value, bool blank)
    {
        int failed = 0;
        for (int i = 0; i < mons.Count; i++)
        {
            string r = Ddc.Write(mons[i].Handle, value, 5000);
            string after = r == "OK" ? Ddc.WaitState(mons[i].Handle, value.ToString(), value == 1 ? 8 : 4) : Ddc.Read(mons[i].Handle, 3000);
            Console.WriteLine("  [" + i + "] " + Info(mons[i]) +
                              "  set D6=" + value + " -> " + r + "  now D6=" + after + " (" + Ddc.StateName(after) + ")");
            if (r != "OK") failed++;
        }
        if (blank) { Ddc.ForceOff(); Console.WriteLine("  SC_MONITORPOWER sent (OS-level blank of all displays; wakes on next input)."); }
        if (failed > 0)
        {
            Console.WriteLine("  note: " + failed + " monitor(s) did not answer DDC/CI - they cannot be controlled from software" +
                              " (DDC/CI may be off in the monitor OSD, or the panel is already powered down).");
            return blank ? 0 : 1;
        }
        return 0;
    }

    static int LockOff(List<MonInfo> mons)
    {
        bool locked = Ddc.LockDesktop();
        Console.WriteLine(locked ? "desktop locked" : "LockWorkStation failed (not an interactive session?)");
        Thread.Sleep(1000);
        int failed = 0;
        for (int i = 0; i < mons.Count; i++)
        {
            string r = Ddc.Write(mons[i].Handle, 5, 5000);   // power-button semantic, no soft off
            if (r != "OK") failed++;
        }
        Console.WriteLine("displays off (D6=5); press the monitor power button or run 'poweron' to wake");
        if (failed > 0)
        {
            Console.WriteLine("  note: " + failed + " monitor(s) did not answer DDC/CI - left alone (no SC_MONITORPOWER fallback)");
            return 1;
        }
        return 0;
    }

    static int Cycle(List<MonInfo> mons, int waitSec)
    {
        int bad = 0;
        for (int i = 0; i < mons.Count; i++)
        {
            Console.WriteLine("  [" + i + "] " + Info(mons[i]));
            string r1 = Ddc.Write(mons[i].Handle, 5, 5000);
            if (r1 != "OK")
            {
                Console.WriteLine("      poweroff D6=5 -> " + r1 + "   panel does not answer DDC/CI - cannot control it from software");
                bad++;
                continue;
            }
            string mid = Ddc.WaitState(mons[i].Handle, "5", 4);
            Console.WriteLine("      poweroff D6=5 -> OK   state D6=" + mid + " (" + Ddc.StateName(mid) + ")");
            if (mid != "5")
            {
                Console.WriteLine("      DDC/CI stopped answering while hard-off - bring it back with the physical power button.");
                bad++;
                continue;
            }
            Thread.Sleep(Math.Max(0, waitSec) * 1000);
            string r2 = Ddc.Write(mons[i].Handle, 1, 5000);
            string after = r2 == "OK" ? Ddc.WaitState(mons[i].Handle, "1", 8) : Ddc.Read(mons[i].Handle, 3000);
            Console.WriteLine("      poweron  D6=1 -> " + r2 + "   state D6=" + after + " (" + Ddc.StateName(after) + ")");
            if (after != "1") bad++;
        }
        if (bad > 0) { Console.WriteLine("  note: " + bad + " monitor(s) did not complete the round trip."); return 1; }
        Console.WriteLine("  power cycle complete.");
        return 0;
    }
}

static class Entry
{
    public static void CrashLog(Exception ex, string where)
    {
        try
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "monitor-power-error.log");
            System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
        }
        catch { }
    }

    static int ApplyUpdate(string pidText, string dst)
    {
        int pid;
        int.TryParse(pidText, out pid);
        if (pid > 0)
        {
            try { Process p = Process.GetProcessById(pid); p.WaitForExit(60000); } catch { }
        }
        string self = Application.ExecutablePath;
        for (int i = 0; i < 120; i++)
        {
            try
            {
                File.Copy(self, dst, true);
                Process.Start(dst);
                return 0;
            }
            catch { Thread.Sleep(500); }
        }
        return 1;
    }

    [STAThread]
    static int Main(string[] args)
    {
        // Self-update helper: this copy runs from %TEMP%, waits for the old process to
        // exit, then overwrites it and starts the new one.
        if (args.Length >= 3 && args[0] == "--apply-update") return ApplyUpdate(args[1], args[2]);

        if (args.Length == 0)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { CrashLog(e.Exception, "ui-thread"); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { CrashLog(e.ExceptionObject as Exception, "appdomain"); };
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                CrashLog(ex, "Application.Run");
                throw;
            }
            return 0;
        }

        Ddc.AttachToParentConsole();      // winexe: CLI output goes to the calling console
        try
        {
            return Cli.Run(args);
        }
        finally
        {
            Console.Out.Flush();
        }
    }
}
