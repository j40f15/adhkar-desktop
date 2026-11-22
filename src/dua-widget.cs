// adhkar-dua.exe - live layers on the desktop, drawn over the wallpaper and under every window:
//   * a big clock + date (top centre)
//   * one dua at a time, fading to the next random one every N seconds (centre)
//   * a countdown to the next prayer (under the prayer-times card on the wallpaper)
// Click-through, no taskbar button, does nothing while a game is fullscreen or the PC is locked.
// Usage: adhkar-dua.exe [seconds]   - reads adhkar.txt and next-prayers.txt next to the exe (edits picked up automatically)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

public static partial class DuaWidget {
  const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
  const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
  const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
  static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

  [StructLayout(LayoutKind.Sequential)]
  struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
  [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
  [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

  // live stats: NVIDIA driver's NVML for the GPU, kernel32 for RAM / CPU (no polling processes)
  [StructLayout(LayoutKind.Sequential)] struct NvmlUtil { public uint gpu, memory; }
  [DllImport("nvml.dll")] static extern int nvmlInit_v2();
  [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
  [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
  [StructLayout(LayoutKind.Sequential)]
  class MEMORYSTATUSEX { public uint dwLength = 64; public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
  [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);
  [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
  static bool gpuReady; static IntPtr gpu;
  static long lastIdle, lastKernel, lastUser;

  // watchdogs: GPU overheating and a frozen foreground app (they keep running during games)
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr HungWindowFromGhostWindow(IntPtr ghost);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  const uint HeatLimit = 85;                                   // RTX 5070 starts slowing itself down around 87-90
  static int hotCount; static bool hotShown; static DateTime lastHeatAlert = DateTime.MinValue;
  static IntPtr hungWnd; static DateTime hungSince; static bool hungAlerted;

  static readonly CultureInfo Ar = CultureInfo.GetCultureInfo("ar-SA");
  static readonly Random Rng = new Random();
  const string AppName = "حِصن";                                 // the program's name (tray, settings, windows)
  static readonly string[] Days = { "الأحد", "الاثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت" };
  static readonly string[] Months = { "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر" };
  static Color Gold = Color.FromArgb(210, 212, 175, 106);        // accent colour, replaced by the chosen theme at start

  static string dir, listFile, prayersFile, weatherFile;
  static DateTime listStamp, prayersStamp;
  static List<string> bag = new List<string>();
  static List<KeyValuePair<DateTime, string>> prayers = new List<KeyValuePair<DateTime, string>>();
  static string last, shownClock, shownCount, shownInfo, shownStats;
  static TextBlock countText, statsText, netText;
  // one set per screen (the main screen, plus the second screen when enabled)
  static readonly List<TextBlock> clockTexts = new List<TextBlock>(), dateTexts = new List<TextBlock>(), infoTexts = new List<TextBlock>(), duaTexts = new List<TextBlock>();

  // control panel (tray icon next to the taskbar clock) + saved settings
  static System.Windows.Forms.NotifyIcon tray;
  static readonly List<Window> layers = new List<Window>();
  static DispatcherTimer duaTimer;
  static Mutex instanceMutex;
  static string muteFile, shownTip;
  static int duaSeconds = 30;
  static bool hiddenLayers;

  // wireless gear batteries (batteries.cs), shown as pills above the prayer card
  static readonly Dictionary<string, KeyValuePair<BatteryInfo, DateTime>> knownBatteries = new Dictionary<string, KeyValuePair<BatteryInfo, DateTime>>();
  static readonly Dictionary<string, DateTime> batteryAlerted = new Dictionary<string, DateTime>();
  static WrapPanel batteryPanel;
  static bool batteryReading, anyCharging;
  static DispatcherTimer batteryTimer, deviceChangeTimer;
  static string shownBatteries;
  static System.Windows.Forms.ToolStripMenuItem batteryMenu;
  static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

  // network line: ping to 1.1.1.1 + live download / upload speed
  static Ping pinger;
  static string pingText = "—", shownNet;
  static long lastRx = -1, lastTx = -1;
  static DateTime lastNetAt;
  static int tick;
  static double scale;

  [STAThread]
  public static void Main(string[] args) {
    dir = AppDomain.CurrentDomain.BaseDirectory;
    // "اقفله الحين" button on the frozen-app notification opens adhkar-kill:<one-time token>
    if (args.Length > 0 && args[0].StartsWith("adhkar-kill:", StringComparison.OrdinalIgnoreCase)) { KillFromToken(args[0]); return; }
    // started as the screensaver (adhkar-rest.scr): /s = show, /p = preview box, /c = settings
    string a0 = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
    if (a0.StartsWith("/s")) { RunRestScreen(args.Length > 1 && args[1] == "/test"); return; }
    if (a0.StartsWith("/p") || a0.StartsWith("/c")) return;

    bool firstInstance;
    instanceMutex = new Mutex(true, "Local\\MakkahAdhkarDuaWidget", out firstInstance);
    if (!firstInstance) return;

    int seconds;
    if (args.Length > 0 && int.TryParse(args[0], out seconds) && seconds >= 5) duaSeconds = seconds;
    cfgFile = Path.Combine(dir, "settings.ini");
    muteFile = Path.Combine(dir, "mute-until.txt");
    LoadSettings();                                            // the settings window's choices win over the task argument
    Gold = ThemeColor(CfgStr("theme", "gold"), 210);
    LoadDrainHistory();
    LoadPlayLog();
    listFile = Path.Combine(dir, "adhkar.txt");
    prayersFile = Path.Combine(dir, "next-prayers.txt");
    weatherFile = Path.Combine(dir, "weather.txt");
    try { gpuReady = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out gpu) == 0; } catch { gpuReady = false; }

    var app = new Application();
    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

    // Same layout as the wallpaper (designed 1080 units tall).
    double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
    scale = sh / 1080.0;
    double vw = sw / scale;
    double cardX = vw - 90 - 440;
    double cx = (150 + cardX - 60) / 2;

    // clock + date + rotating dua on the main screen
    AddClockAndDua(0, 0, cx);

    // ... and on the other screen(s), same layout as the wallpaper there
    if (CfgBool("second_screen", true)) {
      try {
        double dipPerPx = SystemParameters.PrimaryScreenHeight / System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
        foreach (var scr in System.Windows.Forms.Screen.AllScreens) {
          if (scr.Primary) continue;
          double ox = scr.Bounds.X * dipPerPx / scale, oy = scr.Bounds.Y * dipPerPx / scale;
          double svw = scr.Bounds.Width * dipPerPx / scale, scardX = svw - 90 - 440;
          AddClockAndDua(ox, oy, (150 + scardX - 60) / 2);
        }
      } catch { }
    }

    // countdown under the prayer card
    countText = MakeText("Segoe UI", FontWeights.SemiBold, 20, Gold);
    statsText = MakeText("Segoe UI", FontWeights.Normal, 16, Color.FromArgb(105, 255, 255, 255));   // CPU / RAM / GPU temp
    statsText.Margin = new Thickness(0, 10 * scale, 0, 0);
    var countPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    netText = MakeText("Segoe UI", FontWeights.Normal, 16, Color.FromArgb(105, 255, 255, 255));     // ping / download / upload
    netText.Margin = new Thickness(0, 4 * scale, 0, 0);
    countPanel.Children.Add(countText);
    countPanel.Children.Add(statsText);
    countPanel.Children.Add(netText);
    MakeLayer(cardX, 806, 440, 130, countPanel);

    // battery pills, centred right above the taskbar (over the app icons)
    batteryPanel = new WrapPanel { FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    double workBottom = SystemParameters.WorkArea.Bottom / scale;         // top edge of the taskbar
    MakeLayer(vw / 2 - 450, workBottom - 44, 900, 40, batteryPanel);


    UpdateClock(true);
    NextDua(true);

    duaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(duaSeconds) };
    duaTimer.Tick += delegate { NextDua(false); };
    duaTimer.Start();
    var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
    clockTimer.Tick += delegate { UpdateClock(false); };
    clockTimer.Start();
    var watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
    watchTimer.Tick += delegate { Watch(); };
    watchTimer.Start();

    CreateTray();
    if (hiddenLayers) foreach (var w in layers) w.Hide();

    LoadBatteryCache();                                         // show the last known levels right away (dimmed until read live)
    ApplyBatteries(null);
    batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };   // first read soon, then every 5 min (1 min while charging)
    batteryTimer.Tick += delegate { batteryTimer.Interval = TimeSpan.FromMinutes(anyCharging ? 1 : 5); ReadBatteriesAsync(); };
    batteryTimer.Start();
    deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };   // something was plugged in / out
    deviceChangeTimer.Tick += delegate { deviceChangeTimer.Stop(); ReadBatteriesAsync(); };

    // game library window: Ctrl+Alt+G or the tray icon (the list is also used to name games for the play-time count)
    LoadLibraryAsync();
    RegisterLibraryHotkey();
    StartSteamArabic();                                         // Arabic inside Steam (only when turned on in the settings)

    app.Run();
    if (tray != null) { tray.Visible = false; tray.Dispose(); }
    GC.KeepAlive(instanceMutex);
  }

  // ox / oy: the screen's top-left corner, cx: centre of the free area left of the prayer card (design units)
  static void AddClockAndDua(double ox, double oy, double cx) {
    var clock = MakeText("Segoe UI", FontWeights.Light, 104, Color.FromArgb(225, 255, 255, 255));
    var date = MakeText("Segoe UI", FontWeights.Normal, 24, Color.FromArgb(130, 255, 255, 255));
    date.Margin = new Thickness(0, -6 * scale, 0, 0);
    var info = MakeText("Segoe UI", FontWeights.Normal, 19, Gold);              // weather + Ramadan/Eid countdown
    info.Margin = new Thickness(0, 8 * scale, 0, 0);
    var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    panel.Children.Add(clock); panel.Children.Add(date); panel.Children.Add(info);
    MakeLayer(ox + cx - 450, oy + 70, 900, 250, panel);
    clockTexts.Add(clock); dateTexts.Add(date); infoTexts.Add(info);

    var dua = MakeText("Sakkal Majalla", FontWeights.Bold, 62, Color.FromArgb(210, 255, 255, 255));
    dua.TextWrapping = TextWrapping.Wrap;
    dua.Opacity = 0;
    MakeLayer(ox + cx - 500, oy + 300, 1000, 440, dua);
    duaTexts.Add(dua);
  }

  static TextBlock MakeText(string family, FontWeight weight, double size, Color color) {
    var t = new TextBlock();
    t.FlowDirection = FlowDirection.RightToLeft;
    t.TextAlignment = TextAlignment.Center;
    t.HorizontalAlignment = HorizontalAlignment.Center;
    t.VerticalAlignment = VerticalAlignment.Center;
    t.FontFamily = new FontFamily(family);
    t.FontWeight = weight;
    t.FontSize = size * scale;
    t.Foreground = new SolidColorBrush(color);
    return t;
  }

  // A transparent, click-through window owned by the desktop (survives Win+D, always under app windows).
  static void MakeLayer(double x, double y, double w, double h, UIElement content) { MakeLayer(x, y, w, h, content, true); }

  // clickThrough = false for layers you can click (the game shelf); they still never take focus.
  static void MakeLayer(double x, double y, double w, double h, UIElement content, bool clickThrough) {
    var win = new Window();
    win.WindowStyle = WindowStyle.None;
    win.AllowsTransparency = true;
    win.Background = Brushes.Transparent;
    win.ShowInTaskbar = false;
    win.ShowActivated = false;
    win.Focusable = false;
    win.ResizeMode = ResizeMode.NoResize;
    win.Title = "Hisn";
    win.Left = x * scale; win.Top = y * scale; win.Width = w * scale; win.Height = h * scale;
    var grid = new Grid();
    grid.Children.Add(content);
    win.Content = grid;
    win.SourceInitialized += delegate {
      IntPtr hwnd = new WindowInteropHelper(win).Handle;
      SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | (clickThrough ? WS_EX_TRANSPARENT : 0) | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
      IntPtr progman = FindWindow("Progman", null);
      if (progman != IntPtr.Zero) SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, progman);
      SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
      HwndSource.FromHwnd(hwnd).AddHook(WndProc);
    };
    layers.Add(win);
    win.Show();
  }

  static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
    if (msg == 0x0046) {                                       // WM_WINDOWPOSCHANGING: never come to the front
      var wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
      wp.hwndInsertAfter = HWND_BOTTOM;
      wp.flags &= ~SWP_NOZORDER;
      Marshal.StructureToPtr(wp, lParam, false);
    } else if (msg == 0x0021) {                                // WM_MOUSEACTIVATE -> MA_NOACTIVATE
      handled = true;
      return new IntPtr(3);
    } else if (msg == 0x0219 && deviceChangeTimer != null) {   // WM_DEVICECHANGE: charger / pad plugged in or out -> re-read in 3 s
      deviceChangeTimer.Stop();
      deviceChangeTimer.Start();
    }
    return IntPtr.Zero;
  }

  // Locked screen, fullscreen game/app or presentation: leave everything still.
  static bool Busy() {
    int st;
    if (SHQueryUserNotificationState(out st) != 0) return false;
    return st >= 1 && st <= 4;
  }

  static string Digits(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (char c in s) sb.Append(c >= '0' && c <= '9' ? (char)('٠' + (c - '0')) : c);
    return sb.ToString();
  }

  // ---------------------------------------------------------------- clock + countdown
  static void UpdateClock(bool force) {
    if (!force && Busy()) return;
    DateTime now = DateTime.Now;
    int h12 = now.Hour % 12; if (h12 == 0) h12 = 12;
    string time = Digits(h12 + ":" + now.Minute.ToString("00"));
    string ampm = now.Hour < 12 ? "ص" : "م";
    string date = Days[(int)now.DayOfWeek] + "، " + Digits(now.Day.ToString()) + " " + Months[now.Month - 1] + " " + Digits(now.Year.ToString());
    string key = time + ampm + date;
    if (key != shownClock) {
      shownClock = key;
      foreach (var c in clockTexts) {
        c.Inlines.Clear();
        c.Inlines.Add(new Run(time));
        c.Inlines.Add(new Run(" " + ampm) { FontSize = 40 * scale });
      }
      foreach (var d in dateTexts) d.Text = date;
    }
    string count = Countdown(now);
    if (count != shownCount) {
      shownCount = count;
      countText.Text = count ?? "";
    }
    string tip = count ?? AppName;
    if (tray != null && tip != shownTip) { shownTip = tip; tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; }
    string info = string.Join("   •   ", new[] { Weather(now), HijriCountdown(now.Date) }.Where(s => !string.IsNullOrEmpty(s)).ToArray());
    if (info != shownInfo) { shownInfo = info; foreach (var t in infoTexts) t.Text = info; }
    string stats = Stats();
    if (stats != shownStats) { shownStats = stats; statsText.Text = stats; }
    if (tick++ % 2 == 0) SendPing();                           // every 10 s
    string net = NetLine();
    if (net != shownNet) { shownNet = net; netText.Text = net ?? ""; }
  }

  // ---------------------------------------------------------------- batteries
  static DateTime batteryReadStarted;

  static void ReadBatteriesAsync() {
    // a device that never answers must not freeze the battery bar: after a minute, start a fresh read anyway
    if (batteryReading && (DateTime.Now - batteryReadStarted).TotalSeconds < 60) return;
    batteryReading = true;
    batteryReadStarted = DateTime.Now;
    var t = new Thread(delegate () {                           // HID queries take ~1 s: keep them off the UI thread
      List<BatteryInfo> list = null;
      try { list = Batteries.ReadAll(); } catch { }
      Application.Current.Dispatcher.BeginInvoke(new Action(delegate {
        try { ApplyBatteries(list); } catch { }
        batteryReading = false;
      }));
    });
    t.IsBackground = true;                                     // never keeps the widget alive on exit
    t.Start();
  }

  static string PercentText(BatteryInfo b) { return (b.Approx ? "≈" : "") + Digits(Math.Max(0, Math.Min(100, b.Percent)).ToString()) + "٪"; }

  static void ApplyBatteries(List<BatteryInfo> list) {
    DateTime now = DateTime.Now;
    var live = new HashSet<string>();
    if (list != null) foreach (var b in list) {
      if (b.Percent < 0 && b.Percent != Batteries.Connected) continue;
      knownBatteries[b.Key] = new KeyValuePair<BatteryInfo, DateTime>(b, now);
      live.Add(b.Key);
    }
    foreach (var k in knownBatteries.Keys.ToList()) {
      var kb = knownBatteries[k];
      bool stale = (now - kb.Value).TotalHours > 24;                                    // not seen for a day
      bool gone = list != null && !live.Contains(k) && kb.Key.Percent == Batteries.Connected;   // unplugged pad without a level
      bool dup = !live.Contains(k) && knownBatteries.Any(o => live.Contains(o.Key) && o.Value.Key.Kind == kb.Key.Kind && o.Value.Key.Short == kb.Key.Short);
      if (stale || gone || dup) knownBatteries.Remove(k);
    }
    anyCharging = knownBatteries.Any(k => live.Contains(k.Key) && k.Value.Key.Charging);
    if (list != null) {
      SaveBatteryCache();
      foreach (var k in knownBatteries) if (live.Contains(k.Key)) TrackDrain(k.Value.Key, now);
      SaveDrainHistory();
      // full-charge alert: it was charging last time, now it reports full
      foreach (var k in knownBatteries) {
        if (!live.Contains(k.Key) || k.Value.Key.Percent < 0) continue;
        var b = k.Value.Key; bool prev;
        if (wasCharging.TryGetValue(k.Key, out prev) && prev && !b.Charging && b.Percent >= 99 && CfgBool("battery_full_alert", true))
          Notify("«" + b.Short + "» اكتمل شحنه ١٠٠٪", "تقدر تفصله الحين", "");
        wasCharging[k.Key] = b.Charging;
      }
    }

    var items = knownBatteries.OrderBy(k => k.Value.Key.Kind).ThenBy(k => k.Key).ToList();
    bool showEstimate = CfgBool("battery_estimate", true);
    string sig = string.Join("|", items.Select(k => k.Key + ":" + k.Value.Key.Percent + ":" + k.Value.Key.Charging + ":" + live.Contains(k.Key) + ":" + (showEstimate ? HoursText(HoursLeft(k.Value.Key), true) : "")).ToArray());
    if (sig != shownBatteries) {
      shownBatteries = sig;
      batteryPanel.Children.Clear();
      foreach (var k in items) batteryPanel.Children.Add(Pill(k.Value.Key, live.Contains(k.Key)));
      if (batteryMenu != null) {
        batteryMenu.DropDownItems.Clear();
        if (items.Count == 0) batteryMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("ما فيه أجهزة لاسلكية متصلة") { Enabled = false });
        foreach (var k in items) {
          var b = k.Value.Key;
          string est = HoursText(HoursLeft(b), false);
          string t = b.Name + ": " + (b.Percent < 0 ? "متصلة" : PercentText(b)) + (b.Charging ? " (يشحن)" : "") + (live.Contains(k.Key) ? "" : " (آخر قراءة)")
                   + (est != null && live.Contains(k.Key) ? " · يكفي تقريباً " + est : "");
          batteryMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem(t) { Enabled = false });
        }
      }
    }

    int low = CfgInt("battery_low", 20);                        // low battery: once at the chosen level, again at 10%, at most every 6 h
    foreach (var k in items) {
      var b = k.Value.Key;
      if (!live.Contains(k.Key) || b.Percent < 0) continue;
      if (b.Charging || b.Percent > low + 5) { batteryAlerted.Remove(k.Key + "-low"); batteryAlerted.Remove(k.Key + "-10"); continue; }
      string level = b.Percent <= 10 && low > 10 ? "-10" : b.Percent <= low ? "-low" : null;
      if (level == null) continue;
      DateTime last;
      if (batteryAlerted.TryGetValue(k.Key + level, out last) && (now - last).TotalHours < 6) continue;
      batteryAlerted[k.Key + level] = now;
      System.Media.SystemSounds.Asterisk.Play();
      string est = HoursText(HoursLeft(b), false);
      Notify("شحن «" + b.Name + "» " + PercentText(b),
             b.Percent <= 10 ? "قرب يطفي! اشحنه الحين" : "اشحنه قريب عشان ما يطفي عليك وسط اللعب",
             est != null ? "يكفي تقريباً " + est + " بنفس استخدامك" : "الشحن ظاهر فوق شريط المهام");
    }
  }

  // batteries.txt: key|name|kind|percent|charging|ticks - survives restarts and sleeping gear
  static void SaveBatteryCache() {
    try {
      var lines = knownBatteries.Select(k => string.Join("|", new[] { k.Key, k.Value.Key.Name.Replace("|", " "), k.Value.Key.Kind,
        k.Value.Key.Percent.ToString(), k.Value.Key.Charging ? "1" : "0", k.Value.Value.Ticks.ToString(), k.Value.Key.Approx ? "1" : "0" })).ToArray();
      File.WriteAllLines(Path.Combine(dir, "batteries.txt"), lines, new UTF8Encoding(true));
    } catch { }
  }

  static void LoadBatteryCache() {
    try {
      foreach (string line in File.ReadAllLines(Path.Combine(dir, "batteries.txt"), Encoding.UTF8)) {
        string[] p = line.Split('|');
        if (p.Length < 6) continue;
        var b = new BatteryInfo { Key = p[0], Name = p[1], Kind = p[2], Percent = int.Parse(p[3]), Charging = false, Approx = p.Length > 6 && p[6] == "1" };   // a saved reading is never shown as "charging"
        knownBatteries[b.Key] = new KeyValuePair<BatteryInfo, DateTime>(b, new DateTime(long.Parse(p[5])));
      }
    } catch { }
  }

  static string KindGlyph(string kind) {
    switch (kind) {
      case "mouse": return "";
      case "keyboard": return "";
      case "headset": return "";
      case "controller": return "";
      case "ups": return "";
      default: return "";
    }
  }

  static UIElement Pill(BatteryInfo b, bool live) {
    bool noLevel = b.Percent < 0;                              // pad that only says "I'm connected"
    int pct = Math.Max(0, Math.Min(100, b.Percent));
    Color level = b.Charging ? Color.FromRgb(120, 200, 255)    // charging = blue, like a phone
                : pct <= 20 ? Color.FromRgb(235, 95, 95) : pct < 50 ? Color.FromRgb(235, 195, 95) : Color.FromRgb(125, 210, 145);
    var row = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.RightToLeft, VerticalAlignment = VerticalAlignment.Center };

    // compact dock-style pill: [icon] PRO [battery] ٢٠٪ [charging bolt]
    var icon = new TextBlock { Text = KindGlyph(b.Kind), FontFamily = IconFont, FontSize = 15 * scale, Foreground = new SolidColorBrush(Gold), VerticalAlignment = VerticalAlignment.Center };
    row.Children.Add(icon);

    row.Children.Add(new TextBlock { Text = b.Short, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 13.5 * scale, Foreground = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), Margin = new Thickness(6 * scale, 0, 8 * scale, 0), VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight });

    if (noLevel && b.Charging && live) {                       // on the cable: charging, but the pad keeps its level to itself
      var bolt = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 13 * scale, Foreground = new SolidColorBrush(level), VerticalAlignment = VerticalAlignment.Center };
      row.Children.Add(bolt);
      row.Children.Add(new TextBlock { Text = "يشحن", FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 13.5 * scale, Foreground = new SolidColorBrush(level), Margin = new Thickness(0, 0, 4 * scale, 0), VerticalAlignment = VerticalAlignment.Center });
      var pulse = new DoubleAnimation(1.0, 0.3, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
      Timeline.SetDesiredFrameRate(pulse, 8);
      bolt.BeginAnimation(UIElement.OpacityProperty, pulse);
    } else if (noLevel) {
      row.Children.Add(new TextBlock { Text = "متصلة", FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5 * scale, Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center });
      row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7 * scale, Height = 7 * scale, Fill = new SolidColorBrush(Color.FromRgb(125, 210, 145)), Margin = new Thickness(0, 0, 7 * scale, 0), VerticalAlignment = VerticalAlignment.Center });
    } else {
      var fill = new Border { Width = Math.Max(2, 22 * scale * pct / 100.0), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(level), CornerRadius = new CornerRadius(1 * scale) };
      var track = new Border { Width = 27 * scale, Height = 11 * scale, Padding = new Thickness(1.8 * scale), CornerRadius = new CornerRadius(2.5 * scale), BorderThickness = new Thickness(1.1 * scale), BorderBrush = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center, Child = fill };
      row.Children.Add(track);
      row.Children.Add(new TextBlock { Text = PercentText(b), FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 13.5 * scale, Foreground = new SolidColorBrush(level), Margin = new Thickness(0, 0, 6 * scale, 0), VerticalAlignment = VerticalAlignment.Center });
      string est = live && CfgBool("battery_estimate", true) ? HoursText(HoursLeft(b), true) : null;   // e.g. "· ١٢س" left at this pace
      if (est != null)
        row.Children.Add(new TextBlock { Text = "· " + est, FontFamily = new FontFamily("Segoe UI"), FontSize = 12 * scale, Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), Margin = new Thickness(0, 0, 5 * scale, 0), VerticalAlignment = VerticalAlignment.Center });
      if (b.Charging && live) {
        row.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12 * scale, Foreground = new SolidColorBrush(level), Margin = new Thickness(0, 0, 4 * scale, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = "يشحن", FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5 * scale, Foreground = new SolidColorBrush(level), Margin = new Thickness(0, 0, 3 * scale, 0), VerticalAlignment = VerticalAlignment.Center });
        var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, 8);                // gentle and cheap
        fill.BeginAnimation(UIElement.OpacityProperty, pulse);
      }
    }

    return new Border {
      Child = row, Margin = new Thickness(4 * scale, 0, 4 * scale, 0), Padding = new Thickness(11 * scale, 5 * scale, 11 * scale, 5 * scale),
      CornerRadius = new CornerRadius(14 * scale), Background = new SolidColorBrush(Color.FromArgb(150, 20, 20, 20)),
      BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
      Opacity = live ? 1.0 : 0.45
    };
  }

  // ---------------------------------------------------------------- network
  static void SendPing() {
    try {
      if (pinger == null) {
        pinger = new Ping();
        pinger.PingCompleted += (s, e) => {
          pingText = (e.Cancelled || e.Error != null || e.Reply == null || e.Reply.Status != IPStatus.Success)
            ? "—" : Digits(e.Reply.RoundtripTime.ToString()) + "ms";
        };
      }
      pinger.SendAsync("1.1.1.1", 1500, null);
    } catch { }                                                // previous ping still running, or no network
  }

  static string NetLine() {
    long rx = 0, tx = 0;
    try {
      foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
        string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
        if (d.Contains("hyper-v") || d.Contains("virtual") || d.Contains("vethernet") || d.Contains("bluetooth")) continue;
        var st = ni.GetIPStatistics();
        rx += st.BytesReceived; tx += st.BytesSent;
      }
    } catch { return null; }
    DateTime now = DateTime.UtcNow;
    string speed = null;
    if (lastRx >= 0) {
      double secs = (now - lastNetAt).TotalSeconds;
      if (secs > 0.5) speed = "↓ " + Mbps((rx - lastRx) * 8 / secs / 1e6) + "   ↑ " + Mbps((tx - lastTx) * 8 / secs / 1e6) + " ميجابت";
    }
    lastRx = rx; lastTx = tx; lastNetAt = now;
    return "البنق " + pingText + (speed != null ? "   •   " + speed : "");
  }

  static string Mbps(double v) {
    if (v < 0) v = 0;
    string s = v < 10 ? v.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(v).ToString(CultureInfo.InvariantCulture);
    return Digits(s).Replace('.', '٫');
  }

  // ---------------------------------------------------------------- control panel (tray icon)
  static void LoadSettings() {
    LoadCfg();
    int v = CfgInt("dua_seconds", duaSeconds);
    if (v >= 5) duaSeconds = v;
    hiddenLayers = CfgBool("hidden", false);
  }

  static void SaveSettings() {
    LoadCfg();                                                 // keep whatever the settings window saved
    SetCfg("dua_seconds", duaSeconds);
    SetCfg("hidden", hiddenLayers);
    SaveCfg();
  }

  static bool MutedUntil(out DateTime until) {
    until = DateTime.MinValue;
    try { until = new DateTime(long.Parse(File.ReadAllText(muteFile).Trim()), DateTimeKind.Utc); } catch { return false; }
    return until > DateTime.UtcNow;
  }

  static void RunHelper(string args) {
    try {
      var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(dir, "adhkar-run.exe"), args);
      psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WorkingDirectory = dir;
      System.Diagnostics.Process.Start(psi);
    } catch { }
  }

  static string PickFile(string title) {
    using (var dlg = new System.Windows.Forms.OpenFileDialog()) {
      dlg.Title = title;
      dlg.InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
      return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.FileName : null;
    }
  }

  static System.Drawing.Icon TrayIcon() {
    try {
      using (var src = new System.Drawing.Bitmap(Path.Combine(dir, "icon.png")))
      using (var bmp = new System.Drawing.Bitmap(src, new System.Drawing.Size(32, 32)))
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    } catch { return System.Drawing.SystemIcons.Information; }
  }

  static void CreateTray() {
    var menu = new System.Windows.Forms.ContextMenuStrip();
    menu.RightToLeft = System.Windows.Forms.RightToLeft.Yes;
    menu.Font = new System.Drawing.Font("Segoe UI", 10.5f);

    var settingsItem = new System.Windows.Forms.ToolStripMenuItem("الإعدادات...", null, delegate { OpenSettings(); });
    settingsItem.Font = new System.Drawing.Font("Segoe UI", 10.5f, System.Drawing.FontStyle.Bold);
    menu.Items.Add(settingsItem);
    var libraryItem = new System.Windows.Forms.ToolStripMenuItem("مكتبة الألعاب", null, delegate { OpenLibrary(); });
    libraryItem.Font = new System.Drawing.Font("Segoe UI", 10.5f, System.Drawing.FontStyle.Bold);
    libraryItem.ShortcutKeyDisplayString = "Ctrl+Alt+G";
    menu.Items.Add(libraryItem);
    menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("شاشة الراحة الحين", null, delegate {
      try { System.Diagnostics.Process.Start(Path.Combine(dir, "adhkar-rest.scr"), "/s"); } catch { }
    }));
    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
    batteryMenu = new System.Windows.Forms.ToolStripMenuItem("شحن الأجهزة");
    batteryMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("جاري القراءة...") { Enabled = false });
    menu.Items.Add(batteryMenu);
    var playMenu = new System.Windows.Forms.ToolStripMenuItem("وقت اللعب");
    menu.Items.Add(playMenu);
    menu.Opening += delegate {                                 // today, this week, and the top games of the week
      playMenu.DropDownItems.Clear();
      var week = PlayThisWeek();
      playMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("اليوم: " + Duration(PlaySecondsToday())) { Enabled = false });
      playMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("هالأسبوع: " + Duration(week.Values.Sum())) { Enabled = false });
      if (week.Count > 0) playMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
      foreach (var g in week.OrderByDescending(x => x.Value).Take(5))
        playMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem(g.Key + ": " + Duration(g.Value)) { Enabled = false });
    };
    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
    menu.Items.Add("الدعاء التالي", null, delegate { NextDua(false, true); });

    var speed = new System.Windows.Forms.ToolStripMenuItem("سرعة تغيير الدعاء");
    int[] secs = { 15, 30, 60, 300 };
    string[] names = { "كل ١٥ ثانية", "كل ٣٠ ثانية", "كل دقيقة", "كل ٥ دقائق" };
    for (int i = 0; i < secs.Length; i++) {
      int s = secs[i];
      var item = new System.Windows.Forms.ToolStripMenuItem(names[i]);
      item.Checked = s == duaSeconds;
      item.Click += delegate {
        duaSeconds = s;
        duaTimer.Interval = TimeSpan.FromSeconds(s);
        foreach (System.Windows.Forms.ToolStripMenuItem other in speed.DropDownItems) other.Checked = other == item;
        SaveSettings();
      };
      speed.DropDownItems.Add(item);
    }
    menu.Items.Add(speed);

    var hide = new System.Windows.Forms.ToolStripMenuItem();
    hide.Click += delegate {
      hiddenLayers = !hiddenLayers;
      foreach (var w in layers) { if (hiddenLayers) w.Hide(); else w.Show(); }
      SaveSettings();
    };
    menu.Items.Add(hide);

    var mute = new System.Windows.Forms.ToolStripMenuItem();
    mute.Click += delegate {
      DateTime until;
      try {
        if (MutedUntil(out until)) File.Delete(muteFile);
        else File.WriteAllText(muteFile, DateTime.UtcNow.AddHours(1).Ticks.ToString());
      } catch { }
    };
    menu.Items.Add(mute);

    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
    menu.Items.Add("إضافة دعاء...", null, delegate { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + listFile + "\""); } catch { } });
    menu.Items.Add("فحص ملف...", null, delegate { string f = PickFile("اختر الملف اللي تبي تفحصه"); if (f != null) RunHelper("ScanFile \"" + f + "\""); });
    menu.Items.Add("جرّب ملف بأمان (صندوق معزول)...", null, delegate { string f = PickFile("اختر الملف اللي تبي تجرّبه بأمان"); if (f != null) RunHelper("Sandbox \"" + f + "\""); });
    menu.Items.Add("تحديث الخلفية الحين", null, delegate { RunHelper("Refresh"); });
    menu.Items.Add("فتح مجلد الأداة", null, delegate { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + dir.TrimEnd('\\') + "\""); } catch { } });
    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
    menu.Items.Add("إعادة تشغيل الأداة", null, delegate { Restart(); });
    menu.Items.Add("إيقاف الأداة (لين تعيد تشغيل الجهاز)", null, delegate { Application.Current.Shutdown(); });

    menu.Opening += delegate {                                  // refresh the labels every time the menu opens
      hide.Text = hiddenLayers ? "إظهار الأداة على سطح المكتب" : "إخفاء الأداة من سطح المكتب";
      DateTime until;
      if (MutedUntil(out until)) {
        int left = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes));
        mute.Text = "إلغاء كتم التذكيرات (باقي " + Digits(left.ToString()) + " دقيقة)";
        mute.Checked = true;
      } else { mute.Text = "كتم التذكيرات ساعة"; mute.Checked = false; }
    };

    tray = new System.Windows.Forms.NotifyIcon();
    tray.Icon = TrayIcon();
    tray.Text = AppName;
    tray.ContextMenuStrip = menu;
    tray.MouseUp += (s, e) => {                                // left click opens the same menu
      if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
      var show = typeof(System.Windows.Forms.NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
      if (show != null) show.Invoke(tray, null);
    };
    tray.Visible = true;
  }

  static void Restart() {
    try {
      if (tray != null) tray.Visible = false;
      instanceMutex.ReleaseMutex();
      System.Diagnostics.Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location, duaSeconds.ToString());
    } catch { }
    Application.Current.Shutdown();
  }

  // ---------------------------------------------------------------- watchdogs
  static void Watch() {
    try { CheckGameOverlay(); } catch { }
    try { TrackPlay(); } catch { }
    if (gpuReady) {
      try {
        uint t;
        if (nvmlDeviceGetTemperature(gpu, 0, out t) == 0) {
          bool hot = t >= HeatLimit;
          hotCount = hot ? hotCount + 1 : 0;
          if (hot != hotShown) {                               // stats line turns red while hot
            hotShown = hot;
            statsText.Foreground = new SolidColorBrush(hot ? Color.FromArgb(235, 235, 90, 90) : Color.FromArgb(105, 255, 255, 255));
          }
          if (hotCount >= 3 && (DateTime.Now - lastHeatAlert).TotalMinutes >= 10) {   // 15 s in a row, at most every 10 min
            lastHeatAlert = DateTime.Now;
            System.Media.SystemSounds.Exclamation.Play();
            Notify("حرارة كرت الشاشة " + Digits(t.ToString()) + "°",
                   "الجهاز يسخن زيادة: خفّف إعدادات اللعبة أو حدّد الفريمات",
                   "وتأكد إن مراوح الكيس شغالة وما فيه شي يسد فتحات الهواء");
          }
        }
      } catch { gpuReady = false; }
    }

    IntPtr fg = GetForegroundWindow();
    if (fg != IntPtr.Zero) {
      var cls = new StringBuilder(64);
      GetClassName(fg, cls, 64);
      if (cls.ToString() == "Ghost") {                         // Windows swaps a frozen window for a "ghost" copy
        try { IntPtr real = HungWindowFromGhostWindow(fg); if (real != IntPtr.Zero) fg = real; } catch { }
      }
    }
    if (fg != IntPtr.Zero && IsHungAppWindow(fg)) {
      if (fg != hungWnd) { hungWnd = fg; hungSince = DateTime.Now; hungAlerted = false; }
      else if (!hungAlerted && (DateTime.Now - hungSince).TotalSeconds >= 15) {
        hungAlerted = true;
        string name = null; uint pid = 0;
        try { GetWindowThreadProcessId(fg, out pid); name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
        string token = null;
        if (name != null && Array.IndexOf(NeverKill, name.ToLowerInvariant()) < 0) {
          token = Guid.NewGuid().ToString("N");               // one-time, expires in 10 min: only our button can use it
          try { File.WriteAllText(Path.Combine(dir, "kill-token.txt"), token + "|" + pid + "|" + name + "|" + DateTime.UtcNow.Ticks, new UTF8Encoding(true)); } catch { token = null; }
        }
        System.Media.SystemSounds.Exclamation.Play();
        Notify("«" + (name ?? "البرنامج") + "» معلّق",
               token != null ? "اضغط «اقفله الحين» وينقفل على طول" : "اقفله من مدير المهام: Ctrl+Shift+Esc",
               "وإذا الشاشة كلها علّقت أو صارت سودا: Win+Ctrl+Shift+B", token);
      }
    } else {
      hungWnd = IntPtr.Zero;
    }
  }

  // Pops a Windows notification through adhkar.ps1 -Mode Notify (reads alert.txt: title / line / line).
  static readonly string[] NeverKill = { "explorer", "dwm", "csrss", "winlogon", "lsass", "services", "svchost", "system", "adhkar-dua", "taskmgr", "searchhost", "startmenuexperiencehost", "shellexperiencehost" };

  static void KillFromToken(string uri) {
    string tokenFile = Path.Combine(dir, "kill-token.txt");
    string[] p;
    try {
      string token = uri.Substring("adhkar-kill:".Length).Trim('/', ' ');
      p = File.ReadAllText(tokenFile, Encoding.UTF8).Trim().Split('|');   // token|pid|name|utcTicks
      File.Delete(tokenFile);                                              // single use
      if (p.Length < 4 || p[0] != token) return;
      if ((DateTime.UtcNow - new DateTime(long.Parse(p[3]), DateTimeKind.Utc)).TotalMinutes > 10) return;
    } catch { return; }
    System.Diagnostics.Process proc;
    try { proc = System.Diagnostics.Process.GetProcessById(int.Parse(p[1])); } catch { return; }   // already closed
    if (!string.Equals(proc.ProcessName, p[2], StringComparison.OrdinalIgnoreCase)) return;          // PID reused by something else
    try {
      proc.Kill();
      proc.WaitForExit(5000);
    } catch {
      Notify("ما قدرت أقفل «" + p[2] + "»", "يمكن يشتغل بصلاحيات أعلى أو محمي بنظام مكافحة الغش",
             "اضغط Ctrl+Shift+Esc وقفله من مدير المهام", null);
    }
  }

  static void Notify(string title, string line1, string line2) { Notify(title, line1, line2, null); }

  static void Notify(string title, string line1, string line2, string killToken) {
    if (InGame()) ShowOverlay(title, line1);                  // Windows hides notifications during games
    try {
      File.WriteAllText(Path.Combine(dir, "alert.txt"), title + "\n" + line1 + "\n" + line2 + (killToken != null ? "\nkill:" + killToken : ""), new UTF8Encoding(true));
      var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(dir, "adhkar-run.exe"), "Notify");
      psi.UseShellExecute = false;
      psi.CreateNoWindow = true;
      psi.WorkingDirectory = dir;
      System.Diagnostics.Process.Start(psi);
    } catch { }
  }

  static string Percent(double v) { return Digits(((int)Math.Round(v)).ToString()) + "٪"; }

  static string Stats() {
    var parts = new List<string>();
    long idle, kernel, user;
    if (GetSystemTimes(out idle, out kernel, out user)) {
      long dIdle = idle - lastIdle, dTotal = (kernel - lastKernel) + (user - lastUser);
      bool first = lastKernel == 0;
      lastIdle = idle; lastKernel = kernel; lastUser = user;
      if (!first && dTotal > 0) parts.Add("المعالج " + Percent(100.0 * (dTotal - dIdle) / dTotal));
    }
    var m = new MEMORYSTATUSEX();
    if (GlobalMemoryStatusEx(m)) parts.Add("الرام " + Percent(m.dwMemoryLoad));
    if (gpuReady) {
      uint t;
      try { if (nvmlDeviceGetTemperature(gpu, 0, out t) == 0) parts.Add("كرت الشاشة " + Digits(t.ToString()) + "°"); } catch { gpuReady = false; }
    }
    return string.Join("   •   ", parts.ToArray());
  }

  // weather.txt is written hourly by adhkar.ps1:  temp|description|yyyy-MM-ddTHH:mm
  static string Weather(DateTime now) {
    try {
      string[] p = File.ReadAllText(weatherFile, Encoding.UTF8).Trim().Split('|');
      if (p.Length < 3) return null;
      DateTime at = DateTime.ParseExact(p[2], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
      if ((now - at).TotalHours > 3) return null;                     // stale: hide rather than lie
      string city = p.Length > 3 && p[3].Trim().Length > 0 ? p[3].Trim() : "مكة";
      return city + " " + Digits(p[0]) + "°" + (p[1].Length > 0 ? " " + p[1] : "");
    } catch { return null; }
  }

  // Umm al-Qura calendar: days left to Ramadan / Eid al-Fitr / Eid al-Adha
  static string HijriCountdown(DateTime today) {
    try {
      var cal = new UmAlQuraCalendar();
      int y = cal.GetYear(today), m = cal.GetMonth(today), d = cal.GetDayOfMonth(today);
      if (m == 9) return "رمضان كريم · اليوم " + Digits(d.ToString()) + " من رمضان";
      if (m == 10 && d <= 3) return "عيد فطر مبارك";
      if (m == 12 && d >= 10 && d <= 13) return "عيد أضحى مبارك";
      var targets = new List<KeyValuePair<DateTime, string>>();
      foreach (int yy in new[] { y, y + 1 }) {
        AddTarget(targets, cal, yy, 9, 1, "رمضان");
        AddTarget(targets, cal, yy, 10, 1, "عيد الفطر");
        AddTarget(targets, cal, yy, 12, 10, "عيد الأضحى");
      }
      var next = targets.Where(t => t.Key > today).OrderBy(t => t.Key).FirstOrDefault();
      if (next.Value == null) return null;
      return "باقي " + DaysText((int)(next.Key - today).TotalDays) + " على " + next.Value;
    } catch { return null; }
  }

  static void AddTarget(List<KeyValuePair<DateTime, string>> list, System.Globalization.Calendar cal, int y, int m, int d, string name) {
    try { list.Add(new KeyValuePair<DateTime, string>(cal.ToDateTime(y, m, d, 0, 0, 0, 0), name)); } catch { }
  }

  static string DaysText(int n) {
    if (n == 1) return "يوم";
    if (n == 2) return "يومان";
    int r = n % 100;
    if (r >= 3 && r <= 10) return Digits(n.ToString()) + " أيام";
    if (r >= 11 && r <= 99) return Digits(n.ToString()) + " يومًا";
    return Digits(n.ToString()) + " يوم";
  }

  static bool LoadPrayers() {
    try {
      DateTime stamp = File.GetLastWriteTimeUtc(prayersFile);
      if (stamp != prayersStamp) {
        prayersStamp = stamp;
        prayers = File.ReadAllLines(prayersFile, Encoding.UTF8)
          .Select(l => l.Split('|')).Where(p => p.Length == 2)
          .Select(p => new KeyValuePair<DateTime, string>(DateTime.ParseExact(p[0].Trim(), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), p[1].Trim()))
          .OrderBy(p => p.Key).ToList();
      }
      return true;
    } catch { return false; }
  }

  static string Countdown(DateTime now) {
    if (!LoadPrayers()) return null;

    foreach (var p in prayers) {                               // just started: show it for 20 minutes
      double since = (now - p.Key).TotalMinutes;
      if (since >= 0 && since < 20) return "حان الآن وقت صلاة " + p.Value;
    }
    foreach (var p in prayers) {
      if (p.Key <= now) continue;
      return "باقي " + MinutesText((int)Math.Ceiling((p.Key - now).TotalMinutes)) + " على صلاة " + p.Value;
    }
    return null;
  }

  // ---------------------------------------------------------------- dua
  static void NextDua(bool first, bool force = false) {
    if (!first && !force && Busy()) return;
    string dua = PickDua();
    if (dua == null) return;
    if (first) { ShowDua(dua); return; }
    for (int i = 0; i < duaTexts.Count; i++) {
      var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(700));
      Timeline.SetDesiredFrameRate(fadeOut, 30);
      if (i == 0) fadeOut.Completed += delegate { ShowDua(dua); };   // all screens change together
      duaTexts[i].BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }
  }

  static void ShowDua(string dua) {
    Fit(dua);
    foreach (var t in duaTexts) {
      var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(900));
      Timeline.SetDesiredFrameRate(fadeIn, 30);
      t.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }
  }

  // Shrink long duas, and balance the lines so no word is left alone.
  static void Fit(string dua) {
    var tf = new Typeface(new FontFamily("Sakkal Majalla"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    double size = 62, width = 940;
    while (true) {
      var ft = new FormattedText(dua, Ar, FlowDirection.RightToLeft, tf, size, Brushes.White, 1.0);
      int lines = (int)Math.Ceiling(ft.Width / 940);
      if (lines < 1) lines = 1;
      width = Math.Min(940, ft.Width / lines + 90);
      ft.MaxTextWidth = width;
      if (ft.Height <= 330 || size <= 30) break;
      size -= 4;
    }
    foreach (var t in duaTexts) {
      t.FontSize = size * scale;
      t.Width = width * scale;
      t.Text = dua;
    }
  }

  // Shuffle-bag: every dua shows once before any repeats; reloads when adhkar.txt changes.
  static string PickDua() {
    try {
      DateTime stamp = File.GetLastWriteTimeUtc(listFile);
      if (stamp != listStamp || bag.Count == 0) {
        listStamp = stamp;
        var lines = File.ReadAllLines(listFile, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        for (int i = lines.Count - 1; i > 0; i--) { int j = Rng.Next(i + 1); string t = lines[i]; lines[i] = lines[j]; lines[j] = t; }
        if (lines.Count > 1 && lines[0] == last) { string t = lines[0]; lines[0] = lines[lines.Count - 1]; lines[lines.Count - 1] = t; }
        bag = lines;
      }
      if (bag.Count == 0) return null;
      string d = bag[0];
      bag.RemoveAt(0);
      last = d;
      return d;
    } catch { return null; }
  }
}
