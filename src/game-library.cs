// adhkar-dua.exe, part 4: the game library window (Ctrl+Alt+G or the tray icon). Nothing about games is drawn on the desktop.
// Steam and Epic games are found by themselves, anything else can be added by hand. One click on a cover starts the game.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

public static partial class DuaWidget {
  class Game {
    public string Key, Name, Source, LaunchUri, Exe, Dir, Cover;
    public long LastPlayed;                                       // unix seconds
    public int SteamMinutes;                                      // Steam's own total play time
    public bool Hidden;
  }

  static List<Game> libGames = new List<Game>();
  static readonly List<KeyValuePair<string, string>> gameDirs = new List<KeyValuePair<string, string>>();   // install folder\ -> name (play time)
  static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
  static bool libLoading, libDialogOpen, libShowHidden, libHotkey;
  static Window libWin;
  static WrapPanel libGrid;
  static TextBox libSearch;
  static TextBlock libCount, libEmpty;
  static Border libHiddenButton;
  static string shownLib;
  static Dictionary<string, int> libWeek = new Dictionary<string, int>();

  // ---------------------------------------------------------------- hotkey
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
  const int LibHotkeyId = 0xB001;

  static void RegisterLibraryHotkey() {
    libHotkey = RegisterHotKey(IntPtr.Zero, LibHotkeyId, 0x0001 | 0x0002 | 0x4000, 0x47);   // Ctrl+Alt+G, no auto-repeat
    ComponentDispatcher.ThreadFilterMessage += delegate (ref MSG m, ref bool handled) {
      if (m.message == 0x0312 && m.wParam.ToInt32() == LibHotkeyId) { handled = true; OpenLibrary(); }
    };
  }

  // ---------------------------------------------------------------- finding games
  static string LibFile(string name) { return Path.Combine(dir, name); }
  static string CustomCoverDir() { return Path.Combine(Path.Combine(dir, "covers"), "custom"); }

  static string SafeName(string key) {                           // stable file name for a game key
    uint h = 2166136261;
    foreach (char c in key) { h ^= c; h *= 16777619; }
    string s = Regex.Replace(key, "[^A-Za-z0-9_.-]", "_");
    if (s.Length > 40) s = s.Substring(0, 40);
    return s + "-" + h.ToString("x8");
  }

  static HashSet<string> LibHidden() {
    var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try { foreach (string l in File.ReadAllLines(LibFile("library-hidden.txt"), Encoding.UTF8)) if (l.Trim().Length > 0) s.Add(l.Trim()); } catch { }
    return s;
  }

  static string JsonStr(string json, string key) {
    var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
    if (!m.Success) return "";
    try { return Regex.Unescape(m.Groups[1].Value); } catch { return m.Groups[1].Value; }
  }

  // userdata\<id>\config\localconfig.vdf: per app id "LastPlayed" (unix time) and "Playtime" (minutes). Newest file = the signed-in Steam user.
  static Dictionary<string, long[]> ReadSteamPlaytimes() {
    var r = new Dictionary<string, long[]>();
    try {
      string ud = Path.Combine(SteamRoot(), "userdata");
      if (!Directory.Exists(ud)) return r;
      string best = null; DateTime bt = DateTime.MinValue;
      foreach (string d in Directory.GetDirectories(ud)) {
        string f = Path.Combine(d, @"config\localconfig.vdf");
        if (File.Exists(f) && File.GetLastWriteTimeUtc(f) > bt) { bt = File.GetLastWriteTimeUtc(f); best = f; }
      }
      if (best == null) return r;
      string t = File.ReadAllText(best);
      foreach (Match m in Regex.Matches(t, "\"(\\d+)\"\\s*\\{")) {
        int start = m.Index + m.Length, depth = 1, i = start, nested = -1, end = Math.Min(t.Length, start + 4000);
        for (; i < end && depth > 0; i++) {
          if (t[i] == '{') { if (depth == 1 && nested < 0) nested = i; depth++; }
          else if (t[i] == '}') depth--;
        }
        if (depth != 0) continue;                                 // not a small app block
        string own = t.Substring(start, (nested >= 0 ? nested : i - 1) - start);
        var lp = Regex.Match(own, "\"LastPlayed\"\\s+\"(\\d+)\"");
        var pt = Regex.Match(own, "\"Playtime\"\\s+\"(\\d+)\"");
        if (!lp.Success && !pt.Success) continue;
        long a = 0, b = 0;
        if (lp.Success) long.TryParse(lp.Groups[1].Value, out a);
        if (pt.Success) long.TryParse(pt.Groups[1].Value, out b);
        r[m.Groups[1].Value] = new long[] { a, b };
      }
    } catch { }
    return r;
  }

  // Runs off the UI thread (cover downloads can take a moment the first time).
  static List<Game> LoadGames() {
    var list = new List<Game>();
    // Steam
    try {
      var times = ReadSteamPlaytimes();
      var steam = ReadSteamGames();
      lock (steamDirs) { steamDirs.Clear(); foreach (var s in steam) if (s.InstallDir.Length > 0) steamDirs[s.InstallDir.ToLowerInvariant()] = s.Name; }
      foreach (var s in steam) {
        if (!s.Installed) continue;
        var g = new Game { Key = "steam:" + s.AppId, Name = s.Name, Source = "Steam", LaunchUri = "steam://rungameid/" + s.AppId, LastPlayed = s.LastPlayed };
        if (s.Lib != null && s.InstallDir.Length > 0) g.Dir = Path.Combine(Path.Combine(s.Lib, @"steamapps\common"), s.InstallDir);
        long[] tm;
        if (times.TryGetValue(s.AppId, out tm)) { g.LastPlayed = Math.Max(g.LastPlayed, tm[0]); g.SteamMinutes = (int)tm[1]; }
        g.Cover = CoverFor(s);
        list.Add(g);
      }
    } catch { }
    // Epic Games
    try {
      string man = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
      if (Directory.Exists(man)) foreach (string f in Directory.GetFiles(man, "*.item")) {
        try {
          string j = File.ReadAllText(f);
          string name = JsonStr(j, "DisplayName"), app = JsonStr(j, "AppName");
          string loc = JsonStr(j, "InstallLocation").Replace('/', '\\'), exe = JsonStr(j, "LaunchExecutable").Replace('/', '\\');
          if (name.Length == 0 || app.Length == 0 || exe.Length == 0 || !Directory.Exists(loc)) continue;
          string id = JsonStr(j, "CatalogNamespace") + ":" + JsonStr(j, "CatalogItemId") + ":" + app;
          list.Add(new Game { Key = "epic:" + app, Name = name, Source = "Epic", Dir = loc, Exe = Path.Combine(loc, exe),
                              LaunchUri = "com.epicgames.launcher://apps/" + Uri.EscapeDataString(id) + "?action=launch&silent=true" });
        } catch { }
      }
    } catch { }
    // added by hand: library.txt = name|path (exe, or a .lnk / .url shortcut)
    try {
      string lf = LibFile("library.txt");
      if (File.Exists(lf)) foreach (string l in File.ReadAllLines(lf, Encoding.UTF8)) {
        string[] p = l.Split('|');
        if (p.Length < 2) continue;
        string path = p[1].Trim();
        if (path.Length == 0 || !File.Exists(path)) continue;
        var g = new Game { Key = "exe:" + path.ToLowerInvariant(), Name = p[0].Trim(), Source = "مضافة", Exe = path };
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) g.Dir = Path.GetDirectoryName(path);
        if (!list.Any(x => x.Key == g.Key)) list.Add(g);
      }
    } catch { }

    var hidden = LibHidden();
    var dirs = new List<KeyValuePair<string, string>>();
    string custom = CustomCoverDir();
    foreach (var g in list) {
      g.Hidden = hidden.Contains(g.Key);
      foreach (string ext in new[] { ".jpg", ".jpeg", ".png" }) {
        string c = Path.Combine(custom, SafeName(g.Key) + ext);
        if (File.Exists(c)) { g.Cover = c; break; }
      }
      if (!string.IsNullOrEmpty(g.Dir)) dirs.Add(new KeyValuePair<string, string>(g.Dir.TrimEnd('\\') + "\\", g.Name));
    }
    lock (gameDirs) { gameDirs.Clear(); gameDirs.AddRange(dirs); }
    return list;
  }

  // last day this game shows up in playtime.txt (UI thread only: playLog belongs to it)
  static long LastSeen(string name) {
    string best = null;
    foreach (var d in playLog) if (d.Value.ContainsKey(name) && (best == null || string.CompareOrdinal(d.Key, best) > 0)) best = d.Key;
    DateTime day;
    if (best == null || !DateTime.TryParseExact(best, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return 0;
    return (long)(day.AddHours(12).ToUniversalTime() - UnixEpoch).TotalSeconds;
  }

  static void LoadLibraryAsync() {
    if (libLoading) return;
    libLoading = true;
    var t = new System.Threading.Thread(delegate () {
      List<Game> games = null;
      try { games = LoadGames(); } catch { }
      Application.Current.Dispatcher.BeginInvoke(new Action(delegate {
        libLoading = false;
        if (games != null) {
          foreach (var g in games) { long seen = LastSeen(g.Name); if (seen > g.LastPlayed) g.LastPlayed = seen; }
          libGames = games.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        if (libWin != null) FillLibrary();
      }));
    });
    t.IsBackground = true;
    t.Start();
  }

  // ---------------------------------------------------------------- the window
  static void OpenLibrary() {
    if (libWin != null) { libWin.Activate(); if (libSearch != null) libSearch.Focus(); return; }
    var wa = SystemParameters.WorkArea;
    var w = new Window {
      Title = "مكتبة الألعاب", Width = Math.Min(1220, wa.Width - 80), Height = Math.Min(790, wa.Height - 60),
      WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
      WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true, Topmost = true,
      FontFamily = new FontFamily("Segoe UI"), FlowDirection = FlowDirection.RightToLeft, Opacity = 0
    };
    var frame = new Border { CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromRgb(16, 16, 18)),
                             BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1) };
    var grid = new Grid();
    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    grid.RowDefinitions.Add(new RowDefinition());
    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    // header: title + count on the right, search and buttons on the left
    var header = new DockPanel { Margin = new Thickness(32, 26, 26, 16), LastChildFill = false };
    var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    titles.Children.Add(new TextBlock { Text = "مكتبة الألعاب", FontSize = 29, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
    libCount = new TextBlock { FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 0, 7) };
    titles.Children.Add(libCount);
    DockPanel.SetDock(titles, Dock.Left);
    header.Children.Add(titles);

    var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    var sbox = new Grid { Width = 280 };
    var hint = new TextBlock { Text = "دوّر على لعبة...", Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)), FontSize = 15, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    libSearch = new TextBox { Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                              FontSize = 15, Padding = new Thickness(12, 9, 12, 9), VerticalContentAlignment = VerticalAlignment.Center };
    sbox.Children.Add(hint); sbox.Children.Add(libSearch);
    tools.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), Child = sbox });
    libSearch.TextChanged += delegate { hint.Visibility = libSearch.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; ApplyLibFilter(); };
    libHiddenButton = PillButton("المخفية", delegate { libShowHidden = !libShowHidden; FillLibrary(); });
    libHiddenButton.Visibility = Visibility.Collapsed;
    tools.Children.Add(libHiddenButton);
    tools.Children.Add(PillButton("+  أضف لعبة", AddGameByHand));
    tools.Children.Add(PillButton("✕", CloseLibrary));
    DockPanel.SetDock(tools, Dock.Right);
    header.Children.Add(tools);
    grid.Children.Add(header);

    // covers
    libGrid = new WrapPanel { Margin = new Thickness(22, 0, 22, 10) };
    var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = libGrid, Focusable = false };
    libEmpty = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)), FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed };
    var body = new Grid();
    body.Children.Add(scroll); body.Children.Add(libEmpty);
    Grid.SetRow(body, 1);
    grid.Children.Add(body);

    var foot = new TextBlock { FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(105, 105, 105)), Margin = new Thickness(32, 6, 32, 16),
      Text = (libHotkey ? "Ctrl+Alt+G يفتح المكتبة   ·   " : "") + "Enter يشغّل أول لعبة   ·   Esc يقفلها   ·   كلك يمين على اللعبة لخيارات أكثر" };
    Grid.SetRow(foot, 2);
    grid.Children.Add(foot);

    frame.Child = grid;
    w.Content = frame;
    w.PreviewKeyDown += (s, e) => {
      if (e.Key == Key.Escape) { e.Handled = true; CloseLibrary(); }
      else if (e.Key == Key.Enter) {
        e.Handled = true;
        var first = libGrid.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Visibility == Visibility.Visible);
        if (first != null) { LaunchGame((Game)first.Tag); CloseLibrary(); }
      }
    };
    w.Deactivated += delegate { if (!libDialogOpen) CloseLibrary(); };
    w.Closed += delegate { libWin = null; libGrid = null; libSearch = null; libHiddenButton = null; shownLib = null; libShowHidden = false; };
    w.Loaded += delegate {
      w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
      libSearch.Focus();
    };
    libWin = w;
    libWeek = PlayThisWeek();
    FillLibrary();
    w.Show();
    w.Activate();
    LoadLibraryAsync();                                           // picks up games installed since the last look
  }

  static void CloseLibrary() {
    if (libWin == null) return;
    var w = libWin;
    libWin = null;
    try { w.Close(); } catch { }
  }

  static Border PillButton(string text, Action click) {
    var normal = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
    var hover = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
    var b = new Border { CornerRadius = new CornerRadius(10), Background = normal, Padding = new Thickness(15, 9, 15, 9), Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand,
                         Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 15, VerticalAlignment = VerticalAlignment.Center } };
    b.MouseEnter += delegate { b.Background = hover; };
    b.MouseLeave += delegate { b.Background = normal; };
    b.MouseLeftButtonUp += delegate { click(); };
    return b;
  }

  static void FillLibrary() {
    if (libGrid == null) return;
    string sig = libShowHidden + "|" + string.Join("|", libGames.Select(g => g.Key + (g.Hidden ? "#h" : "") + "#" + (g.Cover ?? "")).ToArray());
    if (sig != shownLib) {
      shownLib = sig;
      libGrid.Children.Clear();
      foreach (var g in libGames) if (libShowHidden ? g.Hidden : !g.Hidden) libGrid.Children.Add(GameCard(g));
    }
    ApplyLibFilter();
  }

  static string GamesText(int n) {
    if (n == 1) return "لعبة وحدة";
    if (n == 2) return "لعبتين";
    if (n >= 3 && n <= 10) return Digits(n.ToString()) + " ألعاب";
    return Digits(n.ToString()) + " لعبة";
  }

  static string HoursText2(int h) {
    if (h == 1) return "ساعة";
    if (h == 2) return "ساعتين";
    if (h >= 3 && h <= 10) return Digits(h.ToString()) + " ساعات";
    return Digits(h.ToString()) + " ساعة";
  }

  // "over" typed while the keyboard is on Arabic arrives as "خرثق": map it back to the English keys too
  const string ArabicKeys = "ضصثقفغعهخحجدشسيبلاتنمكطئءؤرىةوزظذ";
  const string LatinKeys  = "qwertyuiop[]asdfghjkl;'zxcvnm,./`";
  static string FromArabicKeyboard(string s) {
    var sb = new StringBuilder();
    s = s.Replace("لا", "b");
    foreach (char c in s) { int i = ArabicKeys.IndexOf(c); sb.Append(i >= 0 ? LatinKeys[i] : c); }
    return sb.ToString();
  }

  static void ApplyLibFilter() {
    if (libGrid == null) return;
    string q = libSearch == null ? "" : libSearch.Text.Trim();
    string q2 = FromArabicKeyboard(q);
    int shown = 0;
    foreach (FrameworkElement c in libGrid.Children) {
      var g = (Game)c.Tag;
      bool ok = q.Length == 0 || g.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || g.Name.IndexOf(q2, StringComparison.CurrentCultureIgnoreCase) >= 0;
      c.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
      if (ok) shown++;
    }
    int total = libGames.Count(g => !g.Hidden), hiddenCount = libGames.Count(g => g.Hidden);
    if (libCount != null) libCount.Text = libShowHidden ? "المخفية: " + GamesText(hiddenCount) : (libLoading && libGames.Count == 0 ? "جاري البحث عن ألعابك..." : (total == 0 ? "" : GamesText(total)));
    if (libHiddenButton != null) {
      libHiddenButton.Visibility = hiddenCount > 0 || libShowHidden ? Visibility.Visible : Visibility.Collapsed;
      ((TextBlock)libHiddenButton.Child).Text = libShowHidden ? "رجوع للمكتبة" : "المخفية (" + Digits(hiddenCount.ToString()) + ")";
    }
    if (libEmpty != null) {
      libEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
      libEmpty.Text = q.Length > 0 ? "ما فيه لعبة بهالاسم" : (libLoading ? "جاري البحث عن ألعابك..." : "ما لقيت ألعاب.\nاضغط «أضف لعبة» واختر ملف تشغيل أي لعبة عندك.");
    }
  }

  static bool HasArabic(string s) { foreach (char c in s) if (c >= 0x0600 && c <= 0x06FF) return true; return false; }

  static BitmapImage LoadPicture(string path, int width) {
    try {
      var b = new BitmapImage();
      b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
      b.DecodePixelWidth = width; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
      return b;
    } catch { return null; }
  }

  [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint sizes);
  [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

  static ImageSource ExeIcon(string exe) {                        // the game's own big icon, for games without a cover
    if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
    try {
      IntPtr big, small;
      if (SHDefExtractIcon(exe, 0, 0, out big, out small, (16u << 16) | 256u) != 0 || big == IntPtr.Zero) return null;
      try {
        var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(big, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
      } finally { DestroyIcon(big); if (small != IntPtr.Zero) DestroyIcon(small); }
    } catch { return null; }
  }

  static string GameSub(Game g) {
    string src = g.Source;
    int week;
    if (libWeek.TryGetValue(g.Name, out week) && week >= 60) return src + "  ·  هالأسبوع " + Duration(week);
    if (g.SteamMinutes >= 60) return src + "  ·  " + HoursText2(g.SteamMinutes / 60) + " لعب";
    if (g.SteamMinutes > 0) return src + "  ·  " + Digits(g.SteamMinutes.ToString()) + " د لعب";
    return src;
  }

  static FrameworkElement GameCard(Game g) {
    const double w = 170, h = 255;
    var card = new StackPanel { Width = w, Margin = new Thickness(11, 8, 11, 16), Tag = g, Cursor = Cursors.Hand, Background = Brushes.Transparent };
    // left-to-right here, or the right-to-left window mirrors the cover art
    var cover = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, FlowDirection = FlowDirection.LeftToRight };
    var inner = new Grid();
    if (g.Cover != null) {
      var pic = LoadPicture(g.Cover, 360);
      if (pic != null) cover.Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
    }
    if (cover.Background == null) {                              // no cover: the game's icon and name on a dark card
      cover.Background = new LinearGradientBrush(Color.FromRgb(44, 44, 50), Color.FromRgb(20, 20, 24), 90);
      var ph = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
      var icon = ExeIcon(g.Exe);
      if (icon != null) ph.Children.Add(new Image { Source = icon, Width = 88, Height = 88, Margin = new Thickness(0, 0, 0, 16) });
      ph.Children.Add(new TextBlock { Text = g.Name, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                                      TextAlignment = TextAlignment.Center, Margin = new Thickness(12, 0, 12, 0), FlowDirection = HasArabic(g.Name) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight });
      inner.Children.Add(ph);
    }
    var play = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 70, CornerRadius = new CornerRadius(0, 0, 10, 10), Opacity = 0,
                            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(225, 0, 0, 0), 90) };
    play.Child = new TextBlock { Text = "▶  العب", FlowDirection = FlowDirection.RightToLeft, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 13) };
    inner.Children.Add(play);
    cover.Child = inner;
    var zoom = new ScaleTransform(1, 1);
    cover.RenderTransformOrigin = new Point(0.5, 0.5);
    cover.RenderTransform = zoom;
    card.Children.Add(cover);

    bool ltr = !HasArabic(g.Name);
    card.Children.Add(new TextBlock { Text = g.Name, Foreground = new SolidColorBrush(Color.FromRgb(236, 236, 236)), FontSize = 14.5, FontWeight = FontWeights.SemiBold,
                                      TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(3, 10, 3, 0), ToolTip = g.Name,
                                      FlowDirection = ltr ? FlowDirection.LeftToRight : FlowDirection.RightToLeft, TextAlignment = ltr ? TextAlignment.Right : TextAlignment.Left });
    card.Children.Add(new TextBlock { Text = GameSub(g), Foreground = new SolidColorBrush(Color.FromRgb(135, 135, 135)), FontSize = 12.5, Margin = new Thickness(3, 3, 3, 0), TextTrimming = TextTrimming.CharacterEllipsis });

    card.MouseEnter += delegate {
      cover.BorderBrush = new SolidColorBrush(Gold);
      play.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
      zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.035, TimeSpan.FromMilliseconds(120)));
      zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.035, TimeSpan.FromMilliseconds(120)));
    };
    card.MouseLeave += delegate {
      cover.BorderBrush = Brushes.Transparent;
      play.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
      zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
      zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    };
    bool pressed = false;
    card.MouseLeftButtonDown += delegate { pressed = true; };
    card.MouseLeave += delegate { pressed = false; };
    card.MouseLeftButtonUp += delegate { if (!pressed) return; pressed = false; LaunchGame(g); CloseLibrary(); };

    var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft };
    AddMenuItem(menu, "▶  شغّل", delegate { LaunchGame(g); CloseLibrary(); });
    if (!string.IsNullOrEmpty(g.Dir) && Directory.Exists(g.Dir))
      AddMenuItem(menu, "افتح مجلد اللعبة", delegate { try { Process.Start("explorer.exe", "\"" + g.Dir + "\""); } catch { } CloseLibrary(); });
    AddMenuItem(menu, "غيّر الصورة...", delegate { ChangeCover(g); });
    if (g.Cover != null && g.Cover.StartsWith(CustomCoverDir(), StringComparison.OrdinalIgnoreCase))
      AddMenuItem(menu, "رجّع الصورة الأصلية", delegate { ResetCover(g); });
    menu.Items.Add(new Separator());
    AddMenuItem(menu, g.Hidden ? "رجّعها للمكتبة" : "اخفها من المكتبة", delegate { SetHidden(g, !g.Hidden); });
    if (g.Key.StartsWith("exe:")) AddMenuItem(menu, "شيلها من المكتبة", delegate { RemoveAddedGame(g); });
    card.ContextMenu = menu;
    return card;
  }

  static void AddMenuItem(ContextMenu menu, string text, Action click) {
    var item = new MenuItem { Header = text };
    item.Click += delegate { click(); };
    menu.Items.Add(item);
  }

  static void LaunchGame(Game g) {
    try {
      if (g.LaunchUri != null) Process.Start(new ProcessStartInfo(g.LaunchUri) { UseShellExecute = true });
      else if (g.Exe != null) Process.Start(new ProcessStartInfo(g.Exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(g.Exe) });
    } catch { }
  }

  // ---------------------------------------------------------------- editing the library
  static string PickFile(string title, string filter, bool links) {
    var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, DereferenceLinks = links };
    libDialogOpen = true;
    bool ok = false;
    try { ok = libWin != null ? dlg.ShowDialog(libWin) == true : dlg.ShowDialog() == true; } finally { libDialogOpen = false; }
    if (libWin != null) libWin.Activate();
    return ok ? dlg.FileName : null;
  }

  static void AddGameByHand() {
    string path = PickFile("اختر ملف تشغيل اللعبة", "اللعبة أو اختصارها (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url", false);
    if (path == null) return;
    string name = Path.GetFileNameWithoutExtension(path);
    try {
      if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
        string pn = (FileVersionInfo.GetVersionInfo(path).ProductName ?? "").Trim();
        string low = pn.ToLowerInvariant();
        if (pn.Length > 1 && !low.Contains("engine") && !low.Contains("unreal") && !low.Contains("unity") && !low.Contains("launcher")) name = pn;
      }
    } catch { }
    try { File.AppendAllText(LibFile("library.txt"), name.Replace("|", " ") + "|" + path + Environment.NewLine, new UTF8Encoding(true)); } catch { }
    LoadLibraryAsync();
  }

  static void RemoveAddedGame(Game g) {
    try {
      string lf = LibFile("library.txt");
      var keep = File.ReadAllLines(lf, Encoding.UTF8).Where(l => { string[] p = l.Split('|'); return p.Length < 2 || !string.Equals(p[1].Trim(), g.Exe, StringComparison.OrdinalIgnoreCase); }).ToArray();
      File.WriteAllLines(lf, keep, new UTF8Encoding(true));
    } catch { }
    libGames.Remove(g);
    FillLibrary();
  }

  static void ChangeCover(Game g) {
    string pic = PickFile("اختر صورة للعبة", "صور (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png", true);
    if (pic == null) return;
    try {
      string custom = CustomCoverDir();
      Directory.CreateDirectory(custom);
      foreach (string old in Directory.GetFiles(custom, SafeName(g.Key) + ".*")) File.Delete(old);
      string dest = Path.Combine(custom, SafeName(g.Key) + Path.GetExtension(pic).ToLowerInvariant());
      File.Copy(pic, dest, true);
      g.Cover = dest;
      FillLibrary();
    } catch { }
  }

  static void ResetCover(Game g) {
    try { foreach (string old in Directory.GetFiles(CustomCoverDir(), SafeName(g.Key) + ".*")) File.Delete(old); } catch { }
    LoadLibraryAsync();
  }

  static void SetHidden(Game g, bool hide) {
    var set = LibHidden();
    if (hide) set.Add(g.Key); else set.Remove(g.Key);
    try { File.WriteAllLines(LibFile("library-hidden.txt"), set.ToArray(), new UTF8Encoding(true)); } catch { }
    g.Hidden = hide;
    if (libGames.All(x => !x.Hidden)) libShowHidden = false;
    FillLibrary();
  }
}
