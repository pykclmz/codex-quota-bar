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
            cache.Clear(); Check(!cache.Usable(owner,144,now), "resume invalidates cached geometry");
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
            string directory=Path.Combine(Path.GetTempPath(),"codex-quota-tests-"+Guid.NewGuid().ToString("N"));
            string report=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"reliability-result.json");
            Directory.CreateDirectory(directory);
            try {
                Anchors(); Retry(); PersistenceAndAlerts(directory); Protocol().GetAwaiter().GetResult();
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
