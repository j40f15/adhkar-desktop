// adhkar-dua.exe, part 5: Arabic inside Steam itself (setting "steam_arabic", off unless you turn it on).
// Steam is started with -cef-enable-debugging, which lets local programs talk to its window on 127.0.0.1:8080.
// This connects to Steam's main window and its store / community pages, adds a small script there that finds
// English text (game updates and news, achievements, descriptions) and swaps in an Arabic translation.
// Game names are never translated; hovering a translated line shows the original. The script is re-added every
// time Steam starts or opens a page, so Steam's own updates don't remove it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public static partial class DuaWidget {
  class SteamSession { public ClientWebSocket Ws; public readonly object SendLock = new object(); public int NextId = 100; }
  static readonly Dictionary<string, SteamSession> steamSessions = new Dictionary<string, SteamSession>();
  static string steamNamesJson;                                   // every game name Steam knows (never translated)
  static DateTime steamNamesAt = DateTime.MinValue;
  static string steamMarksJson = "{}";                            // achievement name -> "m" (might be missed) or "r:٢٪" (rare, you have it)

  // marks for Steam's own achievement lists: the same yellow / 💎 as in the library.
  // Installed games at start, and any other game the moment you open it in Steam (see MarksForApp).
  static readonly Dictionary<string, string> allMarks = new Dictionary<string, string>();
  static readonly Dictionary<string, DateTime> appMarksAt = new Dictionary<string, DateTime>();

  static void AddMarks(List<Ach> list) {
    lock (allMarks) {
      foreach (var a in list) {
        string v = a.Missable ? "m" : (a.Done && a.Percent >= 0 && a.Percent < 5 ? "r:" + PercentAr(a.Percent) : null);
        foreach (string n in new[] { a.Name, a.NameEn, Tr(a.Name) }) {
          if (string.IsNullOrEmpty(n)) continue;
          if (v == null) allMarks.Remove(n); else allMarks[n] = v;
        }
      }
    }
  }

  static string MarksJson() { lock (allMarks) return new JavaScriptSerializer().Serialize(allMarks); }

  static string ComputeSteamMarks() {
    foreach (var g in libGames.ToList()) {
      if (g.AppId == null || g.AchTotal == 0) continue;
      try { var list = ReadSteamAchievements(g.AppId); EnrichAchievements(g.AppId, list); AddMarks(list); } catch { }
    }
    return MarksJson();
  }

  // your Steam id (64-bit) = 76561197960265728 + the signed-in account number Steam keeps in the registry
  static string SteamId64() {
    try {
      using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess")) {
        object v = k == null ? null : k.GetValue("ActiveUser");
        if (v != null) { ulong acc = (uint)(int)v; if (acc != 0) return (76561197960265728UL + acc).ToString(); }
      }
    } catch { }
    return null;
  }

  // any game's achievements and what you unlocked, from your public Steam profile (cached 10 minutes)
  static List<Ach> WebAchievements(string appId) {
    var res = new List<Ach>();
    string sid = SteamId64();
    if (sid == null) return res;
    string folder = Path.Combine(Path.Combine(dir, "covers"), "ach");
    var byApi = new Dictionary<string, Ach>();
    foreach (string lang in new[] { "english", "arabic" }) {
      string file = Path.Combine(folder, appId + "-me-" + lang + ".xml");
      string xml = null;
      try { if (File.Exists(file) && (DateTime.Now - File.GetLastWriteTime(file)).TotalMinutes < 10) xml = File.ReadAllText(file, Encoding.UTF8); } catch { }
      if (xml == null) {
        try {
          using (var wc = new WebClient()) { wc.Encoding = Encoding.UTF8; xml = wc.DownloadString("https://steamcommunity.com/profiles/" + sid + "/stats/" + appId + "/achievements/?xml=1&l=" + lang); }
          Directory.CreateDirectory(folder);
          File.WriteAllText(file, xml, new UTF8Encoding(false));
        } catch { continue; }
      }
      foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml, "<achievement closed=\"(\\d)\">(.*?)</achievement>", System.Text.RegularExpressions.RegexOptions.Singleline)) {
        string body = m.Groups[2].Value;
        Func<string, string> tag = delegate (string t) {
          var mm = System.Text.RegularExpressions.Regex.Match(body, "<" + t + ">(?:<!\\[CDATA\\[)?(.*?)(?:\\]\\]>)?</" + t + ">", System.Text.RegularExpressions.RegexOptions.Singleline);
          return mm.Success ? WebUtility.HtmlDecode(mm.Groups[1].Value.Trim()) : "";
        };
        string api = tag("apiname");
        if (api.Length == 0) continue;
        Ach a;
        if (!byApi.TryGetValue(api, out a)) { a = new Ach { ApiName = api, Done = m.Groups[1].Value == "1" }; long ts; if (long.TryParse(tag("unlockTimestamp"), out ts)) a.Time = ts; byApi[api] = a; res.Add(a); }
        if (lang == "english") { a.NameEn = tag("name"); a.DescEn = tag("description"); if (a.Name == null) a.Name = a.NameEn; if (a.Desc == null) a.Desc = a.DescEn; }
        else { string n = tag("name"); if (HasArabic(n)) { a.Name = n; string d = tag("description"); if (HasArabic(d)) a.Desc = d; } }
      }
    }
    foreach (var a in res) { if (string.IsNullOrEmpty(a.Desc)) a.Desc = ""; }
    return res;
  }

  // the game you just opened in Steam: work out its marks (on this thread, may go online) and send them to the pages
  static void MarksForApp(string appId) {
    lock (appMarksAt) {
      DateTime at;
      if (appMarksAt.TryGetValue(appId, out at) && (DateTime.Now - at).TotalMinutes < 10) { BroadcastMarks(); return; }   // known: just send
      appMarksAt[appId] = DateTime.Now;
    }
    List<Ach> list = ReadSteamAchievements(appId);
    if (list.Count == 0) list = WebAchievements(appId);
    if (list.Count == 0) return;
    EnrichAchievements(appId, list);
    AddMarks(list);
    BroadcastMarks();
  }

  static void BroadcastMarks() {
    string marks = MarksJson();
    steamMarksJson = marks;
    List<SteamSession> open;
    lock (steamSessions) open = steamSessions.Values.Where(x => x.Ws != null).ToList();
    foreach (var s in open) { try { SteamSend(s, "Runtime.evaluate", new { expression = "window.__hisnSetMarks && window.__hisnSetMarks(" + marks + ")" }); } catch { } }
  }

  static void StartSteamArabic() {
    var t = new Thread(delegate () {
      while (true) {
        try { if (CfgBool("steam_arabic", false)) ScanSteamTargets(); } catch { }
        Thread.Sleep(4000);
      }
    });
    t.IsBackground = true;
    t.Start();
  }

  static object[] SteamTargets() {
    try {
      using (var wc = new WebClient()) {
        wc.Encoding = Encoding.UTF8;
        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(wc.DownloadString("http://127.0.0.1:8080/json")) as object[];
      }
    } catch { return null; }                                     // Steam closed, or not started in this mode
  }

  static void ScanSteamTargets() {
    var list = SteamTargets();
    if (list == null) { steamNamesJson = null; return; }
    if (steamNamesJson == null || (DateTime.Now - steamNamesAt).TotalMinutes > 30) {
      foreach (object o in list) {
        var tg = o as Dictionary<string, object>;
        if (tg == null || JsStr(tg, "title") != "SharedJSContext") continue;
        try {
          string v = EvalOnce(JsStr(tg, "webSocketDebuggerUrl"), "JSON.stringify((window.appStore && appStore.allApps || []).map(function(a){ return a.display_name; }))");
          if (v != null) { steamNamesJson = v; steamNamesAt = DateTime.Now; }
        } catch { }
      }
      if (steamNamesJson == null) return;                         // Steam is still starting
      marksDue = true;
    }
    foreach (object o in list) {
      var tg = o as Dictionary<string, object>;
      if (tg == null) continue;
      string id = JsStr(tg, "id"), title = JsStr(tg, "title"), url = JsStr(tg, "url"), ws = JsStr(tg, "webSocketDebuggerUrl");
      if (JsStr(tg, "type") != "page" || ws.Length == 0) continue;
      bool wanted = title == "Steam" || url.StartsWith("https://store.steampowered.com") || url.StartsWith("https://steamcommunity.com");
      if (!wanted) continue;
      lock (steamSessions) { if (steamSessions.ContainsKey(id)) continue; steamSessions[id] = new SteamSession(); }
      string sid = id, swss = ws;
      var t = new Thread(delegate () { RunSteamSession(sid, swss); });
      t.IsBackground = true;
      t.Start();
    }
    if (!ReferenceEquals(marksFor, libGames)) marksDue = true;    // the game list (re)loaded since the last marks
    if (marksDue) {                                               // after the pages are hooked: this one may go online
      marksDue = false;
      marksFor = libGames;
      string marks = steamMarksJson;
      try { marks = ComputeSteamMarks(); } catch { }
      if (marks != steamMarksJson) {                              // tell the pages already open
        steamMarksJson = marks;
        List<SteamSession> open;
        lock (steamSessions) open = steamSessions.Values.Where(x => x.Ws != null).ToList();
        foreach (var s in open) { try { SteamSend(s, "Runtime.evaluate", new { expression = "window.__hisnSetMarks && window.__hisnSetMarks(" + marks + ")" }); } catch { } }
      }
    }
  }
  static bool marksDue;
  static object marksFor;

  static string ReadMessage(ClientWebSocket ws) {
    var buf = new byte[65536];
    using (var ms = new MemoryStream()) {
      WebSocketReceiveResult r;
      do {
        r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
        if (r.MessageType == WebSocketMessageType.Close) return null;
        ms.Write(buf, 0, r.Count);
      } while (!r.EndOfMessage);
      return Encoding.UTF8.GetString(ms.ToArray());
    }
  }

  // one call to a page, for the list of game names
  static string EvalOnce(string wsUrl, string expr) {
    using (var ws = new ClientWebSocket()) {
      if (!ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).Wait(5000)) return null;
      var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
      string msg = js.Serialize(new { id = 1, method = "Runtime.evaluate", @params = new { expression = expr, returnByValue = true } });
      byte[] b = Encoding.UTF8.GetBytes(msg);
      ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
      DateTime until = DateTime.Now.AddSeconds(10);
      while (DateTime.Now < until) {
        string m = ReadMessage(ws);
        if (m == null) return null;
        var d = js.DeserializeObject(m) as Dictionary<string, object>;
        object id;
        if (d == null || !d.TryGetValue("id", out id) || Convert.ToInt32(id) != 1) continue;
        var res = d["result"] as Dictionary<string, object>;
        var inner = res == null ? null : res["result"] as Dictionary<string, object>;
        return inner == null ? null : JsStr(inner, "value");
      }
      return null;
    }
  }

  static void SteamSend(SteamSession s, string method, object prms) {
    var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    lock (s.SendLock) {
      string msg = js.Serialize(new Dictionary<string, object> { { "id", s.NextId++ }, { "method", method }, { "params", prms ?? new Dictionary<string, object>() } });
      byte[] b = Encoding.UTF8.GetBytes(msg);
      s.Ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
    }
  }

  static void RunSteamSession(string id, string wsUrl) {
    SteamSession s;
    lock (steamSessions) s = steamSessions[id];
    try {
      using (var ws = new ClientWebSocket()) {
        s.Ws = ws;
        if (!ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).Wait(5000) || ws.State != WebSocketState.Open) return;
        string script = SteamScript();
        SteamSend(s, "Runtime.enable", null);
        SteamSend(s, "Runtime.addBinding", new { name = "hisnTr" });
        SteamSend(s, "Page.addScriptToEvaluateOnNewDocument", new { source = script });
        SteamSend(s, "Runtime.evaluate", new { expression = script });
        SteamSend(s, "Runtime.evaluate", new { expression = "window.__hisnSetMarks && window.__hisnSetMarks(" + steamMarksJson + "); window.__hisnResendApp && window.__hisnResendApp()" });
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        while (ws.State == WebSocketState.Open) {
          string m = ReadMessage(ws);
          if (m == null) break;
          if (m.IndexOf("\"Runtime.bindingCalled\"", StringComparison.Ordinal) < 0) continue;
          var d = js.DeserializeObject(m) as Dictionary<string, object>;
          var p = d == null ? null : d["params"] as Dictionary<string, object>;
          if (p == null || JsStr(p, "name") != "hisnTr") continue;
          string payload = JsStr(p, "payload");
          object ctx; p.TryGetValue("executionContextId", out ctx);
          ThreadPool.QueueUserWorkItem(delegate { AnswerSteam(s, payload, ctx); });
        }
      }
    } catch { }
    finally { lock (steamSessions) steamSessions.Remove(id); }
  }

  // the page asked for {id, texts}: translate (cache first) and hand the Arabic back in the same order
  static void AnswerSteam(SteamSession s, string payload, object ctx) {
    try {
      var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
      var req = js.DeserializeObject(payload) as Dictionary<string, object>;
      if (req == null) return;
      if (req.ContainsKey("app")) { MarksForApp(Convert.ToString(req["app"])); return; }   // the page shows a game: mark its achievements
      var texts = ((object[])req["texts"]).Select(x => x == null ? "" : x.ToString()).ToList();
      int id = Convert.ToInt32(req["id"]);
      Action<bool> answer = delegate (bool last) {
        var outArr = texts.Select(t => CachedAr(t) ?? "").ToArray();
        string expr = "window.__hisnTrDone && window.__hisnTrDone(" + id + "," + js.Serialize(outArr) + "," + (last ? "true" : "false") + ")";
        var prm = new Dictionary<string, object> { { "expression", expr } };
        if (ctx != null) prm["contextId"] = ctx;
        SteamSend(s, "Runtime.evaluate", prm);
      };
      bool missing = texts.Any(NeedsTr);
      if (missing && texts.Any(t => CachedAr(t) != null)) answer(false);   // what we already know shows up right away
      if (missing) TranslateAll(texts);
      answer(true);
    } catch { }
  }

  static string SteamScript() {
    return @"(function(){
 var V = 6;                                             // raise when this script changes: a newer one replaces an older one
 if ((window.__hisn || 0) >= V) return; window.__hisn = V;
 if (window.__hisnObs) window.__hisnObs.disconnect();
 var names = new Set(" + (steamNamesJson ?? "[]") + @");
 var AR = /[؀-ۿ]/, done = new WeakSet(), cache = new Map(), pend = new Map(), queue = [], seq = 0, timer = null, failed = new Map();
 var inflight = new Set(), waiting = [];
 setInterval(function(){ if (document.body) scan(document.body); }, 60000);   // picks up texts that failed (busy translator)
 var marks = " + steamMarksJson + @";
 function ensureStyle(){
   if (document.getElementById('hisn-style')) return;
   var s = document.createElement('style'); s.id = 'hisn-style';
   s.textContent = '[data-hisn-m]{color:#f5c542 !important}[data-hisn-m]::before{content:""⚠ ممكن تفوتك  "";font-size:.85em}' +
                   '[data-hisn-r]::after{content:""  💎 "" attr(data-hisn-r);color:#78c8ff;font-size:.85em}';
   (document.head || document.documentElement).appendChild(s);
 }
 function markNode(n){
   var k = n.__hisnOrig || (n.nodeValue || '').trim(); var v = k && marks[k]; if (!v) return;
   var p = n.parentElement; if (!p || p.hasAttribute('data-hisn-m') || p.hasAttribute('data-hisn-r')) return;
   ensureStyle();
   if (v === 'm') p.setAttribute('data-hisn-m', '1'); else p.setAttribute('data-hisn-r', v.substring(2));
 }
 window.__hisnSetMarks = function(m){
   marks = m || {};
   if (!document.body) return;
   var w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT), n; while ((n = w.nextNode())) markNode(n);
 };
 function want(t){
   var s = t.trim();
   if (s.length < 4 || AR.test(s) || names.has(s) || /^(https?:|www\.)/i.test(s)) return false;
   if (/^(unlocked\s+)?(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{1,2}(,\s*\d{4})?(,?\s*@?\s*\d{1,2}:\d{2}\s*(am|pm)?)?$/i.test(s)) return false;   // dates
   var tried = failed.get(s); if (tried && Date.now() - tried < 60000) return false;          // failed a moment ago: try again later
   var lat = (s.match(/[A-Za-z]/g) || []).length;
   if (lat < 3 || lat < s.replace(/[\s\d\W]/g, '').length * 0.8) return false;
   if (s.split(/\s+/).length < 2) return /^[A-Z]?[a-z]{5,}[!?.]?$/.test(s);   // one word: only plain words, not user names like xX_Ace99
   return true;
 }
 function skip(el){
   for (var e = el; e; e = e.parentElement) {
     var n = e.nodeName;
     if (n == 'SCRIPT' || n == 'STYLE' || n == 'TEXTAREA' || n == 'INPUT' || n == 'CODE' || n == 'NOSCRIPT' || e.isContentEditable) return true;
   }
   return false;
 }
 function apply(n, ar){
   var v = n.nodeValue; if (!v) return;
   var k = v.trim(); if (!k) return;
   n.__hisnOrig = k; n.nodeValue = v.replace(k, ar); n.__hisnAr = n.nodeValue; done.add(n);
   var p = n.parentElement; if (p && !p.title) p.title = k;
   markNode(n);
 }
 function scan(root){
   if (!root) return;
   if (root.nodeType === 3) root = root.parentNode;
   if (!root || !root.ownerDocument && root !== document) return;
   var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT), n;
   while ((n = w.nextNode())) {
     markNode(n);
     if (done.has(n) || !want(n.nodeValue) || skip(n.parentElement)) continue;
     var k = n.nodeValue.trim();
     if (cache.has(k)) { if (cache.get(k)) apply(n, cache.get(k)); continue; }
     if (inflight.has(k)) { waiting.push(n); continue; }               // already asked: just wait for it
     queue.push(n);
   }
   flush();
 }
 function flush(){
   if (timer) return;
   timer = setTimeout(function(){
     timer = null;
     var nodes = queue.splice(0, 150); if (!nodes.length) return;
     var texts = [];
     nodes.forEach(function(n){ var k = (n.nodeValue || '').trim(); if (k && texts.indexOf(k) < 0 && !cache.has(k) && !inflight.has(k)) texts.push(k); });
     texts.forEach(function(t){ inflight.add(t); });
     if (texts.length) {
       var id = ++seq; nodes.texts = texts; pend.set(id, nodes);
       try { window.hisnTr(JSON.stringify({ id: id, texts: texts })); } catch (e) { pend.delete(id); }
     }
     if (queue.length) flush();
   }, 300);
 }
 window.__hisnTrDone = function(id, out, last){
   var nodes = pend.get(id); if (!nodes) return; if (last !== false) pend.delete(id);
   nodes.texts.forEach(function(t, i){
     if (out[i]) { cache.set(t, out[i]); failed.delete(t); inflight.delete(t); }
     else if (last !== false) { cache.delete(t); failed.set(t, Date.now()); inflight.delete(t); }
   });
   waiting = waiting.filter(function(n){ var k = n.nodeValue && n.nodeValue.trim(); if (k && cache.get(k)) { apply(n, cache.get(k)); return false; } return k && inflight.has(k); });
   nodes.forEach(function(n){ var k = n.nodeValue && n.nodeValue.trim(); if (k && cache.get(k)) apply(n, cache.get(k)); });
 };
 // which game is this page about? (the address, or the game whose pictures fill the page) -> its marks
 var lastApp = null, appTimer = null;
 function detectApp(){
   appTimer = null;
   var m = location.href.match(/\/(?:achievements|stats|app)\/(\d{2,9})/), id = m ? m[1] : null;
   if (!id) {
     var counts = {}, best = 1, imgs = document.images;
     for (var i = 0; i < imgs.length; i++) {
       var mm = (imgs[i].currentSrc || imgs[i].src || '').match(/\/(?:apps|assets)\/(\d{2,9})\//);
       if (mm) counts[mm[1]] = (counts[mm[1]] || 0) + 1;
     }
     for (var k in counts) if (counts[k] > best) { best = counts[k]; id = k; }
   }
   if (id && id !== lastApp) { lastApp = id; try { window.hisnTr(JSON.stringify({ app: id })); } catch (e) {} }
 }
 function soonApp(){ if (!appTimer) appTimer = setTimeout(detectApp, 1500); }
 window.__hisnResendApp = function(){ lastApp = null; detectApp(); };   // the widget restarted: ask for this game's marks again
 (window.__hisnObs = new MutationObserver(function(ms){
   soonApp();
   ms.forEach(function(m){
     if (m.type === 'characterData') { var t = m.target; if (t.nodeValue !== t.__hisnAr) { done.delete(t); scan(t); } }
     else m.addedNodes.forEach(function(a){ scan(a); });
   });
 })).observe(document.documentElement, { childList: true, subtree: true, characterData: true });
 if (document.body) scan(document.body); else document.addEventListener('DOMContentLoaded', function(){ scan(document.body); });
 soonApp();
})();";
  }
}
