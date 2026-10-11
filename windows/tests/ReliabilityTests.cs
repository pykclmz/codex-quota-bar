using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodexQuotaBar
{
    static class ReliabilityTests
    {
        static readonly List<string> checks = new List<string>();
        static void Check(bool value, string message) { if (!value) throw new Exception(message); checks.Add(message); }
        static void Anchors()
        {
            var now = DateTime.UtcNow;
            var owner = new Rectangle(0,0,1600,1000);
            var good = new ComposerAnchor { Owner=owner, Dpi=144, Editor=new Rectangle(400,800,1000,80), Slot=new Rectangle(600,900,500,40) };
            var cache = new AnchorCache();
            Check(cache.Update(good,owner,144,now), "accept initial valid anchor");
            var invalid = new ComposerAnchor { Owner=owner, Dpi=144 };
            Check(!cache.Update(invalid,owner,144,now.AddSeconds(1)) && cache.Current==good, "transient empty result preserves last good anchor");
            Check(!cache.Update(null,owner,144,now.AddSeconds(3)) && cache.Usable(owner,144,now.AddSeconds(5)), "timeout keeps anchor inside grace period");
            Check(!cache.Usable(owner,144,now.AddSeconds(7)), "repeated failure expires cached anchor");
            Check(!cache.Usable(owner,144,now.AddHours(2)), "sleep interval expires anchor");
            Check(!cache.Usable(owner,192,now), "DPI change rejects old geometry");
            Check(!cache.Usable(new Rectangle(0,0,900,700),144,now), "resize rejects old geometry");
            var moved = new Rectangle(-1600,100,1600,1000);
            Check(cache.Usable(moved,144,now) && cache.Current.SlotAt(moved).Left==-1000, "move to negative-coordinate display retains relative placement");
            Check(!cache.Update(good,moved,144,now.AddSeconds(1)) && cache.ValidatedUtc==now,"late result captured at an old window position is rejected after drag");
            cache.BeginTranslation(now.AddSeconds(1));
            Check(cache.Usable(moved,144,now.AddSeconds(20)),"long same-DPI drag does not expire a validated relative anchor");
            Check(!cache.Usable(moved,192,now.AddSeconds(20)),"cross-DPI move cannot reuse old toolbar geometry even during drag");
            cache.EndTranslation(now.AddSeconds(20));
            Check(cache.Usable(moved,144,now.AddSeconds(21)) && !cache.Usable(moved,144,now.AddSeconds(23)),"move end grants only bounded grace for fresh input validation");
            cache.BeginTranslation(now.AddSeconds(30));
            Check(!cache.Usable(moved,144,now.AddSeconds(31)),"starting a drag cannot revive an already expired anchor");
            var inconsistent=new ComposerAnchor { Owner=owner,Dpi=144,Editor=good.Editor,Slot=new Rectangle(-3000,900,500,40) };
            Check(!cache.Update(inconsistent,owner,144,now.AddSeconds(31)),"mixed coordinate frames outside the owner are rejected");
            cache.Clear(); Check(!cache.Usable(owner,144,now), "resume invalidates cached geometry");
            var responsive=new ComposerAnchor { Owner=new Rectangle(0,0,2560,1368),Dpi=144,
                Editor=new Rectangle(929,1196,1176,74),Slot=new Rectangle(1130,1275,664,47) };
            cache.Update(responsive,responsive.Owner,144,now); cache.BeginTranslation(now);
            var resized=new Rectangle(-1800,80,1800,1100);
            var projected=cache.ForFrame(resized,144,now.AddSeconds(20),true);
            Check(projected!=null && projected.Valid && projected.Slot.Bottom==resized.Bottom-46,"continuous resize retains the validated toolbar bottom inset without hiding");
            Check(projected.Slot.Left==responsive.Slot.Left+resized.Left+(resized.Width-responsive.Owner.Width)/2,"centered composer follows width changes while retaining sidebar inset");
            Check(cache.Current==responsive && cache.ValidatedUtc==now,"projection never extends validation or overwrites the real anchor");
            var dpiOwner=new Rectangle(0,0,1707,912);
            var dpiFrame=cache.ForFrame(dpiOwner,96,now.AddSeconds(20),true);
            Check(dpiFrame!=null && dpiFrame.Valid && dpiFrame.Slot.Height==31 && dpiFrame.Slot.Bottom==dpiOwner.Bottom-31,"cross-DPI projection uses the new physical scale and bottom offset");
            Check(cache.ForFrame(resized,144,now.AddSeconds(20),false)==null,"temporary estimates cannot be used outside a layout transition");
            cache.EndTranslation(now.AddSeconds(20));
            Check(cache.ForFrame(resized,144,now.AddSeconds(21),false)!=null,"release after a long resize retains projection until the fresh toolbar arrives");
            Check(cache.ForFrame(resized,144,now.AddSeconds(23),true)==null,"failed validation cannot retain projected geometry indefinitely");
            var fresh=new ComposerAnchor { Owner=resized,Dpi=144,Editor=projected.Editor,Slot=projected.Slot,Mode="permission-toolbar" };
            Check(cache.Update(fresh,resized,144,now.AddSeconds(23)) && cache.ForFrame(resized,144,now.AddSeconds(24),false)==fresh,"fresh controls replace the temporary estimate after resizing");
            Check(responsive.Project(new Rectangle(0,0,300,200),144)==null,"projection refuses space too small for the composer and toolbar");
            var withPane=new ComposerAnchor { Owner=responsive.Owner,Dpi=144,Editor=responsive.Editor,Slot=responsive.Slot,Viewport=new Rectangle(474,0,2086,1368) };
            Check(withPane.Project(resized,144).Slot==projected.Slot,"explicit content-pane geometry matches the centered layout projection");
            var narrowOwner=new Rectangle(0,0,1300,1000);
            var narrow=responsive.Project(narrowOwner,144);
            Check(narrow!=null && cache.Update(narrow,narrowOwner,144,now.AddSeconds(25)),"validate a smaller composer during continuous resize");
            Check(cache.Current.Project(responsive.Owner,144).Editor.Width==responsive.Editor.Width,"expanding restores the observed composer width rather than retaining the last narrow width");
            var staleChildren=new ComposerAnchor { Owner=responsive.Owner,Dpi=144,Editor=narrow.Editor,Slot=narrow.Slot };
            Check(staleChildren.Valid && !cache.Update(staleChildren,responsive.Owner,144,now.AddSeconds(26)),"maximize rejects old child coordinates even when the native owner frame is current");
            var changedFooter=new ComposerAnchor { Owner=new Rectangle(0,0,1300,1200),Dpi=144,
                Editor=new Rectangle(525,1010,720,80),Slot=new Rectangle(715,1100,270,47) };
            Check(cache.Update(changedFooter,changedFooter.Owner,144,now.AddSeconds(26)),"a genuinely changed footer layout is not mistaken for old child coordinates");
            var toolbar=new ComposerAnchor { Owner=narrowOwner,Dpi=144,Toolbar=new Rectangle(500,908,720,47),Slot=new Rectangle(715,908,270,47),Mode="permission-toolbar-only" };
            Check(toolbar.Valid && toolbar.Editor.IsEmpty,"fresh toolbar buttons remain a valid anchor during edit-node replacement");
            Check(cache.Update(toolbar,narrowOwner,144,now.AddSeconds(26)) && cache.Usable(narrowOwner,144,now.AddSeconds(31)),"toolbar confirmation renews validation without requiring an edit node");
            Check(toolbar.Project(new Rectangle(-1500,40,1500,1200),144)!=null,"toolbar-only confirmation also supports live resize projection");
            toolbar.Toolbar=new Rectangle(-5000,908,720,47);
            Check(!toolbar.Valid,"toolbar-only fallback cannot validate controls outside the owner");
            var displays=new[] { new Rectangle(0,0,1920,1040),new Rectangle(1920,0,1920,1040),new Rectangle(-1600,100,1600,900) };
            var otherScreenSlot=new Rectangle(2100,900,600,40);
            Check(QuotaPlacement.ClipToDisplays(otherScreenSlot,displays)==otherScreenSlot,"composer on another monitor remains visible even when owner is on primary monitor");
            var negativeSlot=new Rectangle(-1400,900,500,40);
            Check(QuotaPlacement.ClipToDisplays(negativeSlot,displays)==negativeSlot,"negative monitor origin preserves physical input position");
            var spanningSlot=new Rectangle(1700,900,600,40);
            Check(QuotaPlacement.ClipToDisplays(spanningSlot,displays)==spanningSlot,"adjacent displays preserve the whole toolbar through the seam without a jump");
            Check(QuotaPlacement.ClipToDisplays(spanningSlot,new[] {new Rectangle(0,0,1920,1040),new Rectangle(2000,0,1920,1040)})==new Rectangle(2000,900,300,40),"a real gap between monitors cannot be treated as visible toolbar space");
            Check(QuotaPlacement.ClipToDisplays(new Rectangle(4000,900,500,40),displays).IsEmpty,"offscreen input never leaves a detached floating bar");
        }
        static int FakeAnchorWorker()
        {
            using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))
            using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) {
                output.AutoFlush=true; string line;
                while((line=input.ReadLine())!=null) {
                    int x=Int32.Parse(line); System.Threading.Thread.Sleep(250);
                    output.WriteLine(Json.Encode(new ComposerAnchor { Owner=new Rectangle(0,0,1600,1000),Dpi=144,
                        Editor=new Rectangle(100,800,1200,80),Slot=new Rectangle(x,900,500,40) }));
                }
            }
            return 0;
        }
        static async Task CancelledAnchor(string directory)
        {
            string originalRoot=Program.Root;
            File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location,Path.Combine(directory,"CodexQuotaBar.exe"));
            Program.Root=directory;
            try { using(var reader=new AnchorReader()) {
                var old=reader.Read(new IntPtr(111)); await Task.Delay(150); reader.Dispose();
                var replacement=reader.Read(new IntPtr(222));
                Check(await old==null,"cancelled old geometry query cannot return a usable anchor");
                var fresh=await replacement;
                Check(fresh!=null && fresh.Valid && fresh.Slot.Left==222,"old callback cannot kill or overwrite the replacement worker");
                var next=await reader.Read(new IntPtr(333));
                Check(next!=null && next.Slot.Left==333,"replacement worker remains reusable after cancellation");
            }} finally { Program.Root=originalRoot; System.Threading.Thread.Sleep(100); }
        }
        static void Retry()
        {
            var retry = new RetrySchedule(); var now = DateTime.UtcNow;
            foreach (var seconds in new[] {5,15,30,60,120,120}) { retry.Fail(now); Check(retry.DueUtc==now.AddSeconds(seconds), "failure retry delay " + seconds); }
            retry.Success(now,true); Check(retry.DueUtc==now.AddMinutes(1), "foreground success refreshes each minute");
            retry.Fail(now); Check(retry.DueUtc==now.AddSeconds(5), "success resets failure backoff");
            retry.Success(now,false); Check(retry.DueUtc==now.AddMinutes(3), "background success refreshes less often");
            retry.Reset(); Check(retry.DueUtc==DateTime.MinValue, "resume schedules immediate retry");
        }
        static void PersistenceAndAlerts(string directory)
        {
            string path = Path.Combine(directory,"settings.json");
            var settings = Settings.LoadFrom(path);
            Check(!settings.LowQuotaAlerts && settings.DisplayDetail==2, "fresh defaults keep notifications optional");
            settings.Theme="dark"; settings.DisplayDetail=1; settings.AnchorOffsetX=12.5;
            Check(settings.SaveTo(path), "initial atomic save");
            settings.Theme="light"; Check(settings.SaveTo(path), "replacement creates backup");
            var loaded=Settings.LoadFrom(path);
            Check(loaded.Theme=="light" && loaded.DisplayDetail==1 && loaded.AnchorOffsetX==12.5, "settings round trip");
            File.WriteAllText(path,"{broken"); loaded=Settings.LoadFrom(path);
            Check(loaded.Theme=="dark" && loaded.Notice!=null, "corrupt primary recovers backup with notice");
            Check(loaded.SaveTo(path) && Settings.LoadFrom(path+".bak").Theme=="dark", "repair preserves valid backup");
            Check(!loaded.SaveTo(Path.Combine(directory,"missing","settings.json")) && loaded.SaveError!=null, "save failure is surfaced");
            File.WriteAllText(path,"{broken"); File.WriteAllText(path+".bak","{broken");
            Check(Settings.LoadFrom(path).Notice!=null, "both corrupt files surface fallback notice");
            var window = new QuotaWindow { Remaining=19, Minutes=10080, Reset=2000000000 };
            var windows = new List<QuotaWindow> { window };
            settings = new Settings();
            Check(QuotaAlerts.Take(settings,"codex",windows)==null, "disabled alerts stay quiet");
            settings.LowQuotaAlerts=true;
            Check(QuotaAlerts.Take(settings,"codex",windows)!=null, "first low quota warning");
            Check(QuotaAlerts.Take(settings,"codex",windows)==null, "same threshold does not repeat");
            window.Remaining=9; Check(QuotaAlerts.Take(settings,"codex",windows)!=null, "10 percent threshold produces second warning");
            Check(settings.SaveTo(path), "persist alert suppression");
            loaded=Settings.LoadFrom(path); Check(QuotaAlerts.Take(loaded,"codex",windows)==null, "restart does not repeat warning");
            window.Reset=2000604800; Check(QuotaAlerts.Take(loaded,"codex",windows)!=null, "new quota period can warn again");
            window.Reset=null; Check(QuotaAlerts.Take(loaded,"codex",windows)==null, "unknown reset does not cause repeated warnings");
        }
        static int FakeServer()
        {
            using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))
            using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) {
                output.AutoFlush=true; string line;
                while ((line=input.ReadLine())!=null) {
                    var message=Json.Decode(line); var id=Json.Get(message,"id");
                    if (id==null) continue;
                    string method=Convert.ToString(Json.Get(message,"method"));
                    if (method=="initialize") { output.WriteLine(Json.Encode(new { id=id, result=new { } })); continue; }
                    string mode=Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_MODE");
                    if (mode=="drop") return 0;
                    if (mode=="auth" || mode=="network") {
                        output.WriteLine(Json.Encode(new { id=id, error=new { code=-32000, message=mode=="auth" ? "401 unauthorized" : "network connection failed" } }));
                    } else output.WriteLine(Json.Encode(new { id=id, result=new { rateLimits=new { limitId="codex", primary=new { usedPercent=25, windowDurationMins=10080, resetsAt=2000000000 } } } }));
                }
            }
            return 0;
        }
        static async Task Protocol()
        {
            string executable=System.Reflection.Assembly.GetExecutingAssembly().Location;
            try {
                foreach (var mode in new[] {"auth","network","drop","success"}) {
                    Environment.SetEnvironmentVariable("CODEX_QUOTA_TEST_MODE",mode);
                    using(var server=new AppServer()) {
                        await server.Connect(executable);
                        Exception failure = null;
                        QuotaSnapshot result = null;
                        try {
                            result=await server.Read();
                        } catch(Exception error) { failure = error; }
                        if (mode=="success") {
                            Check(failure==null && result.Windows("codex")[0].Remaining==75, "fresh connection recovers and parses quota");
                        } else {
                            Check(failure!=null, mode + " response fails pending read");
                            string description=QuotaError.Describe(failure);
                            Check(mode=="auth" ? description.Contains("登录") : mode=="network" ? description.Contains("网络") : description.Contains("中断"), "classify " + mode + " failure without leaking raw response");
                        }
                    }
                }
            } finally { Environment.SetEnvironmentVariable("CODEX_QUOTA_TEST_MODE",null); }
        }
        [STAThread] static int Main(string[] args)
        {
            if (args.Length>0 && args[0]=="app-server") return FakeServer();
            if (args.Length>0 && args[0]=="--anchor-worker") return FakeAnchorWorker();
            string directory=Path.Combine(Path.GetTempPath(),"codex-quota-tests-"+Guid.NewGuid().ToString("N"));
            string report=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"reliability-result.json");
            Directory.CreateDirectory(directory);
            try {
                Anchors(); Retry(); PersistenceAndAlerts(directory); CancelledAnchor(directory).GetAwaiter().GetResult(); Protocol().GetAwaiter().GetResult();
                File.WriteAllText(report,Json.Encode(new { passed=true, checks=checks })); return 0;
            } catch(Exception error) { File.WriteAllText(report,Json.Encode(new { passed=false, checks=checks, error=error.ToString() })); return 1; }
            finally {
                // Only files in our freshly created test directory are removed; no recursive deletion.
                foreach(var file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
}
