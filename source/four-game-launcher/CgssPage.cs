using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

// CGSS runs the packaged local replay server through the shared launcher Python.
// The page owns process/UI state; the original CGSS-DMM.exe is only a thin wrapper.
internal sealed class CgssPage : ResourcePage {
    readonly string root; readonly bool renderMode;
    readonly RoundBtn launch,find,choose,more,stop,download,copyLog,clearLog;
    readonly List<RoundBtn> actions=new List<RoundBtn>(); readonly List<Font> fonts=new List<Font>();
    readonly ToolTip hints=new ToolTip{InitialDelay=450,ReshowDelay=100,AutoPopDelay=3500};
    readonly TextBox path,log; Font heading,body,small,latin,buttonFont;
    readonly System.Windows.Forms.Timer poll;
    float density=1,logicalWidth=1100,logicalHeight=760,offset,textWidth,logTop;
    int dpi=96; bool disposed,loaded,running,starting,busyDownload; string gameDir="",status="请选择灰姑娘女孩的游戏目录"; Color statusColor;
    Process server,downloadJob; string lastServerLine="",serverDiagnosis,downloadDiagnosis;
    internal event Action SurfaceChanged;
    internal string StatusText{get{return status;}}
    internal CgssPage(string root,bool renderMode):base(6){
        this.root=root;this.renderMode=renderMode;
        AccessibleName="灰姑娘女孩离线启动页";
        Hero=new StagePass(7);Hero.PreviewTime=2.4;Hero.HoldPose(Hero.CapturePose());Hero.LeftSide=false;Hero.AccessibleName="灰姑娘女孩通行证，点击翻面";Controls.Add(Hero);
        path=new TextBox{ReadOnly=true,BorderStyle=BorderStyle.None,Text="选择 CGSS-DMM.exe 所在目录",AccessibleName="灰姑娘女孩游戏目录"};Controls.Add(path);
        log=new ScrollableLogTextBox{Text="启动、停止、资源下载和卡片仓库的进度会显示在这里。"};log.AccessibleName="灰姑娘女孩操作记录";Controls.Add(log);
        launch=Button("开始游戏",delegate{Launch();});launch.DirectionalArrow=true;
        find=Button("自动寻找",delegate{FindDefault();});choose=Button("选择",delegate{ChooseDirectory();});
        more=Button("更多  ⋯",delegate{ShowTools();});stop=Button("结束运行",delegate{StopAll();});
        download=Button("下载全部资源",delegate{DownloadAll();});
        copyLog=new LogActionButton(false);copyLog.Click+=delegate{if(log.TextLength==0)return;try{Clipboard.SetText(log.Text);}catch(System.Runtime.InteropServices.ExternalException ex){Append("无法复制日志："+ex.Message);}};
        clearLog=new LogActionButton(true);clearLog.Click+=delegate{log.Clear();};
        foreach(RoundBtn tool in new[]{copyLog,clearLog}){actions.Add(tool);Controls.Add(tool);hints.SetToolTip(tool,tool.AccessibleName);}
        path.TextChanged+=delegate{Changed();};log.TextChanged+=delegate{Changed();};log.MouseUp+=delegate{Changed();};log.MouseWheel+=delegate{Changed();};log.KeyUp+=delegate{Changed();};
        poll=new System.Windows.Forms.Timer{Interval=1500};poll.Tick+=delegate{Poll();};
        stop.Visible=false;download.Visible=true;
        ApplyTheme();
    }
    RoundBtn Button(string caption,EventHandler click){RoundBtn button=new RoundBtn{Text=caption};button.Click+=click;actions.Add(button);Controls.Add(button);return button;}
    Font FontFor(string face,float size,bool bold,bool scaled){float pixels=size*(scaled?density:1);foreach(Font f in fonts)if(f.Name==face&&Math.Abs(f.Size-pixels)<.001&&f.Bold==bold)return f;Font created=new Font(face,pixels,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);fonts.Add(created);return created;}
    void Put(Control control,float x,float y,float w,float h){control.SetBounds((int)Math.Round((x+offset)*density),(int)Math.Round(y*density),Math.Max(1,(int)Math.Round(w*density)),Math.Max(1,(int)Math.Round(h*density)));RoundBtn button=control as RoundBtn;if(button!=null){button.Density=density;button.Font=buttonFont;}}
    protected override void OnHandleCreated(EventArgs e){
        base.OnHandleCreated(e);if(loaded||renderMode)return;loaded=true;
        try{if(File.Exists(Path.Combine(root,"cgss-path.txt")))gameDir=File.ReadAllText(Path.Combine(root,"cgss-path.txt"),Encoding.UTF8).Trim();}catch{}
        if(gameDir.Length==0){string built=Path.Combine(root,"CGSS");if(Valid(built))gameDir=built;}
        path.Text=gameDir.Length>0?gameDir:"选择 CGSS-DMM.exe 所在目录";poll.Start();UpdateControls();Changed();
    }
    internal override void ApplyTheme(){
        BackColor=UI.Bg;statusColor=OutcomeColor(statusColor);if(Hero!=null){Hero.BackColor=UI.Bg;Hero.Rebuild();}
        foreach(RoundBtn button in actions){button.BackColor=UI.Bg;button.ForeColor=UI.Text;button.Fill=UI.Card;button.FillHover=UI.GhostH;button.Stroke=UI.Border;button.HasStroke=false;button.Radius=UI.Corner;}
        if(launch!=null){launch.Fill=UI.IsWabi&&!UI.IsDark?UI.C("BD9B69"):UI.Accent;launch.FillHover=UI.IsWabi&&!UI.IsDark?UI.C("C7A574"):UI.AccentH;launch.HasStroke=false;launch.ForeColor=UI.IsWabi&&!UI.IsDark?UI.C("1B304C"):UI.IsDark?UI.Bg:UI.C("FFF5DC");}
        if(path!=null){path.BackColor=UI.Card;log.BackColor=UI.Blend(UI.Bg,UI.Card,.62f);path.ForeColor=UI.Text;log.ForeColor=UI.Dim;}
        LayoutPage();UpdateControls();Changed();
    }
    static Color OutcomeColor(Color color){return color.IsEmpty?UI.Dim:color;}
    internal override void Advance(double dt){Hero.Advance();foreach(RoundBtn button in actions)button.Advance(dt);}
    internal override void UpdateDpi(int value){dpi=value;LayoutPage();}
    protected override void OnResize(EventArgs e){base.OnResize(e);if(Hero!=null&&launch!=null)LayoutPage();}
    void LayoutPage(){
        if(Width<1||Height<1||launch==null)return;float device=dpi/96f;density=device*Math.Min(1,Math.Min(Width/(960*device),Height/(680*device)));
        logicalWidth=Math.Min(1400,Width/density);logicalHeight=Height/density;offset=(Width/density-logicalWidth)/2;HomeLayout home=new HomeLayout(logicalWidth,logicalHeight);textWidth=home.LeftWidth;
        heading=FontFor(UI.TitleFace,(UI.IsDark?41.333f:44)*home.Compression,true,false);body=FontFor(UI.BodyFace,13,false,false);small=FontFor(UI.BodyFace,11,false,false);latin=FontFor(UI.UprightLatin,11,true,false);buttonFont=FontFor(UI.BodyFace,12.666f,false,true);
        RectangleF art=home.Ticket;Put(Hero,art.X,art.Y,art.Width,art.Height);
        RectangleF pathBox=home.PathBox;float col=(logicalWidth-96)/12;
        if(UI.IsWabi){Put(path,60,pathBox.Y+12,3*col-24,home.H(22));Put(find,48+3*col,pathBox.Y,col,pathBox.Height);Put(choose,48+4*col,pathBox.Y,col,pathBox.Height);}
        else{Put(path,60,pathBox.Y+12,textWidth-208,home.H(22));Put(find,pathBox.Right-188,pathBox.Y,92,pathBox.Height);Put(choose,pathBox.Right-88,pathBox.Y,88,pathBox.Height);}
        path.Font=FontFor(UI.BodyFace,12,false,true);
        RectangleF play=home.Play;Put(launch,play.X,play.Y,play.Width,play.Height);launch.Font=FontFor(UI.BodyFace,22.666f*home.Compression,true,true);
        Put(download,48,home.Tools.Y,UI.IsWabi?2*col:214,home.Tools.Height);download.Text="下载全部资源";
        Put(more,UI.IsWabi?48+2*col:274,home.Tools.Y,UI.IsWabi?col:116,home.Tools.Height);more.Text="更多  ⋯";
        Put(stop,UI.IsWabi?48+3*col:402,home.Tools.Y,UI.IsWabi?2*col:86,home.Tools.Height);
        logTop=home.Divider+55;RectangleF logBox=home.Log;Put(log,logBox.X,logBox.Y,logBox.Width,logBox.Height);log.Font=FontFor(UI.BodyFace,11.333f,false,true);RectangleF copy=home.CopyLog,clear=home.ClearLog;Put(copyLog,copy.X,copy.Y,copy.Width,copy.Height);Put(clearLog,clear.X,clear.Y,clear.Width,clear.Height);
        PruneFonts();Changed();
    }
    void PruneFonts(){for(int i=fonts.Count-1;i>=0;i--){Font f=fonts[i];bool used=ReferenceEquals(f,heading)||ReferenceEquals(f,body)||ReferenceEquals(f,small)||ReferenceEquals(f,latin)||ReferenceEquals(f,buttonFont)||ReferenceEquals(f,path.Font)||ReferenceEquals(f,log.Font);foreach(RoundBtn button in actions)used|=ReferenceEquals(f,button.Font);if(!used){fonts.RemoveAt(i);f.Dispose();}}}
    static bool Valid(string dir){
        if(string.IsNullOrWhiteSpace(dir)||!Directory.Exists(dir))return false;
        return File.Exists(Path.Combine(dir,"CGSS-DMM.exe"))&&File.Exists(Path.Combine(dir,"game","imascgstage.exe"))&&File.Exists(Path.Combine(dir,"dmm","launcher","play_release.py"))&&File.Exists(Path.Combine(dir,"vps","data","master.mdb"));
    }
    string Python(){string shared=Path.Combine(root,"python","python.exe");if(File.Exists(shared))return shared;string own=Path.Combine(gameDir,"python","python.exe");return File.Exists(own)?own:null;}
    string Script(string relative){return Path.Combine(gameDir,relative);}
    void SavePath(string dir){try{File.WriteAllText(Path.Combine(root,"cgss-path.txt"),dir,new UTF8Encoding(false));}catch(Exception ex){Append("无法保存路径："+ex.Message);}}
    void FindDefault(){
        if(busyDownload)return;
        string[] candidates={Path.Combine(root,"CGSS"),Path.Combine(root,"CGSS-DMM"),Path.Combine(root,"cgss"),Path.Combine(root,"..","CGSS-DMM")};
        foreach(string candidate in candidates){string full=Path.GetFullPath(candidate);if(Valid(full)){gameDir=full;path.Text=full;SavePath(full);status="已找到游戏目录";statusColor=UI.Ok;Append("[路径] 已识别："+full);UpdateControls();Changed();return;}}
        status="没有找到完整目录，请点「选择」指定";statusColor=UI.Dim;Append("[路径] 未找到 CGSS-DMM.exe、game、dmm、vps 完整的目录。");Changed();
    }
    void ChooseDirectory(){
        if(busyDownload)return;
        using(FolderBrowserDialog dialog=new FolderBrowserDialog()){
            dialog.Description="选择包含 CGSS-DMM.exe 的目录（也就是游戏根目录）";dialog.ShowNewFolderButton=false;
            if(gameDir.Length>0&&Directory.Exists(gameDir))dialog.SelectedPath=gameDir;
            if(dialog.ShowDialog(FindForm())!=DialogResult.OK)return;
            string selected=dialog.SelectedPath;
            if(!Valid(selected)){status="所选目录不是完整的 CGSS-DMM 目录";statusColor=UI.Bad;Append("[路径] 需要包含 CGSS-DMM.exe、game\\imascgstage.exe、dmm\\launcher\\play_release.py、vps\\data\\master.mdb。");Changed();return;}
            gameDir=selected;path.Text=selected;SavePath(selected);status="手动选择已保存";statusColor=UI.Ok;UpdateControls();Changed();
        }
    }
    void Launch(){
        if(renderMode){status="演示模式：不会启动真实游戏";statusColor=UI.Dim;Append("[演示] 启动请求已忽略。");Changed();return;}
        if(busyDownload)return;
        if(!Valid(gameDir)){status="游戏目录不完整，请先选择";statusColor=UI.Bad;Changed();ChooseDirectory();return;}
        string python=Python();if(python==null){status="找不到 Python 运行时";statusColor=UI.Bad;Changed();return;}
        if(Process.GetProcessesByName("imascgstage").Length>0){status="游戏已经在运行";statusColor=UI.Dim;Changed();return;}
        string script=Script(Path.Combine("dmm","launcher","play_release.py"));
        try{
            serverDiagnosis=null;lastServerLine="";
            server=StartPython(python,script,gameDir,true);
            starting=true;status="正在启动本地服务与游戏…";statusColor=UI.Dim;Append("[启动] 正在运行 play_release.py");UpdateControls();Changed();
        }catch(Exception ex){starting=false;status=LauncherErrors.FailedToStart("cgss",ex);statusColor=UI.Bad;Append(status);Changed();}
    }
    Process StartPython(string python,string script,string working,bool track){
        ProcessStartInfo start=new ProcessStartInfo(python,"-u \""+script+"\"");
        start.WorkingDirectory=working;start.UseShellExecute=false;start.CreateNoWindow=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
        start.StandardOutputEncoding=new UTF8Encoding(false);start.StandardErrorEncoding=new UTF8Encoding(false);
        start.EnvironmentVariables.Remove("PYTHONHOME");start.EnvironmentVariables.Remove("PYTHONPATH");start.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";start.EnvironmentVariables["PYTHONUTF8"]="1";
        Process process=new Process{StartInfo=start,EnableRaisingEvents=true};
        DataReceivedEventHandler output=delegate(object s,DataReceivedEventArgs e){
            if(string.IsNullOrWhiteSpace(e.Data))return;
            Post(delegate{
                string hint=LauncherErrors.Diagnostic(e.Data);
                if(track){lastServerLine=e.Data;if(hint!=null)serverDiagnosis=hint;}
                else if(hint!=null)downloadDiagnosis=hint;
                Append(hint==null?e.Data:hint+" [诊断] "+e.Data.Trim());
            });
        };
        process.OutputDataReceived+=output;process.ErrorDataReceived+=output;
        process.Exited+=delegate{Post(delegate{if(track)ServerExited(process);});};
        process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
        return process;
    }
    void ServerExited(Process process){
        if(!ReferenceEquals(process,server))return;
        int code=0;try{code=process.ExitCode;}catch{}
        server.Dispose();server=null;starting=false;
        if(Process.GetProcessesByName("imascgstage").Length>0){status="游戏运行中";statusColor=UI.Ok;}
        else if(code==0){status="启动进程已结束，若游戏未出现请查看记录";statusColor=UI.Dim;}
        else{status=LauncherErrors.Finish("cgss",code,serverDiagnosis);statusColor=UI.Bad;Append(status);if(lastServerLine.Length>0)Append("[诊断] "+lastServerLine);}
        UpdateControls();Changed();
    }
    void DownloadExited(Process process){
        if(!ReferenceEquals(process,downloadJob))return;
        int code=0;try{code=process.ExitCode;}catch{}
        busyDownload=false;downloadJob.Dispose();downloadJob=null;
        status=code==0?"资源下载完成":LauncherErrors.Finish("cgss-resources",code,downloadDiagnosis);statusColor=code==0?UI.Ok:UI.Bad;
        Append("[资源] "+status);UpdateControls();Changed();
    }
    void Poll(){
        if(disposed)return;
        bool exists=Process.GetProcessesByName("imascgstage").Length>0;
        if(exists!=running){running=exists;if(running){starting=false;status="游戏运行中";statusColor=UI.Ok;}else if(!starting&&server==null){status=gameDir.Length>0?"准备就绪":"请选择灰姑娘女孩的游戏目录";statusColor=UI.Dim;}UpdateControls();Changed();}
    }
    void StopAll(){
        if(busyDownload)return;
        string script=Script("Stop.ps1");if(!File.Exists(script)){status="找不到 Stop.ps1";statusColor=UI.Bad;Changed();return;}
        try{
            ProcessStartInfo start=new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy Bypass -File \""+script+"\" -StopGame");
            start.WorkingDirectory=gameDir;start.UseShellExecute=false;start.CreateNoWindow=true;
            using(Process stop=Process.Start(start)){stop.WaitForExit(15000);}
            status="已请求停止本地服务与游戏";statusColor=UI.Dim;Append("[停止] 已发送 Stop.ps1 -StopGame");
        }catch(Exception ex){status="停止失败："+ex.Message;statusColor=UI.Bad;Append("[停止] "+ex.Message);}
        UpdateControls();Changed();
    }
    void DownloadAll(){
        if(renderMode){status="演示模式：不会下载真实资源";Changed();return;}
        if(busyDownload)return;
        if(!Valid(gameDir)){status="游戏目录不完整，请先选择";statusColor=UI.Bad;Changed();return;}
        if(Process.GetProcessesByName("imascgstage").Length>0){status="请先结束游戏再下载资源";statusColor=UI.Dim;Changed();return;}
        if(MessageBox.Show("会从官方 CDN 把全部资源下载到游戏目录，可能占用几十 GB 且耗时较长。中断后重跑会跳过已完成文件。继续？","下载全部资源",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
        string python=Python();if(python==null){status="找不到 Python 运行时";statusColor=UI.Bad;Changed();return;}
        try{
            busyDownload=true;downloadDiagnosis=null;downloadJob=StartPython(python,Script("download_resources.py"),gameDir,false);
            downloadJob.EnableRaisingEvents=true;downloadJob.Exited+=delegate{Post(delegate{DownloadExited(downloadJob);});};
            status="正在下载全部资源…";statusColor=UI.Dim;Append("[资源] 已开始 download_resources.py");UpdateControls();Changed();
        }catch(Exception ex){busyDownload=false;status="下载启动失败："+ex.Message;statusColor=UI.Bad;Append("[资源] "+ex.Message);Changed();}
    }
    void OpenCardTool(){
        if(!Valid(gameDir)){status="游戏目录不完整";statusColor=UI.Bad;Changed();return;}
        string python=Python();if(python==null){status="找不到 Python 运行时";statusColor=UI.Bad;Changed();return;}
        try{
            ProcessStartInfo start=new ProcessStartInfo(python,"-u \""+Script(Path.Combine("dmm","launcher","open_cardtool.py"))+"\"");
            start.WorkingDirectory=gameDir;start.UseShellExecute=false;start.CreateNoWindow=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
            start.StandardOutputEncoding=new UTF8Encoding(false);start.StandardErrorEncoding=new UTF8Encoding(false);
            Process process=Process.Start(start);
            process.OutputDataReceived+=delegate(object s,DataReceivedEventArgs e){if(!string.IsNullOrWhiteSpace(e.Data))Post(delegate{Append(e.Data);});};
            process.ErrorDataReceived+=delegate(object s,DataReceivedEventArgs e){if(!string.IsNullOrWhiteSpace(e.Data))Post(delegate{Append(e.Data);});};
            process.BeginOutputReadLine();process.BeginErrorReadLine();
            status="正在打开卡片仓库（需要本地服务已启动）";statusColor=UI.Dim;Append("[卡片] 已请求打开卡片仓库");
        }catch(Exception ex){status="打开卡片仓库失败："+ex.Message;statusColor=UI.Bad;Append("[卡片] "+ex.Message);}
        Changed();
    }
    void OpenConfig(){
        if(!Valid(gameDir))return;
        string config=Path.Combine(gameDir,"config.txt");
        try{
            if(!File.Exists(config)){string defaults=Path.Combine(gameDir,"config.default.txt");if(File.Exists(defaults))File.Copy(defaults,config);}
            if(File.Exists(config))Process.Start(new ProcessStartInfo(config){UseShellExecute=true});else status="找不到 config.txt";
        }catch(Exception ex){status="打开配置失败："+ex.Message;statusColor=UI.Bad;}
        Changed();
    }
    void OpenFolder(){if(Valid(gameDir))try{Process.Start(new ProcessStartInfo(gameDir){UseShellExecute=true});}catch(Exception ex){Append(ex.Message);}}
    void OpenReadme(){string readme=Path.Combine(gameDir,"README.txt");if(File.Exists(readme))try{Process.Start(new ProcessStartInfo(readme){UseShellExecute=true});}catch(Exception ex){Append(ex.Message);}}
    void ShowTools(){
        ContextMenuStrip menu=new ContextMenuStrip();menu.ShowImageMargin=false;menu.Font=UI.PixelFont(10,false,density);menu.BackColor=UI.Card;menu.ForeColor=UI.Text;menu.Renderer=new ThemeMenuRenderer();if(!renderMode)MenuMotion.Attach(menu);
        ToolStripMenuItem item=new ToolStripMenuItem("结束运行 / 停止服务"){Enabled=running||server!=null};item.Click+=delegate{StopAll();};menu.Items.Add(item);
        item=new ToolStripMenuItem("下载全部资源"){Enabled=!busyDownload};item.Click+=delegate{DownloadAll();};menu.Items.Add(item);
        item=new ToolStripMenuItem("打开卡片仓库"){Enabled=Valid(gameDir)};item.Click+=delegate{OpenCardTool();};menu.Items.Add(item);
        menu.Items.Add(new ToolStripSeparator());
        item=new ToolStripMenuItem("打开游戏文件夹"){Enabled=Valid(gameDir)};item.Click+=delegate{OpenFolder();};menu.Items.Add(item);
        item=new ToolStripMenuItem("使用说明"){Enabled=Valid(gameDir)};item.Click+=delegate{OpenReadme();};menu.Items.Add(item);
        item=new ToolStripMenuItem("打开配置 config.txt"){Enabled=Valid(gameDir)};item.Click+=delegate{OpenConfig();};menu.Items.Add(item);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BrightnessMenu(menu));
        foreach(ToolStripItem row in menu.Items)row.ForeColor=row.Enabled?UI.Text:UI.DotOff;
        menu.Show(more,new Point(0,more.Height));
    }
    ToolStripMenuItem BrightnessMenu(ContextMenuStrip menu){
        ToolStripMenuItem branch=new QuickMenuItem("明暗模式");
        string[] modes={"system","light","dark"},names={"跟随系统","日间模式","暗金夜间"};
        for(int i=0;i<modes.Length;i++){string mode=modes[i];ToolStripMenuItem item=new ToolStripMenuItem(names[i]){Tag=mode};item.Click+=delegate{RaiseAppearance(mode);};branch.DropDownItems.Add(item);}
        branch.DropDown.Renderer=new ThemeMenuRenderer();branch.DropDown.Font=menu.Font;
        ((ToolStripDropDownMenu)branch.DropDown).ShowImageMargin=false;((ToolStripDropDownMenu)branch.DropDown).ShowCheckMargin=true;
        return branch;
    }
    Action<string> appearance;
    internal void SetAppearanceHandler(Action<string> handler){appearance=handler;}
    void RaiseAppearance(string mode){Action<string> handler=appearance;if(handler!=null)handler(mode);}
    void UpdateControls(){
        if(launch==null)return;
        bool valid=Valid(gameDir),busy=busyDownload||starting;
        launch.Enabled=!busy&&!running&&valid;find.Enabled=!busy;choose.Enabled=!busy;download.Enabled=!busy&&!running;stop.Visible=running||server!=null;stop.Enabled=running||server!=null;more.Enabled=!busyDownload;
        Hero.Busy=starting||running;
        if(starting)launch.Text="正在启动…";else launch.Text="开始游戏";
    }
    void Changed(){if(disposed)return;Invalidate();Action changed=SurfaceChanged;if(changed!=null)changed();}
    void Post(Action action){if(disposed||IsDisposed||Disposing||!IsHandleCreated)return;try{if(InvokeRequired)BeginInvoke(new Action(delegate{if(!disposed&&!IsDisposed)action();}));else action();}catch(InvalidOperationException){}}
    void Append(string message){if(log.TextLength>24000)log.Text=log.Text.Substring(log.TextLength-16000);log.AppendText(Environment.NewLine+DateTime.Now.ToString("HH:mm:ss")+"  "+message);log.SelectionStart=log.TextLength;log.ScrollToCaret();}
    void TextAt(Graphics drawing,string text,Font font,Color color,RectangleF bounds){using(Brush brush=new SolidBrush(color))using(StringFormat format=new StringFormat{Trimming=StringTrimming.EllipsisCharacter})drawing.DrawString(text,font,brush,bounds,format);}
    protected override void OnPaint(PaintEventArgs e){
        Graphics drawing=e.Graphics;drawing.Clear(UI.Bg);if(heading==null)return;drawing.SmoothingMode=SmoothingMode.AntiAlias;drawing.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;drawing.TranslateTransform(offset*density,0);drawing.ScaleTransform(density,density);
        HomeLayout home=new HomeLayout(logicalWidth,logicalHeight);home.Paint(drawing);
        TextAt(drawing,"346 PRODUCTION  /  STARLIGHT STAGE",latin,UI.Dim,new RectangleF(48,174,textWidth,22));
        TextAt(drawing,"灰姑娘的魔法，\n正在开场。",heading,UI.Text,new RectangleF(45,home.Y(206)-3,textWidth+4,home.H(138)));
        TextAt(drawing,status,small,statusColor,new RectangleF(48,home.Y(486),textWidth,26));
        TextAt(drawing,"操作记录",small,UI.Dim,new RectangleF(48,logTop-8,100,22));
        if(busyDownload)using(Brush fill=new SolidBrush(UI.Accent))drawing.FillRectangle(fill,48,home.Y(512),textWidth,2);
    }
    internal override List<string> CheckLayout(){List<string> errors=new List<string>();foreach(Control control in Controls){if(!control.Visible)continue;if(!ClientRectangle.Contains(control.Bounds))errors.Add("outside "+control.Text);if(control is RoundBtn){Size size=TextRenderer.MeasureText(control.Text,control.Font,Size.Empty,TextFormatFlags.NoPadding);int extra=control==launch?(int)(60*density):8;if(size.Width+extra>control.Width)errors.Add("text "+control.Text);}}if(log.Top<Hero.Bottom)errors.Add("log overlaps ticket");return errors;}
    protected override void Dispose(bool disposing){
        if(disposing&&!disposed){disposed=true;hints.Dispose();SurfaceChanged=null;
            if(poll!=null){poll.Stop();poll.Dispose();}
            if(downloadJob!=null){try{if(!downloadJob.HasExited)downloadJob.Kill();}catch{}downloadJob.Dispose();downloadJob=null;}
            if(server!=null){server.Dispose();server=null;}
        }
        base.Dispose(disposing);
    }
}
