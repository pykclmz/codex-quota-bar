using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace CodexQuotaBar
{
    // The bridge shares the running watcher's state. No HTTP port or account
    // credentials are exposed; Windows restricts the pipe to this user.
    sealed class SettingsBridge : IDisposable
    {
        internal static string PipeName="CodexQuotaBar-settings-"+WindowsIdentity.GetCurrent().User.Value;
        readonly Func<Dictionary<string,object>,Task<object>> dispatch;
        readonly object gate=new object();
        NamedPipeServerStream waiting;
        bool disposed;
        public SettingsBridge(Func<Dictionary<string,object>,Task<object>> dispatch)
        {
            this.dispatch=dispatch;
            Task.Run((Func<Task>)Listen);
        }
        async Task Listen()
        {
            while(true) {
                NamedPipeServerStream pipe;
                lock(gate) {
                    if(disposed) return;
                    var security=new PipeSecurity();
                    security.SetAccessRuleProtection(true,false);
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
                    pipe=new NamedPipeServerStream(PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security);
                    waiting=pipe;
                }
                try {
                    await Task.Factory.FromAsync(pipe.BeginWaitForConnection,pipe.EndWaitForConnection,null);
                    using(var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true))
                    using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true)) {
                        writer.AutoFlush=true;
                        // Cap the request before deserialization; never read an unbounded line.
                        var reading=ReadLimited(reader);
                        if(await Task.WhenAny(reading,Task.Delay(5000))!=reading) continue;
                        var line=await reading;
                        object result;
                        try { result=await dispatch(Json.Decode(line)); }
                        catch { result=new { error="设置未能完成，请重试。" }; }
                        await writer.WriteLineAsync(Json.Encode(result));
                    }
                } catch(IOException) { } catch(ObjectDisposedException) { } catch(InvalidOperationException) { }
                finally { lock(gate) { if(waiting==pipe) waiting=null; } pipe.Dispose(); }
            }
        }
        static async Task<string> ReadLimited(StreamReader reader)
        {
            var text=new StringBuilder(); var buffer=new char[1];
            while(text.Length<32768) {
                if(await reader.ReadAsync(buffer,0,1)==0) throw new IOException();
                if(buffer[0]=='\n') return text.ToString();
                if(buffer[0]!='\r') text.Append(buffer[0]);
            }
            throw new IOException("Request too large");
        }
        public static Dictionary<string,object> Call(string action,object values)
        {
            using(var pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.InOut,PipeOptions.Asynchronous)) {
                try { pipe.Connect(1500); } catch(TimeoutException) { throw new IOException("额度条未运行，请先启动额度条。"); }
                using(var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true))
                using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true)) {
                    writer.AutoFlush=true; writer.WriteLine(Json.Encode(new { action=action,values=values }));
                    var reading=reader.ReadLineAsync();
                    if(!reading.Wait(35000)) throw new IOException("额度条响应超时，请重试。");
                    if(reading.Result==null) throw new IOException("额度条连接已关闭。");
                    var result=Json.Decode(reading.Result);
                    var error=Json.Get(result,"error"); if(error!=null) throw new IOException(Convert.ToString(error));
                    return result;
                }
            }
        }
        public void Dispose() { lock(gate) { disposed=true; if(waiting!=null) waiting.Dispose(); } }
    }

    static class PluginSettings
    {
        internal static readonly string[] Themes={"跟随 Codex","深色","浅色"};
        internal static readonly string[] Details={"仅额度","额度和倒计时","完整信息"};
        static object Property(string type,string title,string description,string[] choices)
        {
            var p=new Dictionary<string,object> { {"type",type},{"title",title},{"description",description} };
            if(choices!=null) p["enum"]=choices; return p;
        }
        public static Dictionary<string,object> Values(SettingsView view)
        {
            var values=new Dictionary<string,object> {
                {"theme",Themes[view.Theme=="dark" ? 1 : view.Theme=="light" ? 2 : 0]},
                {"detail",Details[Math.Max(0,Math.Min(2,view.Detail))]},
                {"alerts",view.Alerts},{"paused",view.Paused}
            };
            if(view.AutoStartKnown) values["autostart"]=view.AutoStart;
            if(view.Buckets.Count>0) values["bucket"]=view.Bucket ?? view.Buckets.Keys.First();
            return values;
        }
        public static object Read(SettingsView view)
        {
            var properties=new Dictionary<string,object> {
                {"theme",Property("string","主题","自动主题会融入 Codex 的聊天背景。",Themes)},
                {"detail",Property("string","显示内容","空间不足时自动精简，悬停可查看完整额度。",Details)},
                {"alerts",Property("boolean","低额度提醒","剩余 20% 或 10% 时发送 Windows 通知。",null)},
                {"paused",Property("boolean","暂时隐藏额度条","仅影响本次运行，重新启动后恢复显示。",null)}
            };
            var running=new List<object> { new { kind="property",property="paused" } };
            if(view.AutoStartKnown) { properties["autostart"]=Property("boolean","随 Windows 登录启动","登录后等待 Codex 打开，自动显示额度条。",null); running.Add(new { kind="property",property="autostart" }); }
            if(view.Buckets.Count>0) { properties["bucket"]=Property("string","额度类型","按账户实际返回的额度类型选择。",view.Buckets.Keys.ToArray()); running.Add(new { kind="property",property="bucket" }); }
            return new { schema=new { type="object",properties=properties },values=Values(view),layout=new object[] {
                new { kind="group",title="外观",items=new object[] { new { kind="property",property="theme" },new { kind="property",property="detail" } } },
                new { kind="group",title="额度提醒",items=new object[] { new { kind="property",property="alerts" } } },
                new { kind="group",title="运行设置",items=running.ToArray() },
                new { kind="group",title="额度与运行",items=new object[] {
                    new { kind="tool",tool="quota_status",title="查看当前额度",description="读取额度条缓存，不额外请求账户。" },
                    new { kind="tool",tool="quota_refresh",title="刷新额度",description="立即从 Codex 获取额度。" },
                    new { kind="tool",tool="quota_reset_position",title="恢复默认位置",description="重新对齐聊天输入框。" },
                    new { kind="tool",tool="quota_open_settings",title="更多设置",description="选择额度类型、管理自动启动和备份。" }
                } }
            } };
        }
        // Validate the entire patch before touching any preference.
        public static Dictionary<string,object> Validate(Dictionary<string,object> patch)
        {
            if(patch==null || patch.Count==0) throw new ArgumentException("请选择要修改的设置。");
            var result=new Dictionary<string,object>();
            foreach(var p in patch) {
                if(p.Key=="theme") {
                    int index=Array.IndexOf(Themes,p.Value as string);
                    if(index<0) throw new ArgumentException("无效的主题。");
                    result[p.Key]=new[]{"auto","dark","light"}[index];
                } else if(p.Key=="detail") {
                    int index=Array.IndexOf(Details,p.Value as string);
                    if(index<0) throw new ArgumentException("无效的显示内容。"); result[p.Key]=index;
                } else if((p.Key=="alerts" || p.Key=="paused" || p.Key=="autostart") && p.Value is bool) result[p.Key]=p.Value;
                else if(p.Key=="bucket" && p.Value is string && ((string)p.Value).Length>0 && ((string)p.Value).Length<=256) result[p.Key]=p.Value;
                else throw new ArgumentException("无效的设置："+p.Key);
            }
            return result;
        }
    }

    static class QuotaMcp
    {
        static object EmptySchema() { return new { type="object",additionalProperties=false }; }
        static object Tool(string name,string title,bool readOnly,object input,object output)
        {
            var result=new Dictionary<string,object> { {"name",name},{"title",title},{"description",title},
                {"inputSchema",input},{"annotations",new { readOnlyHint=readOnly,destructiveHint=false,openWorldHint=false }} };
            if(output!=null) result["outputSchema"]=output;
            return result;
        }
        internal static object Tools()
        {
            return new { tools=new object[] {
                Tool("settings_read","读取额度条设置",true,EmptySchema(),new { type="object",properties=new { schema=new { type="object" },values=new { type="object" },layout=new { type="array",items=new { type="object" } } },required=new[]{"schema","values"} }),
                Tool("settings_update","修改额度条设置",false,new { type="object",properties=new { set=new { type="object",properties=new { theme=new { type="string",@enum=PluginSettings.Themes },detail=new { type="string",@enum=PluginSettings.Details },alerts=new { type="boolean" },paused=new { type="boolean" },autostart=new { type="boolean" },bucket=new { type="string",minLength=1,maxLength=256 } },minProperties=1,additionalProperties=false } },required=new[]{"set"},additionalProperties=false },new { type="object",properties=new { values=new { type="object" } },required=new[]{"values"} }),
                Tool("quota_status","查看当前额度",true,EmptySchema(),null),
                Tool("quota_refresh","刷新额度",true,EmptySchema(),null),
                Tool("quota_reset_position","恢复默认位置",false,EmptySchema(),null),
                Tool("quota_open_settings","打开额度条设置",false,EmptySchema(),null)
            } };
        }
        internal static object Handle(string method,Dictionary<string,object> parameters)
        {
            var capability=new Dictionary<string,object> { {"openai/settings",new { readTool="settings_read",updateTool="settings_update" }} };
            if(method=="initialize") return new { protocolVersion="2025-11-25",serverInfo=new { name="codex-quota-bar",version=Program.Version },capabilities=new { tools=new { },extensions=capability,experimental=capability } };
            if(method=="server/discover") return new { resultType="complete",supportedVersions=new[]{"2026-07-28"},capabilities=new { tools=new { },extensions=capability } };
            if(method=="ping") return new { };
            if(method=="tools/list") return Tools();
            if(method!="tools/call") throw new NotSupportedException();
            string name=Convert.ToString(Json.Get(parameters,"name"));
            if(!new[]{"settings_read","settings_update","quota_status","quota_refresh","quota_reset_position","quota_open_settings"}.Contains(name)) throw new NotSupportedException();
            var arguments=Json.Obj(Json.Get(parameters,"arguments"));
            try {
                if(name=="settings_update") {
                    if(arguments==null || arguments.Count!=1 || !arguments.ContainsKey("set")) throw new ArgumentException("无效的设置参数。");
                    PluginSettings.Validate(Json.Obj(Json.Get(arguments,"set")));
                }
                else if(arguments!=null && arguments.Count>0) throw new ArgumentException("此操作不接受参数。");
                var result=SettingsBridge.Call(name,Json.Get(arguments,"set"));
                object text=Json.Get(result,"text"),structured=Json.Get(result,"data");
                var response=new Dictionary<string,object> { {"content",text==null ? new object[0] : new object[]{new { type="text",text=Convert.ToString(text) }} } };
                if(structured!=null) response["structuredContent"]=structured;
                return response;
            } catch(Exception e) {
                return new { content=new[]{new { type="text",text=e.Message }},isError=true };
            }
        }
        public static int Run()
        {
            using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))
            using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) {
                output.AutoFlush=true; string line;
                while((line=input.ReadLine())!=null) {
                    Dictionary<string,object> request;
                    try { request=Json.Decode(line); } catch { output.WriteLine(Json.Encode(new { jsonrpc="2.0",id=(object)null,error=new { code=-32700,message="Parse error" } })); continue; }
                    object id=Json.Get(request,"id"); if(id==null) continue;
                    try { output.WriteLine(Json.Encode(new { jsonrpc="2.0",id=id,result=Handle(Convert.ToString(Json.Get(request,"method")),Json.Obj(Json.Get(request,"params"))) })); }
                    catch(NotSupportedException) { output.WriteLine(Json.Encode(new { jsonrpc="2.0",id=id,error=new { code=-32601,message="Method not found" } })); }
                    catch { output.WriteLine(Json.Encode(new { jsonrpc="2.0",id=id,error=new { code=-32603,message="Internal error" } })); }
                }
            }
            return 0;
        }
    }
}
