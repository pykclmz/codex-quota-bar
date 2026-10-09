using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexQuotaBar
{
    static class PluginSettingsTests
    {
        static int checks;
        static void Check(bool value,string message) { if(!value) throw new Exception(message); checks++; }
        static Dictionary<string,object> Object(object value) { return Json.Decode(Json.Encode(value)); }
        static Dictionary<string,object> Call(string name,object arguments) { return Object(QuotaMcp.Handle("tools/call",Object(new { name=name,arguments=arguments }))); }
        static void Run()
        {
            SettingsBridge.PipeName="CodexQuotaBar-test-"+Guid.NewGuid().ToString("N");
            var view=new SettingsView { Theme="auto",Detail=2,Bucket="codex",AutoStartKnown=true,AutoStart=true };
            view.Buckets["codex"]="Codex";
            var native=Object(PluginSettings.Read(view));
            var properties=Json.Obj(Json.Get(Json.Obj(Json.Get(native,"schema")),"properties"));
            var values=Json.Obj(Json.Get(native,"values"));
            Check(properties.Keys.OrderBy(k=>k).SequenceEqual(values.Keys.OrderBy(k=>k)),"Every native setting has a current value");
            Check((string)values["theme"]=="跟随 Codex" && (string)values["detail"]=="完整信息","Enums use human readable Chinese labels");
            var patch=PluginSettings.Validate(Object(new { theme="浅色",detail="仅额度",alerts=true }));
            Check((string)patch["theme"]=="light" && (int)patch["detail"]==0,"Chinese choices map to existing preferences");
            foreach(var invalid in new object[]{new { theme="bogus" },new { alerts="true" },new { paused=1 },new { path="C:\\" },new { detail=3 },new { }}) {
                bool rejected=false; try { PluginSettings.Validate(Object(invalid)); } catch(ArgumentException) { rejected=true; }
                Check(rejected,"Invalid setting rejected before mutation");
            }
            view.AutoStartKnown=false;
            Check(!Json.Obj(Json.Get(Object(PluginSettings.Read(view)),"values")).ContainsKey("autostart"),"Unknown startup state is not shown as disabled");
            var initialized=Object(QuotaMcp.Handle("initialize",new Dictionary<string,object>()));
            var capability=Json.Obj(Json.Get(Json.Obj(Json.Get(initialized,"capabilities")),"extensions"));
            Check(capability.ContainsKey("openai/settings"),"Native settings capability advertised");
            var tools=Object(QuotaMcp.Tools());
            Check(((System.Collections.IList)tools["tools"]).Count==6,"Read, update and action tools discoverable");
            int requests=0;
            using(var bridge=new SettingsBridge(request=> {
                Interlocked.Increment(ref requests);
                var action=(string)request["action"];
                if(action=="settings_update") {
                    var update=PluginSettings.Validate(Json.Obj(request["values"]));
                    if(update.ContainsKey("alerts")) view.Alerts=(bool)update["alerts"];
                    return Task.FromResult<object>(new { data=new { values=PluginSettings.Values(view) } });
                }
                return Task.FromResult<object>(new { data=PluginSettings.Read(view) });
            })) {
                var read=Call("settings_read",new { });
                Check(!read.ContainsKey("isError") && read.ContainsKey("structuredContent"),"Real named pipe returns structured settings");
                var changed=Call("settings_update",new { set=new { alerts=true } });
                Check(view.Alerts && changed.ContainsKey("structuredContent"),"Native update reaches the same preference state");
                var updated=Json.Obj(Json.Get(Json.Obj(changed["structuredContent"]),"values"));
                Check(updated.ContainsKey("theme") && (bool)updated["alerts"],"Update returns all effective values");
                int before=requests;
                var invalid=Call("settings_update",new { set=new { alerts=false,theme="invalid" } });
                Check((bool)invalid["isError"] && view.Alerts && requests==before,"Invalid batch never reaches the watcher or partially applies");
                Check((bool)Call("settings_read",new { arbitrary=1 })["isError"] && requests==before,"Action arguments cannot introduce filesystem access");
            }
            var stopped=Call("settings_read",new { });
            Check((bool)stopped["isError"] && Json.Encode(stopped).Contains("未运行"),"Stopped watcher yields actionable feedback");
        }
        static int Main()
        {
            try { Run(); File.WriteAllText(Path.Combine(Program.Root,"plugin-settings-result.json"),Json.Encode(new { passed=true,checks=checks })); return 0; }
            catch(Exception e) { File.WriteAllText(Path.Combine(Program.Root,"plugin-settings-result.json"),Json.Encode(new { passed=false,checks=checks,error=e.Message })); return 1; }
        }
    }
}
