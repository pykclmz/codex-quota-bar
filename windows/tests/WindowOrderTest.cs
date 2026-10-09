using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Exercises the installed window-order implementation with disposable off-screen windows.
// It never types into Codex, opens an IME, or changes the user's foreground window.
static class WindowOrderTest
{
    delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern void NotifyWinEvent(uint eventType,IntPtr window,int objectId,int childId);
    static MethodInfo place;
    static bool baseline;
    static readonly Rectangle bounds = new Rectangle(-20000, -20000, 240, 28);
    static readonly List<string> checks = new List<string>();

    sealed class TestWindow : CodexQuotaBar.OverlayWindow
    {
        public TestWindow()
        {
            ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; Bounds = bounds;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; }
        }
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks.Add(message);
    }
    static bool Topmost(IntPtr window) { return (GetWindowLong(window, -20) & 8) != 0; }
    static void Place(IntPtr bar, IntPtr target)
    {
        if (baseline) SetWindowPos(bar, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
        else place.Invoke(null, new object[] { bar, target, bounds });
        Application.DoEvents();
    }
    static bool Above(IntPtr first, IntPtr second)
    {
        var windows = new List<IntPtr>();
        EnumWindows(delegate(IntPtr window, IntPtr unused) { windows.Add(window); return true; }, IntPtr.Zero);
        int a = windows.IndexOf(first), b = windows.IndexOf(second);
        return a >= 0 && b >= 0 && a < b;
    }
    static void CandidateCase(bool topmost)
    {
        var foreground = GetForegroundWindow();
        using (var target = new TestWindow())
        using (var bar = new TestWindow())
        using (var candidate = new TestWindow())
        {
            target.Show(); bar.Show(); candidate.Show(); Application.DoEvents();
            SetWindowPos(target.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0013);
            Place(bar.Handle, target.Handle);
            // A non-topmost popup may initially appear between the overlay and its target.
            var after = topmost ? new IntPtr(-1) : GetWindow(target.Handle, 3);
            SetWindowPos(candidate.Handle, after, 0, 0, 0, 0, 0x0013);
            for (int tick = 0; tick < 16; tick++) Place(bar.Handle, target.Handle);
            string kind = topmost ? "topmost" : "normal";
            Check(Above(candidate.Handle, bar.Handle), kind + " candidate stays above bar across repeated updates");
            Check(Above(bar.Handle, target.Handle), kind + " case keeps bar above target");
            Check(!Topmost(bar.Handle), kind + " case keeps bar outside topmost band");
            Check(IsWindowVisible(bar.Handle), kind + " case keeps bar visible");
            Check(GetForegroundWindow() == foreground, kind + " case preserves foreground focus");
            for (int cycle=0; cycle<8; cycle++) {
                SetWindowPos(target.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0013);
                Application.DoEvents();
                Place(bar.Handle,target.Handle);
                Check(Above(bar.Handle,target.Handle), kind + " case survives target activation cycle " + cycle);
            }
            target.Close(); Application.DoEvents();
            Check(!bar.IsDisposed && !bar.IsHandleCreated && !bar.Visible, kind + " case preserves watcher after owner closes");
            using (var reopened = new TestWindow()) {
                reopened.Show(); bar.Show(); Place(bar.Handle,reopened.Handle);
                Check(IsWindowVisible(bar.Handle) && Above(bar.Handle,reopened.Handle), kind + " case recovers after target reopens");
            }
        }
    }
    static void DemotionCase()
    {
        using (var target = new TestWindow())
        using (var bar = new TestWindow())
        {
            target.Show(); bar.Show(); Application.DoEvents();
            SetWindowPos(bar.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
            Place(bar.Handle, target.Handle);
            Check(!Topmost(bar.Handle), "previously topmost bar is demoted");
            Check(Above(bar.Handle, target.Handle), "demoted bar stays above target");
        }
    }
    static void WatcherLoopCase()
    {
        using (var context = new ApplicationContext())
        using (var target = new TestWindow())
        using (var reopened = new TestWindow())
        using (var bar = new TestWindow())
        using (var timer = new Timer { Interval = 100 }) {
            int step = 0;
            Exception failure = null;
            bar.FormClosed += delegate { context.ExitThread(); };
            target.Show(); bar.Show(); Place(bar.Handle,target.Handle);
            timer.Tick += delegate {
                try {
                    if (step == 0) { target.Close(); step++; return; }
                    Check(Application.MessageLoop && !bar.IsDisposed && !bar.IsHandleCreated, "watcher message loop survives owner handle destruction");
                    reopened.Show(); bar.Show(); Place(bar.Handle,reopened.Handle);
                    Check(IsWindowVisible(bar.Handle) && Above(bar.Handle,reopened.Handle), "watcher loop recreates overlay for reopened target");
                    step++; timer.Stop(); context.ExitThread();
                } catch(Exception error) { failure=error; timer.Stop(); context.ExitThread(); }
            };
            timer.Start(); Application.Run(context);
            if (failure != null) throw failure;
            Check(step == 2, "owner close does not terminate watcher early");
        }
    }
    static Rectangle WindowBounds(IntPtr window)
    {
        CodexQuotaBar.Native.Rect rect;
        if(!CodexQuotaBar.Native.GetWindowRect(window,out rect)) throw new Exception("Missing test window");
        return rect.ToRectangle();
    }
    static bool PumpUntil(Func<bool> ready,int milliseconds)
    {
        var watch=Stopwatch.StartNew();
        do { Application.DoEvents(); if(ready()) return true; System.Threading.Thread.Sleep(1); } while(watch.ElapsedMilliseconds<milliseconds);
        return ready();
    }
    static void MovementCase()
    {
        var foreground=GetForegroundWindow(); bool enabled=true; int invalidated=0;
        using(var owner=new TestWindow())
        using(var replacement=new TestWindow())
        using(var bar=new TestWindow())
        using(var candidate=new TestWindow())
        using(var follower=new CodexQuotaBar.WindowFollower(bar,()=>enabled,()=>invalidated++)) {
            owner.Bounds=new Rectangle(-19000,-19000,800,500); bar.Bounds=new Rectangle(-18800,-18600,240,28);
            owner.Show(); bar.Show(); candidate.Show(); Application.DoEvents();
            CodexQuotaBar.Native.PlaceAboveTarget(bar.Handle,owner.Handle,bar.Bounds);
            SetWindowPos(candidate.Handle,new IntPtr(-1),0,0,0,0,0x0013);
            follower.Attach(owner.Handle);
            follower.RememberPlacement(WindowBounds(owner.Handle),CodexQuotaBar.Native.Dpi(owner.Handle),WindowBounds(bar.Handle));
            Check(follower.EventsAvailable,"movement hook is registered");
            Check(!follower.Moving,"no frame timer while idle");
            var offset=new Point(WindowBounds(bar.Handle).Left-WindowBounds(owner.Handle).Left,WindowBounds(bar.Handle).Top-WindowBounds(owner.Handle).Top);
            for(int step=0;step<30;step++) {
                owner.Location=new Point(owner.Left+11,owner.Top+(step%2==0 ? 5 : -3));
                var target=WindowBounds(owner.Handle);
                Check(PumpUntil(()=>WindowBounds(bar.Handle).Location==new Point(target.Left+offset.X,target.Top+offset.Y),120),"event translation maintains input offset step "+step);
            }
            Check(Above(candidate.Handle,bar.Handle) && !Topmost(bar.Handle),"movement preserves input candidate priority");
            Check(GetForegroundWindow()==foreground,"movement preserves foreground focus");
            NotifyWinEvent(0x000A,owner.Handle,0,0);
            Check(PumpUntil(()=>follower.Moving,120),"frame fallback enabled only during move/resize");
            NotifyWinEvent(0x000B,owner.Handle,0,0);
            Check(PumpUntil(()=>!follower.Moving,120),"frame fallback stops at move end");
            var last=WindowBounds(bar.Handle); enabled=false; owner.Left+=20;
            PumpUntil(()=>false,40); Check(WindowBounds(bar.Handle)==last,"paused/hidden overlay is not moved"); enabled=true;
            owner.Width+=80; Check(PumpUntil(()=>invalidated>1,120),"resize invalidates old input geometry");
            Check(WindowBounds(bar.Handle)==last,"resize never reuses stale relative position");
            replacement.Bounds=new Rectangle(-17000,-17000,800,500); replacement.Show(); Application.DoEvents();
            follower.Attach(replacement.Handle);
            var replacementBounds=WindowBounds(replacement.Handle);
            var newBarBounds=new Rectangle(replacementBounds.Left+200,replacementBounds.Top+400,240,28);
            CodexQuotaBar.Native.PlaceAboveTarget(bar.Handle,replacement.Handle,newBarBounds);
            follower.RememberPlacement(replacementBounds,CodexQuotaBar.Native.Dpi(replacement.Handle),newBarBounds);
            owner.Left+=25; PumpUntil(()=>false,40);
            Check(WindowBounds(bar.Handle)==newBarBounds,"old window events ignored after target switch");
            replacement.Left+=17;
            Check(PumpUntil(()=>WindowBounds(bar.Handle).Left==newBarBounds.Left+17,120),"new target resumes immediate movement");
            follower.Attach(IntPtr.Zero); last=WindowBounds(bar.Handle); replacement.Left+=22; PumpUntil(()=>false,40);
            Check(!follower.EventsAvailable && !follower.Moving && WindowBounds(bar.Handle)==last,"detach stops event handling and timer");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "window-order-result.json");
        try
        {
            baseline = args.Length > 1 && args[1] == "--baseline";
            if (!baseline)
            {
                var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
                place = assembly.GetType("CodexQuotaBar.Native", true).GetMethod("PlaceAboveTarget", BindingFlags.Public | BindingFlags.Static);
                if (place == null) throw new Exception("Missing production window-order method");
            }
            CandidateCase(true); CandidateCase(false); DemotionCase(); WatcherLoopCase();
            if(!baseline) MovementCase();
            File.WriteAllText(report, new JavaScriptSerializer().Serialize(new { passed = true, checks = checks }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, new JavaScriptSerializer().Serialize(new { passed = false, checks = checks, error = error.Message }));
            return 1;
        }
    }
}
