using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class MillionResourceReply {
    internal int ExitCode;internal Dictionary<string,object> Data;internal string Error;
    internal string ExplainFailure(){
        string status=MillionResourceService.Text(Data,"status");
        string detail=String.IsNullOrWhiteSpace(Error)?MillionResourceService.Text(Data,"error"):Error;
        if(String.IsNullOrWhiteSpace(detail))detail=status;
        string hint=LauncherErrors.Diagnostic(detail)??LauncherErrors.Diagnostic(status);
        return ExitCode==0?LauncherErrors.FailureDetail("mltd",detail)
            :LauncherErrors.Finish("mltd",ExitCode,hint)+(String.IsNullOrWhiteSpace(detail)?"":" [诊断] "+detail);
    }
}

// Pinned adapter resource commands; Node verifies the explicit game target.
internal sealed class MillionResourceService {
    readonly object gate=new object();bool busy;Process active;
    internal static string SettingsOverride;
    internal bool Busy{get{lock(gate)return busy;}}
    internal int? ActiveProcessId{get{lock(gate){try{return active==null?(int?)null:active.Id;}catch{return null;}}}}
    static string Settings{get{return Path.Combine(SettingsOverride??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MLTDLauncher"),"adapter-location.json");}}
    internal static bool IsAdapter(string directory){return !String.IsNullOrWhiteSpace(directory)&&File.Exists(Path.Combine(directory,"runtime-node","node.exe"))&&File.Exists(Path.Combine(directory,"start-mltd-install.cjs"))&&File.Exists(Path.Combine(directory,"state","optional-resources-90350.json"));}
    internal static string FindAdapter(string launcherRoot){
        try{if(File.Exists(Settings)){Dictionary<string,object> s=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Settings,Encoding.UTF8));string p=Text(s,"directory");if(IsAdapter(p))return Path.GetFullPath(p);}}catch{}
        foreach(string p in new[]{Path.Combine(launcherRoot,"MLTD"),Path.Combine(launcherRoot,"MLTD-adapter"),launcherRoot})if(IsAdapter(p))return Path.GetFullPath(p);return "";
    }
    internal static void SaveAdapter(string directory){
        if(!IsAdapter(directory))throw new IOException("请选择完整解压后的 MLTD 离线适配包目录。");string file=Settings,parent=Path.GetDirectoryName(file);Directory.CreateDirectory(parent);string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temp,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"schema",1},{"directory",Path.GetFullPath(directory)}}),new UTF8Encoding(false));if(File.Exists(file))File.Replace(temp,file,null);else File.Move(temp,file);
    }
    internal static string Quote(string value){StringBuilder s=new StringBuilder("\"");int slashes=0;foreach(char c in value??""){if(c=='\\'){slashes++;continue;}s.Append('\\',c=='"'?slashes*2+1:slashes);s.Append(c);slashes=0;}s.Append('\\',slashes*2);s.Append('"');return s.ToString();}
    internal Task<MillionResourceReply> RunAsync(string adapter,params string[] args){
        if(args.Length==0||!new[]{"--check-content-update","--update-local-content","--rollback-content-update","--list-resource-packs","--resource-status","--plan-resource-pack","--install-resource-pack","--startup-resource-status","--prepare-startup-resources","--desktop-overview","--stop-desktop-service","--discover","--use-installed-profile","--discover-adb","--update-client-adapter","--probe-adb","--use-adb-profile"}.Contains(args[0]))throw new ArgumentException("不支持的 MLTD 本地工具命令。");
        lock(gate){if(busy)throw new InvalidOperationException("当前资源任务尚未结束。");busy=true;}
        return Task.Factory.StartNew(delegate{
            Process process=null;
            try{
                if(!IsAdapter(adapter))throw new IOException("离线适配包不完整，请重新选择目录。");GkmEnvironmentRepair.Repair();
                ProcessStartInfo start=new ProcessStartInfo(Path.Combine(adapter,"runtime-node","node.exe"),Quote(Path.Combine(adapter,"start-mltd-install.cjs"))+" "+String.Join(" ",args.Select(Quote).ToArray()));
                start.WorkingDirectory=Path.GetFullPath(adapter);start.UseShellExecute=false;start.CreateNoWindow=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;start.StandardOutputEncoding=new UTF8Encoding(false);start.StandardErrorEncoding=new UTF8Encoding(false);start.EnvironmentVariables.Remove("NODE_OPTIONS");start.EnvironmentVariables.Remove("NODE_PATH");
                process=new Process{StartInfo=start};lock(gate)active=process;process.Start();
                Task<string> output=process.StandardOutput.ReadToEndAsync(),error=process.StandardError.ReadToEndAsync();process.WaitForExit();Task.WaitAll(output,error);
                string text=output.Result.Trim();if(text.Length>4*1024*1024||error.Result.Length>256*1024)throw new IOException("资源工具输出过长，请保留运行记录。");
                Dictionary<string,object> data=text.Length==0?null:new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Deserialize<Dictionary<string,object>>(text);
                return new MillionResourceReply{ExitCode=process.ExitCode,Data=data,Error=error.Result.Trim()};
            }catch(Exception ex){return new MillionResourceReply{ExitCode=1,Error=ex.Message};}
            finally{lock(gate){active=null;busy=false;}if(process!=null)process.Dispose();}
        });
    }
    internal static string LatestRun(string adapter){
        string root=Path.GetFullPath(adapter).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;List<DirectoryInfo> runs=new List<DirectoryInfo>();
        foreach(string group in new[]{"desktop-launch","desktop-sessions"}){string dir=Path.Combine(root,"runtime",group);if(!Directory.Exists(dir))continue;DirectoryInfo parent=new DirectoryInfo(dir);if((parent.Attributes&FileAttributes.ReparsePoint)!=0)continue;foreach(DirectoryInfo entry in parent.EnumerateDirectories()){if((entry.Attributes&FileAttributes.ReparsePoint)!=0)continue;DirectoryInfo run=group=="desktop-sessions"?new DirectoryInfo(Path.Combine(entry.FullName,"launch")):entry;if(!run.Exists||(run.Attributes&FileAttributes.ReparsePoint)!=0)continue;DirectoryInfo server=new DirectoryInfo(Path.Combine(run.FullName,"server"));FileInfo log=new FileInfo(Path.Combine(run.FullName,"server.log"));if(server.Exists&&log.Exists&&(server.Attributes&FileAttributes.ReparsePoint)==0&&(log.Attributes&FileAttributes.ReparsePoint)==0)runs.Add(run);}}
        DirectoryInfo latest=runs.OrderByDescending(d=>File.GetLastWriteTimeUtc(Path.Combine(d.FullName,"server.log"))).FirstOrDefault();return latest==null?null:latest.FullName.Substring(root.Length).Replace('\\','/')+"/server";
    }
    internal static string Text(Dictionary<string,object> data,string key){object value;return data!=null&&data.TryGetValue(key,out value)&&value!=null?Convert.ToString(value):"";}
    internal static long Number(Dictionary<string,object> data,string key){long n;return Int64.TryParse(Text(data,key),out n)?n:0;}
    internal static IEnumerable<Dictionary<string,object>> Rows(Dictionary<string,object> data,string key){object value;if(data!=null&&data.TryGetValue(key,out value)&&value is IEnumerable)foreach(object row in (IEnumerable)value){Dictionary<string,object> d=row as Dictionary<string,object>;if(d!=null)yield return d;}}
    internal static string Size(long bytes){return bytes>=1000000000?(bytes/1000000000d).ToString("0.00")+" GB":(bytes/1000000d).ToString("0.00")+" MB";}
    internal static string Name(string id){
        if(id=="welcome-singer-audio-family-90350")return "Welcome!! 歌唱音轨";
        if(id=="loading-comics-full-size-90350")return "加载漫画（完整图）";
        string[] ids={"base-observed","mv7-kawaii-observed","ui-images","album-card-backgrounds","startup-cold-additions","live-selection-observed","mv7-kawaii-cold-additions","main-date-observed-additions","live-selection-more-observed","mv5-two-songs-observed-additions","small-jacket-family-additions","full-jacket-family-additions","main-night-observed","mv-ittana-observed-additions","selection-background-observed-additions","selection-background-family-additions","costume-ex029-models","costume-icon-family-additions","existing-ui-family-gaps","mv-typed-config-textures","main-story-data-common","main-story-cover-family","main2-mv-and-shared-additions","main-story-episode1-voices","cold-observed-shared-additions","system-live-voices","mv-glowto-six-candidates","glowto-six-formation-icons","mv-two-three-samples","mv-union13-candidates","mv-bntht39-observed","mv-newden-unit18-additions","mv-welcome39-observed","haruka-idol-story-observed","welcome5-hairstyle-observed","aikotoba-byakko-observed","welcome39-cache-repair","core-runtime"};
        string[] names={"基础资源补充","MV 样例资源补充","界面图片","卡片背景","首次启动补充","选曲界面补充","MV 样例追加资源","主线白天场景","选曲界面追加资源","五人 MV 样例补充","歌曲小封面","歌曲完整封面","主线夜间场景","一旦爱 MV 补充","选曲背景补充","选曲背景合集","衣装模型补充","衣装缩略图","界面缺图补充","MV 配置与贴图","主线剧情共用数据","主线章节封面","主线第二话与共用资源","主线第一话语音","共用场景补充","舞台语音","Glow Together 六人舞台","Glow Together 编队图标","二人及三人 MV 样例","UNION!! 十三人舞台","Brand New Theater! 39 人舞台","電波感傷编队补充","Welcome!! 39 人舞台","春香偶像剧情样例","Welcome!! 发型样例","合言葉はスタートアップ！衣装样例","Welcome!! 39 人缺失补充","随包基础文件"};
        for(int i=0;i<ids.Length;i++)if(id==ids[i]+"-90350")return names[i];return id;
    }
    internal static string State(string value){switch(value){case "not-installed":return "尚未安装";case "invalid-local-content":return "内容校验失败";case "missing-sidecar":return "缺少校验信息";case "invalid-local-sidecar":return "校验信息不一致";case "unverified-local-content":return "本地存在，尚未经可信清单确认";case "valid-now":return "当前文件校验通过，需在游戏中重试";default:return "需检查："+value;}}
}
