using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class ScspNotice {
    internal string Event, Message, Status, Operation;
    internal int Progress=-1;
    internal Dictionary<string,object> Data;
    internal bool Terminal {get{return Event=="result";}}
}

// UI-independent JSONL transport. No file access, networking or waiting occurs in a paint callback.
internal sealed class ScspService : IDisposable {
    readonly string root,settingsPath; readonly object gate=new object();
    Process active; string cancelFile; bool disposed,demoStoryEnabled,demoModelArmed;double demoModelScale=1; int generation;
    internal static readonly string[] RuntimeFiles={"python/python.exe","python/python312.dll","python/vcruntime140.dll","python/vcruntime140_1.dll","python/Lib/encodings/__init__.py","python/DLLs/_ctypes.pyd","tools/scsp_bridge.py","tools/scsp_legacy_migration.py","tools/scsp_template_upgrade.json","components/scsp/SCSPBridge.exe","components/scsp/gateway/local-launch/client.json"};
    internal event Action<ScspNotice> Notice;
    internal bool Busy {get{lock(gate)return active!=null;}}
    internal static string SettingsOverride=null;
    internal static string SettingsDirectory {get{return SettingsOverride??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GakumasUnlock");}}
    internal ScspService(string root){GkmEnvironmentRepair.Repair();this.root=Path.GetFullPath(root);settingsPath=Path.Combine(SettingsDirectory,"scsp-path.txt");}
    internal void LoadPath(Action<string> loaded){ThreadPool.QueueUserWorkItem(delegate{
        string path="";try{if(File.Exists(settingsPath))path=File.ReadAllText(settingsPath,Encoding.UTF8).Trim();}catch(Exception ex){Emit(new ScspNotice{Event="log",Message="无法读取 SCSP 路径："+ex.Message});}
        if(!disposed)loaded(path);
    });}
    internal void SavePath(string path){ThreadPool.QueueUserWorkItem(delegate{
        try{Directory.CreateDirectory(SettingsDirectory);File.WriteAllText(settingsPath,path,new UTF8Encoding(false));}
        catch(Exception ex){Emit(new ScspNotice{Event="log",Message="无法保存 SCSP 路径："+ex.Message});}
    });}
    static string Quote(string value){StringBuilder output=new StringBuilder("\"");int slashes=0;foreach(char c in value??""){if(c=='\\'){slashes++;continue;}if(c=='\"'){output.Append('\\',slashes*2+1);output.Append(c);}else{output.Append('\\',slashes);output.Append(c);}slashes=0;}output.Append('\\',slashes*2);output.Append('"');return output.ToString();}
    internal bool Run(string action,string gameDir,bool demo){
        lock(gate){
            if(disposed||active!=null)return false;
            generation++;int current=generation;
            if(demo){active=new Process();ThreadPool.QueueUserWorkItem(delegate{Demo(action,current);});return true;}
            string script=Path.Combine(root,"tools","scsp_bridge.py"),python=Path.Combine(root,"python","python.exe");
            cancelFile=Path.Combine(SettingsDirectory,"scsp-cancel-"+Guid.NewGuid().ToString("N"));
            Process process=new Process();active=process;
            ProcessStartInfo start=new ProcessStartInfo(python,"-B "+Quote(script)+" --action "+Quote(action)+" --game-dir "+Quote(gameDir)+" --cancel-file "+Quote(cancelFile));
            start.WorkingDirectory=root;start.UseShellExecute=false;start.CreateNoWindow=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
            start.StandardOutputEncoding=new UTF8Encoding(false);start.StandardErrorEncoding=new UTF8Encoding(false);IsolatePython(start);
            process.StartInfo=start;
            ThreadPool.QueueUserWorkItem(delegate{Execute(process,action,current,false);});return true;
        }
    }
    internal List<string> MissingRuntimeFiles(){List<string> missing=new List<string>();foreach(string relative in RuntimeFiles)if(!File.Exists(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))))missing.Add(relative);return missing;}
    static void IsolatePython(ProcessStartInfo start){start.EnvironmentVariables.Remove("PYTHONHOME");start.EnvironmentVariables.Remove("PYTHONPATH");start.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";}
    internal static string MissingRuntimeMessage(List<string> files){return "缺少运行文件："+Environment.NewLine+"- "+string.Join(Environment.NewLine+"- ",files.ToArray())+Environment.NewLine+"请完整解压完整分享包后，从其根目录运行。若使用覆盖备份或补丁，请覆盖到原完整启动器目录；仅解压覆盖备份不能独立运行。";}
    bool Execute(Process process,string action,int current,bool command){
        bool terminal=false;
        try{
            List<string> missing=MissingRuntimeFiles();if(missing.Count>0)throw new FileNotFoundException(MissingRuntimeMessage(missing));
            process.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e){if(e.Data==null)return;ScspNotice note=Parse(e.Data,action);if(note.Terminal){terminal=true;if(note.Status=="failed")note.Message=LauncherErrors.FailureDetail("scsp",note.Message);}Emit(note);};
            process.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e){if(!string.IsNullOrWhiteSpace(e.Data))Emit(new ScspNotice{Event="log",Operation=action,Message=e.Data});};
            process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();process.WaitForExit();
            if(!terminal)Emit(new ScspNotice{Event="result",Operation=action,Status=process.ExitCode==0?"succeeded":process.ExitCode==2?"cancelled":"failed",Message=LauncherErrors.Finish("scsp",process.ExitCode,null)});
            return process.ExitCode==0;
        }catch(Exception ex){Emit(new ScspNotice{Event="result",Operation=action,Status="failed",Message=LauncherErrors.FailedToStart("scsp",ex)});return false;}
        finally{
            string finishedSignal=null;if(!command)lock(gate){if(generation==current){active=null;finishedSignal=cancelFile;cancelFile=null;}}
            if(finishedSignal!=null)try{File.Delete(finishedSignal);}catch(Exception ex){Emit(new ScspNotice{Event="log",Operation=action,Message="取消信号清理失败："+ex.Message});}
            process.Dispose();Emit(new ScspNotice{Event="idle",Operation=action});
        }
    }
    internal void Command(string action,string gameDir,string value,bool demo){
        if(disposed)return;
        if(demo){Emit(new ScspNotice{Event="log",Operation=action,Message="演示指令："+action+(value==null?"":" "+value)});if(action=="stop")Cancel();if(action=="mv_chibi")demoModelArmed=true;if(action=="mv_normal")demoModelArmed=false;if(action=="mv_scale")demoModelScale=double.Parse(value,CultureInfo.InvariantCulture);if(action.StartsWith("mv_"))Emit(new ScspNotice{Event="log",Operation=action,Message="[测试] 模型状态",Data=new Dictionary<string,object>{{"event","mv_state"},{"value",new Dictionary<string,object>{{"Armed",demoModelArmed},{"Active",false},{"Created",true},{"Scale",demoModelScale},{"Error",null}}}}});return;}
        string script=Path.Combine(root,"tools","scsp_bridge.py"),python=Path.Combine(root,"python","python.exe");
        Process process=new Process();process.StartInfo=new ProcessStartInfo(python,"-B "+Quote(script)+" --action "+Quote(action)+" --game-dir "+Quote(gameDir)+(value==null?"":" --value "+Quote(value))){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
        IsolatePython(process.StartInfo);
        ThreadPool.QueueUserWorkItem(delegate{Execute(process,action,0,true);});
    }
    internal void CommandSequence(string[] actions,string gameDir,bool demo){
        if(disposed)return;if(demo){foreach(string action in actions)Command(action,gameDir,null,true);return;}
        ThreadPool.QueueUserWorkItem(delegate{
            foreach(string action in actions){if(disposed)return;string script=Path.Combine(root,"tools","scsp_bridge.py"),python=Path.Combine(root,"python","python.exe");Process process=new Process();process.StartInfo=new ProcessStartInfo(python,"-B "+Quote(script)+" --action "+Quote(action)+" --game-dir "+Quote(gameDir)){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};IsolatePython(process.StartInfo);if(!Execute(process,action,0,true))break;}
        });
    }
    internal void Cancel(){string signal;lock(gate){if(active==null)return;signal=cancelFile;if(signal==null){generation++;active.Dispose();active=null;}}
        if(signal!=null)ThreadPool.QueueUserWorkItem(delegate{try{lock(gate){if(active==null||cancelFile!=signal)return;Directory.CreateDirectory(Path.GetDirectoryName(signal));File.WriteAllText(signal,"cancel",Encoding.ASCII);}}catch(Exception ex){Emit(new ScspNotice{Event="log",Message="无法发送取消信号："+ex.Message});}});
        Emit(new ScspNotice{Event="progress",Status="cancelling",Message="正在请求停止，请等待引擎完成清理…"});
    }
    void Demo(string action,int current){
        string[] steps={"检查游戏目录与运行依赖","核对本地缓存及离线工作目录","准备运行环境","操作完成"};
        for(int i=0;i<steps.Length;i++){
            Thread.Sleep(180);lock(gate)if(disposed||generation!=current){Emit(new ScspNotice{Event="result",Operation=action,Status="cancelled",Message="演示已取消"});Emit(new ScspNotice{Event="idle",Operation=action});return;}
            Emit(new ScspNotice{Event="progress",Operation=action,Message="[演示] "+steps[i],Progress=i*100/(steps.Length-1)});
        }
        if(action=="launch_offline")Emit(new ScspNotice{Event="game_started",Operation=action,Message="[演示] 离线游戏已启动"});
        if(action.StartsWith("story_")){
            if(action=="story_install"||action=="story_update")demoStoryEnabled=true;if(action=="story_uninstall"||action=="story_recover")demoStoryEnabled=false;
            Emit(new ScspNotice{Event="log",Operation=action,Message="[演示] "+(demoStoryEnabled?"剧情服装已启用":"剧情服装未启用"),Data=new Dictionary<string,object>{{"event","story_state"},{"value",new Dictionary<string,object>{{"Enabled",demoStoryEnabled},{"Installed",demoStoryEnabled},{"NeedsRecovery",false},{"NeedsUpdate",false},{"Label",demoStoryEnabled?"剧情服装已启用":"剧情服装未启用"}}}}});
        }
        lock(gate){if(generation==current){active.Dispose();active=null;}}
        Emit(new ScspNotice{Event="result",Operation=action,Status="succeeded",Message="演示完成，未启动游戏或修改游戏文件。"});Emit(new ScspNotice{Event="idle",Operation=action});
    }
    static string String(Dictionary<string,object> data,string key){object value;return data!=null&&data.TryGetValue(key,out value)&&value!=null?Convert.ToString(value,CultureInfo.InvariantCulture):"";}
    internal static ScspNotice Parse(string line,string action){
        try{Dictionary<string,object> json=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(line);int progress=-1;string raw=String(json,"progress");if(raw.Length>0)int.TryParse(raw,out progress);
            string status=String(json,"status");Dictionary<string,object> data=null;object detail;if(json.TryGetValue("data",out detail))data=detail as Dictionary<string,object>;
            if(status.Length==0)status=String(data,"status");object success;if(json.TryGetValue("ok",out success))status=Convert.ToBoolean(success)?"succeeded":status=="cancelled"?"cancelled":"failed";
            string kind=String(json,"event"),message=String(json,"message");if(kind=="log"&&String(data,"event")=="game_started")kind="game_started";
            object state;if(kind=="log"&&data!=null&&data.TryGetValue("value",out state)){string details=new JavaScriptSerializer().Serialize(state);message+=(message.Length>0?"：":"")+(details.Length>1600?details.Substring(0,1600)+"…":details);}
            return new ScspNotice{Event=kind,Status=status,Operation=String(json,"operation").Length>0?String(json,"operation"):action,Message=message,Progress=progress,Data=data??json};
        }catch{return new ScspNotice{Event="log",Operation=action,Message=line};}
    }
    void Emit(ScspNotice note){Action<ScspNotice> handler=Notice;if(!disposed&&handler!=null)handler(note);}
    public void Dispose(){if(disposed)return;Cancel();disposed=true;Notice=null;}
}
