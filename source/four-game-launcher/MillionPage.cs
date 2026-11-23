using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

// MLTD-specific entry; the other game pages retain their existing implementations.
internal sealed class MillionPage : ResourcePage {
    readonly List<RoundBtn> actions=new List<RoundBtn>();readonly List<Font> fonts=new List<Font>();
    readonly TextBox path,log;readonly RoundBtn launch,stop,find,choose,update,more,copy,clear;
    readonly MillionResourceService service=new MillionResourceService();string adapter;Form owner;ContextMenuStrip toolsMenu;MillionDesktopLaunchDialog launchDialog;
    readonly System.Diagnostics.Stopwatch launchEntrance=new System.Diagnostics.Stopwatch();
    Font heading,small,latin,buttonFont;float density=1,logicalWidth,logicalHeight,offset,textWidth,logTop;int dpi=96;bool disposed,stoppingService;
    string status="选择离线适配包，检查和补装所需资源";
    internal event Action SurfaceChanged;
    internal MillionPage():base(7){
        AccessibleName="百万现场离线启动页";Hero=new StagePass(12);Hero.PreviewTime=2.4;Hero.HoldPose(Hero.CapturePose());Hero.AccessibleName="百万现场通行证，点击旋转";Controls.Add(Hero);
        path=new TextBox{ReadOnly=true,BorderStyle=BorderStyle.None,Text="请选择离线适配包目录",AccessibleName="百万现场离线适配包目录"};Controls.Add(path);
        log=new ScrollableLogTextBox{Text="选择本地适配包。首次指定 MLTD 实例和档案，之后可沿用上次选择启动。",AccessibleName="百万现场操作记录"};Controls.Add(log);
        launch=Button("本地连接",true);launch.DirectionalArrow=true;stop=Button("停止服务",false);stop.AccessibleName="停止 MLTD 本地服务并关闭所选游戏";find=Button("自动寻找",false);choose=Button("选择",false);update=Button("更新本地内容",false);more=Button("更多  ⋯",false);
        copy=new LogActionButton(false);clear=new LogActionButton(true);foreach(RoundBtn b in new[]{copy,clear})AddButton(b,false);
        launch.Enabled=false;adapter=MillionResourceService.FindAdapter(MainForm.RootOverride??Path.GetDirectoryName(Application.ExecutablePath));if(adapter.Length>0){path.Text=adapter;status="本地适配包已选择";}UpdateLaunchEnabled();ApplyTheme();
    }
    RoundBtn Button(string caption,bool primary){RoundBtn b=new RoundBtn{Text=caption};AddButton(b,primary);return b;}
    void AddButton(RoundBtn b,bool primary){
        b.Click+=delegate{
            if((service.Busy||stoppingService)&&b!=copy&&b!=clear){Notice("本地工具任务仍在运行，请等待完成。");return;}
            if(b==launch){OpenLaunch();Changed();return;}
            if(b==stop){StopService();return;}
            if(launchDialog!=null&&!launchDialog.IsDisposed&&(b==choose||b==find||b==update||b==more)){Notice("请先返回 MLTD 首页，再更改配置或部署资源。");return;}
            if(b==choose){using(FolderBrowserDialog d=new FolderBrowserDialog{Description="选择已完整解压的 MLTD 离线适配包"})if(d.ShowDialog(FindForm())==DialogResult.OK)SelectAdapter(d.SelectedPath);}
            else if(b==find){string found=MillionResourceService.FindAdapter(MainForm.RootOverride??Path.GetDirectoryName(Application.ExecutablePath));if(found.Length>0)SelectAdapter(found);else Notice("未找到离线适配包，请选择完整解压后的目录。");}
            else if(b==update)UpdateContent();
            else if(b==more){
                // Closed runs inside item dispatch. Keep one menu until the next
                // open or page disposal so the current click can finish safely.
                if(toolsMenu!=null)toolsMenu.Dispose();toolsMenu=new ContextMenuStrip();
                toolsMenu.Items.Add("恢复上次内容更新",null,delegate{RollbackContent();});toolsMenu.Items.Add("本地资源检查与补装",null,delegate{OpenResources(false);});toolsMenu.Items.Add("检查最近缺失资源",null,delegate{OpenResources(true);});toolsMenu.Show(more,new Point(0,more.Height));
            }
            else if(b==copy){if(log.TextLength>0)Clipboard.SetText(log.Text);}
            else if(b==clear)log.Clear();Changed();
        };actions.Add(b);Controls.Add(b);
    }
    internal void SelectAdapter(string directory){try{MillionResourceService.SaveAdapter(directory);adapter=Path.GetFullPath(directory);path.Text=adapter;UpdateLaunchEnabled();Notice("已选择 MLTD 离线适配包。");}catch(Exception ex){Notice(ex.Message);}}
    void UpdateLaunchEnabled(){bool valid=MillionResourceService.IsAdapter(adapter);stop.Enabled=valid&&!stoppingService;launch.Enabled=valid&&!stoppingService&&File.Exists(Path.Combine(adapter,"src","desktop-session-v2.cjs"));}
    async void StopService(){
        if(stoppingService||service.Busy)return;
        if(!MillionResourceService.IsAdapter(adapter)){Notice("请先选择完整适配包。");return;}
        stoppingService=true;UpdateLaunchEnabled();Notice("正在停止 MLTD 本地服务，并关闭所选模拟器中的 MLTD 游戏；模拟器保持开启。");
        try{
            MillionResourceReply reply=await service.RunAsync(adapter,"--stop-desktop-service");
            if(reply.ExitCode!=0||reply.Data==null){Notice("停止服务未完成："+reply.ExplainFailure());return;}
            string result=MillionResourceService.Text(reply.Data,"status");
            Notice(result=="owned-game-and-service-stopped"?"MLTD 本地服务已停止，所选游戏已关闭。":result=="no-managed-service"?"没有本启动器可停止的 MLTD 托管服务。":"未收到停止服务确认，请保留现场记录。");
        }catch(Exception ex){Notice("停止服务未完成："+ex.Message);}finally{stoppingService=false;UpdateLaunchEnabled();Changed();}
    }
    void OpenLaunch(){if(launchDialog!=null&&!launchDialog.IsDisposed)return;if(service.Busy){Notice("本地工具任务仍在运行，请等待完成。");return;}if(!launch.Enabled){Notice("请选择包含一键启动组件的新版适配包。");return;}launchDialog=new MillionDesktopLaunchDialog(service,adapter,Notice);launchDialog.TopLevel=false;launchDialog.FormBorderStyle=FormBorderStyle.None;launchDialog.ShowInTaskbar=false;launchDialog.BackRequested+=CloseLaunch;foreach(Control c in Controls)c.Visible=ReferenceEquals(c,Hero);Controls.Add(launchDialog);launchEntrance.Restart();LayoutLaunchPanel();launchDialog.Show();launchDialog.BringToFront();Changed();}
    void CloseLaunch(){if(launchDialog==null||launchDialog.SessionActive)return;MillionDesktopLaunchDialog old=launchDialog;launchDialog=null;launchEntrance.Stop();Controls.Remove(old);old.Dispose();foreach(Control c in Controls)c.Visible=true;LayoutPage();UpdateLaunchEnabled();Changed();}
    void Notice(string message){if(disposed)return;status=message;log.AppendText(Environment.NewLine+message);Changed();}
    async void UpdateContent(){
        if(service.Busy){Notice("更新任务仍在运行，请等待完成。");return;}
        if(!MillionResourceService.IsAdapter(adapter)){Notice("请先选择完整适配包。");return;}
        update.Enabled=false;launch.Enabled=false;
        Notice("正在通过所选游戏的现有身份同步正式目录并下载兼容的新内容。保持联网和模拟器开启；详细进度写入适配包 runtime/content-updates。校验完成前保留当前内容。");
        try{MillionResourceReply reply=await service.RunAsync(adapter,"--update-local-content");
            if(reply.ExitCode!=0||reply.Data==null){Notice("更新未完成："+reply.ExplainFailure());return;}
            string result=MillionResourceService.Text(reply.Data,"status");
            if(result=="full-content-update-selected"){
                string backup=MillionResourceService.Text(reply.Data,"backup");
                File.WriteAllText(Path.Combine(adapter,"state","content-updates","launcher-last-backup.txt"),backup,new System.Text.UTF8Encoding(false));
                Notice("更新已安装，资源版本 "+MillionResourceService.Text(reply.Data,"assetVersion")+"；新增卡片 "+MillionResourceService.Text(reply.Data,"newCards")+"，衣装 "+MillionResourceService.Text(reply.Data,"newCostumes")+"；选曲总数 "+MillionResourceService.Text(reply.Data,"songCount")+"。歌曲、剧情目录和标题/剧场配置已同步。请关闭本地连接后重新开启，使新内容生效。");
            }else Notice("当前支持版本的完整目录未发现变化。");
        }catch(Exception ex){Notice("更新失败："+ex.Message);}finally{update.Enabled=true;UpdateLaunchEnabled();}
    }
    async void RollbackContent(){
        if(service.Busy){Notice("请等待当前任务完成。");return;}
        try{string file=Path.Combine(adapter,"state","content-updates","launcher-last-backup.txt");if(!File.Exists(file)){Notice("没有由此启动器记录的更新回滚点。");return;}
            MillionResourceReply reply=await service.RunAsync(adapter,"--rollback-content-update",File.ReadAllText(file,System.Text.Encoding.UTF8).Trim());
            Notice(reply.ExitCode==0?"内容已恢复，请重新开启本地连接。":"恢复失败："+reply.ExplainFailure());
        }catch(Exception ex){Notice(ex.Message);}
    }
    void OpenResources(bool diagnostic){if(!MillionResourceService.IsAdapter(adapter)){Notice("请先选择完整的 MLTD 离线适配包。");return;}using(MillionResourcesDialog d=new MillionResourcesDialog(service,adapter,Notice,diagnostic))d.ShowDialog(FindForm());}
    internal override bool CanNavigate{get{return !stoppingService&&!service.Busy&&(launchDialog==null||launchDialog.IsDisposed||!launchDialog.SessionActive);}}
    internal override bool IncludeHeroInSnapshot{get{return true;}}
    internal override bool HandleNavigationKey(Keys key){if(launchDialog!=null){if(key==Keys.Escape){launchDialog.RequestBack();return true;}return key==Keys.Left||key==Keys.Right;}return false;}
    void LayoutLaunchPanel(){if(launchDialog==null)return;float shift=(float)(12*(1-MotionProfile.Coast(launchEntrance.Elapsed.TotalSeconds/.24)));Put(launchDialog,48+shift,174,textWidth,logicalHeight-194);launchDialog.ApplyTheme(density);}
    protected override void OnParentChanged(EventArgs e){base.OnParentChanged(e);if(owner!=null)owner.FormClosing-=OwnerClosing;owner=FindForm();if(owner!=null)owner.FormClosing+=OwnerClosing;}
    void OwnerClosing(object sender,FormClosingEventArgs e){if(!CanNavigate){e.Cancel=true;Notice("MLTD 本地任务仍在运行，请在页内启动面板保留游戏并返回、结束本次服务，或等待任务完成。");}}
    Font F(string face,float size,bool bold,bool scaled){float px=size*(scaled?density:1);foreach(Font f in fonts)if(f.Name==face&&Math.Abs(f.Size-px)<.001&&f.Bold==bold)return f;Font n=new Font(face,px,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);fonts.Add(n);return n;}
    void Put(Control c,float x,float y,float w,float h){c.SetBounds((int)Math.Round((x+offset)*density),(int)Math.Round(y*density),Math.Max(1,(int)Math.Round(w*density)),Math.Max(1,(int)Math.Round(h*density)));RoundBtn b=c as RoundBtn;if(b!=null){b.Density=density;b.Font=buttonFont;}}
    internal override void ApplyTheme(){
        BackColor=UI.Bg;if(Hero!=null){Hero.BackColor=UI.Bg;Hero.Rebuild();}
        foreach(RoundBtn b in actions){b.BackColor=UI.Bg;b.ForeColor=UI.Text;b.Fill=UI.Card;b.FillHover=UI.GhostH;b.HasStroke=false;b.Radius=UI.Corner;}
        if(launch!=null){launch.Fill=UI.Accent;launch.FillHover=UI.AccentH;launch.ForeColor=UI.IsDark?UI.Bg:UI.C("FFF9ED");}
        if(path!=null){path.BackColor=UI.Card;log.BackColor=UI.Blend(UI.Bg,UI.Card,.62f);path.ForeColor=UI.Text;log.ForeColor=UI.Dim;}LayoutPage();Changed();
    }
    internal override void UpdateDpi(int value){dpi=value;LayoutPage();}
    protected override void OnResize(EventArgs e){base.OnResize(e);if(Hero!=null&&launch!=null)LayoutPage();}
    void LayoutPage(){
        if(Width<1||Height<1||launch==null)return;float d=dpi/96f;density=d*Math.Min(1,Math.Min(Width/(960*d),Height/(680*d)));logicalWidth=Math.Min(1400,Width/density);logicalHeight=Height/density;offset=(Width/density-logicalWidth)/2;
        HomeLayout home=new HomeLayout(logicalWidth,logicalHeight);textWidth=home.LeftWidth;heading=F(UI.TitleFace,44*home.Compression,true,false);small=F(UI.BodyFace,11,false,false);latin=F(UI.LatinFace,11,true,false);buttonFont=F(UI.BodyFace,12.666f,false,true);
        RectangleF art=home.Ticket;Put(Hero,art.X,art.Y,art.Width,art.Height);RectangleF p=home.PathBox;Put(path,60,p.Y+12,textWidth-208,home.H(22));path.Font=F(UI.BodyFace,12,false,true);Put(find,p.Right-188,p.Y,92,p.Height);Put(choose,p.Right-88,p.Y,88,p.Height);
        RectangleF play=home.Play;Put(launch,play.X,play.Y,play.Width-124,play.Height);Put(stop,play.Right-112,play.Y,112,play.Height);launch.Font=F(UI.BodyFace,22.666f*home.Compression,true,true);
        float moreWidth=92,gap=12,updateWidth=textWidth-moreWidth-gap;
        Put(update,48,home.Tools.Y,updateWidth,home.Tools.Height);Put(more,48+updateWidth+gap,home.Tools.Y,moreWidth,home.Tools.Height);
        logTop=home.Divider+55;RectangleF l=home.Log;Put(log,l.X,l.Y,l.Width,l.Height);log.Font=F(UI.BodyFace,11.333f,false,true);RectangleF cp=home.CopyLog,cl=home.ClearLog;Put(copy,cp.X,cp.Y,cp.Width,cp.Height);Put(clear,cl.X,cl.Y,cl.Width,cl.Height);if(launchDialog!=null)LayoutLaunchPanel();PruneFonts();Changed();
    }
    void PruneFonts(){for(int i=fonts.Count-1;i>=0;i--){Font f=fonts[i];bool used=ReferenceEquals(f,heading)||ReferenceEquals(f,small)||ReferenceEquals(f,latin)||ReferenceEquals(f,buttonFont)||ReferenceEquals(f,path.Font)||ReferenceEquals(f,log.Font);foreach(RoundBtn b in actions)used|=ReferenceEquals(f,b.Font);if(!used){fonts.RemoveAt(i);f.Dispose();}}}
    internal override void Advance(double dt){if(launchDialog!=null){Hero.Advance();launchDialog.Advance(dt);if(launchEntrance.IsRunning&&launchEntrance.Elapsed.TotalSeconds<.26)LayoutLaunchPanel();}else{Hero.Advance();foreach(RoundBtn b in actions)b.Advance(dt);}}
    void Changed(){if(disposed)return;Invalidate();Action a=SurfaceChanged;if(a!=null)a();}
    void TextAt(Graphics g,string text,Font font,Color color,RectangleF rect){using(Brush b=new SolidBrush(color))using(StringFormat f=new StringFormat{Trimming=StringTrimming.EllipsisCharacter})g.DrawString(text,font,b,rect,f);}
    protected override void OnPaint(PaintEventArgs e){
        Graphics g=e.Graphics;g.Clear(UI.Bg);if(heading==null||launchDialog!=null)return;g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;g.TranslateTransform(offset*density,0);g.ScaleTransform(density,density);
        HomeLayout home=new HomeLayout(logicalWidth,logicalHeight);home.Paint(g);
        TextAt(g,"765 THEATER / MILLION LIVE!",latin,UI.Accent,new RectangleF(48,174,textWidth,22));TextAt(g,"每一份心动，\n都有舞台。",heading,UI.Text,new RectangleF(45,home.Y(206)-3,textWidth+4,home.H(138)));
        TextAt(g,status,small,UI.Dim,new RectangleF(48,home.Y(486),textWidth,26));TextAt(g,"操作记录",small,UI.Dim,new RectangleF(48,logTop-8,100,22));
    }
    internal override List<string> CheckLayout(){List<string> errors=new List<string>();foreach(Control c in Controls){if(!c.Visible)continue;if(!ClientRectangle.Contains(c.Bounds))errors.Add("outside "+c.Text);if(c is RoundBtn){Size measured=TextRenderer.MeasureText(c.Text,c.Font,Size.Empty,TextFormatFlags.NoPadding);int extra=c==launch?(int)(60*density):8;if(measured.Width+extra>c.Width)errors.Add("text "+c.Text);}}if(launchDialog!=null){if(!Hero.Visible||launchDialog.Bounds.IntersectsWith(Hero.Bounds))errors.Add("launch panel hides right artwork");errors.AddRange(launchDialog.CheckLayout());}else if(log.Top<Hero.Bottom)errors.Add("log overlaps ticket");return errors;}
    protected override void Dispose(bool disposing){if(disposing&&!disposed){disposed=true;if(owner!=null)owner.FormClosing-=OwnerClosing;if(toolsMenu!=null){toolsMenu.Dispose();toolsMenu=null;}SurfaceChanged=null;}base.Dispose(disposing);if(disposing){foreach(Font f in fonts)f.Dispose();fonts.Clear();}}
}
