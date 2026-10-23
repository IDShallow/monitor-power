// monitor-power.exe - DDC/CI monitor power controller (VCP 0xD6)
//   no arguments  -> GUI (clickable per-monitor power buttons)
//   with arguments -> same command line interface as before
// Build: src\build.cmd   (Framework csc, C# 5 / .NET 4.x, /target:winexe /codepage:65001)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Monitor Power")]
[assembly: AssemblyDescription("DDC/CI monitor power controller (GUI + CLI)")]
[assembly: AssemblyProduct("monitor-power")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

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

    static List<MonInfo> mons = new List<MonInfo>();
    static MonitorEnumProc keepAlive;

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
            m.Index = mons.Count;
            mons.Add(m);
        }
        return true;
    }

    public static List<MonInfo> Enumerate()
    {
        mons = new List<MonInfo>();
        keepAlive = new MonitorEnumProc(OnMonitor);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, keepAlive, IntPtr.Zero);
        return mons;
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

    public Card(MonInfo m, EventHandler onOn, EventHandler onOff, EventHandler onSoft)
    {
        M = m;
        Height = 96;
        Margin = new Padding(6);
        BackColor = Color.WhiteSmoke;
        BorderStyle = BorderStyle.FixedSingle;

        Label title = new Label();
        title.Text = "[" + m.Index + "] " + m.Model;
        title.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        title.AutoSize = true;
        title.Location = new Point(14, 10);

        DetailLabel = new Label();
        DetailLabel.Text = BaseDetail(m);
        DetailLabel.AutoSize = false;
        DetailLabel.Height = 18;
        DetailLabel.AutoEllipsis = true;
        DetailLabel.ForeColor = Color.DimGray;
        DetailLabel.Location = new Point(14, 36);

        StateLabel = new Label();
        StateLabel.Text = "状态: 读取中…";
        StateLabel.AutoSize = true;
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
        if (busy) { StateLabel.Text = "状态: 执行中…"; StateLabel.ForeColor = Color.DarkOrange; return; }
        StateLabel.Text = "状态: " + Ddc.StateTextCn(raw);
        if (raw == "1") StateLabel.ForeColor = Color.SeaGreen;
        else if (raw == "5") StateLabel.ForeColor = Color.DimGray;
        else if (raw == "4") StateLabel.ForeColor = Color.SteelBlue;
        else if (Ddc.IsNumeric(raw)) StateLabel.ForeColor = Color.SteelBlue;
        else StateLabel.ForeColor = Color.Firebrick;
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
    Button btnRefresh, btnAllOn, btnAllOff, btnBlank, btnLock;
    System.Windows.Forms.Timer pollTimer;
    List<MonInfo> monitors = new List<MonInfo>();
    List<Card> cardList = new List<Card>();
    readonly object cardLock = new object();
    string currentSig = "";
    int tick;
    bool polling;
    bool acting;

    public MainForm()
    {
        Text = "显示器电源控制器 (DDC/CI VCP 0xD6)";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(780, 560);
        MinimumSize = new Size(700, 420);
        StartPosition = FormStartPosition.CenterScreen;

        Panel top = new Panel();
        top.Dock = DockStyle.Top;
        top.Height = 52;
        top.Padding = new Padding(12, 10, 12, 10);

        header = new Label();
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
        cards.BackColor = Color.White;

        logBox = new TextBox();
        logBox.Dock = DockStyle.Bottom;
        logBox.Height = 120;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.BackColor = Color.White;
        logBox.Font = new Font("Consolas", 9F);
        logBox.BorderStyle = BorderStyle.FixedSingle;

        Controls.Add(cards);
        Controls.Add(logBox);
        Controls.Add(top);

        pollTimer = new System.Windows.Forms.Timer();
        pollTimer.Interval = 4000;
        pollTimer.Tick += delegate { tick++; RefreshCycle(tick % 3 == 0, false); };   // every 3rd tick = full re-enumeration (12s)

        Load += delegate
        {
            try { Reload(); }
            catch (Exception ex) { Entry.CrashLog(ex, "Load/Reload"); MessageBox.Show(ex.ToString(), "monitor-power"); }
            pollTimer.Start();
        };
        FormClosing += delegate { pollTimer.Stop(); foreach (MonInfo m in monitors) Ddc.Destroy(m.Handle); };
    }

    static Button ActionButton(string text, EventHandler h)
    {
        Button b = new Button();
        b.Text = text;
        b.Width = 92;
        b.Height = 30;
        b.Margin = new Padding(6, 0, 0, 0);
        b.Click += h;
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
                // nobody answers DDC/CI on this panel: fall back to the OS-level paths
                if (value == 1) { Ddc.NudgeInput(); note = "这块屏 DDC/CI 不通，已发送唤醒输入（不保证点亮）"; }
                else { Ddc.ForceOff(); note = "这块屏 DDC/CI 不通，已改用系统级熄灭：所有显示器一起灭，动鼠标通常能亮回来；若熄灭很久后不亮，用「开机」或物理电源键"; }
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
            if (value == 4 || (failed > 0 && value != 1)) Ddc.ForceOff();
            if (failed > 0 && value == 1) Ddc.NudgeInput();
            UI(delegate
            {
                if (failed > 0 && value != 1)
                    Log("有 " + failed + " 块屏 DDC/CI 不通，已追加系统级熄灭（所有屏一起灭，动鼠标通常能亮回来；熄灭很久后可能要用「开机」或物理电源键）；可在显示器 OSD 里打开 DDC/CI 后重试");
                else if (failed > 0)
                    Log("有 " + failed + " 块屏 DDC/CI 不通，已发送唤醒输入；可在显示器 OSD 里打开 DDC/CI 后重试");
                SetBusy(false);
                RefreshCycle(false, true);
            });
        });
    }

    // One click: lock the session, then (after 1s) blank the panels.
    // D6=4 + the OS-level blank is used on purpose instead of D6=5: a hard-off panel
    // does not come back on the next key press, and the user has to type a password
    // to unlock - they would be staring at a dark screen.
    void LockAndBlank()
    {
        if (acting) { Log("正在执行上一个操作，请稍候"); return; }
        SetBusy(true);
        bool locked = Ddc.LockDesktop();
        Log((locked ? "已锁定桌面" : "锁定桌面失败（可能不是交互式会话）") + "，1 秒后关闭显示器…");
        ThreadPool.QueueUserWorkItem(delegate(object state)
        {
            Thread.Sleep(1000);
            Card[] snap;
            lock (cardLock) { snap = cardList.ToArray(); }
            int failed = 0;
            foreach (Card c in snap)
            {
                string r = Ddc.Write(c.M.Handle, 4, 5000);
                if (r != "OK") failed++;
            }
            Ddc.ForceOff();
            UI(delegate
            {
                Log("显示器已关闭" + (failed > 0 ? "（" + failed + " 块屏 DDC 不通，已用系统级熄灭兜底）" : "") + "；回来动鼠标或按键即可唤醒");
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
                    if (Signature(fresh) != currentSig)
                    {
                        List<MonInfo> give = fresh;
                        InvokeSync(delegate { ApplyMonitors(give); });
                    }
                    else
                    {
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
                        cc.SetState(rr, false);
                        cc.SetDetail(Ddc.IsNumeric(rr) ? "" : ("DDC/CI: " + rr));
                        if (!cc.Hinted && !Ddc.IsNumeric(rr))
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

// ---------------------------------------------------------------- CLI + entry

static class Cli
{
    const string VERSION = "1.0.0";

    public static void Usage()
    {
        Console.WriteLine("monitor-power " + VERSION + " - DDC/CI monitor power controller (VCP 0xD6)");
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
        Console.WriteLine("  lockoff             lock the desktop, wait 1s, then blank the displays");
        Console.WriteLine("  help                this text");
        Console.WriteLine();
        Console.WriteLine("options: --index 0,1  --wait N  --json  --force  --no-force  --version");
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
            else if (al == "--version" || al == "-version" || al == "/version") { Console.WriteLine("monitor-power " + VERSION); return 0; }
            else if (al == "--help" || al == "-h" || al == "/?") cmd = "help";
            else if (al.StartsWith("-")) return UsageError("unknown option: " + args[i]);
            else cmd = al;
        }

        if (cmd == "help") { Usage(); return 0; }

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
            string r = Ddc.Write(mons[i].Handle, 4, 5000);
            if (r != "OK") failed++;
        }
        Ddc.ForceOff();
        Console.WriteLine("displays off (D6=4 + SC_MONITORPOWER); the next mouse/keyboard input wakes them");
        if (failed > 0)
        {
            Console.WriteLine("  note: " + failed + " monitor(s) did not answer DDC/CI - covered by the OS-level blank");
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

    [STAThread]
    static int Main(string[] args)
    {
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
