using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

internal sealed class ThemedScaleTrackBar:TrackBar {
    internal ThemedScaleTrackBar(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    protected override void OnPaint(PaintEventArgs e){
        Graphics drawing=e.Graphics;drawing.Clear(BackColor);drawing.SmoothingMode=SmoothingMode.AntiAlias;float scale=Height/30f,margin=13*scale,middle=Height/2f,width=Math.Max(1,Width-margin*2),amount=(Value-Minimum)/(float)Math.Max(1,Maximum-Minimum),position=margin+amount*width;
        using(Pen track=new Pen(UI.Border,4*scale)){track.StartCap=track.EndCap=LineCap.Round;drawing.DrawLine(track,margin,middle,Width-margin,middle);}
        using(Pen selected=new Pen(Enabled?UI.Accent:UI.DotOff,4*scale)){selected.StartCap=selected.EndCap=LineCap.Round;drawing.DrawLine(selected,margin,middle,position,middle);}
        using(Brush thumb=new SolidBrush(Enabled?UI.Accent:UI.DotOff))drawing.FillEllipse(thumb,position-6*scale,middle-6*scale,12*scale,12*scale);
        if(Focused)using(Pen ring=new Pen(UI.Text,1*scale))drawing.DrawEllipse(ring,position-9*scale,middle-9*scale,18*scale,18*scale);
    }
    protected override void OnValueChanged(EventArgs e){base.OnValueChanged(e);Invalidate();}
    protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);Invalidate();}
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
}

internal sealed class ToggleRoundBtn:RoundBtn {
    bool selected;
    internal bool Checked {get{return selected;}set{if(selected==value)return;selected=value;Invalidate();AccessibilityNotifyClients(AccessibleEvents.StateChange,-1);}}
    internal ToggleRoundBtn(){AccessibleRole=AccessibleRole.CheckButton;}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);float d=Density,w=25*d,h=13*d,x=Width-39*d,y=(Height-h)/2;Graphics drawing=e.Graphics;drawing.SmoothingMode=SmoothingMode.AntiAlias;using(GraphicsPath track=UI.Round(new RectangleF(x,y,w,h),h/2))using(Brush fill=new SolidBrush(Checked&&Enabled?UI.Accent:UI.Border))drawing.FillPath(fill,track);using(Brush thumb=new SolidBrush(Checked&&Enabled?(UI.IsDark?UI.Bg:UI.C("FFF5DC")):UI.Dim))drawing.FillEllipse(thumb,x+(Checked?14:2)*d,y+2*d,9*d,9*d);}
    // Reserve the switch column so the caption never runs into the track.
    protected override void PaintLabel(Graphics drawing,Rectangle bounds,Color color){
        bounds.Width=Math.Max(1,bounds.Width-(int)(44*Density));
        TextRenderer.DrawText(drawing,Text,Font,bounds,color,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
    }
    protected override AccessibleObject CreateAccessibilityInstance(){return new ToggleAccess(this);}
    sealed class ToggleAccess:Control.ControlAccessibleObject {readonly ToggleRoundBtn owner;internal ToggleAccess(ToggleRoundBtn owner):base(owner){this.owner=owner;}public override AccessibleStates State{get{return base.State|(owner.Checked?AccessibleStates.Checked:AccessibleStates.None);}}}
}

// The page owns controls and the ticket; ScspService owns process/JSONL/cancellation work.
internal sealed class ScspPage : ResourcePage {
    readonly ScspService service; readonly string root; readonly bool renderMode;
    readonly RoundBtn launch,choose,story,sync,update,more,stop,resetScale,copyLog,clearLog;
    readonly ToggleRoundBtn modelToggle;
    readonly List<RoundBtn> actions=new List<RoundBtn>(); readonly List<Font> fonts=new List<Font>();
    readonly ToolTip hints=new ToolTip{InitialDelay=450,ReshowDelay=100,AutoPopDelay=3500};
    readonly TextBox path,log;readonly TrackBar modelScale; Font heading,body,small,latin,buttonFont;
    float density=1,logicalWidth=1100,logicalHeight=760,offset,textWidth,logTop;
    int dpi=96,progress=-1; bool disposed,loaded,themedOnce,demo,running,cancelling,storyKnown,storyEnabled,needsRecovery,needsUpdate,legacyMigrationRequired;
    bool pendingStoryCheck,pendingStoryTarget,storyCheckSucceeded,modelArmed,modelChanging,modelTarget;
    string storyLabel="尚未检查剧情服装";
    string status="请选择闪耀色彩的游戏目录",gameDir="",operation="",outcome="idle",inspectedFor=""; Color statusColor;
    ContextMenuStrip toolsMenu;readonly HoverDisclosure menuHover;
    RoundBtn findDirectory;bool locating;System.Threading.CancellationTokenSource directorySearch;
    internal event Action SurfaceChanged;
    internal bool DemoMode {get{return demo;}}
    internal string StatusText {get{return status;}}
    internal RoundBtn[] CommonActions {get{return new RoundBtn[]{launch,stop,story,sync,update,modelToggle};}}
    internal ScspPage(string root,bool renderMode):base(4){
        this.root=root;this.renderMode=renderMode;demo=renderMode;
        AccessibleName="闪耀色彩离线启动与工具着陆页";
        service=new ScspService(root);service.Notice+=OnNotice;
        Hero=new StagePass(5);Hero.PreviewTime=2.4;Hero.HoldPose(Hero.CapturePose());Hero.LeftSide=false;Hero.AccessibleName="闪耀色彩通行证，点击翻面";Controls.Add(Hero);
        path=new TextBox{ReadOnly=true,BorderStyle=BorderStyle.None,Text="选择 imasscprism.exe 所在目录",TabStop=true};path.AccessibleName="闪耀色彩游戏目录";Controls.Add(path);
        log=new ScrollableLogTextBox{Text="离线运行与工具的进度会显示在这里。"};log.AccessibleName="闪耀色彩操作记录";Controls.Add(log);
        launch=Button("开始游戏",delegate{Run("launch_offline");});launch.DirectionalArrow=true;
        choose=Button("选择",delegate{ChooseDirectory();});findDirectory=Button("自动寻找",delegate{FindDirectory(null);});
        story=Button("安装剧情服装",delegate{ToggleStory();});sync=Button("同步本机资源",delegate{Run("sync_resources");});update=Button("更新本地内容",delegate{Run("update");});
        more=Button("更多  ⋯",delegate{ShowTools();});stop=Button("结束运行",delegate{service.Command("stop",gameDir,null,demo);});
        modelToggle=new ToggleRoundBtn{Text="Q版小人"};modelToggle.Click+=delegate{ToggleModel();};actions.Add(modelToggle);Controls.Add(modelToggle);
        modelScale=new ThemedScaleTrackBar{Minimum=25,Maximum=200,Value=100,SmallChange=5,LargeChange=25,TickStyle=TickStyle.None,AutoSize=false};modelScale.AccessibleName="Q版模型比例百分比";modelScale.ValueChanged+=delegate{Changed();};modelScale.MouseUp+=delegate{ApplyScale();};modelScale.KeyUp+=delegate{ApplyScale();};Controls.Add(modelScale);
        resetScale=Button("恢复100%",delegate{modelScale.Value=100;ApplyScale();});copyLog=new LogActionButton(false);copyLog.Click+=delegate{if(log.TextLength==0)return;try{Clipboard.SetText(log.Text);}catch(System.Runtime.InteropServices.ExternalException ex){Append("无法复制日志："+ex.Message);}};clearLog=new LogActionButton(true);clearLog.Click+=delegate{log.Clear();};
        foreach(RoundBtn tool in new[]{copyLog,clearLog}){actions.Add(tool);Controls.Add(tool);hints.SetToolTip(tool,tool.AccessibleName);}
        path.TextChanged+=delegate{Changed();};log.TextChanged+=delegate{Changed();};log.MouseUp+=delegate{Changed();};log.MouseWheel+=delegate{Changed();};log.KeyUp+=delegate{Changed();};
        menuHover=new HoverDisclosure(more,ShowTools,delegate{return toolsMenu;});story.Visible=sync.Visible=false;ApplyTheme();
    }
    RoundBtn Button(string caption,EventHandler click){RoundBtn button=new RoundBtn{Text=caption};button.Click+=click;actions.Add(button);Controls.Add(button);return button;}
    Font FontFor(string face,float size,bool bold,bool scaled){float pixels=size*(scaled?density:1);foreach(Font f in fonts)if(f.Name==face&&Math.Abs(f.Size-pixels)<.001&&f.Bold==bold)return f;Font created=new Font(face,pixels,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);fonts.Add(created);return created;}
    void Put(Control control,float x,float y,float w,float h){control.SetBounds((int)Math.Round((x+offset)*density),(int)Math.Round(y*density),Math.Max(1,(int)Math.Round(w*density)),Math.Max(1,(int)Math.Round(h*density)));RoundBtn button=control as RoundBtn;if(button!=null){button.Density=density;button.Font=buttonFont;}}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);if(loaded||renderMode)return;loaded=true;service.LoadPath(delegate(string saved){Post(delegate{if(gameDir.Length==0&&saved.Length>0){gameDir=saved;path.Text=gameDir;status="已载入游戏目录，正在核对运行环境…";UpdateControls();Changed();InspectEnvironment();}});});}
    // Quietly re-check the selected install so the legacy entry only appears when it applies.
    void InspectEnvironment(){if(demo||service.Busy||running||locating||gameDir.Length==0||inspectedFor==gameDir)return;inspectedFor=gameDir;service.Run("inspect",gameDir,false);}
    internal override void ApplyTheme(){
        BackColor=UI.Bg;statusColor=outcome=="failed"?UI.Bad:outcome=="succeeded"?UI.Ok:UI.Dim;if(Hero!=null){Hero.BackColor=UI.Bg;if(themedOnce)Hero.Rebuild();themedOnce=true;}
        foreach(RoundBtn button in actions){button.BackColor=UI.Bg;button.ForeColor=UI.Text;button.Fill=UI.Card;button.FillHover=UI.GhostH;button.Stroke=UI.Border;button.HasStroke=false;button.Radius=UI.Corner;}
        if(launch!=null){launch.Fill=UI.Accent;launch.FillHover=UI.AccentH;launch.HasStroke=false;launch.ForeColor=UI.IsDark?UI.Bg:UI.C("FFF5DC");}
        if(path!=null){path.BackColor=UI.Card;log.BackColor=UI.Blend(UI.Bg,UI.Card,.62f);path.ForeColor=UI.Text;log.ForeColor=UI.Dim;}if(modelScale!=null)modelScale.BackColor=UI.Bg;
        LayoutPage();UpdateControls();Changed();
    }
    internal override void Advance(double dt){Hero.Advance();foreach(RoundBtn button in actions)button.Advance(dt);}
    internal override void UpdateDpi(int value){dpi=value;LayoutPage();}
    protected override void OnResize(EventArgs e){base.OnResize(e);if(Hero!=null&&launch!=null)LayoutPage();}
    void LayoutPage(){
        if(Width<1||Height<1||launch==null)return;float device=dpi/96f;density=device*Math.Min(1,Math.Min(Width/(960*device),Height/(680*device)));
        logicalWidth=Math.Min(1400,Width/density);logicalHeight=Height/density;offset=(Width/density-logicalWidth)/2;HomeLayout home=new HomeLayout(logicalWidth,logicalHeight);textWidth=home.LeftWidth;
        heading=FontFor(UI.TitleFace,(UI.IsDark?41.333f:44)*home.Compression,true,false);body=FontFor(UI.BodyFace,13,false,false);small=FontFor(UI.BodyFace,11,false,false);latin=FontFor(UI.LatinFace,11,true,false);buttonFont=FontFor(UI.BodyFace,12.666f,false,true);
        RectangleF art=home.Ticket;Put(Hero,art.X,art.Y,art.Width,art.Height);
        RectangleF pathBox=home.PathBox;Put(path,60,pathBox.Y+12,textWidth-208,home.H(22));Put(findDirectory,pathBox.Right-188,pathBox.Y,92,pathBox.Height);path.Font=FontFor(UI.BodyFace,12,false,true);Put(choose,pathBox.Right-88,pathBox.Y,88,pathBox.Height);
        RectangleF play=home.Play;Put(launch,play.X,play.Y,play.Width,play.Height);launch.Font=FontFor(UI.BodyFace,22.666f*home.Compression,true,true);
        Put(update,48,home.Tools.Y,214,home.Tools.Height);update.Text="更新本地内容";Put(more,274,home.Tools.Y,116,home.Tools.Height);more.Text="更多  ⋯";
        Put(stop,402,home.Tools.Y,86,home.Tools.Height);
        Put(modelToggle,48,home.Divider+9,114,34);Put(modelScale,170,home.Divider+11,230,30);Put(resetScale,414,home.Divider+9,98,34);resetScale.Font=FontFor(UI.BodyFace,11,false,true);
        logTop=home.Divider+55;RectangleF logBox=home.Log;Put(log,logBox.X,logBox.Y,logBox.Width,logBox.Height);log.Font=FontFor(UI.BodyFace,11.333f,false,true);RectangleF copy=home.CopyLog,clear=home.ClearLog;Put(copyLog,copy.X,copy.Y,copy.Width,copy.Height);Put(clearLog,clear.X,clear.Y,clear.Width,clear.Height);
        PruneFonts();Changed();
    }
    void PruneFonts(){for(int i=fonts.Count-1;i>=0;i--){Font f=fonts[i];bool used=ReferenceEquals(f,heading)||ReferenceEquals(f,body)||ReferenceEquals(f,small)||ReferenceEquals(f,latin)||ReferenceEquals(f,buttonFont)||ReferenceEquals(f,path.Font)||ReferenceEquals(f,log.Font);foreach(RoundBtn button in actions)used|=ReferenceEquals(f,button.Font);if(!used){fonts.RemoveAt(i);f.Dispose();}}}
    void ChooseDirectory(){
        if(service.Busy||running||locating)return;
        using(FolderBrowserDialog dialog=new FolderBrowserDialog()){
            dialog.Description="选择包含 imasscprism.exe 的 SONGforPRISM 文件夹，也可选择它的直接上级目录";dialog.ShowNewFolderButton=false;
            if(gameDir.Length>0&&Directory.Exists(gameDir))dialog.SelectedPath=gameDir;
            if(dialog.ShowDialog(FindForm())!=DialogResult.OK)return;string selected=dialog.SelectedPath;locating=true;UpdateControls();Changed();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate{
                string valid,error;bool ok=GameDirectoryFinder.TryValidate(GameDirectoryKind.Shiny,selected,out valid,out error);
                Post(delegate{locating=false;if(ok){gameDir=valid;path.Text=valid;service.SavePath(valid);status="手动选择已保存";statusColor=UI.Ok;UpdateControls();Changed();InspectEnvironment();}else{status=error;statusColor=UI.Bad;UpdateControls();Changed();}});
            });
        }
    }
    void FindDirectory(string pendingAction){
        if(service.Busy||running||locating)return;locating=true;outcome="idle";status="正在寻找 SONGforPRISM 游戏目录…";statusColor=UI.Dim;
        directorySearch=new System.Threading.CancellationTokenSource();System.Threading.CancellationTokenSource source=directorySearch;UpdateControls();Changed();
        GameDirectoryRequest request=new GameDirectoryRequest{Game=GameDirectoryKind.Shiny,SavedPath=gameDir,PackageRoot=root,UseSystemSources=!renderMode};
        System.Threading.ThreadPool.QueueUserWorkItem(delegate{
            GameDirectoryResult result=GameDirectoryFinder.Find(request,source.Token,delegate(string message){Post(delegate{status=message;Changed();});});
            Post(delegate{directorySearch=null;source.Dispose();locating=false;
                if(result.UniquePath!=null){gameDir=result.UniquePath;path.Text=gameDir;service.SavePath(gameDir);status="已找到并保存游戏目录";statusColor=UI.Ok;Append(status+"："+gameDir);}
                else{status=result.Cancelled?"搜索已取消，原路径未改动":result.Matches.Count>1?"找到多份有效安装，请点选择指定目录":result.TimedOut?"搜索时间已到，请点选择指定目录":"没有找到完整游戏，请点选择指定目录";statusColor=UI.Dim;Append(status);foreach(GameDirectoryMatch match in result.Matches)Append(match.Path+"（"+match.Source+"）");}
                UpdateControls();Changed();if(result.UniquePath!=null){if(pendingAction!=null)Run(pendingAction);else InspectEnvironment();}
            });
        });
    }

    void Run(string action){
        if(service.Busy||locating)return;if(!demo){string valid,error;if(!GameDirectoryFinder.TryValidate(GameDirectoryKind.Shiny,gameDir,out valid,out error)){FindDirectory(action);return;}}
        operation=action;running=false;cancelling=false;progress=-1;if(action=="launch_offline"){modelArmed=false;modelChanging=false;}outcome="idle";status=demo?"正在演示功能状态…":"正在"+ActionName(action)+"…";statusColor=UI.Dim;
        Append((demo?"[演示] ":"")+ActionName(action));bool started=service.Run(action,gameDir,demo);if(!started&&status.StartsWith("正在"))status="操作未开始，请查看日志。";UpdateControls();Changed();
        if(action=="migrate_legacy")Append("迁移会通过旧版正规事务卸载剧情服装，迁移私人存档并准备外置环境；冲突存档将拒绝覆盖。完成后可在更多功能重新安装剧情服装。");
    }
    void ToggleStory(){
        if(service.Busy||running)return;if(needsRecovery||needsUpdate){status=needsRecovery?"剧情服装需要修复，请在更多中恢复资源":"剧情服装需要更新，请在更多中更新";outcome="idle";statusColor=UI.Dim;Changed();ShowTools();return;}
        if(!storyKnown){pendingStoryTarget=true;pendingStoryCheck=true;storyCheckSucceeded=false;Run("story_status");return;}Run(storyEnabled?"story_uninstall":"story_install");
    }
    void CompleteStoryCheck(){
        if(!pendingStoryCheck)return;pendingStoryCheck=false;if(!storyCheckSucceeded||!storyKnown){if(storyCheckSucceeded){status="未收到剧情服装状态，请在更多中重新检查";statusColor=UI.Bad;Changed();}return;}
        if(needsRecovery||needsUpdate){status=needsRecovery?"剧情服装需要修复，请在更多中恢复资源":"剧情服装需要更新，请在更多中更新";statusColor=UI.Dim;Changed();return;}
        if(storyEnabled!=pendingStoryTarget)Run(pendingStoryTarget?"story_install":"story_uninstall");else{status=storyEnabled?"剧情服装已安装":"剧情服装已卸载";statusColor=UI.Ok;Changed();}
    }
    void ToggleModel(){if(!modelToggle.Enabled||modelChanging)return;modelTarget=!modelArmed;modelChanging=true;status=modelTarget?"正在启用 Q版小人…":"正在恢复普通模型…";statusColor=UI.Dim;UpdateControls();Changed();service.CommandSequence(modelTarget?new[]{"mv_connect","mv_chibi"}:new[]{"mv_normal"},gameDir,demo);}
    void CancelCurrentJob(){if(locating&&directorySearch!=null){directorySearch.Cancel();status="正在取消目录搜索…";Changed();return;}if(!service.Busy||cancelling)return;cancelling=true;pendingStoryCheck=false;service.Cancel();UpdateControls();Changed();}
    static string ActionName(string action){switch(action){case "inspect":return "核对游戏环境";case "prepare":return "准备独立离线环境";case "sync_resources":return "同步本机资源";case "launch_offline":return "启动闪耀色彩";case "update":return "更新本地内容";case "migrate_legacy":return "迁移旧版剧情与存档";case "story_status":return "检查剧情服装";case "story_install":return "启用剧情服装";case "story_uninstall":return "还原剧情服装";case "story_update":return "更新剧情服装";case "story_recover":return "恢复剧情服装资源";default:return action;}}
    void OnNotice(ScspNotice notice){Post(delegate{
        if(notice.Event=="idle"){if(notice.Operation=="story_status"&&!service.Busy)CompleteStoryCheck();UpdateControls();Changed();return;}
        object legacy;if(notice.Data!=null&&notice.Data.TryGetValue("legacy_migration_required",out legacy))legacyMigrationRequired=legacy!=null&&Convert.ToBoolean(legacy);
        ApplyEngineState(notice.Data);
        if(notice.Event=="result"&&notice.Status=="failed"&&notice.Operation!=null&&notice.Operation.StartsWith("mv_")){modelChanging=false;status=notice.Message;statusColor=UI.Bad;outcome="failed";}
        if(!string.IsNullOrEmpty(notice.Operation)&&notice.Operation!=operation){if(!string.IsNullOrEmpty(notice.Message))Append(notice.Message);UpdateControls();Changed();return;}
        if(notice.Event=="game_started"){running=true;outcome="succeeded";status=demo?"[演示] 游戏运行中":"游戏运行中，可在更多功能里连接 MV 工具";statusColor=UI.Ok;Hero.Spin();}
        else if(notice.Event=="progress"){if(notice.Message.Length>0)status=notice.Message;if(notice.Progress>=0)progress=Math.Min(100,notice.Progress);if(notice.Status=="cancelling")cancelling=true;}
        else if(notice.Event=="result"){cancelling=false;running=false;modelChanging=false;if(operation=="launch_offline")modelArmed=false;outcome=notice.Status;status=notice.Message.Length>0?notice.Message:notice.Status=="succeeded"?"操作完成":notice.Status=="cancelled"?"操作已取消":"操作失败";statusColor=notice.Status=="succeeded"?UI.Ok:notice.Status=="cancelled"?UI.Dim:UI.Bad;if(notice.Status=="succeeded")progress=100;if(operation=="story_status")storyCheckSucceeded=notice.Status=="succeeded";}
        if(notice.Event=="result"&&operation=="inspect"&&legacyMigrationRequired){status="检测到旧版剧情绑定，可在更多中迁移旧版剧情与存档";statusColor=UI.Dim;outcome="idle";}
        if(notice.Event=="result"&&operation=="inspect")ApplyInspectionSummary(notice.Data);
        if(notice.Event=="result"&&operation=="migrate_legacy"&&notice.Status=="succeeded")legacyMigrationRequired=false;
        if(notice.Message.Length>0)Append(notice.Message);UpdateControls();Changed();
    });}
    void Post(Action action){if(disposed||IsDisposed||Disposing||!IsHandleCreated)return;try{if(InvokeRequired)BeginInvoke(new Action(delegate{if(!disposed&&!IsDisposed)action();}));else action();}catch(InvalidOperationException){}}
    void Append(string message){if(log.TextLength>24000)log.Text=log.Text.Substring(log.TextLength-16000);log.AppendText(Environment.NewLine+DateTime.Now.ToString("HH:mm:ss")+"  "+message);log.SelectionStart=log.TextLength;log.ScrollToCaret();}
    void UpdateControls(){if(launch==null)return;stop.Visible=running;bool busy=service.Busy||running||locating;launch.Enabled=choose.Enabled=findDirectory.Enabled=sync.Enabled=update.Enabled=!busy;story.Text=storyEnabled?"卸载剧情服装":"安装剧情服装";story.Enabled=!busy&&!needsRecovery&&!needsUpdate;story.AccessibleDescription=needsRecovery?"需要从更多功能恢复剧情服装资源":needsUpdate?"需要从更多功能更新剧情服装":story.Text;stop.Enabled=running&&!cancelling;more.Enabled=true;if(modelToggle!=null){modelToggle.Checked=modelArmed;modelToggle.Enabled=(running||demo)&&!cancelling&&!modelChanging;modelToggle.AccessibleDescription=modelChanging?"正在切换":modelArmed?"已开启，点击恢复普通模型":"已关闭，点击启用 Q版小人";}if(modelScale!=null){modelScale.Enabled=resetScale.Enabled=(running||demo)&&!cancelling;}Hero.Busy=busy;}
    void ShowTools(){
        if(toolsMenu!=null)toolsMenu.Close();ContextMenuStrip menu=new ContextMenuStrip();toolsMenu=menu;menuHover.Watch(menu);menu.ShowImageMargin=false;menu.Font=UI.PixelFont(10,false,density);menu.BackColor=UI.Card;menu.ForeColor=UI.Text;menu.Renderer=new ThemeMenuRenderer();if(!renderMode)MenuMotion.Attach(menu);
        bool busy=service.Busy||locating;
        AddTool(menu,"取消当前操作",delegate{CancelCurrentJob();},busy&&!cancelling);
        AddTool(menu,"重新连接 MV 工具",delegate{Command("mv_connect",null);},(running||demo)&&!cancelling);AddTool(menu,"检查 MV 状态",delegate{Command("mv_status",null);},(running||demo)&&!cancelling);
        AddTool(menu,story.Text,delegate{ToggleStory();},story.Enabled);AddTool(menu,"同步本机资源",delegate{Run("sync_resources");},!service.Busy&&!running&&!locating);
        menu.Items.Add(new ToolStripSeparator());AddTool(menu,storyLabel,delegate{},false);
        AddTool(menu,"恢复剧情服装资源",delegate{Run("story_recover");},!service.Busy&&!running&&needsRecovery);
        AddTool(menu,"更新剧情服装",delegate{Run("story_update");},!service.Busy&&!running&&needsUpdate);
        AddTool(menu,"重新检查剧情服装",delegate{Run("story_status");},!service.Busy);
        menu.Items.Add(new ToolStripSeparator());
        if(legacyMigrationRequired){
            ToolStripMenuItem migrate=AddTool(menu,"迁移旧版剧情与存档",delegate{Run("migrate_legacy");},!busy&&!running);
            migrate.ToolTipText="按正规事务卸载旧版剧情服装、迁移私人存档并准备外置目录；冲突存档不会覆盖。不是切换官方账号。";
        }
        foreach(ToolStripItem item in menu.Items)item.ForeColor=item.Enabled?UI.Text:UI.DotOff;
        menu.Closed+=delegate{
            if(toolsMenu==menu)toolsMenu=null;
            // ToolStrip still finishes OnItemClicked after Closed; dispose on the next UI turn.
            if(!disposed&&IsHandleCreated)try{BeginInvoke(new Action(delegate{Font owned=menu.Font;menu.Dispose();owned.Dispose();}));}catch(InvalidOperationException){}
        };if(renderMode)menu.Opacity=0;menu.Show(more,new Point(0,more.Height));
    }
    static ToolStripMenuItem AddTool(ToolStripDropDown menu,string text,Action action,bool enabled){ToolStripMenuItem item=new ToolStripMenuItem(text){Enabled=enabled};item.Click+=delegate{action();};menu.Items.Add(item);return item;}
    void Command(string command,string value){Append("发送工具指令："+command+(value==null?"":" "+value));service.Command(command,gameDir,value,demo);Changed();}
    static bool Flag(Dictionary<string,object> values,string key){object value;return values.TryGetValue(key,out value)&&value!=null&&Convert.ToBoolean(value);}
    void ApplyInspectionSummary(Dictionary<string,object> data){
        if(data==null)return;object value;
        if(data.TryGetValue("engine_present",out value)&&!Convert.ToBoolean(value)){status="SCSP 运行组件缺失，请重新解压完整分享包";statusColor=UI.Bad;outcome="failed";return;}
        if(data.TryGetValue("package_present",out value)&&!Convert.ToBoolean(value)){status="SCSP 本地服务包缺失，请重新解压完整分享包";statusColor=UI.Bad;outcome="failed";return;}
        if(data.TryGetValue("game_complete",out value)&&!Convert.ToBoolean(value)){status="所选游戏安装不完整，请先补齐游戏文件";statusColor=UI.Bad;outcome="failed";object absent;if(data.TryGetValue("missing_files",out absent)){System.Collections.IEnumerable rows=absent as System.Collections.IEnumerable;if(rows!=null){List<string> names=new List<string>();foreach(object name in rows)names.Add(Convert.ToString(name));if(names.Count>0)Append("所选游戏目录缺少相对文件："+Environment.NewLine+"- "+string.Join(Environment.NewLine+"- ",names.ToArray()));}}return;}
        if(legacyMigrationRequired)return;
        if(data.TryGetValue("resource_cache_present",out value)&&!Convert.ToBoolean(value)){status="尚未找到本机资源缓存，请先完成游戏资源下载";statusColor=UI.Dim;outcome="idle";return;}
        if(data.TryGetValue("game_complete",out value)&&Convert.ToBoolean(value)){status="已识别本地游戏与资源，可准备环境或离线启动";statusColor=UI.Ok;}
    }
    void ApplyEngineState(Dictionary<string,object> data){
        if(data==null)return;object rawKind,rawValue;if(!data.TryGetValue("event",out rawKind)||!data.TryGetValue("value",out rawValue))return;Dictionary<string,object> values=rawValue as Dictionary<string,object>;if(values==null)return;string kind=Convert.ToString(rawKind);
        if(kind=="story_state"){storyKnown=true;storyEnabled=Flag(values,"Enabled");needsRecovery=Flag(values,"NeedsRecovery");needsUpdate=Flag(values,"NeedsUpdate");object label;storyLabel=values.TryGetValue("Label",out label)?Convert.ToString(label):storyEnabled?"剧情服装已启用":"剧情服装未启用";}
        if(kind=="mv_state"){
            modelArmed=Flag(values,"Armed");object modelError;if(modelArmed==modelTarget||values.TryGetValue("Error",out modelError)&&!string.IsNullOrEmpty(Convert.ToString(modelError)))modelChanging=false;
            object error,rawScale;if(values.TryGetValue("Error",out error)&&!string.IsNullOrEmpty(Convert.ToString(error))){status="MV 工具："+Convert.ToString(error);statusColor=UI.Bad;}
            else if(!Flag(values,"Armed")&&Flag(values,"Active")){status="已选择普通模型，退出当前 MV 后生效";statusColor=UI.Dim;}
            else{status=Flag(values,"Armed")?"Q 版模型已启用，可进入 MV 查看":"普通模型已启用";statusColor=UI.Ok;}
            if(values.TryGetValue("Scale",out rawScale)&&rawScale!=null)modelScale.Value=Math.Max(25,Math.Min(200,(int)Math.Round(Convert.ToDouble(rawScale)*100)));
        }
    }
    void ApplyScale(){if(modelScale.Enabled)Command("mv_scale",(modelScale.Value/100.0).ToString(System.Globalization.CultureInfo.InvariantCulture));}
    internal void SetDemo(bool value){if(service.Busy)return;demo=value;status=demo?"演示模式：可体验进度、成功、取消与工具状态":"演示已关闭，准备使用本地游戏";progress=-1;Changed();}
    internal void DemonstrateState(string state){if(state=="running"){running=true;status="游戏运行中，可连接 MV 工具";statusColor=UI.Ok;progress=100;}else if(state=="failed"){running=false;status="准备失败：本地资源不完整，请查看操作记录";statusColor=UI.Bad;progress=42;}else if(state=="cancelled"){running=false;status="操作已取消，已完成环境清理";statusColor=UI.Dim;progress=25;}else{running=false;status="准备就绪，可离线启动";statusColor=UI.Ok;progress=100;}UpdateControls();Changed();}
    void Changed(){if(disposed)return;Invalidate();Action changed=SurfaceChanged;if(changed!=null)changed();}
    void TextAt(Graphics drawing,string text,Font font,Color color,RectangleF bounds){using(Brush brush=new SolidBrush(color))using(StringFormat format=new StringFormat{Trimming=StringTrimming.EllipsisCharacter})drawing.DrawString(text,font,brush,bounds,format);}
    protected override void OnPaint(PaintEventArgs e){
        Graphics drawing=e.Graphics;drawing.Clear(UI.Bg);if(heading==null)return;drawing.SmoothingMode=SmoothingMode.AntiAlias;drawing.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;drawing.TranslateTransform(offset*density,0);drawing.ScaleTransform(density,density);
        HomeLayout home=new HomeLayout(logicalWidth,logicalHeight);home.Paint(drawing);
        TextAt(drawing,"SHINY COLORS / SONG FOR PRISM",latin,UI.Dim,new RectangleF(48,174,textWidth,22));
        TextAt(drawing,UI.IsDark?"让这一刻，\n成为序章。":"让每一种光，\n都有自己的舞台。",heading,UI.Text,new RectangleF(45,home.Y(206)-3,textWidth+4,home.H(138)));
        TextAt(drawing,status,small,statusColor,new RectangleF(48,home.Y(486),textWidth,26));
        if(service.Busy&&progress>=0)using(Brush fill=new SolidBrush(UI.Accent))drawing.FillRectangle(fill,48,home.Y(512),textWidth*progress/100,2);
        TextAt(drawing,modelScale.Value+"%",small,UI.Dim,new RectangleF(528,home.Divider+19,80,24));
        TextAt(drawing,"操作记录",small,UI.Dim,new RectangleF(48,logTop-8,100,22));
    }
    internal override List<string> CheckLayout(){List<string> errors=new List<string>();foreach(Control control in Controls){if(!control.Visible)continue;if(!ClientRectangle.Contains(control.Bounds))errors.Add("outside "+control.Text);if(control is RoundBtn){Size size=TextRenderer.MeasureText(control.Text,control.Font,Size.Empty,TextFormatFlags.NoPadding);int extra=control==launch?(int)(60*density):control==modelToggle?(int)(50*density):8;if(size.Width+extra>control.Width)errors.Add("text "+control.Text);}}if(log.Top<Hero.Bottom)errors.Add("log overlaps ticket");return errors;}
    protected override void Dispose(bool disposing){if(disposing&&!disposed){disposed=true;hints.Dispose();menuHover.Dispose();if(directorySearch!=null)directorySearch.Cancel();SurfaceChanged=null;if(toolsMenu!=null){ContextMenuStrip retiring=toolsMenu;toolsMenu=null;retiring.Close();Font owned=retiring.Font;retiring.Dispose();owned.Dispose();}service.Notice-=OnNotice;service.Dispose();}base.Dispose(disposing);if(disposing){foreach(Font font in fonts)font.Dispose();fonts.Clear();}}
}
