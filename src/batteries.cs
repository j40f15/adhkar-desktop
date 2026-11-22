// Battery levels of wireless gear, read directly (no vendor software needed):
//   * Logitech mice / keyboards / headsets (HID++ 2.0 through the Lightspeed / Unifying receiver)
//   * PlayStation controllers: DualSense / DualSense Edge (PS5) and DualShock 4 (PS4), USB or Bluetooth
//   * any Bluetooth device that reports its battery to Windows (headphones, BLE mice / keyboards)
// Read-only status queries, every I/O call has a hard timeout (see hidio.cs).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public class BatteryInfo {
  public string Name;        // full name (tray menu, alerts)
  public string Short { get { return Batteries.ShortName(Name); } }   // label above the taskbar: PRO, PS5, Xbox...
  public string Kind;        // mouse, keyboard, headset, controller, device
  public int Percent;        // 0..100, or Connected (-2) = plugged in / online but it does not report a level
  public bool Charging;
  public bool Approx;        // the device only reports steps (Xbox, Switch, PlayStation...): shown with ≈
  public string Key;         // stable id for "low battery" alerts
}

public static class Batteries {
  public const int Connected = -2;
  [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
  [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
  [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
  [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS {
    public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
    public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                  NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps,
                  NumberFeatureValueCaps, NumberFeatureDataIndices;
  }
  [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }
  [StructLayout(LayoutKind.Sequential)] struct HIDP_VALUE_CAPS {
    public ushort UsagePage; public byte ReportID; public byte IsAlias;
    public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
    public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
    public ushort BitSize, ReportCount, R0, R1, R2, R3, R4;
    public uint UnitsExp, Units;
    public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
    public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;   // NotRange.Usage = UsageMin
  }
  [StructLayout(LayoutKind.Sequential)] struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
  [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pp);
  [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int reportType, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, int len);
  [DllImport("hid.dll")] static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
  [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder buf, int len);
  [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

  [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
  [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
  [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
  [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
  [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
  [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
  [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int flags);
  [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(IntPtr g, IntPtr e, IntPtr w, int flags);
  [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr di, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA d);
  [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr di);
  [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int i, ref SP_DEVINFO_DATA d);
  [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA d, ref DEVPROPKEY k, out uint type, byte[] buf, int size, out int req, int flags);
  [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

  class HidDev { public string Path; public ushort Vid, Pid, UsagePage, Usage, InLen, OutLen, FeatLen; public List<BatCap> Battery = new List<BatCap>(); }
  // a standard HID battery value: Generic Device Controls / Battery Strength, or Battery System / state of charge
  class BatCap { public int ReportType; public byte ReportId; public ushort Page, Usage, Link; public int Min, Max; }

  static void FindBatteryCaps(IntPtr pp, HidDev dev) {
    foreach (int rt in new[] { 0, 2 }) {                       // input, feature
      ushort n = 64; var caps = new HIDP_VALUE_CAPS[64];
      if (HidP_GetValueCaps(rt, caps, ref n, pp) != 0x00110000) continue;   // HIDP_STATUS_SUCCESS
      for (int i = 0; i < n; i++) {
        var c = caps[i];
        if (c.IsRange != 0) continue;
        bool strength = c.UsagePage == 0x06 && c.UsageMin == 0x20;
        bool charge = c.UsagePage == 0x85 && (c.UsageMin == 0x64 || c.UsageMin == 0x65 || c.UsageMin == 0x66);
        if (strength || charge) dev.Battery.Add(new BatCap { ReportType = rt, ReportId = c.ReportID, Page = c.UsagePage, Usage = c.UsageMin, Link = c.LinkCollection, Min = c.LogicalMin, Max = c.LogicalMax });
      }
    }
  }

  static List<HidDev> EnumHid() {
    var list = new List<HidDev>();
    Guid g; HidD_GetHidGuid(out g);
    IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);   // PRESENT | DEVICEINTERFACE
    if (set == new IntPtr(-1)) return list;
    try {
      for (int i = 0; ; i++) {
        var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
        if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
        int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
        IntPtr buf = Marshal.AllocHGlobal(req);
        try {
          Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
          if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
          string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
          using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {   // attribute-only open
            if (h.IsInvalid) continue;
            var a = new HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(a);
            if (!HidD_GetAttributes(h, ref a)) continue;
            var dev = new HidDev { Path = path, Vid = a.VendorID, Pid = a.ProductID };
            IntPtr pp;
            if (HidD_GetPreparsedData(h, out pp)) {
              HIDP_CAPS c; HidP_GetCaps(pp, out c);
              dev.UsagePage = c.UsagePage; dev.Usage = c.Usage; dev.InLen = c.InputReportByteLength; dev.OutLen = c.OutputReportByteLength; dev.FeatLen = c.FeatureReportByteLength;
              try { FindBatteryCaps(pp, dev); } catch { }
              HidD_FreePreparsedData(pp);
            }
            list.Add(dev);
          }
        } finally { Marshal.FreeHGlobal(buf); }
      }
    } finally { SetupDiDestroyDeviceInfoList(set); }
    return list;
  }

  static byte[] ReadUntil(HidIo io, int len, int timeoutMs, Func<byte[], bool> match) {
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs) {
      byte[] r = io.Read(len, Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds));
      if (r == null) return null;
      if (match(r)) return r;
    }
    return null;
  }

  public static List<BatteryInfo> ReadAll() {
    var result = new List<BatteryInfo>();
    List<HidDev> hid;
    try { hid = EnumHid(); } catch { hid = new List<HidDev>(); }
    try { ReadLogitech(hid, result); } catch { }
    try { ReadSony(hid, result); } catch { }
    try { ReadXbox(hid, result); } catch { }
    try { ReadSwitchPro(hid, result); } catch { }
    try { ReadHeadsetControl(result); } catch { }
    try { ReadGenericHid(hid, result); } catch { }
    try { ReadBluetooth(result); } catch { }
    try { ReadSystemBattery(result); } catch { }
    // an Xbox pad on Bluetooth shows up twice (XInput + Bluetooth): keep the XInput one
    if (result.Exists(b => b.Key.StartsWith("xbox-"))) result.RemoveAll(b => b.Key.StartsWith("bt-") && b.Name.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0);
    // same gear found two ways (receiver + cable, BT property + HID report): keep the best reading, prefer the charging one
    result.Sort((a, b) => { int c = (b.Charging ? 1 : 0) - (a.Charging ? 1 : 0); return c != 0 ? c : b.Percent.CompareTo(a.Percent); });
    var seen = new HashSet<string>();
    result.RemoveAll(b => !seen.Add(b.Kind + "|" + ShortName(b.Name).ToLowerInvariant()));
    return result;
  }

  static string GuessKind(string name, ushort page, ushort usage) {
    string n = (name ?? "").ToLowerInvariant();
    if (n.Contains("mouse") || n.Contains("mice")) return "mouse";
    if (n.Contains("keyboard") || n.Contains("keys")) return "keyboard";
    if (n.Contains("head") || n.Contains("buds") || n.Contains("airpods") || n.Contains("earphone") || n.Contains("arctis") || n.Contains("cloud") || n.Contains("wh-") || n.Contains("wf-")) return "headset";
    if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense")) return "controller";
    if (page == 0x01 && usage == 0x02) return "mouse";
    if (page == 0x01 && usage == 0x06) return "keyboard";
    if (page == 0x01 && (usage == 0x04 || usage == 0x05)) return "controller";
    return "device";
  }

  // ------------------------------------------------------------------ any HID gear that reports a standard battery value
  static void ReadGenericHid(List<HidDev> hid, List<BatteryInfo> result) {
    var done = new HashSet<string>();
    foreach (var d in hid) {
      if (d.Battery.Count == 0 || d.Vid == 0x054C) continue;   // Sony pads are read from their own reports
      string key = "hid-" + d.Vid.ToString("X4") + d.Pid.ToString("X4");
      if (done.Contains(key)) continue;
      using (var h = CreateFile(d.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
        if (h.IsInvalid) continue;
        IntPtr pp;
        if (!HidD_GetPreparsedData(h, out pp)) continue;
        try {
          foreach (var cap in d.Battery) {
            int len = cap.ReportType == 2 ? d.FeatLen : d.InLen;
            if (len <= 0) continue;
            var buf = new byte[len]; buf[0] = cap.ReportId;
            bool ok = cap.ReportType == 2 ? HidD_GetFeature(h, buf, len) : HidD_GetInputReport(h, buf, len);
            if (!ok) continue;
            uint v;
            if (HidP_GetUsageValue(cap.ReportType, cap.Page, cap.Link, cap.Usage, out v, pp, buf, len) != 0x00110000) continue;
            int pct = cap.Max > cap.Min ? (int)Math.Round(((int)v - cap.Min) * 100.0 / (cap.Max - cap.Min)) : (int)v;
            if (pct < 0 || pct > 100) continue;
            var sb = new StringBuilder(128);
            string name = HidD_GetProductString(h, sb, 256) ? sb.ToString().Trim() : "";
            if (name.Length == 0) name = "جهاز";
            result.Add(new BatteryInfo { Name = name, Kind = GuessKind(name, d.UsagePage, d.Usage), Percent = pct, Key = key });
            done.Add(key);
            break;
          }
        } finally { HidD_FreePreparsedData(pp); }
      }
    }
  }

  // ------------------------------------------------------------------ UPS (or a laptop's own battery)
  static void ReadSystemBattery(List<BatteryInfo> result) {
    SYSTEM_POWER_STATUS s;
    if (!GetSystemPowerStatus(out s)) return;
    if ((s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255 || s.BatteryLifePercent > 100) return;   // no battery / unknown
    result.Add(new BatteryInfo { Name = "البطارية", Kind = "ups", Percent = s.BatteryLifePercent, Charging = s.ACLineStatus == 1 && s.BatteryLifePercent < 100, Key = "system" });
  }

  // ------------------------------------------------------------------ wireless headsets via HeadsetControl (open source, optional)
  // If headsetcontrol.exe is put next to this tool, SteelSeries / HyperX / Corsair / Logitech / Roccat headsets show up too.
  static void ReadHeadsetControl(List<BatteryInfo> result) {
    string exe = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "headsetcontrol.exe");
    if (!System.IO.File.Exists(exe)) return;
    var psi = new System.Diagnostics.ProcessStartInfo(exe, "-b -o JSON");
    psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
    string json;
    using (var p = System.Diagnostics.Process.Start(psi)) {
      json = p.StandardOutput.ReadToEnd();
      if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return; }
    }
    var rx = new System.Text.RegularExpressions.Regex("\"device\"\\s*:\\s*\"([^\"]+)\"[\\s\\S]*?\"battery\"\\s*:\\s*\\{[^}]*?\"status\"\\s*:\\s*\"(\\w+)\"[^}]*?\"level\"\\s*:\\s*(-?\\d+)");
    int n = 0;
    foreach (System.Text.RegularExpressions.Match m in rx.Matches(json)) {
      int level = int.Parse(m.Groups[3].Value);
      if (level < 0 || level > 100 || m.Groups[2].Value == "BATTERY_UNAVAILABLE") continue;
      result.Add(new BatteryInfo { Name = m.Groups[1].Value, Kind = "headset", Percent = level, Charging = m.Groups[2].Value == "BATTERY_CHARGING", Key = "headset-" + (n++) });
    }
  }

  // Short label: "PRO X Wireless" -> "PRO", "Galaxy Buds2 Pro" -> "Buds2", DualSense -> "PS5"
  static readonly string[] Brands = { "logitech", "g", "sony", "galaxy", "samsung", "jbl", "razer", "hyperx", "steelseries", "corsair", "apple", "beats", "anker", "soundcore", "microsoft" };
  public static string ShortName(string name) {
    if (string.IsNullOrEmpty(name)) return "?";
    string n = name.Trim(), low = n.ToLowerInvariant();
    if (low.Contains("xbox")) return "Xbox";
    if (low.Contains("manba")) return "Manba";
    if (low.Contains("joy-con")) return n.Substring(low.IndexOf("joy-con"));
    if (low.Contains("switch")) return "Switch";
    if (low.Contains("dualsense edge") || n.Contains("PS5 Edge")) return "PS5 Edge";
    if (low.Contains("dualsense") || n.Contains("PS5")) return "PS5";
    if (low.Contains("dualshock") || n.Contains("PS4") || low == "wireless controller") return "PS4";
    string[] words = n.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    int i = 0;
    while (i < words.Length - 1 && Array.IndexOf(Brands, words[i].ToLowerInvariant()) >= 0) i++;
    string s = words.Length > 0 ? words[i] : n;
    return s.Length > 10 ? s.Substring(0, 10) : s;
  }

  // ------------------------------------------------------------------ Xbox controllers (XInput: wireless adapter, USB, Bluetooth)
  [StructLayout(LayoutKind.Sequential)] struct XINPUT_BATTERY_INFORMATION { public byte BatteryType; public byte BatteryLevel; }
  [DllImport("xinput1_4.dll")] static extern int XInputGetBatteryInformation(int userIndex, byte devType, out XINPUT_BATTERY_INFORMATION info);

  static void ReadXbox(List<HidDev> hid, List<BatteryInfo> result) {
    // Manba One: the pad itself is XInput, its built-in screen shows up as a separate "USB Display" device (VID 0145).
    // In PC mode it reports neither a level nor whether it is charging, so it is only ever shown as "connected".
    bool manba = hid.Exists(d => d.Vid == 0x0145);
    for (int i = 0; i < 4; i++) {
      XINPUT_BATTERY_INFORMATION b;
      if (XInputGetBatteryInformation(i, 0, out b) != 0) continue;            // not connected
      if (b.BatteryType == 0) continue;                                       // disconnected
      if (b.BatteryType == 1 || b.BatteryType == 0xFF) {                      // wired, or a 3rd-party pad posing as an Xbox 360 one: no level
        bool isManba = manba && !result.Exists(x => x.Key == "manba");
        result.Add(new BatteryInfo { Name = isManba ? "Manba One" : "يد ألعاب", Kind = "controller", Percent = Connected, Key = isManba ? "manba" : "xinput-" + i });
        continue;
      }
      int pct = b.BatteryLevel == 0 ? 5 : b.BatteryLevel == 1 ? 25 : b.BatteryLevel == 2 ? 60 : 100;   // XInput only gives 4 steps
      result.Add(new BatteryInfo { Name = "يد Xbox", Kind = "controller", Percent = pct, Approx = true, Key = "xbox-" + i });
    }
    // the Manba's screen stays on USB even while the pad part sleeps: keep showing the controller as connected
    if (manba && !result.Exists(x => x.Key == "manba"))
      result.Add(new BatteryInfo { Name = "Manba One", Kind = "controller", Percent = Connected, Key = "manba" });
  }

  // ------------------------------------------------------------------ Nintendo Switch Pro (and pads in "Switch mode": Manba, 8BitDo...)
  // Full input report 0x30/0x31/0x21: byte 2, high nibble = battery 0..8 (bit 4 = charging).
  static void ReadSwitchPro(List<HidDev> hid, List<BatteryInfo> result) {
    foreach (var d in hid) {
      if (d.Vid != 0x057E || d.UsagePage != 0x0001 || (d.Usage != 0x0005 && d.Usage != 0x0004)) continue;
      if (d.Pid != 0x2009 && d.Pid != 0x2006 && d.Pid != 0x2007 && d.Pid != 0x200E) continue;
      using (var io = HidIo.Open(d.Path, true)) {
        if (io == null) continue;
        Func<byte[], bool> full = b => b.Length > 2 && (b[0] == 0x30 || b[0] == 0x31 || b[0] == 0x21);
        byte[] r = ReadUntil(io, Math.Max((int)d.InLen, 64), 400, full);
        if (r == null && d.OutLen > 0) {
          // still in the simple 0x3F mode: ask for the standard full report mode (what Steam and every Switch tool does)
          var sub = new byte[Math.Max((int)d.OutLen, 49)];
          sub[0] = 0x01; sub[1] = 0x00;
          byte[] neutral = { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 };
          Array.Copy(neutral, 0, sub, 2, 8);
          sub[10] = 0x03; sub[11] = 0x30;
          io.Write(sub, sub.Length, 500);
          r = ReadUntil(io, Math.Max((int)d.InLen, 64), 600, full);
        }
        if (r == null) continue;
        int lv = (r[2] >> 5) & 0x07;                           // 0 empty, 1 critical, 2 low, 3 medium, 4 full
        bool charging = (r[2] & 0x10) != 0;
        string name = d.Pid == 0x2006 ? "Joy-Con L" : d.Pid == 0x2007 ? "Joy-Con R" : "يد Switch";
        result.Add(new BatteryInfo { Name = name, Kind = "controller", Percent = Math.Min(100, lv * 25), Charging = charging, Approx = true, Key = "switch-" + d.Pid.ToString("X4") });
      }
    }
  }

  // ------------------------------------------------------------------ Logitech HID++ 2.0
  const byte SWID = 0x0D;

  class Hidpp { public HidIo W, LongR, ShortR; public bool LongOnly; }   // headsets often have only the long (0x11) channel

  // returns the reply, or null; offline = the receiver says the device is not reachable right now
  static byte[] Request(Hidpp c, byte dev, byte feat, byte func, byte[] args, out bool offline) {
    offline = false;
    var req = new byte[c.LongOnly ? 20 : 7];
    req[0] = c.LongOnly ? (byte)0x11 : (byte)0x10; req[1] = dev; req[2] = feat; req[3] = (byte)((func << 4) | SWID);
    if (args != null) Array.Copy(args, 0, req, 4, Math.Min(3, args.Length));
    if (!c.W.Write(req, req.Length, 500)) return null;
    Func<byte[], bool> mine = b => b.Length > 4 && b[1] == dev && ((b[2] == feat && b[3] == req[3]) || ((b[2] == 0x8F || b[2] == 0xFF) && b[4] == req[3]));
    byte[] r = ReadUntil(c.LongR, 20, 350, mine) ?? (c.ShortR != null ? ReadUntil(c.ShortR, 7, 150, mine) : null);
    if (r == null) return null;
    if (r[2] == 0x8F || r[2] == 0xFF) { offline = r[2] == 0x8F && (r[5] == 0x08 || r[5] == 0x09 || r[5] == 0x04); return null; }
    return r;
  }

  static byte[] Request(Hidpp c, byte dev, byte feat, byte func, byte[] args) { bool off; return Request(c, dev, feat, func, args, out off); }

  static byte FeatureIndex(Hidpp c, byte dev, ushort feature) {
    var r = Request(c, dev, 0x00, 0, new byte[] { (byte)(feature >> 8), (byte)feature, 0 });
    return r == null ? (byte)0 : r[4];
  }

  static void ReadLogitech(List<HidDev> hid, List<BatteryInfo> result) {
    var groups = new Dictionary<string, List<HidDev>>();
    foreach (var d in hid) {
      if (d.Vid != 0x046D || (d.UsagePage != 0xFF00 && d.UsagePage != 0xFF43)) continue;   // HID++ pages (FF43 = headsets)
      string key = d.Path.ToLowerInvariant();
      int col = key.IndexOf("&col"); if (col > 0) key = key.Substring(0, col);
      if (!groups.ContainsKey(key)) groups[key] = new List<HidDev>();
      groups[key].Add(d);
    }
    foreach (var grp in groups.Values) {
      HidDev s = grp.Find(x => x.Usage == 0x0001 && x.UsagePage == 0xFF00), l = grp.Find(x => x.Usage == 0x0002 || x.Usage == 0x0202);
      if (l == null) continue;
      var c = new Hidpp { LongOnly = s == null, W = HidIo.Open((s ?? l).Path, true), LongR = HidIo.Open(l.Path, false), ShortR = s != null ? HidIo.Open(s.Path, false) : null };
      if (s == null) s = l;
      try {
        if (c.W == null || c.LongR == null || (!c.LongOnly && c.ShortR == null)) continue;
        foreach (byte dev in new byte[] { 0x01, 0x02, 0x03, 0xFF }) {
          // asleep and empty slots both answer "unknown device"; the widget keeps showing the last value it read
          var ping = Request(c, dev, 0x00, 1, new byte[] { 0, 0, 0x5A });
          if (ping == null) continue;
          var info = LogitechDevice(c, dev);
          if (info != null) { info.Key = "logi-" + info.Name; result.Add(info); }   // same key over the receiver or the charging cable
        }
      } finally {
        if (c.W != null) c.W.Dispose(); if (c.LongR != null) c.LongR.Dispose(); if (c.ShortR != null) c.ShortR.Dispose();
      }
    }
  }

  static BatteryInfo LogitechDevice(Hidpp c, byte dev) {
    int percent = -1; bool charging = false, approx = false;
    byte ub = FeatureIndex(c, dev, 0x1004);                    // UNIFIED_BATTERY: [soc%, level flags, charging, external]
    if (ub != 0) {
      var r = Request(c, dev, ub, 1, null);
      if (r != null) {
        percent = r[4];
        if (percent == 0) { int lv = r[5]; approx = true; percent = (lv & 8) != 0 ? 90 : (lv & 4) != 0 ? 60 : (lv & 2) != 0 ? 25 : (lv & 1) != 0 ? 5 : -1; }
        charging = r[6] == 1 || r[6] == 2;                     // 3 = charging complete: show it as full, not "charging"
        if (r[6] == 3) percent = 100;
      }
    }
    if (percent < 0) {
      byte bs = FeatureIndex(c, dev, 0x1000);                  // BATTERY_STATUS: [level%, next, status]
      if (bs != 0) { var r = Request(c, dev, bs, 0, null); if (r != null) { percent = r[4]; charging = r[6] == 1 || r[6] == 2 || r[6] == 4; if (r[6] == 3) percent = 100; } }
    }
    if (percent < 0) {
      byte bv = FeatureIndex(c, dev, 0x1001);                  // BATTERY_VOLTAGE: [mV hi, mV lo, flags]
      if (bv != 0) {
        var r = Request(c, dev, bv, 0, null);
        if (r != null) { int mv = (r[4] << 8) | r[5]; percent = Math.Max(0, Math.Min(100, (mv - 3500) * 100 / 600)); charging = (r[6] & 0x80) != 0; approx = true; }
      }
    }
    if (percent < 0) return null;
    string name = null, kind = "mouse";
    byte nm = FeatureIndex(c, dev, 0x0005);                    // DEVICE_NAME: length, name chunks, type
    if (nm != 0) {
      var cnt = Request(c, dev, nm, 0, null);
      if (cnt != null) {
        var sb = new StringBuilder();
        for (int off = 0; off < cnt[4] && off < 64; off += 16) {
          var part = Request(c, dev, nm, 1, new byte[] { (byte)off, 0, 0 });
          if (part == null) break;
          for (int i = 4; i < part.Length && sb.Length < cnt[4]; i++) sb.Append((char)part[i]);
        }
        name = sb.ToString().Trim('\0', ' ');
        var tp = Request(c, dev, nm, 2, null);
        if (tp != null) { int t = tp[4]; kind = t == 0 ? "keyboard" : (t == 3 || t == 4 || t == 5) ? "mouse" : t == 8 ? "headset" : "device"; }
      }
    }
    return new BatteryInfo { Name = string.IsNullOrEmpty(name) ? "Logitech" : name, Kind = kind, Percent = percent, Charging = charging, Approx = approx };
  }

  // ------------------------------------------------------------------ PlayStation controllers
  static void ReadSony(List<HidDev> hid, List<BatteryInfo> result) {
    foreach (var d in hid) {
      if (d.Vid != 0x054C || d.UsagePage != 0x0001 || d.Usage != 0x0005) continue;   // gamepad collection
      bool ds5 = d.Pid == 0x0CE6 || d.Pid == 0x0DF2, ds4 = d.Pid == 0x05C4 || d.Pid == 0x09CC;
      if (!ds5 && !ds4) continue;
      bool bt = d.InLen != 64;                                 // USB input report = 64 bytes
      using (var io = HidIo.Open(d.Path, false)) {
        if (io == null) continue;
        if (bt) {                                              // ask for the full report mode once (same request games make)
          var f = new byte[ds5 ? 41 : 37]; f[0] = 0x05;
          try { HidD_GetFeature(io.Handle, f, f.Length); } catch { }
        }
        byte want = ds5 ? (bt ? (byte)0x31 : (byte)0x01) : (bt ? (byte)0x11 : (byte)0x01);
        byte[] r = ReadUntil(io, Math.Max((int)d.InLen, 64), 600, b => b[0] == want);
        if (r == null) continue;
        int idx = ds5 ? (bt ? 54 : 53) : (bt ? 32 : 30);
        if (r.Length <= idx) continue;
        int v = r[idx], percent; bool charging;
        if (ds5) {
          int st = (v >> 4) & 0x0F;
          percent = Math.Min(100, (v & 0x0F) * 10 + 5); charging = st == 1; if (st == 2) percent = 100;
        } else {
          bool cable = (v & 0x10) != 0; int lv = v & 0x0F;
          percent = Math.Min(100, cable ? lv * 100 / 11 : lv * 10 + 5); charging = cable && lv < 11;
        }
        string name = ds5 ? (d.Pid == 0x0DF2 ? "يد PS5 Edge" : "يد PS5") : "يد PS4";
        result.Add(new BatteryInfo { Name = name, Kind = "controller", Percent = percent, Charging = charging, Approx = percent < 100, Key = "sony-" + d.Pid.ToString("X4") + (bt ? "-bt" : "-usb") });
      }
    }
  }

  // ------------------------------------------------------------------ Bluetooth (what Settings > Bluetooth shows)
  static void ReadBluetooth(List<BatteryInfo> result) {
    var batteryKey = new DEVPROPKEY { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
    var nameKey = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    var seen = new HashSet<string>();
    IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x06);   // ALLCLASSES | PRESENT
    if (set == new IntPtr(-1)) return;
    try {
      for (int i = 0; ; i++) {
        var di = new SP_DEVINFO_DATA(); di.cbSize = Marshal.SizeOf(di);
        if (!SetupDiEnumDeviceInfo(set, i, ref di)) break;
        uint type; int req; var b = new byte[4];
        if (!SetupDiGetDevicePropertyW(set, ref di, ref batteryKey, out type, b, b.Length, out req, 0)) continue;
        var nb = new byte[512];
        string name = "جهاز بلوتوث";
        if (SetupDiGetDevicePropertyW(set, ref di, ref nameKey, out type, nb, nb.Length, out req, 0)) name = Encoding.Unicode.GetString(nb, 0, Math.Max(0, req - 2));
        if (!seen.Add(name)) continue;
        string n = name.ToLowerInvariant();
        string kind = n.Contains("mouse") ? "mouse" : n.Contains("keyboard") ? "keyboard"
                    : (n.Contains("head") || n.Contains("buds") || n.Contains("airpods") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("hands-free") || n.Contains("stereo")) ? "headset"
                    : (n.Contains("controller") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense")) ? "controller" : "device";
        result.Add(new BatteryInfo { Name = name, Kind = kind, Percent = Math.Min(100, (int)b[0]), Charging = false, Key = "bt-" + name });
      }
    } finally { SetupDiDestroyDeviceInfoList(set); }
  }
}
