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
  }

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
      var texts = ((object[])req["texts"]).Select(x => x == null ? "" : x.ToString()).ToList();
      TranslateAll(texts);
      var outArr = texts.Select(t => CachedAr(t) ?? "").ToArray();
      string expr = "window.__hisnTrDone && window.__hisnTrDone(" + Convert.ToInt32(req["id"]) + "," + js.Serialize(outArr) + ")";
      var prm = new Dictionary<string, object> { { "expression", expr } };
      if (ctx != null) prm["contextId"] = ctx;
      SteamSend(s, "Runtime.evaluate", prm);
    } catch { }
  }

  static string SteamScript() {
    return @"(function(){
 if (window.__hisn) return; window.__hisn = 1;
 var names = new Set(" + (steamNamesJson ?? "[]") + @");
 var AR = /[؀-ۿ]/, done = new WeakSet(), cache = new Map(), pend = new Map(), queue = [], seq = 0, timer = null;
 function want(t){
   var s = t.trim();
   if (s.length < 4 || AR.test(s) || names.has(s) || /^(https?:|www\.)/i.test(s)) return false;
   var lat = (s.match(/[A-Za-z]/g) || []).length;
   if (lat < 3 || lat < s.replace(/[\s\d\W]/g, '').length * 0.8) return false;
   if (s.split(/\s+/).length < 2) return /^[A-Z]?[a-z]{8,}$/.test(s);   // one word: only real words, not user names
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
 }
 function scan(root){
   if (!root) return;
   if (root.nodeType === 3) root = root.parentNode;
   if (!root || !root.ownerDocument && root !== document) return;
   var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT), n;
   while ((n = w.nextNode())) {
     if (done.has(n) || !want(n.nodeValue) || skip(n.parentElement)) continue;
     var k = n.nodeValue.trim();
     if (cache.has(k)) { if (cache.get(k)) apply(n, cache.get(k)); continue; }
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
     nodes.forEach(function(n){ var k = (n.nodeValue || '').trim(); if (k && texts.indexOf(k) < 0 && !cache.has(k)) texts.push(k); });
     if (texts.length) {
       var id = ++seq; nodes.texts = texts; pend.set(id, nodes);
       try { window.hisnTr(JSON.stringify({ id: id, texts: texts })); } catch (e) { pend.delete(id); }
     }
     if (queue.length) flush();
   }, 300);
 }
 window.__hisnTrDone = function(id, out){
   var nodes = pend.get(id); if (!nodes) return; pend.delete(id);
   nodes.texts.forEach(function(t, i){ cache.set(t, out[i] || ''); });
   nodes.forEach(function(n){ var k = n.nodeValue && n.nodeValue.trim(); if (k && cache.get(k)) apply(n, cache.get(k)); });
 };
 new MutationObserver(function(ms){
   ms.forEach(function(m){
     if (m.type === 'characterData') { var t = m.target; if (t.nodeValue !== t.__hisnAr) { done.delete(t); scan(t); } }
     else m.addedNodes.forEach(function(a){ scan(a); });
   });
 }).observe(document.documentElement, { childList: true, subtree: true, characterData: true });
 if (document.body) scan(document.body); else document.addEventListener('DOMContentLoaded', function(){ scan(document.body); });
})();";
  }
}
