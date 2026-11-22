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
using System.Web.Script.Serialization;

public static partial class DuaWidget {
  // ---------------------------------------------------------------- Arabic translation
  // Google Translate's free web endpoint. Only game text is sent (achievement names, game descriptions), nothing about you.
  // Every result is kept in covers\translations-ar.txt so a text is only translated once.
  static Dictionary<string, string> trCache;
  static readonly object trLock = new object();
  static readonly HashSet<string> trTried = new HashSet<string>();   // pages already sent this run (no retry loop when offline)
  static bool trOriginal;                                         // "English" button: show the original text

  static bool Translating() { return CfgBool("translate_ar", true) && !trOriginal; }
  static string TrFile() { return Path.Combine(Path.Combine(dir, "covers"), "translations-ar.txt"); }
  static string TrEsc(string s) { return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n"); }
  static string TrUnesc(string s) {
    var sb = new StringBuilder();
    for (int i = 0; i < s.Length; i++) {
      if (s[i] == '\\' && i + 1 < s.Length) { char c = s[++i]; sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c); }
      else sb.Append(s[i]);
    }
    return sb.ToString();
  }

  static void LoadTr() {
    lock (trLock) {
      if (trCache != null) return;
      trCache = new Dictionary<string, string>();
      try {
        foreach (string l in File.ReadAllLines(TrFile(), Encoding.UTF8)) {
          int i = l.IndexOf('\t');
          if (i > 0) trCache[TrUnesc(l.Substring(0, i))] = TrUnesc(l.Substring(i + 1));
        }
      } catch { }
    }
  }

  static string CachedAr(string en) {
    if (string.IsNullOrEmpty(en)) return null;
    LoadTr();
    lock (trLock) { string ar; return trCache.TryGetValue(en, out ar) ? ar : null; }
  }

  static bool NeedsTr(string t) { return !string.IsNullOrWhiteSpace(t) && !HasArabic(t) && CachedAr(t) == null; }

  static string Tr(string t) {                                    // the Arabic text when we have it and it's wanted
    if (string.IsNullOrEmpty(t) || !Translating()) return t;
    return CachedAr(t) ?? t;
  }

  static string GoogleAr(string text) {
    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
    using (var wc = new System.Net.WebClient()) {
      wc.Encoding = Encoding.UTF8;
      wc.Headers[System.Net.HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded;charset=UTF-8";
      string resp = wc.UploadString("https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ar&dt=t", "q=" + Uri.EscapeDataString(text));
      var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(resp) as object[];
      var sb = new StringBuilder();
      if (root != null && root.Length > 0 && root[0] is object[])
        foreach (object seg in (object[])root[0]) { var s = seg as object[]; if (s != null && s.Length > 0 && s[0] is string) sb.Append((string)s[0]); }
      return sb.ToString();
    }
  }

  // Translates every text that isn't cached yet: short ones together (one per line), long ones on their own. Off the UI thread.
  static void TranslateAll(IEnumerable<string> texts) {
    var todo = new List<string>();
    foreach (string t in texts) if (NeedsTr(t) && !todo.Contains(t)) todo.Add(t);
    var done = new List<KeyValuePair<string, string>>();
    int i = 0;
    while (i < todo.Count) {
      if (todo[i].Contains("\n") || todo[i].Length > 1500) {
        try { string ar = GoogleAr(todo[i]).Trim(); if (ar.Length > 0) done.Add(new KeyValuePair<string, string>(todo[i], ar)); } catch { }
        i++;
        continue;
      }
      var batch = new List<string>();
      int len = 0;
      while (i < todo.Count && !todo[i].Contains("\n") && todo[i].Length <= 1500 && len + todo[i].Length < 3500) { batch.Add(todo[i]); len += todo[i].Length + 1; i++; }
      try {
        string[] lines = GoogleAr(string.Join("\n", batch.ToArray())).Split('\n');
        if (lines.Length == batch.Count) { for (int k = 0; k < batch.Count; k++) if (lines[k].Trim().Length > 0) done.Add(new KeyValuePair<string, string>(batch[k], lines[k].Trim())); }
        else foreach (string one in batch) { try { done.Add(new KeyValuePair<string, string>(one, GoogleAr(one).Trim())); } catch { } }
      } catch { }
    }
    if (done.Count == 0) return;
    lock (trLock) {
      foreach (var d in done) trCache[d.Key] = d.Value;
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(TrFile()));
        File.AppendAllLines(TrFile(), done.Select(d => TrEsc(d.Key) + "\t" + TrEsc(d.Value)), new UTF8Encoding(false));
      } catch { }
    }
  }

  // Translate in the background, then redraw the page if it's still the one on screen.
  static bool TranslateThen(string pageKey, IEnumerable<string> texts, Action redraw) {
    var list = texts.Where(NeedsTr).Distinct().ToList();
    if (list.Count == 0 || !Translating() || trTried.Contains(pageKey)) return false;
    trTried.Add(pageKey);
    var t = new System.Threading.Thread(delegate () {
      try { TranslateAll(list); } catch { }
      Application.Current.Dispatcher.BeginInvoke(redraw);
    });
    t.IsBackground = true;
    t.Start();
    return true;
  }

  class Game {
    public string Key, Name, Source, LaunchUri, Exe, Dir, Cover;
    public long LastPlayed;                                       // unix seconds
    public int SteamMinutes;                                      // Steam's own total play time
    public bool Hidden;
    public string AppId;                                          // Steam only
    public int AchDone, AchTotal;                                 // Steam achievements
  }

  // ---------------------------------------------------------------- Steam achievements (read from Steam's own files, no sign-in or key)
  // appcache\stats\UserGameStatsSchema_<app>.bin = the list (names, descriptions, icons),
  // appcache\stats\UserGameStats_<account>_<app>.bin = what you unlocked and when. Both are Steam's binary KeyValues.
  class KV {
    public string Name; public object Value; public List<KV> Kids;
    public KV Get(string n) { if (Kids == null) return null; foreach (var k in Kids) if (string.Equals(k.Name, n, StringComparison.OrdinalIgnoreCase)) return k; return null; }
    public string Str(string n) { var k = Get(n); return k == null || k.Value == null ? null : Convert.ToString(k.Value, CultureInfo.InvariantCulture); }
  }

  static string CStr(byte[] b, ref int p) {
    int s = p;
    while (p < b.Length && b[p] != 0) p++;
    string r = Encoding.UTF8.GetString(b, s, p - s);
    p++;
    return r;
  }

  static List<KV> ReadBinKV(byte[] b, ref int p) {
    var list = new List<KV>();
    while (p < b.Length) {
      byte t = b[p++];
      if (t == 0x08 || t == 0x0B) break;                          // end of this section
      var kv = new KV { Name = CStr(b, ref p) };
      switch (t) {
        case 0x00: kv.Kids = ReadBinKV(b, ref p); break;
        case 0x01: kv.Value = CStr(b, ref p); break;
        case 0x02: case 0x04: case 0x06: kv.Value = BitConverter.ToInt32(b, p); p += 4; break;
        case 0x03: kv.Value = BitConverter.ToSingle(b, p); p += 4; break;
        case 0x07: kv.Value = BitConverter.ToUInt64(b, p); p += 8; break;
        case 0x0A: kv.Value = BitConverter.ToInt64(b, p); p += 8; break;
        default: return list;                                     // unknown type: stop rather than misread
      }
      list.Add(kv);
    }
    return list;
  }

  class Ach { public string Name, Desc, Icon, IconGray; public bool Done, Hidden; public long Time; }

  static string Localized(KV k) {                                 // Arabic when the game has it, else English
    if (k == null) return "";
    if (k.Kids == null) return k.Value == null ? "" : k.Value.ToString();
    string s = k.Str("arabic");
    if (string.IsNullOrEmpty(s)) s = k.Str("english");
    if (string.IsNullOrEmpty(s)) foreach (var c in k.Kids) if (c.Value is string && c.Name != "token") { s = (string)c.Value; break; }
    return s ?? "";
  }

  static List<Ach> ReadSteamAchievements(string appId) {
    var res = new List<Ach>();
    if (string.IsNullOrEmpty(appId)) return res;
    string stats = Path.Combine(SteamRoot(), @"appcache\stats");
    string schemaFile = Path.Combine(stats, "UserGameStatsSchema_" + appId + ".bin");
    if (!File.Exists(schemaFile)) return res;
    int p = 0;
    var schemaTop = ReadBinKV(File.ReadAllBytes(schemaFile), ref p);
    if (schemaTop.Count == 0 || schemaTop[0].Kids == null) return res;
    var statsKv = schemaTop[0].Get("stats");
    if (statsKv == null || statsKv.Kids == null) return res;

    KV user = null;                                               // newest file = the signed-in Steam account
    try {
      var uf = new DirectoryInfo(stats).GetFiles("UserGameStats_*_" + appId + ".bin").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
      if (uf != null) { int q = 0; var top = ReadBinKV(File.ReadAllBytes(uf.FullName), ref q); if (top.Count > 0) user = top[0]; }
    } catch { }

    foreach (var stat in statsKv.Kids) {
      var bits = stat.Get("bits");
      if (bits == null || bits.Kids == null) continue;            // a plain number stat, not achievements
      int data = 0;
      KV times = null;
      if (user != null) {
        var us = user.Get(stat.Name);
        if (us != null) {
          var d = us.Get("data");
          if (d != null && d.Value is int) data = (int)d.Value;
          times = us.Get("AchievementTimes");
        }
      }
      foreach (var bit in bits.Kids) {
        int index;
        string bs = bit.Str("bit");
        if (bs == null || !int.TryParse(bs, out index)) if (!int.TryParse(bit.Name, out index)) continue;
        var disp = bit.Get("display");
        var a = new Ach();
        if (disp != null) {
          a.Name = Localized(disp.Get("name"));
          a.Desc = Localized(disp.Get("desc"));
          a.Icon = disp.Str("icon");
          a.IconGray = disp.Str("icon_gray");
          a.Hidden = disp.Str("hidden") == "1";
        }
        if (string.IsNullOrEmpty(a.Name)) a.Name = bit.Str("name") ?? "";
        a.Done = index >= 0 && index < 32 && ((data >> index) & 1) == 1;
        if (times != null) { var tk = times.Get(index.ToString()); if (tk != null && tk.Value is int) a.Time = (uint)(int)tk.Value; }
        res.Add(a);
      }
    }
    return res;
  }

  static List<Game> libGames = new List<Game>();
  static readonly List<KeyValuePair<string, string>> gameDirs = new List<KeyValuePair<string, string>>();   // install folder\ -> name (play time)
  static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
  static bool libLoading, libDialogOpen, libShowHidden, libHotkey;
  static Window libWin;
  static WrapPanel libGrid;
  static TextBox libSearch;
  static TextBlock libCount, libEmpty;
  static Border libHiddenButton, libAddButton, libBackButton;
  static ScrollViewer libScroll;
  static TextBlock libTitle, libHint;
  static Game libAchGame;                                         // set while a game's achievements are shown
  static Game libAboutGame;                                       // set while "about the game" is shown
  static Border libLangButton;
  static bool InPage() { return libAchGame != null || libAboutGame != null; }
  static StackPanel libAchList;
  static string shownLib;
  static Dictionary<string, int> libWeek = new Dictionary<string, int>();

  // ---------------------------------------------------------------- hotkey (any key you choose in the settings)
  // A keyboard hook on its own thread instead of RegisterHotKey: the key is only taken when the library opens.
  // While a game or a coding app is in front the key goes to that app untouched.
  delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr hMod, uint thread);
  [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
  [DllImport("user32.dll")] static extern int GetMessage(out MSG32 msg, IntPtr h, uint min, uint max);
  [StructLayout(LayoutKind.Sequential)] struct MSG32 { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
  [StructLayout(LayoutKind.Sequential)] struct KBDLL { public uint vk, scan, flags, time; public IntPtr extra; }
  static LowLevelProc hookProc;                                   // kept alive for the hook
  static volatile int hkMods, hkVk;                               // mods: 1 Alt, 2 Ctrl, 4 Shift, 8 Win
  static volatile bool hkRecording;                               // the settings window is reading a new shortcut
  static volatile bool hkBlockGame = true, hkBlockCode = true;
  static readonly string[] CodingApps = { "code", "code - insiders", "cursor", "windsurf", "zed", "devenv", "claude", "windowsterminal", "wt", "openconsole",
    "powershell", "pwsh", "cmd", "idea64", "pycharm64", "rider64", "webstorm64", "clion64", "goland64", "phpstorm64", "studio64", "notepad++", "sublime_text",
    "unity", "unity hub", "unrealeditor", "godot", "blender", "robloxstudiobeta" };

  static void LoadHotkeySetting() {
    hkMods = 3; hkVk = 0x47;                                      // Ctrl+Alt+G
    string v = CfgStr("library_hotkey", "3,71");
    string[] p = v.Split(',');
    int m, k;
    if (p.Length == 2 && int.TryParse(p[0], out m) && int.TryParse(p[1], out k)) { hkMods = m; hkVk = k; }
    hkBlockGame = CfgBool("library_block_game", true);
    hkBlockCode = CfgBool("library_block_coding", true);
  }

  static string HotkeyText(int mods, int vk) {
    if (vk == 0) return "بدون اختصار";
    var parts = new List<string>();
    if ((mods & 2) != 0) parts.Add("Ctrl");
    if ((mods & 1) != 0) parts.Add("Alt");
    if ((mods & 4) != 0) parts.Add("Shift");
    if ((mods & 8) != 0) parts.Add("Win");
    string key = KeyInterop.KeyFromVirtualKey(vk).ToString();
    if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) key = key.Substring(1);
    if (key.StartsWith("NumPad")) key = "Num " + key.Substring(6);
    parts.Add(key);
    return string.Join("+", parts.ToArray());
  }

  static int CurrentMods() {
    int m = 0;
    if (GetAsyncKeyState(0x12) < 0) m |= 1;
    if (GetAsyncKeyState(0x11) < 0) m |= 2;
    if (GetAsyncKeyState(0x10) < 0) m |= 4;
    if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) m |= 8;
    return m;
  }

  // false = leave the key to the app in front (a game, or a code editor / terminal)
  static bool LibraryAllowed() {
    try {
      IntPtr fg = GetForegroundWindow();
      uint pid = 0;
      if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out pid);
      if (pid == (uint)Process.GetCurrentProcess().Id) return true;    // the library itself (toggle)
      if (hkBlockGame && (InGame() || CurrentGame() != null)) return false;
      if (hkBlockCode && pid != 0) {
        string path = ProcessPath(pid);
        if (path != null && CodingApps.Contains(Path.GetFileNameWithoutExtension(path).ToLowerInvariant())) return false;
      }
    } catch { }
    return true;
  }

  static IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam) {
    if (code >= 0 && hkVk != 0 && !hkRecording) {
      int msg = wParam.ToInt32();
      if (msg == 0x0100 || msg == 0x0104) {                       // WM_KEYDOWN / WM_SYSKEYDOWN
        var k = (KBDLL)Marshal.PtrToStructure(lParam, typeof(KBDLL));
        if (k.vk == hkVk && CurrentMods() == hkMods) {
          if (hkMods == 0 && IsTypingKey(hkVk)) {
            // a letter or number on its own: only when you aren't typing somewhere, and it never closes the
            // library (you may be typing it in the search box)
            IntPtr fg = GetForegroundWindow();
            uint pid = 0;
            if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out pid);
            if (pid != OwnPid() && !TypingNow(fg) && LibraryAllowed()) {
              Application.Current.Dispatcher.BeginInvoke(new Action(OpenLibrary));
              return new IntPtr(1);
            }
          } else if (LibraryAllowed()) {
            Application.Current.Dispatcher.BeginInvoke(new Action(ToggleLibrary));
            return new IntPtr(1);                                 // used: the app in front doesn't get it
          }
        }
      }
    }
    return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
  }

  static uint ownPid;
  static uint OwnPid() { if (ownPid == 0) ownPid = (uint)Process.GetCurrentProcess().Id; return ownPid; }

  static bool IsTypingKey(int vk) {                               // keys that type a character
    return (vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A) || (vk >= 0x60 && vk <= 0x6F) || vk == 0x20
        || (vk >= 0xBA && vk <= 0xC0) || (vk >= 0xDB && vk <= 0xDF) || vk == 0xE2;
  }

  [StructLayout(LayoutKind.Sequential)] struct RECT4 { public int l, t, r, b; }
  [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT4 rcCaret; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);   // GetClassName is in dua-widget.cs

  // Is there a text box with the cursor in it? Browsers, Discord, Steam and other web-based apps always count as typing.
  static bool TypingNow(IntPtr fg) {
    if (fg == IntPtr.Zero) return false;
    uint pid;
    uint tid = GetWindowThreadProcessId(fg, out pid);
    var gi = new GUITHREADINFO();
    gi.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
    if (!GetGUIThreadInfo(tid, ref gi)) return false;
    if (gi.hwndCaret != IntPtr.Zero || (gi.flags & 1) != 0) return true;   // a blinking text cursor
    var sb = new StringBuilder(128);
    GetClassName(gi.hwndFocus != IntPtr.Zero ? gi.hwndFocus : fg, sb, 128);
    string c = sb.ToString();
    return c == "Edit" || c.StartsWith("RichEdit") || c.StartsWith("Chrome_") || c.Contains("Cef") || c.StartsWith("Mozilla") || c == "Scintilla"
        || c == "ConsoleWindowClass" || c == "CASCADIA_HOSTING_WINDOW_CLASS" || c == "Windows.UI.Core.CoreWindow" || c.StartsWith("WinUIDesktop");
  }

  static void RegisterLibraryHotkey() {
    LoadHotkeySetting();
    var t = new System.Threading.Thread(delegate () {
      hookProc = HookProc;
      IntPtr h = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);   // WH_KEYBOARD_LL
      libHotkey = h != IntPtr.Zero;
      if (!libHotkey) return;
      MSG32 m;
      while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
    });
    t.IsBackground = true;
    t.Start();
  }

  static void ToggleLibrary() {
    if (libWin != null && libWin.IsActive) CloseLibrary(); else OpenLibrary();
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
        var g = new Game { Key = "steam:" + s.AppId, Name = s.Name, Source = "Steam", LaunchUri = "steam://rungameid/" + s.AppId, LastPlayed = s.LastPlayed, AppId = s.AppId };
        try { var ach = ReadSteamAchievements(s.AppId); g.AchTotal = ach.Count; g.AchDone = ach.Count(a => a.Done); } catch { }
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
    libTitle = new TextBlock { Text = "مكتبة الألعاب", FontSize = 29, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis };
    titles.Children.Add(libTitle);
    libCount = new TextBlock { FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 0, 7) };
    titles.Children.Add(libCount);
    DockPanel.SetDock(titles, Dock.Left);
    header.Children.Add(titles);

    var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    var sbox = new Grid { Width = 280 };
    var hint = libHint = new TextBlock { Text = "دوّر على لعبة...", Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)), FontSize = 15, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    libSearch = new TextBox { Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                              FontSize = 15, Padding = new Thickness(12, 9, 12, 9), VerticalContentAlignment = VerticalAlignment.Center };
    sbox.Children.Add(hint); sbox.Children.Add(libSearch);
    tools.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), Child = sbox });
    libSearch.TextChanged += delegate { hint.Visibility = libSearch.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; ApplyLibFilter(); };
    libHiddenButton = PillButton("المخفية", delegate { libShowHidden = !libShowHidden; FillLibrary(); });
    libHiddenButton.Visibility = Visibility.Collapsed;
    tools.Children.Add(libHiddenButton);
    libAddButton = PillButton("+  أضف لعبة", AddGameByHand);
    tools.Children.Add(libAddButton);
    libLangButton = PillButton("English", delegate {
      trOriginal = !trOriginal;
      if (libAchGame != null) ShowAchievements(libAchGame); else if (libAboutGame != null) ShowAbout(libAboutGame);
    });
    libLangButton.Visibility = Visibility.Collapsed;
    tools.Children.Add(libLangButton);
    libBackButton = PillButton("→  رجوع للمكتبة", HideAchievements);
    libBackButton.Visibility = Visibility.Collapsed;
    tools.Children.Add(libBackButton);
    tools.Children.Add(PillButton("✕", CloseLibrary));
    DockPanel.SetDock(tools, Dock.Right);
    header.Children.Add(tools);
    grid.Children.Add(header);

    // covers
    libGrid = new WrapPanel { Margin = new Thickness(22, 0, 22, 10) };
    var scroll = libScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = libGrid, Focusable = false };
    try {                                                         // thin dark scrollbar instead of the default light one
      var bar = (Style)System.Windows.Markup.XamlReader.Parse(
        "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
        "<Setter Property='Width' Value='8'/><Setter Property='MinWidth' Value='8'/><Setter Property='Margin' Value='0,0,6,0'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>" +
        "<Track x:Name='PART_Track' IsDirectionReversed='True'><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
        "<Border CornerRadius='4' Background='#40FFFFFF'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track>" +
        "</ControlTemplate></Setter.Value></Setter></Style>");
      scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), bar);
    } catch { scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden; }
    libEmpty = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)), FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed };
    var body = new Grid();
    body.Children.Add(scroll); body.Children.Add(libEmpty);
    Grid.SetRow(body, 1);
    grid.Children.Add(body);

    var foot = new TextBlock { FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(105, 105, 105)), Margin = new Thickness(32, 6, 32, 16),
      Text = (libHotkey && hkVk != 0 ? HotkeyText(hkMods, hkVk) + " يفتح المكتبة (تغيّره من الإعدادات)   ·   " : "") + "Enter يشغّل أول لعبة   ·   Esc يقفلها   ·   كلك يمين على اللعبة لخيارات أكثر" };
    Grid.SetRow(foot, 2);
    grid.Children.Add(foot);

    frame.Child = grid;
    w.Content = frame;
    w.PreviewKeyDown += (s, e) => {
      if (e.Key == Key.Escape) { e.Handled = true; if (InPage()) HideAchievements(); else CloseLibrary(); }
      else if (e.Key == Key.Enter && !InPage()) {
        e.Handled = true;
        var first = libGrid.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Visibility == Visibility.Visible);
        if (first != null) { LaunchGame((Game)first.Tag); CloseLibrary(); }
      }
    };
    w.Deactivated += delegate { if (!libDialogOpen) CloseLibrary(); };
    w.Closed += delegate { libWin = null; libGrid = null; libSearch = null; libHiddenButton = null; shownLib = null; libShowHidden = false; libAchGame = null; libAchList = null; libAboutGame = null; };
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

  static string TrophiesText(int n) {
    if (n == 1) return "جائزة وحدة";
    if (n == 2) return "جائزتين";
    if (n >= 3 && n <= 10) return Digits(n.ToString()) + " جوائز";
    return Digits(n.ToString()) + " جائزة";
  }

  static void ApplyLibFilter() {
    if (libGrid == null) return;
    string q = libSearch == null ? "" : libSearch.Text.Trim();
    string q2 = FromArabicKeyboard(q);
    if (libAboutGame != null) { if (libEmpty != null) libEmpty.Visibility = Visibility.Collapsed; return; }
    if (libAchGame != null && libAchList != null) {               // achievements page: the search box filters achievements
      int n = 0;
      foreach (FrameworkElement r in libAchList.Children) {
        var a = r.Tag as Ach;
        if (a == null) continue;
        string text = (a.Hidden && !a.Done ? "" : a.Name + " " + a.Desc + " " + Tr(a.Name) + " " + Tr(a.Desc));
        bool ok = q.Length == 0 || text.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || text.IndexOf(q2, StringComparison.CurrentCultureIgnoreCase) >= 0;
        r.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        if (ok) n++;
      }
      if (libEmpty != null) { libEmpty.Text = "ما فيه جائزة بهالاسم"; libEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed; }
      return;
    }
    int shown = 0;
    foreach (FrameworkElement c in libGrid.Children) {
      var g = (Game)c.Tag;
      bool ok = q.Length == 0 || g.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || g.Name.IndexOf(q2, StringComparison.CurrentCultureIgnoreCase) >= 0;
      c.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
      if (ok) shown++;
    }
    int total = libGames.Count(g => !g.Hidden), hiddenCount = libGames.Count(g => g.Hidden);
    int trophies = libGames.Where(g => !g.Hidden).Sum(g => g.AchDone);
    if (libCount != null) libCount.Text = libShowHidden ? "المخفية: " + GamesText(hiddenCount)
      : (libLoading && libGames.Count == 0 ? "جاري البحث عن ألعابك..." : (total == 0 ? "" : GamesText(total) + (trophies > 0 ? "   ·   🏆 " + TrophiesText(trophies) : "")));
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
    if (g.AchTotal > 0) {                                         // trophies badge: opens the achievements, not the game
      var badge = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), Padding = new Thickness(8, 3, 8, 4),
                               CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), Cursor = Cursors.Hand, ToolTip = "الجوائز",
                               Child = new TextBlock { Text = "🏆 " + Digits(g.AchDone + "/" + g.AchTotal), Foreground = new SolidColorBrush(g.AchDone == g.AchTotal ? Gold : Color.FromRgb(235, 235, 235)), FontSize = 12.5, FontWeight = FontWeights.SemiBold } };
      badge.MouseLeftButtonDown += (s, e) => { e.Handled = true; };
      badge.MouseLeftButtonUp += (s, e) => { e.Handled = true; ShowAchievements(g); };
      inner.Children.Add(badge);
    }
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
    if (g.AchTotal > 0) AddMenuItem(menu, "🏆  الجوائز", delegate { ShowAchievements(g); });
    if (g.AppId != null) AddMenuItem(menu, "📖  عن اللعبة", delegate { ShowAbout(g); });
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

  // ---------------------------------------------------------------- achievements page (inside the library window)
  static void ShowAchievements(Game g) {
    if (libWin == null || libScroll == null) return;
    List<Ach> list;
    try { list = ReadSteamAchievements(g.AppId); } catch { list = new List<Ach>(); }
    if (list.Count == 0) return;
    libAboutGame = null;
    libAchGame = g;
    int done = list.Count(a => a.Done);
    double pct = 100.0 * done / list.Count;

    var page = new StackPanel { Margin = new Thickness(34, 0, 34, 16) };
    var sum = new TextBlock { FontSize = 17, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10),
                              Text = "حصلت " + Digits(done.ToString()) + " من " + Digits(list.Count.ToString()) + " جائزة   ·   " + Digits(((int)Math.Round(pct)).ToString()) + "٪" };
    page.Children.Add(sum);
    var bar = new Grid { Height = 8, Margin = new Thickness(0, 0, 0, 22), FlowDirection = FlowDirection.RightToLeft };
    bar.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) });
    var fill = new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Gold), HorizontalAlignment = HorizontalAlignment.Left };
    bar.Children.Add(fill);
    bar.SizeChanged += delegate { fill.Width = bar.ActualWidth * pct / 100.0; };
    page.Children.Add(bar);

    libAchList = new StackPanel();
    // unlocked first (newest first), then the ones still to get
    foreach (var a in list.Where(x => x.Done).OrderByDescending(x => x.Time).Concat(list.Where(x => !x.Done))) libAchList.Children.Add(AchRow(g.AppId, a));
    page.Children.Add(libAchList);

    libScroll.Content = page;
    libScroll.ScrollToTop();
    libTitle.Text = g.Name;
    libTitle.FlowDirection = HasArabic(g.Name) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    libCount.Text = "🏆 الجوائز";
    libAddButton.Visibility = Visibility.Collapsed;
    if (libHiddenButton != null) libHiddenButton.Visibility = Visibility.Collapsed;
    libBackButton.Visibility = Visibility.Visible;
    libHint.Text = "دوّر على جائزة...";
    libSearch.Text = "";
    libSearch.Focus();
    ApplyLibFilter();
    SetLangButton(list.Any(a => !HasArabic(a.Name ?? "")));
    var texts = list.SelectMany(a => new[] { a.Name, a.Desc }).ToList();
    if (TranslateThen("ach:" + g.AppId, texts, delegate { if (libAchGame == g && libWin != null) ShowAchievements(g); }))
      sum.Text += "   ·   جاري التعريب...";
  }

  static void SetLangButton(bool useful) {
    if (libLangButton == null) return;
    libLangButton.Visibility = useful && CfgBool("translate_ar", true) ? Visibility.Visible : Visibility.Collapsed;
    ((TextBlock)libLangButton.Child).Text = trOriginal ? "عربي" : "English";
  }

  static void HideAchievements() {
    if (libWin == null) return;
    libAchGame = null; libAchList = null; libAboutGame = null;
    trOriginal = false;
    libScroll.Content = libGrid;
    libTitle.Text = "مكتبة الألعاب";
    libTitle.FlowDirection = FlowDirection.RightToLeft;
    libAddButton.Visibility = Visibility.Visible;
    libBackButton.Visibility = Visibility.Collapsed;
    libLangButton.Visibility = Visibility.Collapsed;
    libHint.Text = "دوّر على لعبة...";
    libSearch.Text = "";
    libSearch.Focus();
    ApplyLibFilter();
  }

  static FrameworkElement AchRow(string appId, Ach a) {
    bool secret = a.Hidden && !a.Done;
    var row = new Border { Tag = a, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8),
                           Background = new SolidColorBrush(a.Done ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(10, 255, 255, 255)) };
    var g = new Grid();
    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    var iconBox = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), FlowDirection = FlowDirection.LeftToRight };
    var img = new Image { Width = 64, Height = 64, Stretch = Stretch.UniformToFill, Opacity = a.Done ? 1 : 0.55 };
    iconBox.Child = img;
    SetAchIcon(img, appId, a.Done ? a.Icon : (a.IconGray ?? a.Icon));
    g.Children.Add(iconBox);

    string name = secret ? "جائزة مخفية" : Tr(a.Name);
    string desc = secret ? "تنكشف لما تحصلها" : Tr(a.Desc);
    bool ltr = !HasArabic(name);
    var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
    text.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                                      Foreground = new SolidColorBrush(a.Done ? Color.FromRgb(240, 240, 240) : Color.FromRgb(160, 160, 160)),
                                      FlowDirection = ltr ? FlowDirection.LeftToRight : FlowDirection.RightToLeft, TextAlignment = ltr ? TextAlignment.Right : TextAlignment.Left });
    if (!string.IsNullOrEmpty(desc)) {
      bool dltr = !HasArabic(desc);
      text.Children.Add(new TextBlock { Text = desc, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
                                        Foreground = new SolidColorBrush(Color.FromRgb(135, 135, 135)),
                                        FlowDirection = dltr ? FlowDirection.LeftToRight : FlowDirection.RightToLeft, TextAlignment = dltr ? TextAlignment.Right : TextAlignment.Left });
    }
    Grid.SetColumn(text, 1);
    g.Children.Add(text);

    string when = "ما حصلتها للحين";
    if (a.Done) {
      when = "✔ حصلتها";
      if (a.Time > 0) { DateTime t = UnixEpoch.AddSeconds(a.Time).ToLocalTime(); when = "✔ " + Digits(t.Day.ToString()) + " " + Months[t.Month - 1] + " " + Digits(t.Year.ToString()); }
    }
    var whenText = new TextBlock { Text = when, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                                   Foreground = new SolidColorBrush(a.Done ? Gold : Color.FromRgb(110, 110, 110)) };
    Grid.SetColumn(whenText, 2);
    g.Children.Add(whenText);
    row.Child = g;
    return row;
  }

  // ---------------------------------------------------------------- "about the game" page (Steam store text, translated; the game's name stays as it is)
  class AboutInfo { public string Short, About, Header, Genres, Devs, Release; }

  static string HtmlToText(string html) {
    if (string.IsNullOrEmpty(html)) return "";
    string s = Regex.Replace(html, @"<\s*(br|/p|/li|/h\d|/div|/ul)[^>]*>", "\n", RegexOptions.IgnoreCase);
    s = Regex.Replace(s, @"<\s*li[^>]*>", "• ", RegexOptions.IgnoreCase);
    s = Regex.Replace(s, "<[^>]+>", "");
    s = System.Net.WebUtility.HtmlDecode(s);
    s = Regex.Replace(s, @"[ \t ]+", " ");
    s = Regex.Replace(s, @" ?\n ?", "\n");
    s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
    if (s.Length > 3500) { int cut = s.LastIndexOf('\n', 3500); s = s.Substring(0, cut > 1500 ? cut : 3500).Trim() + "\n…"; }
    return s;
  }

  static string JsStr(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : ""; }

  // store.steampowered.com/api/appdetails (public, no sign-in); kept a week in covers\about
  static AboutInfo FetchAbout(string appId) {
    string folder = Path.Combine(Path.Combine(dir, "covers"), "about");
    string file = Path.Combine(folder, appId + ".json");
    string json = null;
    if (File.Exists(file) && (DateTime.Now - File.GetLastWriteTime(file)).TotalDays < 7) json = File.ReadAllText(file, Encoding.UTF8);
    if (json == null) {
      System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
      using (var wc = new System.Net.WebClient()) { wc.Encoding = Encoding.UTF8; json = wc.DownloadString("https://store.steampowered.com/api/appdetails?appids=" + appId + "&l=arabic"); }
      try { Directory.CreateDirectory(folder); File.WriteAllText(file, json, new UTF8Encoding(false)); } catch { }
    }
    var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
    object app;
    if (root == null || !root.TryGetValue(appId, out app)) return null;
    var appD = app as Dictionary<string, object>;
    object dataO;
    if (appD == null || !appD.TryGetValue("data", out dataO)) return null;
    var data = dataO as Dictionary<string, object>;
    var info = new AboutInfo { Short = System.Net.WebUtility.HtmlDecode(JsStr(data, "short_description")).Trim(), About = HtmlToText(JsStr(data, "about_the_game")), Header = JsStr(data, "header_image") };
    object o;
    if (data.TryGetValue("genres", out o) && o is object[]) info.Genres = string.Join("، ", ((object[])o).Select(x => JsStr(x as Dictionary<string, object>, "description")).Where(x => x.Length > 0).ToArray());
    if (data.TryGetValue("developers", out o) && o is object[]) info.Devs = string.Join("، ", ((object[])o).Select(x => x == null ? "" : x.ToString()).ToArray());
    if (data.TryGetValue("release_date", out o)) info.Release = JsStr(o as Dictionary<string, object>, "date");
    if (info.About == info.Short) info.About = "";
    return info;
  }

  static void ShowAbout(Game g) {
    if (libWin == null || libScroll == null || g.AppId == null) return;
    libAchGame = null; libAchList = null;
    libAboutGame = g;
    libTitle.Text = g.Name;
    libTitle.FlowDirection = HasArabic(g.Name) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    libCount.Text = "📖 عن اللعبة";
    libAddButton.Visibility = Visibility.Collapsed;
    if (libHiddenButton != null) libHiddenButton.Visibility = Visibility.Collapsed;
    libBackButton.Visibility = Visibility.Visible;
    libHint.Text = "";
    libScroll.Content = new TextBlock { Text = "جاري التحميل...", Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)), FontSize = 17, Margin = new Thickness(34, 20, 34, 0) };
    ApplyLibFilter();
    var t = new System.Threading.Thread(delegate () {
      AboutInfo info = null;
      try { info = FetchAbout(g.AppId); } catch { }
      Application.Current.Dispatcher.BeginInvoke(new Action(delegate { if (libAboutGame == g && libWin != null) RenderAbout(g, info); }));
    });
    t.IsBackground = true;
    t.Start();
  }

  static TextBlock Para(string text, double size, Color color, Thickness margin) {
    bool ar = HasArabic(text);
    return new TextBlock { Text = text, FontSize = size, Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap, Margin = margin, LineHeight = size * 1.55,
                           FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, TextAlignment = TextAlignment.Left };
  }

  static void RenderAbout(Game g, AboutInfo info) {
    var page = new StackPanel { Margin = new Thickness(34, 0, 34, 20) };
    if (info == null) {
      page.Children.Add(Para("ما قدرت أجيب شرح اللعبة من ستيم. تأكد إن النت شغّال وجرّب مرة ثانية.", 17, Color.FromRgb(150, 150, 150), new Thickness(0, 20, 0, 0)));
      libScroll.Content = page;
      return;
    }
    if (!string.IsNullOrEmpty(info.Header)) {
      try {
        var img = new Image { Source = new BitmapImage(new Uri(info.Header)), Stretch = Stretch.Uniform, MaxHeight = 250, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 18), Child = img });
      } catch { }
    }
    var facts = new List<string>();
    if (!string.IsNullOrEmpty(info.Genres)) facts.Add("النوع: " + Tr(info.Genres));
    if (!string.IsNullOrEmpty(info.Devs)) facts.Add("المطوّر: " + info.Devs);
    if (!string.IsNullOrEmpty(info.Release)) facts.Add("نزلت: " + Digits(Tr(info.Release)));
    if (facts.Count > 0) page.Children.Add(new TextBlock { Text = string.Join("   ·   ", facts.ToArray()), FontSize = 13.5, Foreground = new SolidColorBrush(Gold), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
    if (!string.IsNullOrEmpty(info.Short)) page.Children.Add(Para(Tr(info.Short), 18, Color.FromRgb(240, 240, 240), new Thickness(0, 0, 0, 18)));
    if (!string.IsNullOrEmpty(info.About)) page.Children.Add(Para(Tr(info.About), 15, Color.FromRgb(175, 175, 175), new Thickness(0)));
    libScroll.Content = page;
    libScroll.ScrollToTop();
    var texts = new[] { info.Short, info.About, info.Genres, info.Release };
    SetLangButton(texts.Any(x => !string.IsNullOrEmpty(x) && !HasArabic(x)));
    if (TranslateThen("about:" + g.AppId, texts, delegate { if (libAboutGame == g && libWin != null) RenderAbout(g, info); }))
      page.Children.Insert(0, new TextBlock { Text = "جاري التعريب...", FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130)), Margin = new Thickness(0, 0, 0, 10) });
  }

  // icons come from Steam's public image server (only the game id and the icon's file name are sent), kept in covers\ach
  static void SetAchIcon(Image img, string appId, string icon) {
    if (string.IsNullOrEmpty(icon)) return;
    icon = Path.GetFileName(icon);
    if (icon.IndexOf('.') < 0) icon += ".jpg";
    string folder = Path.Combine(Path.Combine(Path.Combine(dir, "covers"), "ach"), appId);
    string file = Path.Combine(folder, icon);
    if (File.Exists(file)) { img.Source = LoadPicture(file, 128); return; }
    string url = "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/" + appId + "/" + icon;
    System.Threading.ThreadPool.QueueUserWorkItem(delegate {
      try {
        Directory.CreateDirectory(folder);
        string part = file + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + ".part";
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        using (var wc = new System.Net.WebClient()) wc.DownloadFile(url, part);
        if (!File.Exists(file)) File.Move(part, file); else File.Delete(part);
      } catch { return; }
      Application.Current.Dispatcher.BeginInvoke(new Action(delegate { img.Source = LoadPicture(file, 128); }));
    });
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
