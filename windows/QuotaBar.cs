using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyVersion("1.3.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.1.0")]

namespace CodexQuotaBar
{
    static class Json
    {
        public static string Encode(object value) { return new JavaScriptSerializer().Serialize(value); }
        public static Dictionary<string, object> Decode(string value) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(value); }
        public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object>; }
        public static object Get(Dictionary<string, object> obj, string key) { object v; return obj != null && obj.TryGetValue(key, out v) ? v : null; }
        public static double? Number(object value) { double d; return value != null && Double.TryParse(Convert.ToString(value), out d) && !Double.IsNaN(d) && !Double.IsInfinity(d) ? (double?)d : null; }
    }

    sealed class QuotaWindow
    {
        public double? Remaining;
        public double? Minutes;
        public double? Reset;
        public static QuotaWindow Parse(object value)
        {
            var obj = Json.Obj(value);
            if (obj == null) return null;
            var used = Json.Number(Json.Get(obj, "usedPercent"));
            return new QuotaWindow { Remaining = used.HasValue ? Math.Max(0, Math.Min(100, 100 - used.Value)) : (double?)null,
                Minutes = Json.Number(Json.Get(obj, "windowDurationMins")), Reset = Json.Number(Json.Get(obj, "resetsAt")) };
        }
        public string Label
        {
            get {
                if (!Minutes.HasValue) return "额度";
                if (Minutes.Value == 10080) return "周额度";
                if (Minutes.Value % 1440 == 0) return (Minutes.Value / 1440).ToString("0.#") + "天额度";
                if (Minutes.Value % 60 == 0) return (Minutes.Value / 60).ToString("0.#") + "小时";
                return Minutes.Value.ToString("0.#") + "分钟";
            }
        }
        public string Text { get { return Label + "剩余 " + (Remaining.HasValue ? Remaining.Value.ToString("0.#") + "%" : "未知"); } }
        public string ResetText
        {
            get {
                if (!Reset.HasValue || Reset.Value <= 0) return null;
                try {
                    var utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Reset.Value);
                    return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")).ToString("MM/dd HH:mm");
                } catch { return null; }
            }
        }
        public string Countdown(DateTime utcNow)
        {
            if (!Reset.HasValue || Reset.Value <= 0) return null;
            try {
                var left = new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(Reset.Value) - utcNow;
                if (left <= TimeSpan.Zero) return "等待额度重置";
                if (left.TotalDays >= 1) return ((int)left.TotalDays) + "天" + left.Hours + "小时后重置";
                if (left.TotalHours >= 1) return ((int)left.TotalHours) + "小时" + left.Minutes + "分钟后重置";
                return Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + "分钟后重置";
            } catch { return null; }
        }
    }

    sealed class QuotaSnapshot
    {
        public Dictionary<string, Dictionary<string, object>> Buckets = new Dictionary<string, Dictionary<string, object>>();
        public DateTime Updated;
        public static QuotaSnapshot Parse(Dictionary<string, object> result)
        {
            var s = new QuotaSnapshot { Updated = DateTime.Now };
            var map = Json.Obj(Json.Get(result, "rateLimitsByLimitId"));
            if (map != null) foreach (var pair in map) { var bucket = Json.Obj(pair.Value); if (bucket != null) s.Buckets[pair.Key] = bucket; }
            if (s.Buckets.Count == 0) {
                var single = Json.Obj(Json.Get(result, "rateLimits"));
                if (single != null) s.Buckets[Convert.ToString(Json.Get(single, "limitId")) ?? "codex"] = single;
            }
            return s;
        }
        public string Choose(string preferred)
        {
            if (!String.IsNullOrEmpty(preferred) && Buckets.ContainsKey(preferred)) return preferred;
            if (Buckets.ContainsKey("codex")) return "codex";
            return Buckets.Keys.FirstOrDefault();
        }
        public List<QuotaWindow> Windows(string id)
        {
            var list = new List<QuotaWindow>();
            if (id == null || !Buckets.ContainsKey(id)) return list;
            foreach (var key in new[] { "primary", "secondary" }) {
                var w = QuotaWindow.Parse(Json.Get(Buckets[id], key)); if (w != null) list.Add(w);
            }
            return list;
        }
        public string Line(string id)
        {
            var windows = Windows(id);
            if (windows.Count == 0) return "此账户暂未返回额度数据";
            var text = String.Join("   ·   ", windows.Select(w => w.Text));
            if (windows.Count == 1 && windows[0].ResetText != null) text += "   ·   " + windows[0].ResetText + " 重置";
            return text;
        }
    }

    sealed class AppServer : IDisposable
    {
        Process process;
        readonly object gate = new object();
        readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        int sequence;
        bool disposed;
        public async Task Connect(string executable)
        {
            var info = new ProcessStartInfo(executable, "app-server") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Program.Root };
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.Exited += delegate { FailPending(); };
            if (!process.Start()) throw new IOException("无法启动 Codex 额度接口");
            process.StandardInput.AutoFlush = true;
            Task.Run(async delegate {
                try { string line; while ((line = await process.StandardOutput.ReadLineAsync()) != null) {
                    Dictionary<string, object> msg; try { msg = Json.Decode(line); } catch { continue; }
                    var id = Json.Number(Json.Get(msg, "id")); if (!id.HasValue) continue;
                    TaskCompletionSource<Dictionary<string, object>> waiter = null;
                    lock (gate) { if (pending.TryGetValue((int)id.Value, out waiter)) pending.Remove((int)id.Value); }
                    if (waiter == null) continue;
                    if (Json.Get(msg, "error") != null) waiter.TrySetException(QuotaError.FromResponse(Json.Obj(Json.Get(msg, "error"))));
                    else waiter.TrySetResult(Json.Obj(Json.Get(msg, "result")) ?? new Dictionary<string, object>());
                }} catch { } finally { FailPending(); }
            });
            // Drain diagnostics without saving potentially sensitive server messages.
            Task.Run(async delegate { try { while (await process.StandardError.ReadLineAsync() != null) { } } catch { } });
            await Request("initialize", new { clientInfo = new { name = "codex_quota_bar", title = "Codex Quota Bar", version = Program.Version } });
            lock (gate) process.StandardInput.WriteLine(Json.Encode(new { method = "initialized", @params = new { } }));
        }
        async Task<Dictionary<string, object>> Request(string method, object parameters)
        {
            var waiter = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously); int id;
            lock (gate) {
                if (disposed || process == null || process.HasExited) throw new IOException("额度连接已断开");
                id = ++sequence; pending[id] = waiter;
                try { process.StandardInput.WriteLine(Json.Encode(new { id = id, method = method, @params = parameters })); }
                catch { pending.Remove(id); throw; }
            }
            if (await Task.WhenAny(waiter.Task, Task.Delay(20000)) != waiter.Task) {
                lock (gate) pending.Remove(id);
                throw new TimeoutException("额度请求超时");
            }
            return await waiter.Task;
        }
        public async Task<QuotaSnapshot> Read() { return QuotaSnapshot.Parse(await Request("account/rateLimits/read", null)); }
        void FailPending()
        {
            lock (gate) { foreach (var p in pending.Values) p.TrySetException(new IOException("额度连接已断开")); pending.Clear(); }
        }
        public void Dispose()
        {
            disposed = true; FailPending();
            if (process == null) return;
            try { process.StandardInput.Close(); if (!process.WaitForExit(700)) process.Kill(); } catch { }
            process.Dispose();
        }
    }

    sealed class ComposerAnchor
    {
        public Rectangle Editor;
        public Rectangle Slot;
        public Rectangle Owner;
        public int Dpi;
        public string Mode;
        public List<object> Controls = new List<object>();
        public bool Valid { get { return !Editor.IsEmpty && Slot.Width > 0 && Slot.Height > 0; } }
        public Rectangle SlotAt(Rectangle owner) { return new Rectangle(owner.Left+Slot.Left-Owner.Left,owner.Top+Slot.Top-Owner.Top,Slot.Width,Slot.Height); }
        public bool Matches(Rectangle owner, int dpi) { return Owner.Size==owner.Size && Dpi==dpi; }
    }

    sealed class AnchorCache
    {
        public ComposerAnchor Current;
        public DateTime ValidatedUtc = DateTime.MinValue;
        public bool Update(ComposerAnchor found, Rectangle owner, int dpi, DateTime now)
        {
            if (found == null || !found.Valid || !found.Matches(owner, dpi)) return false;
            Current = found; ValidatedUtc = now; return true;
        }
        public bool Usable(Rectangle owner, int dpi, DateTime now)
        {
            return Current != null && Current.Matches(owner, dpi) && now >= ValidatedUtc && now-ValidatedUtc <= TimeSpan.FromSeconds(6);
        }
        public void Clear() { Current = null; ValidatedUtc = DateTime.MinValue; }
    }

    sealed class RetrySchedule
    {
        int failures;
        public DateTime DueUtc = DateTime.MinValue;
        public void Success(DateTime now, bool active) { failures = 0; DueUtc = now.AddSeconds(active ? 60 : 180); }
        public void Fail(DateTime now)
        {
            int[] delays = { 5, 15, 30, 60, 120 };
            DueUtc = now.AddSeconds(delays[Math.Min(failures++, delays.Length-1)]);
        }
        public void Reset() { failures = 0; DueUtc = DateTime.MinValue; }
    }

    sealed class QuotaError : IOException
    {
        public QuotaError(string message) : base(message) { }
        public static QuotaError FromResponse(Dictionary<string, object> response)
        {
            // Use the server's message only for classification; never store or display its raw contents.
            string message = Convert.ToString(Json.Get(response, "message")).ToLowerInvariant();
            if (message.Contains("401") || message.Contains("unauthorized") || message.Contains("not logged") || message.Contains("authentication") || message.Contains("sign in"))
                return new QuotaError("登录状态已失效，请重新登录 Codex CLI");
            if (message.Contains("network") || message.Contains("dns") || message.Contains("connect") || message.Contains("timed out"))
                return new QuotaError("网络连接失败，正在自动重试");
            return new QuotaError("额度接口读取失败，正在自动重试");
        }
        public static string Describe(Exception error)
        {
            if (error is QuotaError) return error.Message;
            if (error is FileNotFoundException) return "未找到 Codex CLI，请检查安装路径";
            if (error is TimeoutException) return "额度请求超时，正在自动重试";
            if (error is UnauthorizedAccessException || error is System.ComponentModel.Win32Exception) return "无法启动 Codex CLI，请检查文件和运行权限";
            return "额度连接中断，正在自动重试";
        }
    }

    sealed class AnchorReader : IDisposable
    {
        Process worker;
        public string LastResult="not-started";
        public async Task<ComposerAnchor> Read(IntPtr window)
        {
            try {
                if(worker==null || worker.HasExited) {
                    worker=Process.Start(new ProcessStartInfo(Path.Combine(Program.Root,"CodexQuotaBar.exe"),"--anchor-worker "+Process.GetCurrentProcess().Id) {
                        UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,
                        StandardOutputEncoding=Encoding.UTF8,WorkingDirectory=Program.Root });
                    worker.StandardInput.AutoFlush=true;
                }
                worker.StandardInput.WriteLine(window.ToInt64());
                var read=worker.StandardOutput.ReadLineAsync();
                if(await Task.WhenAny(read,Task.Delay(5000))!=read) { LastResult="timeout"; Dispose(); return null; }
                var line=await read; if(line==null) { LastResult="worker-exited"; Dispose(); return null; }
                var result=new JavaScriptSerializer().Deserialize<ComposerAnchor>(line);
                LastResult=result.Valid ? "located" : "no-editor-or-toolbar"; return result;
            } catch(Exception e) { LastResult=e.GetType().Name; Dispose(); return null; }
        }
        public void Dispose()
        {
            var previous=worker; worker=null;
            if(previous==null) return;
            try { if(!previous.HasExited) previous.Kill(); } catch { }
            Task.Run(delegate { previous.Dispose(); });
        }
    }

    static class QuotaPlacement
    {
        public static Rectangle Fit(Rectangle slot, Size size, double scale, double offsetX, double offsetY)
        {
            if(size.Width>slot.Width || size.Height>slot.Height || size.Width<=0 || size.Height<=0) return Rectangle.Empty;
            int x = slot.Left + (int)Math.Round(offsetX*scale);
            int y = slot.Top + (slot.Height-size.Height)/2 + (int)Math.Round(offsetY*scale);
            return new Rectangle(Math.Max(slot.Left,Math.Min(x,slot.Right-size.Width)),Math.Max(slot.Top,Math.Min(y,slot.Bottom-size.Height)),size.Width,size.Height);
        }
    }

    static class Native
    {
        static readonly Dictionary<uint, Tuple<DateTime, bool>> identities = new Dictionary<uint, Tuple<DateTime, bool>>();
        public delegate bool EnumCallback(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle,int command);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr handle, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLong64(IntPtr w, int n, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong32(IntPtr w, int n, int value);
        public static void Owner(IntPtr window, IntPtr owner) { if (IntPtr.Size == 8) SetWindowLong64(window, -8, owner); else SetWindowLong32(window, -8, owner.ToInt32()); }
        public static bool IsTopmost(IntPtr window) { return (GetWindowLong(window, -20) & 0x00000008) != 0; }
        public static void PlaceAboveTarget(IntPtr window, IntPtr target, Rectangle bounds)
        {
            const uint noActivate = 0x0010, noZOrder = 0x0004;
            // Windows keeps an owned popup above its owner even after the owner regains focus.
            // A standalone normal window can be pushed behind the foreground app.
            if (GetWindow(window, 4) != target) Owner(window, target);
            // A quota overlay must stay outside the topmost band used by IME candidates.
            if (IsTopmost(window)) SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, noActivate | 0x0003);
            var previous = GetWindow(target, 3); // GW_HWNDPREV: the window immediately above Codex.
            uint flags = noActivate;
            if (previous == window) {
                Rect existing;
                if (GetWindowRect(window, out existing) && existing.ToRectangle() == bounds) return;
                flags |= noZOrder;
            }
            // Keep popups already above Codex above the bar, including non-topmost IME windows.
            // When Codex is first in its band, HWND_TOP raises the bar within the normal band.
            else if (previous != IntPtr.Zero && IsTopmost(previous)) previous = IntPtr.Zero;
            SetWindowPos(window, previous, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags);
        }
        public static int Dpi(IntPtr window) { try { return (int)GetDpiForWindow(window); } catch { return 96; } }
        public static Color? SurfaceColor(IntPtr window, Rectangle bar, Rectangle owner)
        {
            // Sample a few neighboring background pixels only, without capturing or storing a screenshot.
            var pixels = new List<Color>();
            var dc = GetDC(IntPtr.Zero); if (dc == IntPtr.Zero) return null;
            try {
                var anchors = new[] { new Point(bar.Left - 8, bar.Top + bar.Height / 2),
                    new Point(bar.Right + 8, bar.Top + bar.Height / 2), new Point(bar.Left + bar.Width / 2, bar.Bottom + 4) };
                foreach (var anchor in anchors) foreach (int dx in new[] { -2, 2 }) foreach (int dy in new[] { 0 }) {
                    var point = new Point(anchor.X + dx, anchor.Y + dy);
                    if (!owner.Contains(point) || GetAncestor(WindowFromPoint(point), 2) != window) continue;
                    var rgb = GetPixel(dc, point.X, point.Y); if (rgb == 0xFFFFFFFF) continue;
                    pixels.Add(Color.FromArgb((int)(rgb & 255), (int)((rgb >> 8) & 255), (int)((rgb >> 16) & 255)));
                }
            } finally { ReleaseDC(IntPtr.Zero, dc); }
            if (pixels.Count < 4) return null;
            return pixels.OrderByDescending(c => pixels.Count(p => Math.Abs(c.R-p.R) + Math.Abs(c.G-p.G) + Math.Abs(c.B-p.B) < 12)).First();
        }
        public static bool IsCodex(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindowVisible(window)) return false;
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (!name.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return false;
            uint pid; GetWindowThreadProcessId(window, out pid);
            Tuple<DateTime, bool> cached;
            if (identities.TryGetValue(pid, out cached) && DateTime.UtcNow-cached.Item1 < TimeSpan.FromSeconds(5)) return cached.Item2;
            bool matches = false;
            try { using (var p = Process.GetProcessById((int)pid)) {
                if (String.Equals(p.ProcessName, "Codex", StringComparison.OrdinalIgnoreCase)) matches = true;
                // Current Store builds retain the Codex package name but run ChatGPT.exe.
                if (String.Equals(p.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase)) {
                    var path = p.MainModule.FileName;
                    matches = path.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }} catch { }
            if (identities.Count > 64) identities.Clear();
            identities[pid] = Tuple.Create(DateTime.UtcNow, matches);
            return matches;
        }
        public static List<IntPtr> Windows()
        {
            var windows = new List<IntPtr>(); EnumWindows(delegate(IntPtr w, IntPtr x) { if (IsCodex(w)) windows.Add(w); return true; }, IntPtr.Zero); return windows;
        }
        public static Rectangle Composer(IntPtr window)
        {
            return ReadComposer(window).Editor;
        }
        public static ComposerAnchor ReadComposer(IntPtr window)
        {
            // Read control geometry and toolbar button labels, never editable values or chat text.
            var result = new ComposerAnchor();
            try {
                Rect bounds; if (!GetWindowRect(window, out bounds)) return result;
                var r = bounds.ToRectangle();
                result.Owner = r; result.Dpi = Dpi(window);
                var root = AutomationElement.FromHandle(window);
                var cache = new CacheRequest();
                cache.Add(AutomationElement.BoundingRectangleProperty); cache.Add(AutomationElement.IsOffscreenProperty);
                using(cache.Activate()) {
                var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                Rectangle best = Rectangle.Empty;
                AutomationElement bestElement=null;
                foreach (AutomationElement edit in edits) {
                    if (edit.Cached.IsOffscreen) continue;
                    var b = edit.Cached.BoundingRectangle;
                    if (b.IsEmpty) continue;
                    var candidate = Rectangle.FromLTRB((int)b.Left, (int)b.Top, (int)b.Right, (int)b.Bottom);
                    if (candidate.Width < 180 || candidate.Height < 22 || candidate.Height > r.Height * 0.4 || candidate.Top < r.Top + r.Height * 0.5 || candidate.Bottom > r.Bottom - 12) continue;
                    if (candidate.Top > best.Top) { best = candidate; bestElement=edit; }
                }
                result.Editor = best;
                if (best.IsEmpty) return result;
                // Prefer the composer's containing group instead of querying every chat action button.
                var scope=root; var ancestor=bestElement;
                for(int depth=0;depth<6;depth++) {
                    ancestor=TreeWalker.ControlViewWalker.GetParent(ancestor);
                    if(ancestor==null) break;
                    var b=ancestor.Current.BoundingRectangle;
                    if(b.Height>r.Height*.5) break;
                    if(b.Left<=best.Left && b.Right>=best.Right && b.Bottom>=best.Bottom+20*result.Dpi/96.0) { scope=ancestor; break; }
                }
                var buttons = scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                var candidates = new List<Tuple<string,Rectangle>>();
                foreach(AutomationElement button in buttons) {
                    try {
                        if (button.Cached.IsOffscreen) continue;
                        var b = button.Cached.BoundingRectangle;
                        if (b.IsEmpty) continue;
                        var rect = Rectangle.FromLTRB((int)b.Left,(int)b.Top,(int)b.Right,(int)b.Bottom);
                        if (rect.Width<=0 || rect.Height<=0 || rect.Left < best.Left-30 || rect.Right>best.Right+30 || rect.Top<best.Bottom-8 || rect.Bottom>r.Bottom || rect.Height>100) continue;
                        string name = button.Current.Name ?? "";
                        candidates.Add(Tuple.Create(name,rect));
                        result.Controls.Add(new { name = name, left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom });
                    } catch { }
                }
                var permissions = candidates.FirstOrDefault(p => p.Item1.Contains("帮我批准") || p.Item1.Contains("权限") || p.Item1.IndexOf("approval",StringComparison.OrdinalIgnoreCase)>=0 || p.Item1.IndexOf("permission",StringComparison.OrdinalIgnoreCase)>=0);
                if(permissions != null) {
                    var p = permissions.Item2;
                    var right = candidates.Where(c => c.Item2.Left>p.Right+5 && Math.Abs((c.Item2.Top+c.Item2.Bottom)-(p.Top+p.Bottom))<Math.Max(c.Item2.Height,p.Height)).OrderBy(c=>c.Item2.Left).FirstOrDefault();
                    int gap = (int)(8*result.Dpi/96.0);
                    result.Slot = Rectangle.FromLTRB(p.Right+gap,p.Top,right == null ? best.Right-gap : right.Item2.Left-gap,p.Bottom);
                    result.Mode = "permission-toolbar";
                }
                else if(candidates.Count>1) {
                    // Alternate language/label: use the largest actual gap in the bottom button row.
                    var bottom = candidates.OrderByDescending(c=>c.Item2.Bottom).First().Item2;
                    var row = candidates.Where(c=>Math.Abs((c.Item2.Top+c.Item2.Bottom)-(bottom.Top+bottom.Bottom))<bottom.Height).OrderBy(c=>c.Item2.Left).ToList();
                    int gap = (int)(8*result.Dpi/96.0);
                    for(int i=1;i<row.Count;i++) {
                        var left=row[i-1].Item2; var right=row[i].Item2;
                        int width=right.Left-left.Right-2*gap;
                        if(width>result.Slot.Width) result.Slot=Rectangle.FromLTRB(left.Right+gap,Math.Max(left.Top,right.Top),right.Left-gap,Math.Min(left.Bottom,right.Bottom));
                    }
                    result.Mode="toolbar-gap";
                }
                return result;
                }
            } catch { return result; }
        }
    }

    sealed class Settings
    {
        public bool CustomPosition;
        public string Bucket = "codex";
        public string Theme = "auto";
        public int PositionVersion = 2;
        public double AnchorOffsetX;
        public double AnchorOffsetY;
        public int DisplayDetail = 2;
        public bool LowQuotaAlerts;
        public Dictionary<string, int> Alerted = new Dictionary<string, int>();
        [ScriptIgnore] public string Notice;
        [ScriptIgnore] public string SaveError;
        public static Settings Load() { return LoadFrom(Path.Combine(Program.Root, "settings.json")); }
        static Settings Read(string path)
        {
            var obj = Json.Decode(File.ReadAllText(path, Encoding.UTF8));
            if (obj == null) throw new InvalidDataException();
            bool position = Json.Number(Json.Get(obj,"PositionVersion")) == 2;
            var settings = new Settings {
                CustomPosition = position && Object.Equals(Json.Get(obj, "CustomPosition"), true),
                Bucket = String.IsNullOrEmpty(Convert.ToString(Json.Get(obj,"Bucket"))) ? "codex" : Convert.ToString(Json.Get(obj,"Bucket")),
                AnchorOffsetX = position ? Json.Number(Json.Get(obj,"AnchorOffsetX")) ?? 0 : 0,
                AnchorOffsetY = position ? Json.Number(Json.Get(obj,"AnchorOffsetY")) ?? 0 : 0,
                Theme = Convert.ToString(Json.Get(obj,"Theme")),
                DisplayDetail = (int)Math.Max(0, Math.Min(2, Json.Number(Json.Get(obj,"DisplayDetail")) ?? 2)),
                LowQuotaAlerts = Object.Equals(Json.Get(obj,"LowQuotaAlerts"), true)
            };
            if (settings.Theme != "dark" && settings.Theme != "light") settings.Theme = "auto";
            var alerted = Json.Obj(Json.Get(obj,"Alerted"));
            if (alerted != null) foreach (var pair in alerted.Take(64)) settings.Alerted[pair.Key] = (int)(Json.Number(pair.Value) ?? 0);
            return settings;
        }
        public static Settings LoadFrom(string path)
        {
            if (!File.Exists(path) && !File.Exists(path+".bak")) return new Settings();
            try { return Read(path); } catch { }
            try { var saved = Read(path+".bak"); saved.Notice = "设置文件读取失败，已恢复上次备份"; return saved; }
            catch { return new Settings { Notice = "设置文件无法读取，已使用默认设置" }; }
        }
        public bool Save() { return SaveTo(Path.Combine(Program.Root, "settings.json")); }
        public bool SaveTo(string path)
        {
            string temporary = path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                File.WriteAllText(temporary, Json.Encode(this), new UTF8Encoding(false));
                if (File.Exists(path)) {
                    bool valid = false; try { Read(path); valid = true; } catch { }
                    File.Replace(temporary, path, valid ? path+".bak" : null, true);
                } else File.Move(temporary, path);
                SaveError = null; return true;
            } catch (Exception error) { SaveError = error.GetType().Name; return false; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }

    static class QuotaAlerts
    {
        public static string Take(Settings settings, string bucket, List<QuotaWindow> windows)
        {
            if (!settings.LowQuotaAlerts) return null;
            var messages = new List<string>();
            foreach (var window in windows) {
                if (!window.Remaining.HasValue || !window.Reset.HasValue || window.Reset <= 0) continue;
                string key = bucket+":"+window.Minutes+":"+window.Reset.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                int previous; settings.Alerted.TryGetValue(key, out previous);
                int current = window.Remaining <= 10 ? 3 : window.Remaining <= 20 ? 1 : 0;
                if ((current & ~previous) == 0) continue;
                settings.Alerted[key] = previous | current;
                messages.Add(window.Text);
            }
            if (settings.Alerted.Count > 64) {
                var currentKeys = new HashSet<string>(windows.Where(w => w.Reset.HasValue).Select(w => bucket+":"+w.Minutes+":"+w.Reset.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                foreach (var key in settings.Alerted.Keys.Where(k => !currentKeys.Contains(k)).ToArray()) settings.Alerted.Remove(key);
            }
            return messages.Count == 0 ? null : String.Join("；", messages);
        }
    }

    static class BarDesign
    {
        public static Color Mix(Color a, Color b, double weight) { return Color.FromArgb((int)(a.R*(1-weight)+b.R*weight), (int)(a.G*(1-weight)+b.G*weight), (int)(a.B*(1-weight)+b.B*weight)); }
        public static bool IsLight(Color color) { return color.R * .2126 + color.G * .7152 + color.B * .0722 > 145; }
        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            var path = new GraphicsPath(); float d = radius * 2;
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90); path.AddArc(rect.Right-d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right-d, rect.Bottom-d, d, d, 0, 90); path.AddArc(rect.Left, rect.Bottom-d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        static string Label(QuotaWindow w) { return (w.Label == "周额度" ? "周" : w.Label.Replace("额度", "")) + "剩余"; }
        static string Value(QuotaWindow w) { return w.Remaining.HasValue ? w.Remaining.Value.ToString("0.#") + "%" : "未知"; }
        static void TimeIcon(Graphics g, float x, float centerY, float s, Color color, bool calendar)
        {
            if(g==null) return;
            using(var pen=new Pen(color,1.25f*s)) {
                pen.StartCap=pen.EndCap=LineCap.Round; pen.LineJoin=LineJoin.Round;
                if(!calendar) {
                    g.DrawEllipse(pen,x,centerY-6*s,12*s,12*s);
                    g.DrawLines(pen,new[] { new PointF(x+6*s,centerY-3.5f*s),new PointF(x+6*s,centerY),new PointF(x+8.5f*s,centerY+1.5f*s) });
                } else {
                    using(var box=Round(new RectangleF(x,centerY-5*s,12*s,11*s),1.4f*s)) g.DrawPath(pen,box);
                    g.DrawLine(pen,x,centerY-1.5f*s,x+12*s,centerY-1.5f*s);
                    g.DrawLine(pen,x+3*s,centerY-7*s,x+3*s,centerY-3.5f*s);
                    g.DrawLine(pen,x+9*s,centerY-7*s,x+9*s,centerY-3.5f*s);
                }
            }
        }
        public static int Layout(Graphics graphics, Size size, double scale, Color background, bool hover, List<QuotaWindow> windows, string status, bool stale, int detail = 2)
        {
            float s = (float)scale;
            bool light = IsLight(background);
            var ink = light ? Color.FromArgb(35, 38, 42) : Color.FromArgb(224, 225, 228);
            var muted = Mix(background, ink, .62); var timeInk = Mix(background, ink, .88);
            var surface = hover ? Mix(background, ink, .045) : background;
            var warning = light ? Color.FromArgb(166, 112, 34) : Color.FromArgb(218, 174, 98);
            var rules = Mix(background, ink, .20);
            using (var measureBitmap = new Bitmap(1,1))
            using (var measure = Graphics.FromImage(measureBitmap))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            using (var regular = new Font("Microsoft YaHei UI", 12*s, FontStyle.Regular, GraphicsUnit.Pixel)) {
                // One font instance for every run: identical numeral height, hinting and origin.
                var strong=regular; var small=regular;
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                measure.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                Func<string,Font,float> textWidth = (value,font) => measure.MeasureString(value,font,PointF.Empty,format).Width;
                Func<Font,float> ascent = font => font.Size*font.FontFamily.GetCellAscent(font.Style)/font.FontFamily.GetEmHeight(font.Style);
                float descent = regular.Size*regular.FontFamily.GetCellDescent(regular.Style)/regular.FontFamily.GetEmHeight(regular.Style);
                float baseline = size.Height/2f+(ascent(regular)-descent)/2f;
                if (graphics != null) {
                    graphics.Clear(background); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    if (hover) using (var shape = Round(new RectangleF(.5f, .5f, size.Width-1, size.Height-1), 7*s)) {
                        using (var fill = new SolidBrush(surface)) graphics.FillPath(fill, shape);
                        using (var border = new Pen(Mix(background, ink, .085))) graphics.DrawPath(border, shape);
                    }
                }
                float x = 8*s;
                float textTop=(float)Math.Round(baseline-ascent(regular));
                // Shared pixel-snapped origin keeps all numerals on the same visible baseline.
                Action<string, Font, Color> text = delegate(string value, Font font, Color color) {
                    float width = textWidth(value,font);
                    if (graphics != null) {
                        var saved=graphics.Save();
                        graphics.SetClip(new RectangleF(x,0,Math.Max(0,size.Width-18*s-x),size.Height),CombineMode.Intersect);
                        using(var brush=new SolidBrush(color)) graphics.DrawString(value,regular,brush,new PointF(x,textTop),format);
                        graphics.Restore(saved);
                    }
                    x += width;
                };
                Action divider = delegate {
                    x += 8*s;
                    if (graphics != null) using (var pen = new Pen(rules)) graphics.DrawLine(pen, x, size.Height/2f-5*s, x, size.Height/2f+5*s);
                    x += 8*s;
                };
                if (windows.Count == 0) text(status, regular, muted);
                for (int i=0; i<windows.Count; i++) {
                    var w = windows[i]; if (i > 0) divider();
                    var accent = stale || (w.Remaining.HasValue && w.Remaining.Value <= 10) ? warning : Mix(background, ink, .70);
                    if (graphics != null) {
                        var circle = new RectangleF(x, size.Height/2f-5*s, 10*s, 10*s);
                        using (var pen = new Pen(Mix(background, ink, .13), 1.6f*s)) graphics.DrawEllipse(pen, circle);
                        if (w.Remaining.HasValue && w.Remaining.Value > 0) using (var pen = new Pen(accent, 1.6f*s)) {
                            pen.StartCap = pen.EndCap = LineCap.Round;
                            graphics.DrawArc(pen, circle, -90, (float)(w.Remaining.Value*3.6));
                        }
                    }
                    x += 16*s; text(Label(w), regular, muted); x += 4*s;
                    float valueStart=x;
                    text((stale ? "~" : "")+Value(w), w.Remaining.HasValue ? strong : regular, stale ? muted : ink);
                    x=Math.Max(x,valueStart+textWidth("100%",strong));
                }
                if (stale && detail>0) { divider(); text("待更新", small, warning); }
                else if (!stale && detail>0 && windows.Count == 1 && windows[0].ResetText != null) {
                    divider();
                    TimeIcon(graphics,x,size.Height/2f,s,timeInk,false); x+=18*s;
                    string countdown=windows[0].Countdown(DateTime.UtcNow).Replace("后重置","");
                    float countdownStart=x;
                    text(countdown, small, timeInk);
                    string countdownTemplate=countdown.Contains("天") ? "00天00小时" : (countdown.Contains("小时") ? "00小时00分钟" : "00分钟");
                    x=Math.Max(x,countdownStart+textWidth(countdownTemplate,small));
                    float resetWidth = textWidth(windows[0].ResetText, small);
                    if (detail>1 && (graphics == null || x + 52*s + resetWidth <= size.Width)) {
                        divider();
                        TimeIcon(graphics,x,size.Height/2f,s,timeInk,true); x+=18*s;
                        text(windows[0].ResetText, small, timeInk);
                    }
                }
                x += 18*s;
                if (graphics != null && hover) using (var brush = new SolidBrush(muted)) {
                    for(int i=0; i<3; i++) graphics.FillEllipse(brush, size.Width - 16*s + i*3*s, size.Height/2f-s, 1.6f*s, 1.6f*s);
                }
                return (int)Math.Ceiling(x);
            }
        }
    }

    static class AutoStart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "CodexQuotaBar";
        static string UserId { get { return System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; } }
        public static string TaskName { get { return Name+"-"+UserId; } }
        public static string Command { get { return "\""+Path.Combine(Program.Root,"CodexQuotaBar.exe")+"\" --background"; } }
        static dynamic Scheduler()
        {
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect(); return service;
        }
        public static bool Enabled {
            get { try { return ReadEnabled(); } catch { return false; } }
        }
        public static bool ReadEnabled()
        {
            try {
                dynamic task=Scheduler().GetFolder(@"\").GetTask(TaskName);
                dynamic action=task.Definition.Actions.Item(1);
                return task.Enabled && String.Equals((string)action.Path,Path.Combine(Program.Root,"CodexQuotaBar.exe"),StringComparison.OrdinalIgnoreCase);
            } catch(COMException error) {
                if(error.ErrorCode==unchecked((int)0x80070002)) return false;
                throw;
            }
        }
        public static void Set(bool enabled)
        {
            dynamic service=Scheduler(); dynamic folder=service.GetFolder(@"\");
            if(enabled) {
                dynamic task=service.NewTask(0);
                task.RegistrationInfo.Description="Codex 额度条：当前用户登录后等待 Codex，自动显示剩余额度。";
                task.Principal.UserId=UserId; task.Principal.LogonType=3; task.Principal.RunLevel=0;
                dynamic trigger=task.Triggers.Create(9);
                trigger.UserId=UserId; trigger.Delay="PT20S"; trigger.Enabled=true;
                dynamic action=task.Actions.Create(0);
                action.Path=Path.Combine(Program.Root,"CodexQuotaBar.exe");
                action.Arguments="--background"; action.WorkingDirectory=Program.Root;
                task.Settings.Enabled=true; task.Settings.StartWhenAvailable=true;
                task.Settings.DisallowStartIfOnBatteries=false; task.Settings.StopIfGoingOnBatteries=false;
                task.Settings.ExecutionTimeLimit="PT0S"; task.Settings.MultipleInstances=2;
                task.Settings.RestartInterval="PT1M"; task.Settings.RestartCount=3;
                folder.RegisterTaskDefinition(TaskName,task,6,UserId,null,3,null);
                if(!Enabled) throw new IOException("自动启动任务验证失败");
            } else {
                try { folder.GetTask(TaskName); folder.DeleteTask(TaskName,0); }
                catch(COMException e) { if(e.ErrorCode!=unchecked((int)0x80070002)) throw; }
            }
            // Migrate only our own old Run entry, after successful task registration.
            using(var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key)) {
                key.DeleteValue(Name,false);
            }
            Program.LogLifecycle(enabled ? "autostart-enabled" : "autostart-disabled");
        }
        public static void StartBackground(bool debug)
        {
            dynamic service=Scheduler(); dynamic folder=service.GetFolder(@"\");
            if(!debug) try {
                dynamic existing=folder.GetTask(TaskName);
                dynamic action=existing.Definition.Actions.Item(1);
                if(existing.Enabled && String.Equals((string)action.Path,Path.Combine(Program.Root,"CodexQuotaBar.exe"),StringComparison.OrdinalIgnoreCase)
                    && (string)action.Arguments=="--background") { existing.Run(null); return; }
            } catch(COMException e) { if(e.ErrorCode!=unchecked((int)0x80070002)) throw; }
            // A demand-only task gives the watcher an independent Windows parent.
            // It has no logon trigger and does not change the user's autostart choice.
            dynamic task=service.NewTask(0);
            task.RegistrationInfo.Description="Codex 额度条：按需启动后台显示（无自动启动触发器）。";
            task.Principal.UserId=UserId; task.Principal.LogonType=3; task.Principal.RunLevel=0;
            dynamic launch=task.Actions.Create(0);
            launch.Path=Path.Combine(Program.Root,"CodexQuotaBar.exe");
            launch.Arguments=debug ? "--background --debug-state" : "--background";
            launch.WorkingDirectory=Program.Root;
            task.Settings.Enabled=true; task.Settings.AllowDemandStart=true;
            task.Settings.DisallowStartIfOnBatteries=false; task.Settings.StopIfGoingOnBatteries=false;
            task.Settings.ExecutionTimeLimit="PT0S"; task.Settings.MultipleInstances=2;
            task.Settings.RestartInterval="PT1M"; task.Settings.RestartCount=3;
            dynamic registered=folder.RegisterTaskDefinition(Name+"-Manual-"+UserId,task,6,UserId,null,3,null);
            registered.Run(null);
        }
    }

    class OverlayWindow : Form
    {
        protected override void OnHandleDestroyed(EventArgs e)
        {
            base.OnHandleDestroyed(e);
            if (!Disposing && !IsDisposed && !RecreatingHandle) {
                // Windows destroys owned native handles with the owner. Keep the managed watcher
                // alive; a new handle will be created when another Codex window is found.
                Hide(); OwnerWindowClosed();
            }
        }
        protected virtual void OwnerWindowClosed() { }
    }

    sealed class Bar : OverlayWindow
    {
        readonly Settings settings = Settings.Load();
        readonly NotifyIcon tray = new NotifyIcon();
        SettingsWindow settingsWindow;
        SettingsBridge settingsBridge;
        bool autoStartEnabled;
        bool autoStartKnown, autoStartReading;
        string autoStartError;
        readonly ToolTip tooltip = new ToolTip { AutoPopDelay = 20000 };
        readonly System.Windows.Forms.Timer tracker = new System.Windows.Forms.Timer { Interval = 250 };
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly RetrySchedule retry = new RetrySchedule();
        readonly WindowFollower follower;
        AppServer server;
        readonly AnchorReader anchorReader=new AnchorReader();
        QuotaSnapshot snapshot;
        IntPtr target;
        readonly AnchorCache anchorCache = new AnchorCache();
        ComposerAnchor composerAnchor { get { return anchorCache.Current; } }
        Rectangle lastOwner;
        int lastDpi;
        int detailLevel = 2;
        Rectangle currentSlot;
        DateTime anchorChecked = DateTime.MinValue;
        DateTime lastWindowSearch = DateTime.MinValue;
        bool anchorBusy, refreshing, paused, dragging, closing;
        string status = "正在读取 Codex 额度…";
        string error;
        Point dragStart, formStart;
        double scale = 1;
        bool light;
        bool hovered;
        Color surface;
        Color codexSurface;
        DateTime surfaceChecked = DateTime.MinValue;
        DateTime stateWritten = DateTime.MinValue;
        long countdownMinute = -1;
        string lastRenderKey;
        string layoutKey;
        readonly Dictionary<int, int> layoutWidths = new Dictionary<int, int>();
        bool wasActive;
        DateTime lastSaveWarning = DateTime.MinValue;
        public Bar()
        {
            follower=new WindowFollower(this,delegate {
                return !closing && !paused && !dragging && Visible && target!=IntPtr.Zero &&
                    Native.GetForegroundWindow()==target && !Native.IsIconic(target);
            },delegate { if(!closing) Track(); });
            Text = "Codex 额度条"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = false;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true; Size = new Size(340, 26);
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel);
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) light = Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1; } catch { }
            surface = light ? Color.FromArgb(249,249,249) : Color.FromArgb(33,33,33);
            codexSurface=surface;
            tray.Icon = SystemIcons.Information; tray.Text = "Codex 额度条：正在连接"; tray.Visible = true;
            tray.MouseUp += delegate(object sender, MouseEventArgs e) { if(e.Button==MouseButtons.Right) OpenLocalSettings(); };
            tray.DoubleClick += delegate { OpenSettings(); };
            tracker.Tick += delegate { Track(); };
            refresh.Tick += async delegate { if(DateTime.UtcNow >= retry.DueUtc && Native.IsCodex(target)) await RefreshQuota(); };
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            MouseEnter += delegate { hovered = true; Invalidate(); };
            MouseLeave += delegate { hovered = false; Invalidate(); };
            MouseDown += delegate(object sender, MouseEventArgs e) {
                if (e.Button != MouseButtons.Left) return;
                if (e.X > Width - 18*scale) { OpenSettings(); return; }
                dragging = true; Capture = true; dragStart = Cursor.Position; formStart = Location;
            };
            MouseMove += delegate { if (dragging) Location = new Point(formStart.X + Cursor.Position.X - dragStart.X, formStart.Y + Cursor.Position.Y - dragStart.Y); };
            MouseUp += delegate(object sender, MouseEventArgs e) {
                if(e.Button==MouseButtons.Right) { OpenSettings(); return; }
                if (!dragging) return; dragging = false; Capture = false;
                if (Math.Abs(Cursor.Position.X-dragStart.X) + Math.Abs(Cursor.Position.Y-dragStart.Y) < 4) return;
                if(!currentSlot.IsEmpty) {
                    settings.AnchorOffsetX = (Left-currentSlot.Left)/scale;
                    settings.AnchorOffsetY = (Top-currentSlot.Top-(currentSlot.Height-Height)/2.0)/scale;
                    settings.CustomPosition = true; SaveSettings(); Track();
                }
            };
            Shown += delegate {
                Hide(); tracker.Start(); refresh.Start(); Program.LogLifecycle("watcher-ready " + Program.Version); Track();
                settingsBridge=new SettingsBridge(DispatchSettings);
                if (Program.OpenSettingsAtStartup) OpenSettings();
                if (settings.Notice != null) tray.ShowBalloonTip(5000,"Codex 额度条",settings.Notice,ToolTipIcon.Warning);
            };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; } }
        bool SaveSettings()
        {
            bool saved = settings.Save();
            if (!saved && DateTime.UtcNow-lastSaveWarning > TimeSpan.FromSeconds(30)) {
                lastSaveWarning = DateTime.UtcNow;
                tray.ShowBalloonTip(5000,"设置未保存","请检查程序目录的写入权限或磁盘空间；本次设置暂时生效。",ToolTipIcon.Warning);
                Program.LogLifecycle("settings-save-failed " + settings.SaveError);
            }
            return saved;
        }
        Task<object> DispatchSettings(Dictionary<string,object> request)
        {
            var completion=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if(closing || !IsHandleCreated) { completion.SetResult(new { error="额度条正在退出。" }); return completion.Task; }
            try { BeginInvoke((Action)(async delegate {
                try {
                    if(closing) { completion.TrySetResult(new { error="额度条正在退出。" }); return; }
                    string action=Convert.ToString(Json.Get(request,"action"));
                    if(action=="settings_read") {
                        if(!autoStartKnown && !autoStartReading) {
                            try { autoStartEnabled=await Task.Run(()=>AutoStart.ReadEnabled()); autoStartKnown=true; }
                            catch { autoStartError="无法读取自动启动状态。"; }
                        }
                        completion.TrySetResult(new { data=PluginSettings.Read(ReadSettingsView()) });
                    }
                    else if(action=="settings_update") {
                        var patch=PluginSettings.Validate(Json.Obj(Json.Get(request,"values")));
                        if(patch.ContainsKey("bucket") && (snapshot==null || !snapshot.Buckets.ContainsKey((string)patch["bucket"]))) throw new ArgumentException("此额度类型已不可用，请重新打开设置。");
                        foreach(var item in patch) {
                            var failure=item.Key=="autostart" ? await Task.Run(()=>ApplyPreference(item.Key,item.Value)) : ApplyPreference(item.Key,item.Value);
                            if(failure!=null) { completion.TrySetResult(new { error=failure }); return; }
                        }
                        if(settingsWindow!=null && !settingsWindow.IsDisposed) settingsWindow.UpdateLive();
                        completion.TrySetResult(new { data=new { values=PluginSettings.Values(ReadSettingsView()) } });
                    } else if(action=="quota_open_settings") { OpenLocalSettings(); completion.TrySetResult(new { text="已打开额度条设置。" }); }
                    else if(action=="quota_reset_position") {
                        var failure=ApplyPreference("position",null);
                        completion.TrySetResult(failure==null ? (object)new { text="已恢复默认位置。" } : new { error=failure });
                    } else if(action=="quota_status" || action=="quota_refresh") {
                        if(action=="quota_refresh") await RefreshQuota();
                        var view=ReadSettingsView();
                        if(action=="quota_refresh" && view.Error!=null) { completion.TrySetResult(new { error=view.Error }); return; }
                        var text=view.Preview+(view.Stale ? "\n数据待更新" : "")+(view.Updated.HasValue ? "\n上次更新 "+view.Updated.Value.ToString("MM/dd HH:mm:ss") : "");
                        completion.TrySetResult(new { text=text,data=new { preview=view.Preview,stale=view.Stale,updated=view.Updated.HasValue ? view.Updated.Value.ToString("o") : null,paused=view.Paused,
                            running=true,visible=Visible,targetFound=view.TargetFound,inputLocated=composerAnchor!=null && composerAnchor.Valid } });
                    } else completion.TrySetResult(new { error="未知操作。" });
                } catch(ArgumentException e) { completion.TrySetResult(new { error=e.Message }); }
                catch { completion.TrySetResult(new { error="设置未能完成，请重试。" }); }
            })); } catch(InvalidOperationException) { completion.TrySetResult(new { error="额度条正在退出。" }); }
            return completion.Task;
        }
        void OpenSettings()
        {
            if(TryOpenCodexSettings()) return;
            OpenLocalSettings();
        }
        bool TryOpenCodexSettings()
        {
            try {
                var path=Path.Combine(Program.Root,"plugin-integration.json");
                if(!File.Exists(path)) return false;
                var data=Json.Decode(File.ReadAllText(path));
                string url=Convert.ToString(Json.Get(data,"url"));
                Uri uri;
                if(!Uri.TryCreate(url,UriKind.Absolute,out uri) || uri.Scheme!="codex" || uri.Host!="plugins" || (uri.AbsolutePath!="/codex-quota-bar" && uri.AbsolutePath!="/codex-quota-bar@quota-local")) return false;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute=true }); return true;
            } catch { return false; }
        }
        void OpenLocalSettings()
        {
            if (closing) return;
            if (settingsWindow == null || settingsWindow.IsDisposed) {
                settingsWindow=new SettingsWindow(ReadSettingsView,ApplyPreference,RefreshQuota,SettingsCommand,
                    Screen.FromHandle(target==IntPtr.Zero ? Handle : target).WorkingArea);
                ReadAutoStartState();
            }
            if(settingsWindow.WindowState==FormWindowState.Minimized) settingsWindow.WindowState=FormWindowState.Normal;
            settingsWindow.Show();
            // A hidden background launch can suppress the first native ShowWindow
            // call. The managed Visible flag alone is insufficient in that case.
            if(!Native.IsWindowVisible(settingsWindow.Handle)) Native.ShowWindow(settingsWindow.Handle,5);
            settingsWindow.BringToFront(); settingsWindow.Activate();
        }
        async void ReadAutoStartState()
        {
            if(autoStartReading) return; autoStartReading=true; autoStartError=null;
            try { autoStartEnabled=await Task.Run(()=>AutoStart.ReadEnabled()); autoStartKnown=true; }
            catch { autoStartKnown=false; autoStartError="无法读取启动状态，请重新打开设置窗口重试。"; }
            finally { autoStartReading=false; }
            if(!closing && IsHandleCreated) try { BeginInvoke((Action)delegate {
                if(!closing && settingsWindow!=null && !settingsWindow.IsDisposed) settingsWindow.UpdateLive();
            }); } catch(InvalidOperationException) { }
        }
        SettingsView ReadSettingsView()
        {
            var windows=snapshot==null ? new List<QuotaWindow>() : snapshot.Windows(settings.Bucket);
            var preview=String.Join("\n",windows.Select(w=>w.Text+(settings.DisplayDetail>0 && w.Countdown(DateTime.UtcNow)!=null ? "  ·  "+w.Countdown(DateTime.UtcNow) : "")+
                (settings.DisplayDetail==2 && w.ResetText!=null ? "  "+w.ResetText+" 重置" : "")));
            var view=new SettingsView { Theme=settings.Theme,Detail=settings.DisplayDetail,Bucket=settings.Bucket,
                Dark=settings.Theme=="dark" || (settings.Theme=="auto" && codexSurface.GetBrightness()<0.5), Paused=paused,Alerts=settings.LowQuotaAlerts,AutoStart=autoStartEnabled,
                AutoStartKnown=autoStartKnown,AutoStartBusy=autoStartReading,
                AutoStartError=autoStartError,
                Refreshing=refreshing,Stale=Stale,TargetFound=target!=IntPtr.Zero,Status=status,Error=error,RetryUtc=retry.DueUtc,
                Updated=snapshot==null ? (DateTime?)null : snapshot.Updated,Windows=windows,Preview=String.IsNullOrEmpty(preview) ? status : preview };
            if(snapshot!=null) foreach(var pair in snapshot.Buckets) {
                var name=Convert.ToString(Json.Get(pair.Value,"limitName")); view.Buckets[pair.Key]=String.IsNullOrEmpty(name) ? pair.Key : name;
            }
            return view;
        }
        string ApplyPreference(string key, object value)
        {
            if(key=="autostart") {
                try { AutoStart.Set((bool)value); autoStartEnabled=AutoStart.Enabled; autoStartKnown=true;
                    return autoStartEnabled==(bool)value ? null : "自动启动设置未能完成，请稍后重试。";
                } catch { autoStartEnabled=AutoStart.Enabled; autoStartKnown=true; return "自动启动设置失败，请稍后重试。"; }
            }
            if(key=="paused") { paused=(bool)value; Track(); return null; }
            if(key=="theme") { settings.Theme=(string)value; surfaceChecked=DateTime.MinValue; }
            else if(key=="detail") { settings.DisplayDetail=(int)value; layoutKey=null; }
            else if(key=="alerts") settings.LowQuotaAlerts=(bool)value;
            else if(key=="position") { settings.CustomPosition=false; settings.AnchorOffsetX=settings.AnchorOffsetY=0; }
            else if(key=="bucket") { settings.Bucket=(string)value; if(snapshot!=null) status=snapshot.Line(settings.Bucket); layoutKey=null; }
            else return "未知设置";
            bool saved=SaveSettings(); UpdateText(); Track(); Invalidate();
            return saved ? null : "本次设置已生效，但未保存。请检查目录写入权限或磁盘空间。";
        }
        void SettingsCommand(string name)
        {
            if(name=="exit") { Close(); return; }
            string path=name=="backups" ? Path.Combine(Program.Root,"backups") : Path.Combine(Program.Root,"使用说明.md");
            if(name=="backups") Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute=true });
        }
        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode != PowerModes.Resume || closing || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate {
                if (closing) return;
                anchorCache.Clear(); anchorChecked = DateTime.MinValue; retry.Reset();
                surfaceChecked = DateTime.MinValue; lastWindowSearch = DateTime.MinValue;
                ReleaseServer(); Track();
            }); } catch (InvalidOperationException) { }
        }
        async Task RefreshQuota()
        {
            if (refreshing || closing) return; refreshing = true;
            AppServer activeServer = server;
            try {
                if (activeServer == null) {
                    var path = Program.FindCodex(); if (path == null) throw new FileNotFoundException();
                    activeServer = new AppServer(); server = activeServer; await activeServer.Connect(path);
                }
                if (closing || server != activeServer) return;
                var fresh = await activeServer.Read();
                if (closing || server != activeServer) return;
                snapshot = fresh; error = null;
                settings.Bucket = snapshot.Choose(settings.Bucket);
                status = snapshot.Line(settings.Bucket);
                retry.Success(DateTime.UtcNow, Native.GetForegroundWindow() == target);
                var alert = QuotaAlerts.Take(settings, settings.Bucket, snapshot.Windows(settings.Bucket));
                if (alert != null) { SaveSettings(); tray.ShowBalloonTip(5000,"Codex 额度偏低",alert,ToolTipIcon.Warning); }
            } catch (Exception e) {
                if (closing || server != activeServer) return;
                error = QuotaError.Describe(e); retry.Fail(DateTime.UtcNow);
                if (snapshot == null) status = "额度暂不可用";
                ReleaseServer();
                Program.LogLifecycle("quota-read-failed " + e.GetType().Name);
            } finally { refreshing = false; if (!closing) { UpdateText(); Invalidate(); Track(); } }
        }
        bool Stale { get { return snapshot != null && (error != null || DateTime.Now - snapshot.Updated > TimeSpan.FromMinutes(3)); } }
        string Display { get { return status + (Stale ? "  ·  数据待更新" : ""); } }
        void UpdateText()
        {
            var detail = Display;
            if (snapshot != null) {
                detail += "\n" + (settings.Bucket ?? "Codex") + " · 上次读取 " + snapshot.Updated.ToString("HH:mm:ss");
                foreach (var w in snapshot.Windows(settings.Bucket)) if (w.ResetText != null) detail += "\n" + w.Label + "：" + w.Countdown(DateTime.UtcNow) + " · " + w.ResetText + "（北京时间）";
            }
            if (error != null) detail += "\n" + error + "\n下次重试 " + retry.DueUtc.ToLocalTime().ToString("HH:mm:ss");
            tooltip.SetToolTip(this, detail + "\n左键拖动调整位置；右键设置；前台每分钟刷新");
            var text = "Codex · " + Display; tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
        void Track()
        {
            if (Program.StopSignal != null && Program.StopSignal.WaitOne(0)) { Close(); return; }
            if (Program.SettingsSignal != null && Program.SettingsSignal.WaitOne(0)) OpenSettings();
            long minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            if (countdownMinute != minute) { countdownMinute = minute; UpdateText(); Invalidate(); }
            if (Program.DebugState && DateTime.Now - stateWritten > TimeSpan.FromSeconds(1)) {
                stateWritten = DateTime.Now;
                try { File.WriteAllText(Path.Combine(Program.Root, "runtime-state.json"), Json.Encode(new { running = true,
                    pid = Process.GetCurrentProcess().Id, handle = Handle.ToInt64(), target = target.ToInt64(), foreground = Native.GetForegroundWindow().ToInt64(), targetFound = target != IntPtr.Zero, visible = Visible,
                    paused = paused, dragging = dragging, anchorBusy = anchorBusy, anchorResult = anchorReader.LastResult, anchorChecked = anchorChecked.ToString("o"), anchorValidated = anchorCache.ValidatedUtc.ToString("o"),
                    inputLocated = composerAnchor != null && composerAnchor.Valid, detailLevel = detailLevel,
                    slot = new { x = currentSlot.Left, y = currentSlot.Top, width = currentSlot.Width, height = currentSlot.Height },
                    bounds = new { x = Left, y = Top, width = Width, height = Height },
                    display = Display, version = Program.Version, nextRefreshUtc = retry.DueUtc.ToString("o"), trackerMs = tracker.Interval,
                    movementEvents = follower.EventsAvailable, ownerMoving = follower.Moving,
                    theme = settings.Theme, background = ColorTranslator.ToHtml(surface), updatedAt = stateWritten.ToString("s") }), Encoding.UTF8); } catch { }
                var renderKey = Display + ":" + Size + ":" + surface + ":" + countdownMinute + ":" + Visible + ":" + detailLevel;
                if (snapshot != null && renderKey != lastRenderKey) {
                    // Render our own component even while the user is in another app; never steal focus.
                    var previewScale = Visible ? scale : 1.75;
                    var previewBackground = Visible ? surface : Color.FromArgb(43,43,43);
                    var previewWindows = snapshot.Windows(settings.Bucket);
                    int previewWidth = BarDesign.Layout(null, Size.Empty, previewScale, previewBackground, false, previewWindows, status, Stale,detailLevel);
                    var previewSize = new Size(previewWidth, (int)(26*previewScale));
                    try { using (var bitmap = new Bitmap(previewSize.Width, previewSize.Height)) {
                        using(var graphics = Graphics.FromImage(bitmap)) BarDesign.Layout(graphics, previewSize, previewScale, previewBackground, false, previewWindows, status, Stale,detailLevel);
                        bitmap.Save(Path.Combine(Program.Root, "额度条预览.png"), System.Drawing.Imaging.ImageFormat.Png);
                        lastRenderKey = renderKey;
                    }} catch { }
                }
            }
            if (closing || dragging) return;
            var foreground = Native.GetForegroundWindow();
            if (target != foreground && Native.IsCodex(foreground)) SetTarget(foreground);
            if (!Native.IsCodex(target) && DateTime.Now - lastWindowSearch > TimeSpan.FromSeconds(2)) {
                lastWindowSearch = DateTime.Now; SetTarget(Native.Windows().FirstOrDefault());
            }
            if(target==IntPtr.Zero && !refreshing && server!=null) { ReleaseServer(); }
            bool active = target != IntPtr.Zero && foreground == target && !Native.IsIconic(target);
            tracker.Interval = active || dragging ? 250 : target == IntPtr.Zero ? 2000 : 1000;
            if (active && !wasActive && (snapshot == null || DateTime.Now-snapshot.Updated >= TimeSpan.FromSeconds(60))) retry.Reset();
            wasActive = active;
            if (target == IntPtr.Zero || paused || Native.IsIconic(target) || (foreground != target && foreground != Handle)) { if (Visible) Hide(); return; }
            Native.Rect nativeRect; if (!Native.GetWindowRect(target, out nativeRect)) { Hide(); return; }
            var rect = nativeRect.ToRectangle(); if (rect.Width < 250 || rect.Height < 200) { Hide(); return; }
            var dpi = Native.Dpi(target);
            if(lastOwner.Size!=rect.Size || lastDpi!=dpi) {
                anchorCache.Clear(); currentSlot=Rectangle.Empty; anchorChecked=DateTime.MinValue;
                if(Visible) Hide();
            }
            lastOwner=rect; lastDpi=dpi;
            if(!anchorBusy && DateTime.UtcNow-anchorChecked>TimeSpan.FromMilliseconds(composerAnchor == null ? 600 : 1500)) ReadAnchor(target);
            if(!anchorCache.Usable(rect,dpi,DateTime.UtcNow)) { if(Visible) Hide(); return; }
            currentSlot=Rectangle.Intersect(composerAnchor.SlotAt(rect),rect);
            currentSlot=Rectangle.Intersect(currentSlot,Screen.FromHandle(target).WorkingArea);
            if(currentSlot.IsEmpty) { Hide(); return; }
            // Toolbar geometry also responds to the app's zoom, unlike monitor DPI alone.
            scale = Math.Max(0.75,Math.Min(3,composerAnchor.Slot.Height/28.0));
            int height = (int)(26 * scale);
            if (Math.Abs(Font.Size - 12 * scale) > 0.1) { var old = Font; Font = new Font("Microsoft YaHei UI", (float)(12 * scale), FontStyle.Regular, GraphicsUnit.Pixel); old.Dispose(); }
            var quotaWindows = snapshot == null ? new List<QuotaWindow>() : snapshot.Windows(settings.Bucket);
            string measureKey = Display + ":" + scale + ":" + countdownMinute;
            if (layoutKey != measureKey) { layoutKey = measureKey; layoutWidths.Clear(); }
            int width=0, previousDetail=detailLevel;
            for(detailLevel=settings.DisplayDetail;detailLevel>=0;detailLevel--) {
                if (!layoutWidths.TryGetValue(detailLevel, out width)) {
                    width=BarDesign.Layout(null,Size.Empty,scale,surface,false,quotaWindows,status,Stale,detailLevel);
                    layoutWidths[detailLevel] = width;
                }
                if(width<=currentSlot.Width) break;
            }
            if(detailLevel<0) { Hide(); return; }
            var placement=QuotaPlacement.Fit(currentSlot,new Size(width,height),scale,settings.AnchorOffsetX,settings.AnchorOffsetY);
            if(placement.IsEmpty) { Hide(); return; }
            int x=placement.Left,y=placement.Top;
            if(previousDetail!=detailLevel) Invalidate();
            if (Bounds != new Rectangle(x, y, width, height)) {
                Bounds = new Rectangle(x, y, width, height);
                using (var shape = BarDesign.Round(new RectangleF(0,0,width,height), (float)(7*scale))) {
                    var previous = Region; Region = new Region(shape); if (previous != null) previous.Dispose();
                }
            }
            if (DateTime.Now - surfaceChecked > TimeSpan.FromSeconds(2)) {
                surfaceChecked = DateTime.Now;
                Color next = surface;
                if(foreground==target) codexSurface=Native.SurfaceColor(target,Bounds,rect) ?? codexSurface;
                if (settings.Theme == "dark") next = Color.FromArgb(33,33,33);
                else if (settings.Theme == "light") next = Color.FromArgb(249,249,249);
                else next=codexSurface;
                if (next != surface) { surface = next; Invalidate(); }
            }
            if (!Visible) Show();
            Native.PlaceAboveTarget(Handle, target, new Rectangle(x, y, width, height));
            follower.RememberPlacement(rect,dpi,new Rectangle(x,y,width,height));
        }
        void SetTarget(IntPtr window)
        {
            bool changed = target != window;
            target = window; anchorCache.Clear(); currentSlot = Rectangle.Empty; anchorChecked = DateTime.MinValue;
            lastOwner=Rectangle.Empty; lastDpi=0;
            Native.Owner(Handle, window);
            follower.Attach(window);
            if(changed && window!=IntPtr.Zero) { retry.Reset(); var pendingRefresh=RefreshQuota(); }
        }
        async void ReadAnchor(IntPtr window)
        {
            anchorBusy = true; anchorChecked = DateTime.UtcNow;
            try {
                // The accessibility provider can stall; never block the UI or queue more probes.
                var found = await anchorReader.Read(window);
                if (closing || target != window) return;
                Native.Rect wr;
                var previous = composerAnchor;
                if (Native.GetWindowRect(window, out wr) && anchorCache.Update(found, wr.ToRectangle(), Native.Dpi(window), DateTime.UtcNow)) {
                    if(previous==null || previous.Slot!=found.Slot) surfaceChecked=DateTime.MinValue;
                }
            } finally { anchorBusy = false; }
            if(!closing) Track();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            BarDesign.Layout(e.Graphics, Size, scale, surface, hovered, snapshot == null ? new List<QuotaWindow>() : snapshot.Windows(settings.Bucket), status, Stale,Math.Max(0,detailLevel));
        }
        protected override void OwnerWindowClosed()
        {
            if (closing) return;
            target = IntPtr.Zero; anchorCache.Clear(); currentSlot = Rectangle.Empty;
            follower.Attach(IntPtr.Zero);
            lastWindowSearch = DateTime.MinValue; anchorChecked = DateTime.MinValue; wasActive = false;
            ReleaseServer();
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Program.LogLifecycle("watcher-stopped");
            closing = true; tracker.Stop(); refresh.Stop(); tray.Visible = false;
            if(settingsBridge!=null) settingsBridge.Dispose();
            follower.Dispose();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            anchorReader.Dispose(); ReleaseServer(); tracker.Dispose(); refresh.Dispose(); tooltip.Dispose(); tray.Dispose();
            if(settingsWindow!=null && !settingsWindow.IsDisposed) settingsWindow.Close();
            if (Program.DebugState) try { File.WriteAllText(Path.Combine(Program.Root, "runtime-state.json"), Json.Encode(new { running = false }), Encoding.UTF8); } catch { }
            base.OnFormClosed(e);
        }
        void ReleaseServer()
        {
            var previous=server; server=null;
            // Pipe teardown can wait for a pending read; never block the window tracker or exit.
            if(previous!=null) Task.Run(delegate { previous.Dispose(); });
        }
    }

    static class Program
    {
        public const string Version = "1.3.1-local";
        public static string Root = AppDomain.CurrentDomain.BaseDirectory;
        public static EventWaitHandle StopSignal;
        public static EventWaitHandle SettingsSignal;
        public static bool OpenSettingsAtStartup;
        public static bool DebugState;
        public static string FindCodex()
        {
            var config = Path.Combine(Root, "codex-path.txt");
            if (File.Exists(config)) { var path = File.ReadAllText(config).Trim(); if (File.Exists(path)) return path; }
            var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\OpenAI\Codex\bin\codex.exe");
            if (File.Exists(standard)) return standard;
            // A Store-installed Codex bundles its CLI next to Electron resources.
            // Resolve through the running application, without a machine-specific path.
            foreach (var name in new[] { "ChatGPT", "Codex" }) foreach (var running in Process.GetProcessesByName(name)) {
                using (running) try {
                    var app = running.MainModule.FileName;
                    if (name == "ChatGPT" && app.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var bundled = Path.Combine(Path.GetDirectoryName(app), @"resources\codex.exe");
                    if (File.Exists(bundled)) return bundled;
                } catch { }
            }
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
                try { var candidate = Path.Combine(folder.Trim('"'), "codex.exe"); if (File.Exists(candidate)) return candidate; } catch { }
            }
            return null;
        }
        static void Report(string path, object value) { File.WriteAllText(path, Json.Encode(value), Encoding.UTF8); }
        public static void LogLifecycle(string state)
        {
            try {
                var path=Path.Combine(Root,"startup.log");
                var previous=File.Exists(path) ? File.ReadAllLines(path) : new string[0];
                var lines=previous.Skip(Math.Max(0,previous.Length-99)).ToList();
                lines.Add(DateTimeOffset.Now.ToString("o")+" pid="+Process.GetCurrentProcess().Id+" "+state);
                File.WriteAllLines(path,lines,Encoding.UTF8);
            } catch { }
        }
        [STAThread] static int Main(string[] args)
        {
            try { return Run(args); }
            catch(Exception e) { LogLifecycle("fatal "+e.GetType().Name+" code="+e.HResult); return 1; }
        }
        static int Run(string[] args)
        {
            if(args.Contains("--mcp")) return QuotaMcp.Run();
            if(args.Contains("--start-background")) { AutoStart.StartBackground(args.Contains("--debug-state")); return 0; }
            if(args.Contains("--check-running")) { try { SettingsBridge.Call("quota_status",null); return 0; } catch { return 1; } }
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            if(args.Length==2 && args[0]=="--anchor-worker") {
                int parentId=Int32.Parse(args[1]);
                Task.Run(delegate { try { using(var parent=Process.GetProcessById(parentId)) parent.WaitForExit(); } catch { } Environment.Exit(0); });
                using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))
                using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) {
                    output.AutoFlush=true;
                    string request;
                    while((request=input.ReadLine())!=null) {
                        long handle; if(!Int64.TryParse(request,out handle)) continue;
                        var anchor=Task.Run(() => Native.ReadComposer(new IntPtr(handle))).GetAwaiter().GetResult();
                        anchor.Controls.Clear();
                        output.WriteLine(Json.Encode(anchor));
                    }
                }
                return 0;
            }
            if(args.Contains("--enable-autostart") || args.Contains("--disable-autostart")) {
                try { AutoStart.Set(args.Contains("--enable-autostart")); }
                catch(Exception e) { LogLifecycle("autostart-failed "+e.GetType().Name+" code="+e.HResult); if(!args.Contains("--configure-only")) MessageBox.Show("无法设置自动启动，请查看 startup.log。","Codex 额度条"); return 1; }
                if(args.Contains("--disable-autostart") || args.Contains("--configure-only")) return 0;
            }
            if (args.Contains("--quit")) {
                try { using (var signal = EventWaitHandle.OpenExisting("Local\\CodexQuotaBar-v1-stop")) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
            if (args.Contains("--self-test")) { try { SelfTest.Run(); Report(Path.Combine(Root, "test-result.json"), new { passed = true }); return 0; }
                catch (Exception e) { Report(Path.Combine(Root, "test-result.json"), new { passed = false, error = e.Message }); return 1; } }
            if (args.Contains("--diagnose")) return Diagnose().GetAwaiter().GetResult();
            if (args.Contains("--layout-probe")) {
                var windows = Native.Windows(); var window = windows.FirstOrDefault();
                var probe = Task.Run(() => Native.ReadComposer(window));
                if(!probe.Wait(10000)) { Report(Path.Combine(Root,"layout-probe.json"),new { error="timeout" }); return 1; }
                Report(Path.Combine(Root,"layout-probe.json"),probe.Result); return 0;
            }
            if(args.Contains("--show-settings")) {
                try { using(var signal=EventWaitHandle.OpenExisting("Local\\CodexQuotaBar-v1-settings")) { signal.Set(); return 0; } }
                catch(WaitHandleCannotBeOpenedException) { OpenSettingsAtStartup=true; }
            }
            bool created; using (var mutex = new Mutex(true, "Local\\CodexQuotaBar-v1", out created)) {
                if (!created) { LogLifecycle("already-running"); return 0; }
                LogLifecycle(args.Contains("--background") ? "background-start" : "manual-start");
                DebugState = args.Contains("--debug-state") || File.Exists(Path.Combine(Root,"enable-debug.flag"));
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (SettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexQuotaBar-v1-settings"))
                using (StopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\CodexQuotaBar-v1-stop")) {
                    StopSignal.Reset();
                    // The message loop belongs to the watcher, not an owned native window handle.
                    // Closing Codex can destroy that handle without terminating the watcher.
                    using (var context = new ApplicationContext())
                    using (var bar = new Bar()) {
                        bar.FormClosed += delegate { context.ExitThread(); };
                        bar.Show(); Application.Run(context);
                    }
                }
            }
            return 0;
        }
        static async Task<int> Diagnose()
        {
            var windows = Native.Windows(); var composer = Rectangle.Empty;
            if (windows.Count > 0) {
                var anchor = Task.Run(() => Native.Composer(windows[0]));
                if (await Task.WhenAny(anchor, Task.Delay(8000)) == anchor) composer = await anchor;
            }
            try { using (var server = new AppServer()) {
                var executable = FindCodex(); if (executable == null) throw new IOException("未找到 codex.exe");
                await server.Connect(executable); var snapshot = await server.Read(); var id = snapshot.Choose("codex");
                Native.Rect bounds = new Native.Rect(); if (windows.Count > 0) Native.GetWindowRect(windows[0], out bounds);
                Report(Path.Combine(Root, "diagnostics.json"), new { connected = true, windowCount = windows.Count, composerFound = !composer.IsEmpty,
                    composer = new { x = composer.X, y = composer.Y, width = composer.Width, height = composer.Height },
                    window = new { left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom },
                    bucket = id, display = snapshot.Line(id), updatedAt = snapshot.Updated.ToString("s") }); return 0;
            }} catch (Exception e) { Report(Path.Combine(Root, "diagnostics.json"), new { connected = false, windowCount = windows.Count, error = e.GetType().Name }); return 1; }
        }
    }

    static class SelfTest
    {
        static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
        public static void Run()
        {
            var snapshot = QuotaSnapshot.Parse(Json.Decode(@"{""rateLimitsByLimitId"":{""codex_other"":{""primary"":{""usedPercent"":10,""windowDurationMins"":60}},""codex"":{""primary"":{""usedPercent"":21,""windowDurationMins"":10080,""resetsAt"":1791613008},""secondary"":null}}}"));
            Check(snapshot.Choose(null) == "codex", "应优先选择 codex 额度");
            Check(snapshot.Line("codex").StartsWith("周额度剩余 79%"), "已用额度应正确换算为剩余额度");
            Check(snapshot.Windows("codex").Count == 1 && !snapshot.Line("codex").Contains("5小时"), "未返回的窗口不得虚构");
            Check(snapshot.Choose("codex_other") == "codex_other", "保留用户选择的额度类型");
            Check(QuotaWindow.Parse(Json.Decode(@"{""usedPercent"":null}")).Remaining == null, "缺失额度应显示未知");
            Check(QuotaWindow.Parse(Json.Decode(@"{""usedPercent"":120}")).Remaining == 0, "额度不能小于零");
            Check(QuotaWindow.Parse(Json.Decode(@"{""usedPercent"":-20}")).Remaining == 100, "额度不能大于100");
            var dual = QuotaSnapshot.Parse(Json.Decode(@"{""rateLimits"":{""limitId"":""codex"",""primary"":{""usedPercent"":35,""windowDurationMins"":300},""secondary"":{""usedPercent"":60,""windowDurationMins"":10080}}}"));
            Check(dual.Line("codex") == "5小时剩余 65%   ·   周额度剩余 40%", "支持双窗口及旧版格式");
            var empty = QuotaSnapshot.Parse(Json.Decode(@"{""rateLimits"":null}"));
            Check(empty.Choose(null) == null && empty.Line(null).Contains("暂未返回"), "无数据不能显示满额");
            var resetWindow = new QuotaWindow { Reset = 1791613008 };
            var resetUtc = new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(resetWindow.Reset.Value);
            Check(resetWindow.Countdown(resetUtc.AddDays(-2).AddHours(-3)) == "2天3小时后重置", "倒计时显示天和小时");
            Check(resetWindow.Countdown(resetUtc.AddHours(-3).AddMinutes(-12)) == "3小时12分钟后重置", "不足一天显示小时和分钟");
            Check(resetWindow.Countdown(resetUtc.AddSeconds(-20)) == "1分钟后重置", "不足一分钟不显示零分钟");
            Check(resetWindow.Countdown(resetUtc) == "等待额度重置", "已到重置时间不能虚构满额或负数倒计时");
            Check(new QuotaWindow().Countdown(resetUtc) == null, "缺失重置时间不推算倒计时");
            var owner = new Rectangle(2508,48,2544,1620);
            var anchor = new ComposerAnchor { Owner=owner, Dpi=168, Editor=new Rectangle(3160,1476,1247,77), Slot=new Rectangle(3375,1560,698,49) };
            var original=QuotaPlacement.Fit(anchor.Slot,new Size(600,45),1.75,0,0);
            Check(original.Left==3375 && original.Top==1562 && anchor.Slot.Contains(original), "贴合权限与模型按钮之间的工具栏空白");
            var movedOwner=new Rectangle(owner.X-700,owner.Y+50,owner.Width,owner.Height);
            var moved=QuotaPlacement.Fit(anchor.SlotAt(movedOwner),new Size(600,45),1.75,0,0);
            Check(moved.Left==original.Left-700 && moved.Top==original.Top+50, "移动窗口时保持输入框相对位置");
            Check(anchor.Matches(movedOwner,168), "仅移动位置时缓存仍有效");
            Check(!anchor.Matches(new Rectangle(owner.X,owner.Y,2100,1280),168), "窗口缩小时拒绝旧坐标");
            Check(!anchor.Matches(owner,144), "切换屏幕缩放时拒绝旧坐标");
            var narrowSlot=new Rectangle(880,1110,360,42);
            var clamped=QuotaPlacement.Fit(narrowSlot,new Size(250,39),1.5,-500,800);
            Check(narrowSlot.Contains(clamped), "拖动偏移不能越过工具栏边界");
            Check(QuotaPlacement.Fit(narrowSlot,new Size(600,39),1.5,0,0).IsEmpty, "过宽时不能覆盖相邻按钮");
            int fullWidth=BarDesign.Layout(null,Size.Empty,1.75,Color.FromArgb(43,43,43),false,snapshot.Windows("codex"),"",false,2);
            int shortWidth=BarDesign.Layout(null,Size.Empty,1.75,Color.FromArgb(43,43,43),false,snapshot.Windows("codex"),"",false,0);
            Check(fullWidth>narrowSlot.Width && shortWidth<narrowSlot.Width, "窄窗口可降为仅显示额度的紧凑模式");
        }
    }
}
