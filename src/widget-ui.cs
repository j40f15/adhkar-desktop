// adhkar-dua.exe, part 2: settings (settings.ini), the settings window, themes, the in-game overlay,
// and the battery extras (full-charge alert, "how long will it last" estimate).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

public static partial class DuaWidget {
  // ---------------------------------------------------------------- settings.ini (shared with adhkar.ps1)
  static readonly Dictionary<string, string> Cfg = new Dictionary<string, string>();
  static string cfgFile;

  static void LoadCfg() {
    Cfg.Clear();
    try {
      foreach (string line in File.ReadAllLines(cfgFile, Encoding.UTF8)) {
        int i = line.IndexOf('=');
        if (i > 0) Cfg[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
      }
    } catch { }
    // one-time move from the old widget-settings.txt
    string old = Path.Combine(dir, "widget-settings.txt");
    if (File.Exists(old)) {
      try {
        foreach (string line in File.ReadAllLines(old)) {
          string[] kv = line.Split('=');
          if (kv.Length != 2) continue;
          if (kv[0] == "interval" && !Cfg.ContainsKey("dua_seconds")) Cfg["dua_seconds"] = kv[1].Trim();
          if (kv[0] == "hidden" && !Cfg.ContainsKey("hidden")) Cfg["hidden"] = kv[1].Trim();
        }
        SaveCfg();
        File.Delete(old);
      } catch { }
    }
  }

  static void SaveCfg() {
    try { File.WriteAllLines(cfgFile, Cfg.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value).ToArray(), new UTF8Encoding(true)); } catch { }
  }

  static int CfgInt(string k, int def) { string v; int r; return Cfg.TryGetValue(k, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def; }
  static bool CfgBool(string k, bool def) { string v; return Cfg.TryGetValue(k, out v) ? v == "1" : def; }
  static string CfgStr(string k, string def) { string v; return Cfg.TryGetValue(k, out v) && v.Length > 0 ? v : def; }
  static void SetCfg(string k, int v) { Cfg[k] = v.ToString(CultureInfo.InvariantCulture); }
  static void SetCfg(string k, bool v) { Cfg[k] = v ? "1" : "0"; }
  static void SetCfg(string k, string v) { Cfg[k] = v ?? ""; }

  // ---------------------------------------------------------------- themes
  static readonly string[] ThemeKeys = { "gold", "green", "blue", "white" };
  static readonly string[] ThemeNames = { "ذهبي", "أخضر", "أزرق", "أبيض" };

  static Color ThemeColor(string name, byte a) {
    switch (name) {
      case "green": return Color.FromArgb(a, 110, 200, 150);
      case "blue": return Color.FromArgb(a, 110, 170, 240);
      case "white": return Color.FromArgb(a, 225, 225, 225);
      default: return Color.FromArgb(a, 212, 175, 106);
    }
  }

  // ---------------------------------------------------------------- cities (same list as the installer)
  static readonly string[] CityNames = { "مكة المكرمة", "المدينة المنورة", "الرياض", "جدة", "الدمام", "الخبر", "الطائف", "أبها", "خميس مشيط", "تبوك",
                                          "بريدة", "حائل", "جازان", "نجران", "الأحساء", "ينبع", "الجبيل", "الباحة", "عرعر", "سكاكا" };
  static readonly string[] CityCoords = { "21.4225|39.8262", "24.4672|39.6111", "24.7136|46.6753", "21.4858|39.1925", "26.4207|50.0888", "26.2172|50.1971",
                                           "21.2703|40.4158", "18.2164|42.5053", "18.3000|42.7333", "28.3838|36.5550", "26.3260|43.9750", "27.5114|41.7208",
                                           "16.8892|42.5511", "17.5656|44.2289", "25.3833|49.5833", "24.0890|38.0618", "27.0174|49.6225", "20.0129|41.4677",
                                           "30.9753|41.0381", "29.9697|40.2064" };

  static string CurrentCity() {
    try { string[] p = File.ReadAllLines(Path.Combine(dir, "city.txt"), Encoding.UTF8)[0].Split('|'); if (p[0].Trim().Length > 0) return p[0].Trim(); } catch { }
    return "مكة المكرمة";
  }

  static string MinutesText(int mins) {
    if (mins >= 60) return Digits((mins / 60) + ":" + (mins % 60).ToString("00"));
    if (mins == 1) return "دقيقة";
    if (mins == 2) return "دقيقتان";
    if (mins <= 10) return Digits(mins.ToString()) + " دقائق";
    return Digits(mins.ToString()) + " دقيقة";
  }

  // ---------------------------------------------------------------- in-game overlay
  // Windows silences notifications while you play; this small banner shows on top of borderless / windowed games.
  static Window overlayWin;
  static readonly HashSet<string> overlayShown = new HashSet<string>();

  static bool InGame() {                                     // fullscreen app / game / presentation, but not the lock screen
    int st;
    if (SHQueryUserNotificationState(out st) != 0) return false;
    return st >= 2 && st <= 4;
  }

  static void ShowOverlay(string title, string line) {
    if (!CfgBool("game_overlay", true)) return;
    try {
      if (overlayWin != null) { try { overlayWin.Close(); } catch { } overlayWin = null; }
      var w = new Window();
      w.WindowStyle = WindowStyle.None; w.AllowsTransparency = true; w.Background = Brushes.Transparent;
      w.ShowInTaskbar = false; w.ShowActivated = false; w.Topmost = true; w.Focusable = false;
      w.ResizeMode = ResizeMode.NoResize; w.SizeToContent = SizeToContent.WidthAndHeight; w.Title = "Hisn";
      var panel = new StackPanel { FlowDirection = FlowDirection.RightToLeft };
      panel.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 22 * scale, Foreground = new SolidColorBrush(Gold), TextAlignment = TextAlignment.Center });
      if (!string.IsNullOrEmpty(line))
        panel.Children.Add(new TextBlock { Text = line, FontFamily = new FontFamily("Segoe UI"), FontSize = 15 * scale, Foreground = new SolidColorBrush(Color.FromArgb(205, 255, 255, 255)), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4 * scale, 0, 0) });
      var card = new Border {
        Child = panel, Background = new SolidColorBrush(Color.FromArgb(220, 12, 12, 12)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(140, Gold.R, Gold.G, Gold.B)), BorderThickness = new Thickness(1.2),
        CornerRadius = new CornerRadius(18 * scale), Padding = new Thickness(28 * scale, 12 * scale, 28 * scale, 12 * scale)
      };
      card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));   // size it ourselves: always lands centred
      w.Content = card;
      w.SizeToContent = SizeToContent.Manual;
      w.Width = Math.Ceiling(card.DesiredSize.Width) + 2; w.Height = Math.Ceiling(card.DesiredSize.Height) + 2;
      w.SourceInitialized += delegate {
        IntPtr h = new WindowInteropHelper(w).Handle;
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
      };
      w.Opacity = 0;
      w.Left = (SystemParameters.PrimaryScreenWidth - w.Width) / 2; w.Top = 36 * scale;
      w.Show();
      w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
      var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
      timer.Tick += delegate {
        timer.Stop();
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(600));
        fadeOut.Completed += delegate { try { w.Close(); } catch { } if (overlayWin == w) overlayWin = null; };
        w.BeginAnimation(UIElement.OpacityProperty, fadeOut);
      };
      timer.Start();
      overlayWin = w;
    } catch { }
  }

  // Prayer heads-up / prayer time while a game is running (the normal notifications cover the rest).
  static void CheckGameOverlay() {
    if (!InGame()) return;
    DateTime until;
    if (MutedUntil(out until)) return;
    LoadPrayers();
    DateTime now = DateTime.Now;
    int before = CfgInt("remind_before", 10);
    foreach (var p in prayers) {
      string key = p.Key.ToString("yyyyMMddHHmm");
      if (before > 0) {
        DateTime t = p.Key.AddMinutes(-before);
        if (now >= t && now < t.AddMinutes(2) && overlayShown.Add("b" + key))
          ShowOverlay("باقي " + MinutesText(before) + " على صلاة " + p.Value, "بتوقيت " + CurrentCity());
      }
      if (now >= p.Key && now < p.Key.AddMinutes(3) && overlayShown.Add("p" + key)) {
        System.Media.SystemSounds.Asterisk.Play();
        ShowOverlay("حان الآن وقت صلاة " + p.Value, "الله أكبر");
      }
    }
  }

  // ---------------------------------------------------------------- battery extras
  static readonly Dictionary<string, bool> wasCharging = new Dictionary<string, bool>();
  static readonly Dictionary<string, List<KeyValuePair<DateTime, int>>> drainHistory = new Dictionary<string, List<KeyValuePair<DateTime, int>>>();

  static void LoadDrainHistory() {
    try {
      foreach (string line in File.ReadAllLines(Path.Combine(dir, "battery-history.txt"), Encoding.UTF8)) {
        string[] p = line.Split('|');
        if (p.Length < 3) continue;
        if (!drainHistory.ContainsKey(p[0])) drainHistory[p[0]] = new List<KeyValuePair<DateTime, int>>();
        drainHistory[p[0]].Add(new KeyValuePair<DateTime, int>(new DateTime(long.Parse(p[1])), int.Parse(p[2])));
      }
    } catch { }
  }

  static void SaveDrainHistory() {
    try {
      var lines = new List<string>();
      foreach (var k in drainHistory) foreach (var e in k.Value) lines.Add(k.Key.Replace("|", " ") + "|" + e.Key.Ticks + "|" + e.Value);
      File.WriteAllLines(Path.Combine(dir, "battery-history.txt"), lines.ToArray(), new UTF8Encoding(true));
    } catch { }
  }

  // Records live readings; charging wipes the history (a new discharge starts after it).
  static void TrackDrain(BatteryInfo b, DateTime now) {
    if (b.Percent < 0) return;
    List<KeyValuePair<DateTime, int>> h;
    if (!drainHistory.TryGetValue(b.Key, out h)) { h = new List<KeyValuePair<DateTime, int>>(); drainHistory[b.Key] = h; }
    if (b.Charging) { h.Clear(); return; }
    if (h.Count > 0 && b.Percent > h[h.Count - 1].Value + 2) h.Clear();      // jumped up: it was charged in between
    if (h.Count == 0 || h[h.Count - 1].Value != b.Percent || (now - h[h.Count - 1].Key).TotalMinutes >= 30)
      h.Add(new KeyValuePair<DateTime, int>(now, b.Percent));
    h.RemoveAll(e => (now - e.Key).TotalHours > 72);
  }

  // Hours left at the current pace, or -1 until there is enough data (1.5 h of use and 5 % drop).
  static double HoursLeft(BatteryInfo b) {
    List<KeyValuePair<DateTime, int>> h;
    if (b.Charging || b.Percent <= 0 || !drainHistory.TryGetValue(b.Key, out h) || h.Count < 2) return -1;
    var first = h[0]; var lastE = h[h.Count - 1];
    double hours = (lastE.Key - first.Key).TotalHours;
    int drop = first.Value - lastE.Value;
    if (hours < 1.5 || drop < 5) return -1;
    return b.Percent / (drop / hours);
  }

  static string HoursText(double hours, bool shortForm) {
    if (hours < 0) return null;
    if (hours < 1) { int m = Math.Max(5, (int)Math.Round(hours * 60 / 5) * 5); return shortForm ? Digits(m.ToString()) + "د" : Digits(m.ToString()) + " دقيقة"; }
    int hh = (int)Math.Round(hours);
    if (shortForm) return Digits(hh.ToString()) + "س";
    if (hh == 1) return "ساعة"; if (hh == 2) return "ساعتين";
    if (hh <= 10) return Digits(hh.ToString()) + " ساعات";
    return Digits(hh.ToString()) + " ساعة";
  }

  // ---------------------------------------------------------------- settings window
  static Window settingsWin;

  static TextBlock Heading(string text) {
    return new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Gold), Margin = new Thickness(0, 18, 0, 8) };
  }

  static FrameworkElement Row(string label, FrameworkElement control) {
    var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 225)) };
    Grid.SetColumn(t, 0); Grid.SetColumn(control, 1);
    g.Children.Add(t); g.Children.Add(control);
    return g;
  }

  static ComboBox Combo(string[] items, int selected, double width) {
    var c = new ComboBox { Width = width, FlowDirection = FlowDirection.RightToLeft };
    foreach (string s in items) c.Items.Add(s);
    c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, selected));
    return c;
  }

  static CheckBox Check(string text, bool value) {
    return new CheckBox { Content = text, IsChecked = value, Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 225)), Margin = new Thickness(0, 5, 0, 5) };
  }

  static void OpenSettings() {
    if (settingsWin != null) { settingsWin.Activate(); return; }
    LoadCfg();
    var w = new Window {
      Title = "إعدادات " + AppName, Width = 540, Height = 760, WindowStartupLocation = WindowStartupLocation.CenterScreen,
      FlowDirection = FlowDirection.RightToLeft, Background = new SolidColorBrush(Color.FromRgb(18, 18, 18)), ResizeMode = ResizeMode.CanMinimize,
      FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Foreground = Brushes.White
    };
    var root = new StackPanel { Margin = new Thickness(26, 18, 26, 18) };
    root.Children.Add(new TextBlock { Text = AppName + "  ·  الإعدادات", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Gold) });
    root.Children.Add(new TextBlock { Text = "أي تغيير ينطبق أول ما تضغط «حفظ»", Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)), Margin = new Thickness(0, 2, 0, 0) });

    // prayer times
    root.Children.Add(Heading("مواقيت الصلاة"));
    var city = Combo(CityNames, Array.IndexOf(CityNames, CurrentCity()), 190);
    root.Children.Add(Row("المدينة", city));
    string[] offsetItems = new string[21];
    for (int i = 0; i < 21; i++) { int v = i - 10; offsetItems[i] = v == 0 ? "بدون تعديل" : (v > 0 ? "+" : "−") + Digits(Math.Abs(v).ToString()) + " د"; }
    string[] prayerKeys = { "fajr", "dhuhr", "asr", "maghrib", "isha" };
    string[] prayerNames = { "الفجر", "الظهر", "العصر", "المغرب", "العشاء" };
    var offsetBoxes = new ComboBox[5];
    for (int i = 0; i < 5; i++) {
      offsetBoxes[i] = Combo(offsetItems, CfgInt("offset_" + prayerKeys[i], 0) + 10, 130);
      root.Children.Add(Row("تقديم أو تأخير " + prayerNames[i], offsetBoxes[i]));
    }

    // reminders
    root.Children.Add(Heading("التذكيرات"));
    int[] beforeVals = { 0, 5, 10, 15, 20 };
    string[] beforeNames = { "بدون", "٥ دقائق", "١٠ دقائق", "١٥ دقيقة", "٢٠ دقيقة" };
    var before = Combo(beforeNames, Array.IndexOf(beforeVals, CfgInt("remind_before", 10)), 130);
    root.Children.Add(Row("تنبيه قبل الصلاة", before));
    var morning = Check("أذكار الصباح والمساء", CfgBool("morning_evening", true));
    var friday = Check("تذكير الجمعة (سورة الكهف)", CfgBool("friday", true));
    var fasting = Check("تذكير صيام الاثنين والخميس والأيام البيض", CfgBool("fasting", true));
    var duha = Check("تذكير صلاة الضحى", CfgBool("duha", true));
    var sleep = Check("أذكار النوم (١١ بالليل)", CfgBool("sleep", true));
    foreach (var c in new[] { morning, friday, fasting, duha, sleep }) root.Children.Add(c);

    // desktop
    root.Children.Add(Heading("سطح المكتب"));
    int[] speedVals = { 15, 30, 60, 300 };
    var speed = Combo(new[] { "كل ١٥ ثانية", "كل ٣٠ ثانية", "كل دقيقة", "كل ٥ دقائق" }, Array.IndexOf(speedVals, duaSeconds), 130);
    root.Children.Add(Row("تغيير الدعاء", speed));
    var themePanel = new StackPanel { Orientation = Orientation.Horizontal };
    var themeRadios = new List<RadioButton>();
    string curTheme = CfgStr("theme", "gold");
    for (int i = 0; i < ThemeKeys.Length; i++) {
      var sw = new StackPanel { Orientation = Orientation.Horizontal };
      sw.Children.Add(new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(ThemeColor(ThemeKeys[i], 255)), Margin = new Thickness(0, 0, 5, 0) });
      sw.Children.Add(new TextBlock { Text = ThemeNames[i], Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 225)) });
      var r = new RadioButton { Content = sw, GroupName = "theme", IsChecked = ThemeKeys[i] == curTheme, Margin = new Thickness(0, 0, 14, 0), Tag = ThemeKeys[i] };
      themeRadios.Add(r); themePanel.Children.Add(r);
    }
    root.Children.Add(Row("اللون", themePanel));
    var second = Check("اعرض الساعة والدعاء على الشاشة الثانية بعد", CfgBool("second_screen", true));
    root.Children.Add(second);
    int[] restVals = { 0, 5, 10, 15, 30 };
    var rest = Combo(new[] { "مطفية", "بعد ٥ دقائق", "بعد ١٠ دقائق", "بعد ١٥ دقيقة", "بعد ٣٠ دقيقة" }, Math.Max(0, Array.IndexOf(restVals, CfgInt("rest_minutes", 10))), 130);
    root.Children.Add(Row("شاشة الراحة (لا تركت الجهاز)", rest));

    // games
    root.Children.Add(Heading("الألعاب"));
    var overlay = Check("تنبيه فوق اللعبة (وقت الصلاة، الشحن، الحرارة)", CfgBool("game_overlay", true));
    root.Children.Add(overlay);

    // batteries
    root.Children.Add(Heading("شحن الأجهزة"));
    int[] lowVals = { 10, 15, 20, 30 };
    var low = Combo(new[] { "١٠٪", "١٥٪", "٢٠٪", "٣٠٪" }, Array.IndexOf(lowVals, CfgInt("battery_low", 20)), 130);
    root.Children.Add(Row("نبهني إذا الشحن أقل من", low));
    var full = Check("نبهني لما يكتمل الشحن عشان أفصله", CfgBool("battery_full_alert", true));
    var estimate = Check("اعرض كم يكفي الشحن (يحسبه من استخدامك)", CfgBool("battery_estimate", true));
    root.Children.Add(full); root.Children.Add(estimate);

    // buttons
    var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(26, 10, 26, 18) };
    var save = new Button { Content = "حفظ", Width = 110, Height = 36, Background = new SolidColorBrush(Gold), Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
    var cancel = new Button { Content = "إلغاء", Width = 90, Height = 36, IsCancel = true };
    buttons.Children.Add(save); buttons.Children.Add(cancel);

    var dock = new DockPanel();
    DockPanel.SetDock(buttons, Dock.Bottom);
    dock.Children.Add(buttons);
    dock.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    w.Content = dock;

    cancel.Click += delegate { w.Close(); };
    save.Click += delegate {
      for (int i = 0; i < 5; i++) SetCfg("offset_" + prayerKeys[i], offsetBoxes[i].SelectedIndex - 10);
      SetCfg("remind_before", beforeVals[Math.Max(0, before.SelectedIndex)]);
      SetCfg("morning_evening", morning.IsChecked == true); SetCfg("friday", friday.IsChecked == true);
      SetCfg("fasting", fasting.IsChecked == true); SetCfg("duha", duha.IsChecked == true); SetCfg("sleep", sleep.IsChecked == true);
      SetCfg("dua_seconds", speedVals[Math.Max(0, speed.SelectedIndex)]);
      foreach (var r in themeRadios) if (r.IsChecked == true) SetCfg("theme", (string)r.Tag);
      SetCfg("second_screen", second.IsChecked == true);
      SetCfg("game_overlay", overlay.IsChecked == true);
      SetCfg("rest_minutes", restVals[Math.Max(0, rest.SelectedIndex)]);
      SetCfg("battery_low", lowVals[Math.Max(0, low.SelectedIndex)]);
      SetCfg("battery_full_alert", full.IsChecked == true);
      SetCfg("battery_estimate", estimate.IsChecked == true);
      SaveCfg();
      int ci = city.SelectedIndex;
      if (ci >= 0 && CityNames[ci] != CurrentCity()) {
        try { File.WriteAllText(Path.Combine(dir, "city.txt"), CityNames[ci] + "|" + CityCoords[ci], new UTF8Encoding(true)); } catch { }
      }
      w.Close();
      RunHelper("Refresh");                                     // wallpaper, prayer times and reminders pick up the changes
      var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
      t.Tick += delegate { t.Stop(); Restart(); };             // and the widget restarts with the new look
      t.Start();
    };
    w.Closed += delegate { settingsWin = null; };
    settingsWin = w;
    w.Show();
    w.Activate();
  }
}
