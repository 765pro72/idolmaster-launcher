using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// The streaming process is a reconnectable client of a detached service manager.
internal sealed class MillionDesktopSessionService {
    readonly object gate=new object();Process active;bool busy,canStop,canDetach;string token;
    internal bool Busy{get{lock(gate)return busy;}}
    internal bool CanStop{get{lock(gate)return busy&&canStop;}}
    internal bool CanDetach{get{lock(gate)return busy&&canDetach;}}
    internal int? ActiveProcessId{get{lock(gate){try{return active==null?(int?)null:active.Id;}catch{return null;}}}}
    internal Task<MillionResourceReply> RunAsync(string adapter,string config,Action<Dictionary<string,object>> message){
        lock(gate){if(busy)throw new InvalidOperationException("MLTD 启动任务仍在运行。");busy=true;canStop=canDetach=false;token=null;}
        return Task.Factory.StartNew(delegate{
            Process process=null;
            try{
                if(!MillionResourceService.IsAdapter(adapter)||!File.Exists(Path.Combine(adapter,"src","desktop-session-v2.cjs")))throw new IOException("请选择包含后台启动组件的新版 MLTD 适配包。");
                GkmEnvironmentRepair.Repair();
                ProcessStartInfo start=new ProcessStartInfo(Path.Combine(adapter,"runtime-node","node.exe"),MillionResourceService.Quote(Path.Combine(adapter,"start-mltd-install.cjs"))+" --desktop-session "+MillionResourceService.Quote(config));
                start.WorkingDirectory=Path.GetFullPath(adapter);start.UseShellExecute=false;start.CreateNoWindow=true;start.RedirectStandardInput=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;start.StandardOutputEncoding=new UTF8Encoding(false);start.StandardErrorEncoding=new UTF8Encoding(false);start.EnvironmentVariables.Remove("NODE_OPTIONS");start.EnvironmentVariables.Remove("NODE_PATH");
                process=new Process{StartInfo=start};lock(gate)active=process;process.Start();Task<string> errors=process.StandardError.ReadToEndAsync();string line;Dictionary<string,object> last=null;int count=0;
                while((line=process.StandardOutput.ReadLine())!=null){
                    if(line.Length>1024*1024||++count>2048)throw new IOException("启动记录过长，请保留当前进程与记录。");
                    Dictionary<string,object> data=new JavaScriptSerializer{MaxJsonLength=1024*1024}.Deserialize<Dictionary<string,object>>(line);string type=MillionResourceService.Text(data,"type");
                    lock(gate){if(type=="started")token=MillionResourceService.Text(data,"session");if(type=="ready"||type=="failed"){canStop=MillionResourceService.Text(data,"canStop")=="True";canDetach=MillionResourceService.Text(data,"retained")=="True";}if(type=="ended")canStop=canDetach=false;}
                    last=data;if(message!=null)message(data);
                }
                process.WaitForExit();errors.Wait();return new MillionResourceReply{ExitCode=process.ExitCode,Data=last,Error=errors.Result.Trim()};
            }catch(Exception ex){return new MillionResourceReply{ExitCode=1,Error=ex.Message};}
            finally{lock(gate){active=null;busy=false;canStop=canDetach=false;token=null;}if(process!=null)process.Dispose();}
        });
    }
    internal void StopOwnedSession(){
        lock(gate){if(!busy||!canStop||active==null||String.IsNullOrEmpty(token))throw new InvalidOperationException("当前会话没有可结束的自有服务。");active.StandardInput.WriteLine(new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"command","stop"},{"session",token}}));active.StandardInput.Flush();canStop=canDetach=false;}
    }
    internal void Detach(){
        lock(gate){if(!busy||!canDetach||active==null||String.IsNullOrEmpty(token))throw new InvalidOperationException("后台连接尚未就绪，请等待当前任务完成。");active.StandardInput.WriteLine(new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"command","detach"},{"session",token}}));active.StandardInput.Flush();canStop=canDetach=false;}
    }
}
