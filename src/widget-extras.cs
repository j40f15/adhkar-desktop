// adhkar-dua.exe, part 3: the rest screen (a real Windows screensaver: adhkar-rest.scr = this exe with /s),
// the game shelf (last played Steam games, click to launch) and play-time tracking.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static partial class DuaWidget {
  // ================================================================ rest screen (screensaver)
  // Windows starts it after the idle time chosen in the settings, and never while a video or game keeps the display awake.
  // test = true: ignores the keyboard and mouse and closes itself after 8 s (for checking how it looks)
  static void RunRestScreen() { RunRestScreen(false); }
  static void RunRestScreen(bool test) {
    cfgFile = Path.Combine(dir, "settings.ini");
    LoadCfg();
    Gold = ThemeColor(CfgStr("theme", "gold"), 230);
    listFile = Path.Combine(dir, "adhkar.txt");
    prayersFile = Path.Combine(dir, "next-prayers.txt");
    weatherFile = Path.Combine(dir, "weather.txt");

    var app = new Application();
    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    scale = SystemParameters.PrimaryScreenHeight / 1080.0;
    double dipPerPx = SystemParameters.PrimaryScreenHeight / System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;

    Point? firstMouse = null;
    DateTime armedAt = DateTime.Now.AddMilliseconds(1500);      // ignore the hand still on the mouse for a moment
    Action quit = delegate { if (!test && DateTime.Now >= armedAt) app.Shutdown(); };
    if (test) { var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) }; stop.Tick += delegate { app.Shutdown(); }; stop.Start(); }
    Window main = null;
    foreach (var scr in System.Windows.Forms.Screen.AllScreens) {
      var w = new Window {
        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
        Background = Brushes.Black, Cursor = Cursors.None, Title = "Hisn",
        Left = scr.Bounds.X * dipPerPx, Top = scr.Bounds.Y * dipPerPx, Width = scr.Bounds.Width * dipPerPx, Height = scr.Bounds.Height * dipPerPx
      };
      w.KeyDown += delegate { quit(); };
      w.MouseDown += delegate { quit(); };
      w.MouseMove += (s, e) => {                               // ignore the tiny jitter some mice send
        Point p = e.GetPosition(null);
        if (firstMouse == null || DateTime.Now < armedAt) { firstMouse = p; return; }
        if (Math.Abs(p.X - firstMouse.Value.X) + Math.Abs(p.Y - firstMouse.Value.Y) > 12) quit();
      };
      if (scr.Primary) main = w;
      w.Show();
    }
    if (main == null) { app.Shutdown(); return; }

    // content: big clock, dates, next prayer, a dua - drifting slowly so nothing burns into the screen
    var clock = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Light, FontSize = 190 * scale, Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.RightToLeft };
    var date = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 28 * scale, Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.RightToLeft, Margin = new Thickness(0, -10 * scale, 0, 0) };
    var next = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 30 * scale, Foreground = new SolidColorBrush(Gold), HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.RightToLeft, Margin = new Thickness(0, 26 * scale, 0, 0) };
    var dua = new TextBlock { FontFamily = new FontFamily("Sakkal Majalla"), FontWeight = FontWeights.Bold, FontSize = 54 * scale, Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 1150 * scale, FlowDirection = FlowDirection.RightToLeft, Margin = new Thickness(0, 70 * scale, 0, 0) };
    var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    stack.Children.Add(clock); stack.Children.Add(date); stack.Children.Add(next); stack.Children.Add(dua);
    var drift = new TranslateTransform();
    stack.RenderTransform = drift;
    var grid = new Grid(); grid.Children.Add(stack);
    main.Content = grid;

    Action tick = delegate {
      DateTime now = DateTime.Now;
      int h12 = now.Hour % 12; if (h12 == 0) h12 = 12;
      clock.Inlines.Clear();
      clock.Inlines.Add(new System.Windows.Documents.Run(Digits(h12 + ":" + now.Minute.ToString("00"))));
      clock.Inlines.Add(new System.Windows.Documents.Run(" " + (now.Hour < 12 ? "ص" : "م")) { FontSize = 60 * scale });
      string hijri = "";
      try { hijri = " · " + Digits(now.ToString("d MMMM yyyy", Ar)) + "هـ"; } catch { }
      date.Text = Days[(int)now.DayOfWeek] + "، " + Digits(now.Day.ToString()) + " " + Months[now.Month - 1] + hijri;
      next.Text = Countdown(now) ?? "";
    };
    tick();
    string first = PickDua(); if (first != null) dua.Text = first;

    var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
    clockTimer.Tick += delegate { tick(); };
    clockTimer.Start();
    var duaTimer2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
    duaTimer2.Tick += delegate {
      string d = PickDua(); if (d == null) return;
      var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(800));
      fadeOut.Completed += delegate { dua.Text = d; dua.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(1000))); };
      dua.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    };
    duaTimer2.Start();
    var driftTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
    driftTimer.Tick += delegate {
      var ax = new DoubleAnimation(Rng.Next(-70, 71) * scale, TimeSpan.FromSeconds(8)); Timeline.SetDesiredFrameRate(ax, 20);
      var ay = new DoubleAnimation(Rng.Next(-45, 46) * scale, TimeSpan.FromSeconds(8)); Timeline.SetDesiredFrameRate(ay, 20);
      drift.BeginAnimation(TranslateTransform.XProperty, ax);
      drift.BeginAnimation(TranslateTransform.YProperty, ay);
    };
    driftTimer.Start();
    main.Activate(); main.Focus();
    app.Run();
  }

  // ================================================================ Steam games (read by the game library in game-library.cs)
  class SteamGame { public string AppId, Name, InstallDir, Lib; public long LastPlayed; public bool Installed; }

  static string SteamRoot() {
    string steam = null;
    try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) { if (k != null) steam = k.GetValue("SteamPath") as string; } } catch { }
    if (string.IsNullOrEmpty(steam)) steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
    return steam.Replace('/', '\\');
  }

  static string AcfValue(string text, string key) {
    var m = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
    return m.Success ? m.Groups[1].Value : "";
  }

  static List<SteamGame> ReadSteamGames() {
    var games = new List<SteamGame>();
    string steam = SteamRoot();
    var libs = new List<string> { steam };
    try {
      string vdf = File.ReadAllText(Path.Combine(steam, @"steamapps\libraryfolders.vdf"));
      foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"")) {
        string p = m.Groups[1].Value.Replace(@"\\", @"\");
        if (!libs.Any(l => string.Equals(l.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) libs.Add(p);
      }
    } catch { }
    foreach (string lib in libs) {
      string apps = Path.Combine(lib, "steamapps");
      if (!Directory.Exists(apps)) continue;
      foreach (string f in Directory.GetFiles(apps, "appmanifest_*.acf")) {
        try {
          string t = File.ReadAllText(f);
          var g = new SteamGame { AppId = AcfValue(t, "appid"), Name = AcfValue(t, "name"), InstallDir = AcfValue(t, "installdir"), Lib = lib };
          long lp; long.TryParse(AcfValue(t, "LastPlayed"), out lp); g.LastPlayed = lp;
          int sf; int.TryParse(AcfValue(t, "StateFlags"), out sf); g.Installed = (sf & 4) != 0;
          string n = g.Name.ToLowerInvariant();
          bool tool = g.AppId == "228980" || n.Contains("redistributable") || n.StartsWith("proton") || n.Contains("steam linux runtime")
                      || n.Contains("steamvr") || n.Contains("dedicated server") || n.Contains(" sdk") || n.EndsWith("sdk");
          if (!tool && g.AppId.Length > 0) games.Add(g);
        } catch { }
      }
    }
    return games;
  }

  static readonly HashSet<string> noCover = new HashSet<string>();   // ids with no cover anywhere (don't ask the CDN again this run)

  // Cover art (tall 600x900 capsule): Steam's own library cache, else Steam's public CDN (only the game's id is sent).
  // Copied to Adhkar\covers. Newer Steam keeps it as librarycache\<id>\<hash>\library_capsule.jpg.
  static string CoverFor(SteamGame g) {
    string cacheDir = Path.Combine(dir, "covers");
    string cached = Path.Combine(cacheDir, g.AppId + ".jpg");
    if (File.Exists(cached)) return cached;
    try {
      Directory.CreateDirectory(cacheDir);
      string lc = Path.Combine(SteamRoot(), @"appcache\librarycache");
      string sub = Path.Combine(lc, g.AppId);
      var found = new List<string>();
      foreach (string pattern in new[] { "library_600x900*.jpg", "library_capsule*.jpg" }) {
        if (Directory.Exists(sub)) found.AddRange(Directory.GetFiles(sub, pattern, SearchOption.AllDirectories).Where(f => !f.Contains("_blur")));
        if (Directory.Exists(lc)) found.AddRange(Directory.GetFiles(lc, g.AppId + "_" + pattern));
      }
      if (found.Count > 0) { File.Copy(found[0], cached, true); return cached; }
    } catch { }
    if (noCover.Contains(g.AppId)) return null;
    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
    foreach (string url in new[] { "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/" + g.AppId + "/library_600x900.jpg",
                                   "https://cdn.cloudflare.steamstatic.com/steam/apps/" + g.AppId + "/library_600x900.jpg" }) {
      try {
        using (var wc = new System.Net.WebClient()) wc.DownloadFile(url, cached);
        return cached;
      } catch { try { File.Delete(cached); } catch { } }
    }
    noCover.Add(g.AppId);
    return null;
  }

  // ================================================================ play time
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

  static readonly Dictionary<string, string> steamDirs = new Dictionary<string, string>();       // installdir -> game name
  static readonly Dictionary<string, Dictionary<string, int>> playLog = new Dictionary<string, Dictionary<string, int>>();   // date -> game -> seconds
  static DateTime playSaved = DateTime.MinValue;

  static string ProcessPath(uint pid) {
    IntPtr h = OpenProcess(0x1000, false, pid);                // PROCESS_QUERY_LIMITED_INFORMATION
    if (h == IntPtr.Zero) return null;
    try { var sb = new StringBuilder(1024); int size = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null; }
    finally { CloseHandle(h); }
  }

  // The game in front right now: anything under a Steam library's steamapps\common, or any app running fullscreen 3D.
  static string CurrentGame() {
    IntPtr fg = GetForegroundWindow();
    if (fg == IntPtr.Zero) return null;
    uint pid; GetWindowThreadProcessId(fg, out pid);
    if (pid == 0 || pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return null;
    string path = ProcessPath(pid);
    if (path != null) {
      int i = path.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase);
      if (i >= 0) {
        string rest = path.Substring(i + 18);
        int slash = rest.IndexOf('\\');
        string folder = slash > 0 ? rest.Substring(0, slash) : rest;
        string name;
        lock (steamDirs) { if (!steamDirs.TryGetValue(folder.ToLowerInvariant(), out name)) name = folder; }
        return name;
      }
      lock (gameDirs) foreach (var d in gameDirs) if (path.StartsWith(d.Key, StringComparison.OrdinalIgnoreCase)) return d.Value;   // Epic / added by hand
    }
    int st;
    if (SHQueryUserNotificationState(out st) == 0 && st == 3 && path != null)   // fullscreen Direct3D app outside Steam
      return Path.GetFileNameWithoutExtension(path);
    return null;
  }

  static void LoadPlayLog() {
    try {
      foreach (string line in File.ReadAllLines(Path.Combine(dir, "playtime.txt"), Encoding.UTF8)) {
        string[] p = line.Split('|');
        if (p.Length < 3) continue;
        if (!playLog.ContainsKey(p[0])) playLog[p[0]] = new Dictionary<string, int>();
        playLog[p[0]][p[1]] = int.Parse(p[2]);
      }
    } catch { }
  }

  static void SavePlayLog() {
    try {
      string cutoff = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd");
      var lines = new List<string>();
      foreach (var d in playLog.Where(d => string.CompareOrdinal(d.Key, cutoff) >= 0)) foreach (var g in d.Value) lines.Add(d.Key + "|" + g.Key.Replace("|", " ") + "|" + g.Value);
      File.WriteAllLines(Path.Combine(dir, "playtime.txt"), lines.ToArray(), new UTF8Encoding(true));
    } catch { }
  }

  // called every 5 s from Watch()
  static void TrackPlay() {
    string game = CurrentGame();
    if (game == null) return;
    string day = DateTime.Today.ToString("yyyy-MM-dd");
    Dictionary<string, int> d;
    if (!playLog.TryGetValue(day, out d)) { d = new Dictionary<string, int>(); playLog[day] = d; }
    int s; d.TryGetValue(game, out s); d[game] = s + 5;
    if ((DateTime.Now - playSaved).TotalSeconds >= 60) { playSaved = DateTime.Now; SavePlayLog(); }
  }

  static int PlaySecondsToday() {
    Dictionary<string, int> d;
    return playLog.TryGetValue(DateTime.Today.ToString("yyyy-MM-dd"), out d) ? d.Values.Sum() : 0;
  }

  static Dictionary<string, int> PlayThisWeek() {
    var sum = new Dictionary<string, int>();
    for (int i = 0; i < 7; i++) {
      Dictionary<string, int> d;
      if (!playLog.TryGetValue(DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd"), out d)) continue;
      foreach (var g in d) { int s; sum.TryGetValue(g.Key, out s); sum[g.Key] = s + g.Value; }
    }
    return sum;
  }

  static string Duration(int seconds) {
    int m = seconds / 60;
    if (m < 60) return Digits(m.ToString()) + " د";
    return Digits((m / 60) + ":" + (m % 60).ToString("00"));
  }

}
