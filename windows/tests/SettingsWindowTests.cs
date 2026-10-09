using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexQuotaBar
{
    static class SettingsWindowTests
    {
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
        static int checks;
        static void Check(bool condition,string description) { if(!condition) throw new Exception(description); checks++; }
        static IEnumerable<Control> All(Control parent) { foreach(Control child in parent.Controls) { yield return child; foreach(var nested in All(child)) yield return nested; } }
        static T Find<T>(Control form,string name) where T:Control { return All(form).OfType<T>().First(c=>c.Name==name); }
        static void Click(Control form,string name) { Find<Button>(form,name).PerformClick(); Application.DoEvents(); }
        static bool PumpUntil(Func<bool> ready,int timeout)
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();
            do { Application.DoEvents(); if(ready()) return true; System.Threading.Thread.Sleep(1); } while(watch.ElapsedMilliseconds<timeout);
            return ready();
        }
        static void Capture(Form form,string name)
        {
            using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size)); bitmap.Save(Path.Combine(Program.Root,name+".png")); }
        }
        static void LayoutCheck(Form form)
        {
            foreach(var card in All(form).OfType<RoundedCard>()) foreach(Control child in card.Controls) {
                Check(child.Left>=0 && child.Top>=0 && child.Right<=card.Width+1 && child.Bottom<=card.Height+1,"Control outside card: "+child.Name+" / "+child.Text);
                foreach(Control other in card.Controls) if(child!=other && child.Bounds.IntersectsWith(other.Bounds)) Check(false,"Overlapping card content: "+child.Text+" / "+other.Text);
            }
            foreach(var label in All(form).OfType<Label>()) if(label.Text.Length>0) Check(label.GetPreferredSize(new Size(label.Width,0)).Height<=label.Height,"Clipped text: "+label.Text);
            foreach(var row in All(form).OfType<ActionRow>()) foreach(Control button in row.Controls) Check(row.ClientRectangle.Contains(button.Bounds),"Wrapped action must fit its row");
            foreach(var tile in All(form).OfType<QuotaTile>()) Check(tile.GetPreferredSize(new Size(tile.Width,0)).Height<=tile.Height,"Quota reset text must fit without ellipsis");
            var body=All(form).OfType<FlowLayoutPanel>().First();
            foreach(var card in All(form).OfType<RoundedCard>()) Check(card.Width+body.Padding.Horizontal<=body.ClientSize.Width+1,"Card must fit viewport width: "+card.Width+" + "+body.Padding.Horizontal+" vs "+body.ClientSize.Width+" scroll="+body.VerticalScroll.Visible+" heading="+card.Heading.Text);
        }
        static void AsyncCases()
        {
            var view=new SettingsView { Theme="dark",Dark=true,TargetFound=true,Updated=DateTime.Now,Preview="额度预览" };
            var pending=new TaskCompletionSource<int>(); int requests=0,startups=0;
            using(var form=new SettingsWindow(()=>view,delegate(string key,object value) { if(key=="autostart") { startups++; System.Threading.Thread.Sleep(150); view.AutoStart=(bool)value; } return null; },()=>{ requests++; return pending.Task; },name=>{},Screen.PrimaryScreen.WorkingArea)) {
                form.Location=new Point(-20000,-20000); ShowWindow(form.Handle,4); Application.DoEvents();
                Click(form,"立即刷新"); Check(!Find<Button>(form,"立即刷新").Enabled && requests==1,"Pending manual refresh is disabled immediately");
                Click(form,"外观显示"); view.Dark=false; view.Theme="light"; form.UpdateLive();
                Click(form,"额度概览"); Check(!Find<Button>(form,"立即刷新").Enabled,"Pending refresh survives navigation and theme rebuild");
                Click(form,"立即刷新"); Check(requests==1,"Duplicate manual refresh prevented");
                pending.SetResult(0); Check(PumpUntil(()=>Find<Button>(form,"立即刷新").Enabled,2000),"Refresh completes after navigation");
                Check(All(form).OfType<Label>().Any(c=>c.Text=="额度已更新"),"Successful manual refresh gets feedback");
                Click(form,"运行设置"); Find<ToggleSwitch>(form,"AutoStart").PerformClick();
                Check(!Find<ToggleSwitch>(form,"AutoStart").Enabled,"Startup write disables its switch while pending");
                Click(form,"额度提醒"); Click(form,"运行设置");
                Check(!Find<ToggleSwitch>(form,"AutoStart").Enabled,"Pending startup write survives navigation");
                Check(PumpUntil(()=>Find<ToggleSwitch>(form,"AutoStart").Enabled,2000),"Startup write finishes without blocking navigation");
                Check(startups==1 && view.AutoStart && Find<ToggleSwitch>(form,"AutoStart").Checked,"Startup result is synchronized");
                view.Paused=true; form.UpdateLive(); Check(Find<ToggleSwitch>(form,"Paused").Checked,"External pause state synchronizes without clicks");
                view.AutoStartKnown=false; view.AutoStartBusy=true; form.UpdateLive();
                Check(!Find<ToggleSwitch>(form,"AutoStart").Enabled && All(form).OfType<Label>().Any(c=>c.Text=="正在读取启动状态…"),"Unknown startup state must not pretend to be disabled");
                view.AutoStartKnown=true; view.AutoStartBusy=false; form.UpdateLive();
                view.AutoStartError="无法读取启动状态，请重新打开设置窗口重试。"; form.UpdateLive();
                Check(!Find<ToggleSwitch>(form,"AutoStart").Enabled && All(form).OfType<Label>().Any(c=>c.Text==view.AutoStartError),"Startup read failure is explicit and cannot be edited as a false state");
                view.AutoStartError=null; form.UpdateLive();
                LayoutCheck(form);
                form.Close();
            }
            var late=new TaskCompletionSource<int>();
            using(var form=new SettingsWindow(()=>view,(key,value)=>null,()=>late.Task,name=>{},Screen.PrimaryScreen.WorkingArea)) {
                form.Location=new Point(-20000,-20000); ShowWindow(form.Handle,4); Application.DoEvents();
                Click(form,"立即刷新"); form.Close(); late.SetException(new IOException("test failure after close"));
                PumpUntil(()=>false,80); Check(form.IsDisposed,"Late refresh failure is safe after window closes");
            }
        }
        static void ResponsiveCases(SettingsWindow form,SettingsView view)
        {
            view.Error="网络暂时无法连接，请检查网络或代理设置。额度条会保留上次成功读取的数据并自动重试，不会把读取失败显示为满额。";
            view.Stale=true; view.Preview="5小时剩余 9%  ·  3小时45分钟后重置  ·  10/09 18:30 重置\n周额度剩余 88%  ·  5天23小时后重置  ·  10/15 11:22 重置";
            view.Buckets["review"]=new string('审',60)+"额度类型";
            var dpi=typeof(SettingsWindow).GetMethod("ApplyDpi",BindingFlags.NonPublic|BindingFlags.Instance);
            foreach(float scale in new[]{1F,1.25F,1.5F,1.75F,2F,2.5F,3F}) {
                dpi.Invoke(form,new object[]{scale}); form.Size=form.MinimumSize; Application.DoEvents();
                foreach(var page in new[]{"额度概览","外观显示","额度提醒","运行设置"}) {
                    Click(form,page); LayoutCheck(form);
                    if(scale==1.75F) Capture(form,"settings-responsive-"+(page=="额度概览" ? "overview" : page=="外观显示" ? "display" : page=="额度提醒" ? "alerts" : "runtime"));
                }
                var scroll=All(form).OfType<FlowLayoutPanel>().First(); var last=Find<Button>(form,"退出额度条");
                scroll.ScrollControlIntoView(last); Application.DoEvents();
                Check(scroll.RectangleToScreen(scroll.ClientRectangle).Contains(last.RectangleToScreen(last.ClientRectangle)),"Last action remains reachable by scrolling at DPI "+scale);
            }
            view.Error=null; view.Stale=false;
        }
        [STAThread] static int Main(string[] args)
        {
            string result=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings-window-result.json");
            try {
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Program.Root=AppDomain.CurrentDomain.BaseDirectory;
                var view=new SettingsView { Theme="dark", Dark=true,Detail=2,Bucket="codex",AutoStart=true,TargetFound=true,
                    Updated=DateTime.Now, Preview="周额度剩余 88%  ·  6天后重置", Buckets=new Dictionary<string,string> { {"codex","Codex"},{"review","代码审查"} },
                    Windows=new List<QuotaWindow> { new QuotaWindow { Remaining=88,Minutes=10080,Reset=(DateTime.UtcNow.AddDays(6)-new DateTime(1970,1,1)).TotalSeconds } } };
                var changes=new List<string>(); int requests=0; string action=null; bool reject=false;
                using(var form=new SettingsWindow(()=>view,delegate(string key,object value) {
                    changes.Add(key); if(reject) return "测试保存失败";
                    if(key=="theme") { view.Theme=(string)value; view.Dark=view.Theme!="light"; }
                    if(key=="detail") view.Detail=(int)value;
                    if(key=="alerts") view.Alerts=(bool)value;
                    if(key=="paused") view.Paused=(bool)value;
                    if(key=="autostart") view.AutoStart=(bool)value;
                    if(key=="bucket") view.Bucket=(string)value;
                    return null;
                },delegate { requests++; return Task.FromResult(0); },name=>action=name,Screen.PrimaryScreen.WorkingArea)) {
                    form.Location=new Point(-20000,-20000); ShowWindow(form.Handle,4); Application.DoEvents();
                    Check(!form.TopMost,"Settings must remain a normal window");
                    Check(form.ShowInTaskbar,"Independent page must appear in taskbar");
                    Check(Find<Label>(form,"Brand").Text=="Codex","Brand shows Codex rather than path-like glyphs");
                    LayoutCheck(form); Capture(form,"settings-overview-dark");
                    Check(Find<ComboBox>(form,"额度类型").Items.Count==2,"All returned quota buckets available");
                    Find<ComboBox>(form,"额度类型").SelectedIndex=1;
                    Check(view.Bucket=="review","Changing bucket applies immediately");
                    Click(form,"立即刷新"); Check(requests==1,"Manual refresh delivered once");
                    view.Error="网络暂时不可用"; view.Stale=true; form.UpdateLive();
                    Check(All(form).OfType<Label>().Any(c=>c.Text==view.Error),"Read errors displayed");
                    view.Error=null; view.Stale=false;
                    view.Windows.Add(new QuotaWindow { Remaining=9,Minutes=300 }); form.UpdateLive();
                    Check(All(form).OfType<QuotaTile>().Count()==2,"Dual quota windows become visible"); LayoutCheck(form);
                    Click(form,"外观显示"); LayoutCheck(form); Capture(form,"settings-display-dark");
                    Find<ComboBox>(form,"显示内容").SelectedIndex=0; Check(view.Detail==0,"Display detail updates");
                    Click(form,"恢复默认位置"); Check(changes.Contains("position"),"Position reset delivered");
                    Find<ComboBox>(form,"主题").SelectedIndex=2; Check(!view.Dark,"Light theme applied");
                    Check(form.BackColor.GetBrightness()>0.8,"Light theme repaints the whole page");
                    LayoutCheck(form); Capture(form,"settings-display-light");
                    Click(form,"额度提醒"); LayoutCheck(form);
                    Find<ToggleSwitch>(form,"LowQuotaAlerts").PerformClick(); Check(view.Alerts,"Alert toggle applies");
                    Click(form,"运行设置"); LayoutCheck(form);
                    Find<ToggleSwitch>(form,"Paused").PerformClick(); Check(view.Paused,"Pause is exposed");
                    reject=true; Find<ToggleSwitch>(form,"AutoStart").PerformClick();
                    Check(PumpUntil(()=>Find<ToggleSwitch>(form,"AutoStart").Enabled,2000),"Startup save completes asynchronously");
                    Check(Find<ToggleSwitch>(form,"AutoStart").Checked,"Failed change restores persisted state");
                    Check(All(form).OfType<Label>().Any(c=>c.Text=="测试保存失败"),"Failure feedback remains visible"); reject=false;
                    Click(form,"打开使用说明"); Check(action=="docs","Help action");
                    Click(form,"打开备份目录"); Check(action=="backups","Backup action");
                    form.Size=form.MinimumSize; Application.DoEvents(); LayoutCheck(form); Capture(form,"settings-runtime-small");
                    Click(form,"额度概览"); LayoutCheck(form);
                    for(int i=0;i<20;i++) { Click(form,"外观显示"); Click(form,"运行设置"); }
                    Capture(form,"settings-runtime-light");
                    ResponsiveCases(form,view);
                    Click(form,"退出额度条"); Check(action=="exit","Exit action is delivered separately from closing settings"); action="backups";
                    form.Close(); Check(action=="backups","Closing settings does not exit utility");
                }
                AsyncCases();
                // Exercise the actual overlay controller and on-disk settings, without
                // touching the installed copy, account data, or scheduled startup task.
                string previousRoot=Program.Root;
                string staging=Path.GetFullPath(Path.Combine(previousRoot,"settings-test-"+Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(staging);
                try {
                    Program.Root=staging;
                    using(var bar=new Bar()) {
                        // Keep window tracking and account requests out of this controller test.
                        typeof(Bar).GetField("closing",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(bar,true);
                        var unusedHandle=bar.Handle;
                        var apply=typeof(Bar).GetMethod("ApplyPreference",BindingFlags.NonPublic|BindingFlags.Instance);
                        foreach(var change in new[]{new object[]{"theme","light"},new object[]{"detail",1},new object[]{"alerts",true},new object[]{"bucket","review"}})
                            Check(apply.Invoke(bar,change)==null,"Controller saved "+change[0]);
                        var loaded=Settings.Load(); Check(loaded.Theme=="light" && loaded.DisplayDetail==1 && loaded.LowQuotaAlerts && loaded.Bucket=="review","Controller changes survive reload");
                        Check(apply.Invoke(bar,new object[]{"position",null})==null && !Settings.Load().CustomPosition,"Controller reset persists");
                        Check(apply.Invoke(bar,new object[]{"paused",true})==null,"Controller pause can be applied without a target");
                        var reader=typeof(Bar).GetMethod("ReadSettingsView",BindingFlags.NonPublic|BindingFlags.Instance);
                        Check(((SettingsView)reader.Invoke(bar,null)).Paused,"Page receives paused state");
                        Check(apply.Invoke(bar,new object[]{"theme","auto"})==null,"Automatic theme can be selected");
                        typeof(Bar).GetField("codexSurface",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(bar,Color.White);
                        typeof(Bar).GetField("surface",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(bar,Color.Black);
                        Check(!((SettingsView)reader.Invoke(bar,null)).Dark,"Automatic page theme follows Codex rather than a previously forced overlay theme");
                        bar.Close();
                    }
                } finally {
                    Program.Root=previousRoot;
                    if(staging.StartsWith(Path.GetFullPath(previousRoot),StringComparison.OrdinalIgnoreCase)) Directory.Delete(staging,true);
                }
                File.WriteAllText(result,Json.Encode(new { passed=true,checks=checks })); return 0;
            } catch(Exception e) { File.WriteAllText(result,Json.Encode(new { passed=false,checks=checks,error=e.ToString() })); return 1; }
        }
    }
}
