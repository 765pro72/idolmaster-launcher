using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal enum GameDirectoryKind { Gakumas, Shiny }
internal sealed class GameDirectoryMatch {
    internal string Path, Source;
    internal GameDirectoryMatch(string path,string source){Path=path;Source=source;}
}
internal sealed class GameDirectoryRequest {
    internal GameDirectoryKind Game;
    internal string SavedPath, PackageRoot;
    internal bool UseSystemSources=true;
    internal int BudgetMilliseconds=8000, MaxDirectories=5000, MaxDepth=5;
    internal readonly List<string> Roots=new List<string>(), KnownPaths=new List<string>(), MetadataFiles=new List<string>(), LogFiles=new List<string>();
}
internal sealed class GameDirectoryResult {
    internal readonly List<GameDirectoryMatch> Matches=new List<GameDirectoryMatch>();
    internal bool Cancelled, TimedOut;
    internal int Inspected;
    internal string Error;
    internal string UniquePath {get{return !Cancelled&&!TimedOut&&Error==null&&Matches.Count==1?Matches[0].Path:null;}}
}

// Read-only discovery. Call Find on a worker; it never starts games, changes
// registry/proxy state, scans network drives or reads authentication stores.
internal static class GameDirectoryFinder {
    internal static string Folder(GameDirectoryKind game){return game==GameDirectoryKind.Gakumas?"gakumas":"SONGforPRISM";}
    internal static string Exe(GameDirectoryKind game){return game==GameDirectoryKind.Gakumas?"gakumas.exe":"imasscprism.exe";}
    static string Data(GameDirectoryKind game){return game==GameDirectoryKind.Gakumas?"gakumas_Data":"imasscprism_Data";}
    static bool Pe(string file){try{using(FileStream f=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))return f.Length>=64&&f.ReadByte()==77&&f.ReadByte()==90;}catch{return false;}}
    static bool Derived(string path){foreach(string part in path.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)){
        string p=part.ToLowerInvariant();if(p==".prism-local"||p==".prism-story"||p=="gakumasunlock"||p=="$recycle.bin"||p=="system volume information"||p.StartsWith("backup")||p.StartsWith("备份")||p.Contains(".bak"))return true;
    }return false;}
    internal static bool TryValidate(GameDirectoryKind game,string input,out string path,out string error){
        path=null;error="请选择包含 "+Exe(game)+" 的 "+Folder(game)+" 游戏目录。";
        try{
            if(string.IsNullOrWhiteSpace(input))return false;
            string candidate=Path.GetFullPath(Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'))).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(string.Equals(Path.GetFileName(candidate),Exe(game),StringComparison.OrdinalIgnoreCase))candidate=Path.GetDirectoryName(candidate);
            if(!string.Equals(Path.GetFileName(candidate),Folder(game),StringComparison.OrdinalIgnoreCase)){
                string child=Path.Combine(candidate,Folder(game));
                if(Directory.Exists(child))candidate=child;else return false;
            }
            if(!Pe(Path.Combine(candidate,Exe(game)))||!Pe(Path.Combine(candidate,"GameAssembly.dll"))||!Directory.Exists(Path.Combine(candidate,Data(game)))){error="目录缺少有效的 "+Exe(game)+"、GameAssembly.dll 或 "+Data(game)+"，请确认游戏已完整安装。";return false;}
            path=candidate;error=null;return true;
        }catch(Exception ex){error="无法读取游戏目录："+ex.Message;return false;}
    }
    sealed class Search {
        internal GameDirectoryRequest Request;internal GameDirectoryResult Result=new GameDirectoryResult();
        internal CancellationToken Token;internal Stopwatch Watch=Stopwatch.StartNew();internal Action<string> Progress;
        readonly HashSet<string> seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool Stop {get{if(Token.IsCancellationRequested)Result.Cancelled=true;if(Watch.ElapsedMilliseconds>=Request.BudgetMilliseconds)Result.TimedOut=true;return Result.Cancelled||Result.TimedOut;}}
        internal void Note(string text){if(Progress!=null)Progress(text);}
        internal void Add(string value,string source){
            if(Stop||string.IsNullOrWhiteSpace(value)||value.Trim().Trim('"').StartsWith("\\\\",StringComparison.Ordinal))return;
            string path,error;if(!TryValidate(Request.Game,value,out path,out error)||Derived(path)||!seen.Add(path))return;
            Result.Matches.Add(new GameDirectoryMatch(path,source));
        }
        internal void Metadata(string file){
            if(Stop||!File.Exists(file))return;
            try{FileInfo info=new FileInfo(file);if(info.Length>2*1024*1024)return;
                object data=new JavaScriptSerializer{MaxJsonLength=2*1024*1024,RecursionLimit=32}.DeserializeObject(File.ReadAllText(file,Encoding.UTF8));int visited=0;Values(data,0,ref visited);
            }catch{}
        }
        void Values(object value,int depth,ref int visited){
            if(Stop||depth>16||visited++>20000)return;
            IDictionary<string,object> map=value as IDictionary<string,object>;
            if(map!=null){foreach(KeyValuePair<string,object> row in map){
                string text=row.Value as string;
                if(text!=null&&Regex.IsMatch(row.Key,"path|directory|install.*dir|location|executable",RegexOptions.IgnoreCase))Add(text,"DMM安装配置");
                else if(!(row.Value is string))Values(row.Value,depth+1,ref visited);
            }return;}
            IEnumerable list=value as IEnumerable;if(list!=null&&!(value is string))foreach(object item in list)Values(item,depth+1,ref visited);
        }
        internal void Log(string file){
            if(Stop||!File.Exists(file))return;
            try{string tail;using(FileStream f=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                f.Seek(Math.Max(0,f.Length-1024*1024),SeekOrigin.Begin);using(StreamReader r=new StreamReader(f,Encoding.UTF8))tail=r.ReadToEnd();}
                string pattern="exe[:=]\\s*(?<path>[A-Za-z]:[\\\\/].*?"+Regex.Escape(Exe(Request.Game))+")(?=[\\s\"']|$)";
                foreach(Match m in Regex.Matches(tail,pattern,RegexOptions.IgnoreCase)){if(Stop)break;Add(m.Groups["path"].Value.Replace("\\\\","\\"),"DMM历史启动路径");}
            }catch{}
        }
        internal void RegistryPaths(){
            foreach(RegistryKey hive in new[]{Registry.CurrentUser,Registry.LocalMachine})foreach(string prefix in new[]{"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall","SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"}){
                if(Stop)return;try{using(RegistryKey root=hive.OpenSubKey(prefix)){if(root==null)continue;foreach(string key in root.GetSubKeyNames()){
                    if(Stop)return;try{using(RegistryKey item=root.OpenSubKey(key)){if(item==null)continue;
                        string name=Convert.ToString(item.GetValue("DisplayName"));
                        string pattern=Request.Game==GameDirectoryKind.Gakumas?"gakumas|学[園园]アイドル|学园偶像|Gakuen.*IDOL":"SONG.*PRISM|imasscprism|シャイニーカラーズ";
                        if(!Regex.IsMatch(name,pattern,RegexOptions.IgnoreCase))continue;
                        Add(Convert.ToString(item.GetValue("InstallLocation")),"系统安装记录");
                        string icon=Convert.ToString(item.GetValue("DisplayIcon"));if(icon.Length>0)Add(icon.Split(',')[0].Trim('"'),"系统安装记录");
                    }}catch{}
                }}}catch{}
            }
        }
        internal void Running(){
            Process[] processes;try{processes=Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Exe(Request.Game)));}catch{return;}
            foreach(Process p in processes)using(p){if(Stop)continue;try{Add(p.MainModule.FileName,"运行中的原游戏");}catch{}}
        }
        internal void Scan(List<string> roots){
            Queue<KeyValuePair<string,int>> queue=new Queue<KeyValuePair<string,int>>();HashSet<string> visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string root in roots)queue.Enqueue(new KeyValuePair<string,int>(root,0));
            while(queue.Count>0&&!Stop&&Result.Inspected<Request.MaxDirectories){
                KeyValuePair<string,int> node=queue.Dequeue();if(!visited.Add(node.Key))continue;Result.Inspected++;
                Add(node.Key,"固定目录名搜索");if(node.Value>=Request.MaxDepth)continue;
                try{List<string> children=new List<string>();foreach(string child in Directory.EnumerateDirectories(node.Key)){
                    if(Stop||children.Count>=400)break;string name=Path.GetFileName(child);
                    if(Skip(name)||Derived(child)||(File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)continue;children.Add(child);
                }
                children.Sort(delegate(string a,string b){int pa=Likely(Path.GetFileName(a)),pb=Likely(Path.GetFileName(b));return pa==pb?StringComparer.OrdinalIgnoreCase.Compare(a,b):pb.CompareTo(pa);});
                foreach(string child in children)queue.Enqueue(new KeyValuePair<string,int>(child,node.Value+1));
                }catch(UnauthorizedAccessException){}catch(IOException){}catch(System.Security.SecurityException){}
            }
        }
    }
    static int Likely(string name){return Regex.IsMatch(name,"gakumas|gakuen|SONGforPRISM|scsp|DMM|game|idol|imas|游戏",RegexOptions.IgnoreCase)?1:0;}
    static bool Skip(string name){return Regex.IsMatch(name,"^(Windows|Windows.old|ProgramData|AppData|Recovery|PerfLogs|node_modules|\\.git|\\.svn|\\$.*|System Volume Information)$",RegexOptions.IgnoreCase);}
    internal static GameDirectoryResult Find(GameDirectoryRequest request,CancellationToken token,Action<string> progress){
        Search s=new Search{Request=request,Token=token,Progress=progress};
        try{
            s.Note("检查已保存的游戏目录…");s.Add(request.SavedPath,"已保存的手动/自动选择");if(s.Result.UniquePath!=null)return s.Result;
            List<string> roots=new List<string>(request.Roots);
            s.Note("核对安装记录、DMM配置和启动路径…");
            if(request.UseSystemSources){
                s.Running();s.RegistryPaths();
                foreach(string basePath in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)})foreach(string player in new[]{"dmmgameplayer5","DMMGamePlayer"}){
                    string dmm=Path.Combine(basePath,player);
                    foreach(string name in new[]{"dmmgame.cnf","games.json","game_config.json","config.json"})s.Metadata(Path.Combine(dmm,name));
                    s.Log(Path.Combine(dmm,"logs","dll.log"));
                    foreach(string games in new[]{"games","DMMGames"})s.Add(Path.Combine(dmm,games,Folder(request.Game)),"DMM默认安装目录");
                }
                foreach(DriveInfo drive in DriveInfo.GetDrives())try{if(drive.DriveType==DriveType.Fixed&&drive.IsReady)roots.Add(drive.RootDirectory.FullName);}catch{}
            }
            foreach(string file in request.MetadataFiles)s.Metadata(file);foreach(string file in request.LogFiles)s.Log(file);
            // An installed, validated DMM/registry location outranks an unused
            // second copy in a common directory. Conflicting registered copies
            // still require a manual choice; historical log paths do not win.
            List<GameDirectoryMatch> registered=s.Result.Matches.FindAll(delegate(GameDirectoryMatch m){return m.Source=="DMM安装配置"||m.Source=="系统安装记录"||m.Source=="运行中的原游戏";});
            if(registered.Count>0){s.Result.Matches.Clear();s.Result.Matches.AddRange(registered);return s.Result;}
            foreach(string known in request.KnownPaths)s.Add(known,"指定候选目录");
            if(!string.IsNullOrWhiteSpace(request.PackageRoot)){s.Add(request.PackageRoot,"启动器相邻目录");s.Add(Path.GetDirectoryName(request.PackageRoot),"启动器相邻目录");}
            s.Note("检查本地磁盘的常见游戏目录…");
            foreach(string root in roots){foreach(string middle in new[]{"","Games","Game","DMMGames","DMM\\Games","DMMGamePlayer\\games","Program Files\\DMMGames","Program Files (x86)\\DMMGames","游戏","scsp","Gakuen IDOLM@STER"})s.Add(Path.Combine(root,middle,Folder(request.Game)),"常见安装目录");}
            if(s.Result.Matches.Count==0){s.Note("按固定目录名进行限时搜索…");s.Scan(roots);}
        }catch(Exception ex){s.Result.Error=ex.Message;}
        return s.Result;
    }
    internal static void SaveGakumasPath(string root,string directory){
        string path,error;if(!TryValidate(GameDirectoryKind.Gakumas,directory,out path,out error))throw new IOException(error);
        string argsFile=Path.Combine(root,"launch-args.json"),pathFile=Path.Combine(root,"game_path.txt");
        byte[] oldArgs=File.Exists(argsFile)?File.ReadAllBytes(argsFile):null;
        byte[] oldPath=File.Exists(pathFile)?File.ReadAllBytes(pathFile):null;
        string updated=null;
        if(oldArgs!=null){JavaScriptSerializer serializer=new JavaScriptSerializer();Dictionary<string,object> args=serializer.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(oldArgs).TrimStart('\ufeff'));args["exe_path"]=Path.Combine(path,"gakumas.exe");updated=serializer.Serialize(args);}
        try{if(updated!=null)File.WriteAllText(argsFile,updated,new UTF8Encoding(false));File.WriteAllText(pathFile,path,new UTF8Encoding(false));}
        catch{if(oldArgs!=null)File.WriteAllBytes(argsFile,oldArgs);if(oldPath!=null)File.WriteAllBytes(pathFile,oldPath);throw;}
    }
}
