using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class MillionDesktopProfile {
    internal string Config,Name,Instance;public override string ToString(){return Name+" · "+Instance;}
}
internal sealed class MillionDesktopTarget {
    internal string Directory,Name;internal long Index;internal bool Running;
    public override string ToString(){return Name+" · 编号 "+Index+" · "+(Running?"运行中":"未启动");}
}
internal sealed class MillionDesktopLaunchDialog:Form {
    readonly MillionResourceService service;readonly MillionDesktopSessionService session=new MillionDesktopSessionService();readonly string adapter;readonly Action<string> notice;
    readonly ComboBox profiles=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill,AccessibleName="启动档案"},targets=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill,AccessibleName="MLTD模拟器实例"};
    readonly RoundBtn start=new RoundBtn{Text="启动所选档案",AutoSize=true},locate=new RoundBtn{Text="查找雷电",AutoSize=true},folder=new RoundBtn{Text="选择雷电目录",AutoSize=true},configure=new RoundBtn{Text="接入所选游戏",AutoSize=true},coreMediaButton=new RoundBtn{Text="选择基础资源包",AutoSize=true},keep=new RoundBtn{Text="保留游戏并返回",AutoSize=true},close=new RoundBtn{Text="返回",AutoSize=true};string coreMedia;
    readonly RoundBtn setup=new RoundBtn{Text="连接设置",AutoSize=true},done=new RoundBtn{Text="完成设置",AutoSize=true};
    readonly RoundBtn adapt=new RoundBtn{Text="更新游戏适配",AutoSize=true};readonly RoundBtn generic=new RoundBtn{Text="选择 MuMu ADB",AutoSize=true};readonly ComboBox endpoints=new ComboBox{DropDownStyle=ComboBoxStyle.DropDown,Dock=DockStyle.Fill,AccessibleName="模拟器 ADB 地址和端口"};bool genericAdb=true,disconnecting;string selectedAdb;
    readonly Label hint=new Label{AutoSize=true};readonly FlowLayoutPanel primary=new FlowLayoutPanel(),discovery=new FlowLayoutPanel(),setupActions=new FlowLayoutPanel(),sessionActions=new FlowLayoutPanel();bool configuring;
    readonly TextBox details=new TextBox{ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,AccessibleName="MLTD启动进度"};bool busy,loading,autoLaunch=true,returnWhenDetached;DateTime connectedAtUtc;
    readonly Label title=new Label{Text="准备登台",AutoSize=true};readonly List<Font> fonts=new List<Font>();TableLayoutPanel layoutGrid;float density=1;internal event Action BackRequested;
    string Preferences{get{return Path.Combine(MillionResourceService.SettingsOverride??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MLTDLauncher"),"desktop-location.json");}}
    internal bool SessionActive{get{return busy||service.Busy||session.Busy;}}
    internal MillionDesktopLaunchDialog(MillionResourceService service,string adapter,Action<string> notice){
        this.service=service;this.adapter=adapter;this.notice=notice;Text="MLTD · 启动";ClientSize=new Size(820,580);AutoScaleMode=AutoScaleMode.None;MaximizeBox=false;DoubleBuffered=true;BackColor=UI.Bg;ForeColor=UI.Text;
        TableLayoutPanel grid=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(0),Margin=new Padding(0),ColumnCount=1,RowCount=7};layoutGrid=grid;for(int i=0;i<6;i++)grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(grid);grid.Controls.Add(title,0,0);grid.Controls.Add(hint,0,1);grid.Controls.Add(profiles,0,2);grid.Controls.Add(targets,0,2);grid.Controls.Add(endpoints,0,2);
        foreach(FlowLayoutPanel flow in new[]{primary,discovery,setupActions,sessionActions}){flow.Dock=DockStyle.Fill;flow.AutoSize=true;flow.AutoSizeMode=AutoSizeMode.GrowAndShrink;}
        primary.Controls.Add(start);primary.Controls.Add(setup);foreach(Button b in new[]{generic,adapt,configure})discovery.Controls.Add(b);setupActions.Controls.Add(coreMediaButton);setupActions.Controls.Add(done);sessionActions.Controls.Add(keep);sessionActions.Controls.Add(close);
        grid.Controls.Add(primary,0,3);grid.Controls.Add(discovery,0,3);grid.Controls.Add(setupActions,0,4);grid.Controls.Add(sessionActions,0,5);grid.Controls.Add(details,0,6);
        foreach(RoundBtn b in Buttons())b.AutoSizeMode=AutoSizeMode.GrowAndShrink;
        profiles.FlatStyle=targets.FlatStyle=endpoints.FlatStyle=FlatStyle.Flat;profiles.DrawMode=targets.DrawMode=endpoints.DrawMode=DrawMode.OwnerDrawFixed;profiles.DrawItem+=DrawChoice;targets.DrawItem+=DrawChoice;endpoints.DrawItem+=DrawChoice;details.BorderStyle=BorderStyle.None;
        profiles.SelectedIndexChanged+=delegate{UpdateEnabled();};targets.SelectedIndexChanged+=delegate{UpdateEnabled();};endpoints.TextChanged+=delegate{UpdateEnabled();};generic.Click+=async delegate{await ChooseGenericAdb();};adapt.Click+=async delegate{await AdaptClient();};start.Click+=async delegate{try{if(session.CanStop){disconnecting=true;session.StopOwnedSession();Append("正在关闭本地连接，结束本次游戏与服务。");UpdateEnabled();}else await StartSelected();}catch(Exception ex){disconnecting=false;Append("连接操作未完成："+ex.Message);UpdateEnabled();}};locate.Click+=async delegate{await Discover(null);};folder.Click+=async delegate{using(FolderBrowserDialog d=new FolderBrowserDialog{Description="选择含 ldconsole.exe 和 adb.exe 的雷电安装目录"})if(d.ShowDialog(this)==DialogResult.OK)await Discover(d.SelectedPath);};configure.Click+=async delegate{await Configure();};
        close.Click+=delegate{RequestBack();};
        keep.Click+=delegate{KeepRunning();};
        setup.Click+=delegate{ShowSetup(true);};done.Click+=delegate{ShowSetup(false);};
        coreMediaButton.Click+=delegate{using(FolderBrowserDialog d=new FolderBrowserDialog{Description="选择已解压的基础资源包目录，其中应包含 objects 文件夹"})if(d.ShowDialog(this)==DialogResult.OK){coreMedia=d.SelectedPath;Append("已选择基础资源包。点击启动时将核对并补齐缺失文件。");}};
        FormClosing+=delegate(object sender,FormClosingEventArgs e){if(SessionActive){e.Cancel=true;if(session.CanDetach)KeepRunning();else if(!returnWhenDetached)Append("任务仍在运行，请等待连接就绪后保留游戏或结束服务。");}};
        Shown+=async delegate{await LoadProfiles();};ShowSetup(false);ApplyTheme(1);UpdateEnabled();
    }
    RoundBtn[] Buttons(){return new[]{start,coreMediaButton,locate,folder,generic,adapt,configure,keep,close,setup,done};}
    void ShowSetup(bool value){configuring=value;title.Text=value?"连接设置":"准备登台";hint.Text=value?"打开 MuMu，再选择 MLTD 所在实例的 ADB。\n接入时保留现有游戏身份。":"启动完成后，请在游戏标题画面手动点击进入。\n首次使用请先打开连接设置。";profiles.Visible=primary.Visible=sessionActions.Visible=!value;targets.Visible=value&&!genericAdb;endpoints.Visible=value&&genericAdb;discovery.Visible=setupActions.Visible=value;configure.Text=genericAdb?"检查并接入":"接入所选游戏";if(value&&genericAdb)hint.Text="先打开 MuMu。选择 ADB 工具，再填写该实例的 ADB 地址。\n在面具中允许 ADB shell 获取 Root；接入时检查版本与隔离能力。";layoutGrid.RowStyles[4].SizeType=value?SizeType.AutoSize:SizeType.Absolute;layoutGrid.RowStyles[4].Height=0;layoutGrid.RowStyles[5].SizeType=value?SizeType.Absolute:SizeType.AutoSize;layoutGrid.RowStyles[5].Height=0;SizeChoiceRows();PerformLayout();}
    Font FontFor(float size,bool bold){foreach(Font f in fonts)if(Math.Abs(f.Size-size)<.001&&f.Bold==bold)return f;Font font=new Font(UI.BodyFace,size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);fonts.Add(font);return font;}
    void DrawChoice(object sender,DrawItemEventArgs e){ComboBox choice=(ComboBox)sender;using(Brush b=new SolidBrush((e.State&DrawItemState.Selected)!=0?UI.GhostH:UI.Card))e.Graphics.FillRectangle(b,e.Bounds);if(e.Index<0||e.Index>=choice.Items.Count)return;Rectangle text=e.Bounds;text.X+=(int)(8*density);text.Width-=(int)(16*density);TextRenderer.DrawText(e.Graphics,choice.Items[e.Index].ToString(),choice.Font,text,choice.Enabled?UI.Text:UI.Dim,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);if((e.State&DrawItemState.Focus)!=0)e.DrawFocusRectangle();}
    internal void ApplyTheme(float scale){density=scale;BackColor=UI.Bg;ForeColor=UI.Text;Font=FontFor(12.666f*scale,false);title.Font=FontFor(30*scale,true);title.Margin=new Padding(0);hint.Margin=new Padding(0,(int)(8*scale),0,(int)(12*scale));foreach(Control c in new Control[]{profiles,targets,endpoints,details}){c.BackColor=UI.Card;c.ForeColor=UI.Text;c.Margin=new Padding(0,(int)(3*scale),0,(int)(3*scale));}foreach(FlowLayoutPanel flow in new[]{primary,discovery,setupActions,sessionActions})flow.Margin=new Padding(0,(int)(5*scale),0,(int)(5*scale));foreach(ComboBox c in new[]{profiles,targets,endpoints}){c.Font=Font;c.ItemHeight=Math.Max(1,(int)Math.Round(24*scale));}SizeChoiceRows();foreach(RoundBtn b in Buttons()){b.Density=scale;b.Font=Font;b.BackColor=UI.Bg;b.ForeColor=UI.Text;b.Fill=UI.Card;b.FillHover=UI.GhostH;b.Radius=UI.Corner;b.HasStroke=false;b.Padding=new Padding((int)(12*scale),(int)(5*scale),(int)(12*scale),(int)(5*scale));b.Margin=new Padding(0,0,(int)(6*scale),0);}start.Fill=UI.Accent;start.FillHover=UI.AccentH;start.ForeColor=UI.IsDark?UI.Bg:UI.C("FFF9ED");PerformLayout();Invalidate();}
    void SizeChoiceRows(){if(layoutGrid==null)return;ComboBox choice=configuring?(genericAdb?endpoints:targets):profiles;RowStyle row=layoutGrid.RowStyles[2];row.SizeType=SizeType.Absolute;row.Height=Math.Max(choice.PreferredHeight,choice.ItemHeight+8)+choice.Margin.Vertical;}
    internal void Advance(double dt){foreach(RoundBtn b in Buttons())b.Advance(dt);}
    internal List<string> CheckLayout(){List<string> errors=new List<string>();CheckBounds(this,errors);if(details.Height<80*density)errors.Add("launch log too short");return errors;}
    static void CheckBounds(Control parent,List<string> errors){foreach(Control c in parent.Controls){if(!c.Visible)continue;if(!parent.ClientRectangle.Contains(c.Bounds))errors.Add("outside "+c.GetType().Name+" / "+c.Text);TableLayoutPanel table=parent as TableLayoutPanel;if(table!=null){int row=table.GetPositionFromControl(c).Row,span=table.GetRowSpan(c),top=0,bottom=0;int[] heights=table.GetRowHeights();for(int i=0;i<heights.Length;i++){if(i<row)top+=heights[i];if(i<row+span)bottom+=heights[i];}if(row<0||c.Top<top||c.Bottom>bottom)errors.Add("table row overlap / "+c.GetType().Name);}ComboBox choice=c as ComboBox;if(choice!=null&&(choice.ItemHeight<choice.Font.Height||choice.Height<choice.Font.Height+4))errors.Add("choice text clipped");CheckBounds(c,errors);}}
    internal void RequestBack(){if(session.CanDetach){KeepRunning();return;}if(SessionActive){Append("任务仍在运行，请等待连接就绪后保留游戏或结束服务。");return;}Action a=BackRequested;if(a!=null)a();else Close();}
    protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing){foreach(Font f in fonts)f.Dispose();fonts.Clear();BackRequested=null;}}
    void Append(string text){if(IsDisposed)return;details.AppendText((details.TextLength==0?"":Environment.NewLine)+text);}
    void UpdateEnabled(){bool free=!busy&&!service.Busy&&!session.Busy;profiles.Enabled=targets.Enabled=endpoints.Enabled=generic.Enabled=locate.Enabled=folder.Enabled=coreMediaButton.Enabled=setup.Enabled=free;start.Enabled=!disconnecting&&(session.CanStop||(free&&profiles.SelectedItem is MillionDesktopProfile));start.Text=session.CanStop?"关闭本地连接":"开启本地连接";configure.Enabled=free&&(genericAdb?File.Exists(selectedAdb)&&endpoints.Text.Trim().Length>0:targets.SelectedItem is MillionDesktopTarget);adapt.Enabled=free&&genericAdb&&File.Exists(selectedAdb)&&endpoints.Text.Trim().Length>0;keep.Enabled=session.CanDetach&&!returnWhenDetached;close.Enabled=free||session.CanStop;close.Text="返回";UseWaitCursor=busy;}
    void KeepRunning(){if(returnWhenDetached||!session.CanDetach)return;try{returnWhenDetached=true;session.Detach();Append("正在保留后台游戏与服务，返回启动器。");UpdateEnabled();}catch(Exception ex){returnWhenDetached=false;Append(ex.Message);UpdateEnabled();}}
    async Task<MillionResourceReply> Command(params string[] args){busy=true;UpdateEnabled();try{return await service.RunAsync(adapter,args);}finally{busy=false;if(!IsDisposed)UpdateEnabled();}}
    async Task LoadProfiles(){
        MillionResourceReply r=await Command("--desktop-overview");if(IsDisposed)return;if(r.ExitCode!=0||r.Data==null){Append("无法读取本地档案："+r.ExplainFailure());return;}loading=true;MillionDesktopProfile selected=null;
        try{profiles.Items.Clear();foreach(Dictionary<string,object> p in MillionResourceService.Rows(r.Data,"profiles"))profiles.Items.Add(new MillionDesktopProfile{Config=MillionResourceService.Text(p,"config"),Name=MillionResourceService.Text(p,"name"),Instance=MillionResourceService.Text(p,"instanceName")});
            if(File.Exists(Preferences)){Dictionary<string,object> prefs=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Preferences,Encoding.UTF8));if(MillionResourceService.Text(prefs,"adapter")==Path.GetFullPath(adapter)){coreMedia=MillionResourceService.Text(prefs,"coreMedia");foreach(MillionDesktopProfile p in profiles.Items)if(p.Config==MillionResourceService.Text(prefs,"config")){profiles.SelectedItem=p;selected=p;break;}}}
        }catch(Exception ex){Append("上次启动选择未恢复："+ex.Message);}finally{loading=false;UpdateEnabled();}
        if(selected!=null&&autoLaunch){autoLaunch=false;await StartSelected();}else Append(profiles.Items.Count==0?"尚未配置本地档案。请打开 MuMu，在连接设置中选择 ADB 并填写实例端口。":"请选择要启动的本地档案。不会自动选择其他模拟器实例。");
    }
    async Task Discover(string directory){
        genericAdb=false;ShowSetup(true);
        MillionResourceReply r=await Command(directory==null?new[]{"--discover"}:new[]{"--discover",directory});if(IsDisposed)return;if(r.ExitCode!=0||r.Data==null){Append("查找失败："+r.ExplainFailure());return;}targets.Items.Clear();foreach(Dictionary<string,object> install in MillionResourceService.Rows(r.Data,"installations"))foreach(Dictionary<string,object> instance in MillionResourceService.Rows(install,"instances"))targets.Items.Add(new MillionDesktopTarget{Directory=MillionResourceService.Text(install,"directory"),Name=MillionResourceService.Text(instance,"name"),Index=MillionResourceService.Number(instance,"index"),Running=MillionResourceService.Text(instance,"running")=="True"});targets.SelectedIndex=-1;Append(targets.Items.Count==0?"未找到雷电实例，请选择安装目录。":"已列出实例，请明确选择 MLTD 所在的一个。首次接入时请先手动启动该实例。");UpdateEnabled();
    }
    async Task ChooseGenericAdb(){
        using(OpenFileDialog picker=new OpenFileDialog{Title="选择 MuMu 安装目录 nx_main 中的 adb.exe",Filter="ADB 工具 (adb.exe)|adb.exe",CheckFileExists=true}){
            if(picker.ShowDialog(this)!=DialogResult.OK)return;selectedAdb=picker.FileName;
        }
        genericAdb=true;ShowSetup(true);endpoints.Items.Clear();endpoints.Text="";Append("已选择 ADB："+selectedAdb);
        MillionResourceReply reply=await Command("--discover-adb",selectedAdb);if(IsDisposed)return;
        if(reply.ExitCode!=0||reply.Data==null){Append("读取已连接设备失败，可手动填写所选模拟器的 ADB 地址："+reply.ExplainFailure());return;}
        object values;if(reply.Data.TryGetValue("devices",out values)){System.Collections.IEnumerable list=values as System.Collections.IEnumerable;if(list!=null)foreach(object value in list)if(value is string)endpoints.Items.Add((string)value);}
        if(endpoints.Items.Count==1){endpoints.SelectedIndex=0;Append("发现一个已连接设备。点击检查并接入前，会核对游戏版本与权限。");}
        else Append(endpoints.Items.Count>1?"发现多个设备，请选择装有 MLTD 的一个。":"暂未发现已连接设备。请填写模拟器设置中显示的 ADB 地址，例如 127.0.0.1:端口。");UpdateEnabled();
    }
    async Task AdaptClient(){
        Append("正在检查当前游戏并定位本地接口，可能需要几分钟。不会自动点击游戏标题。");
        MillionResourceReply r=await Command("--update-client-adapter",selectedAdb,endpoints.Text.Trim());
        if(IsDisposed)return;if(r.ExitCode!=0||r.Data==null){Append("游戏适配未完成，旧配置已保留："+r.ExplainFailure());return;}
        Append("游戏适配已生成："+MillionResourceService.Text(r.Data,"versionName")+"。正在接入；新歌曲、衣装等资源请再更新本地内容。");await Configure();
    }
    async Task Configure(){
        if(genericAdb){
            string address=endpoints.Text.Trim();MillionResourceReply configured=await Command("--use-adb-profile",selectedAdb,address);
            if(IsDisposed)return;if(configured.ExitCode!=0||configured.Data==null){Append("通用连接未完成："+configured.ExplainFailure());return;}
            Append("已核对游戏版本、Root 与网络隔离能力，并保留现有游戏身份。下次可直接开启本地连接。");autoLaunch=false;await LoadProfiles();ShowSetup(false);return;
        }
        MillionDesktopTarget p=targets.SelectedItem as MillionDesktopTarget;if(p==null)return;if(!p.Running){Append("请先启动所选 MLTD 实例，再重新查找。此步骤不会启动其他实例。");return;}
        MillionResourceReply r=await Command("--use-installed-profile",p.Directory,p.Index.ToString(),p.Name,"player-"+p.Index);if(IsDisposed)return;if(r.ExitCode!=0||r.Data==null){Append("接入未完成："+r.ExplainFailure());return;}Append("已保留现有游戏身份并建立本机档案。请选择档案后启动。");autoLaunch=false;await LoadProfiles();ShowSetup(false);
    }
    void SessionMessage(Dictionary<string,object> data){
        if(IsDisposed)return;if(InvokeRequired){try{BeginInvoke(new Action<Dictionary<string,object>>(SessionMessage),data);}catch(InvalidOperationException){}return;}
        string type=MillionResourceService.Text(data,"type");if(type=="started"){disconnecting=false;DateTime started;connectedAtUtc=DateTime.TryParse(MillionResourceService.Text(data,"time"),out started)?started.ToUniversalTime():DateTime.UtcNow;}
        else if(type=="progress")Append(Stage(MillionResourceService.Text(data,"stage")));
        else if(type=="ready"){DateTime ready;bool resumed=DateTime.TryParse(MillionResourceService.Text(data,"time"),out ready)&&ready.ToUniversalTime()<connectedAtUtc,retained=MillionResourceService.Text(data,"retained")=="True";Append((resumed?"本地连接已就绪，已重新连接后台。请查看当前游戏画面。":"本地连接已就绪。请在游戏标题画面手动点击进入。")+(retained?"\r\n后台服务已保留，可返回启动器。再次打开此处可重新连接。":""));notice("MLTD 本地连接已就绪，请查看游戏画面。");}
        else if(type=="failed"||type=="command-rejected"){disconnecting=false;Append("未完成："+LauncherErrors.FailureDetail("mltd",MillionResourceService.Text(data,"error"))+"\r\n记录："+MillionResourceService.Text(data,"evidence"));}
        else if(type=="ended"){disconnecting=false;Append(MillionResourceService.Text(data,"status")=="service-retained"?"游戏与后台服务已保留。":"本次启动会话已结束。记录："+MillionResourceService.Text(data,"evidence"));}UpdateEnabled();
    }
    static string Stage(string value){switch(value){case "generic-adb-target-verified":return "已核对通用 ADB 设备，正在开启本地连接…";case "launching-selected-profile":return "正在检查所选游戏…";case "release-files-and-saves-verified":return "本地文件和存档检查通过。";case "dedicated-emulator-start-requested":return "正在启动所选模拟器…";case "game-external-network-isolation-verified":return "游戏离线连接设置已核对。";case "existing-offline-service-verified":return "已连接现有本地服务。";case "new-offline-service-verified":return "本次本地服务已就绪。";case "game-start-requested":return "正在打开 MLTD…";case "api-runtime-initializing":return "等待游戏初始化…";case "api-runtime-ready":return "游戏连接组件已就绪。";case "local-api-url-verified":return "本地地址检查通过。";case "closing-selected-game":return "正在关闭所选 MLTD…";default:return "检查进度："+value;}}
    internal async Task StartSelected(){
        MillionDesktopProfile p=profiles.SelectedItem as MillionDesktopProfile;if(p==null||loading||SessionActive)return;
        try{Directory.CreateDirectory(Path.GetDirectoryName(Preferences));string temp=Preferences+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temp,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"adapter",Path.GetFullPath(adapter)},{"config",p.Config},{"coreMedia",coreMedia}}),new UTF8Encoding(false));if(File.Exists(Preferences))File.Replace(temp,Preferences,null);else File.Move(temp,Preferences);}catch(Exception ex){Append("无法保存启动选择："+ex.Message);return;}
        Append("正在准备基础资源…");MillionResourceReply prepared=await Command(String.IsNullOrEmpty(coreMedia)||!Directory.Exists(coreMedia)?new[]{"--prepare-startup-resources"}:new[]{"--prepare-startup-resources",coreMedia});if(IsDisposed)return;if(prepared.ExitCode!=0||MillionResourceService.Text(prepared.Data,"status")!="startup-resources-ready"){string status=MillionResourceService.Text(prepared.Data,"status");Append(status=="startup-resources-media-missing"?"基础资源不齐。请选择完整解压的基础资源包后重试。":status=="startup-resources-conflict"?"基础资源校验不符。请保留现有文件，检查资源包版本。":"基础资源准备未完成："+prepared.ExplainFailure());return;}Append("基础资源检查通过（"+MillionResourceService.Number(prepared.Data,"objects")+" 个文件）。");
        returnWhenDetached=false;Append("开始启动："+p);Task<MillionResourceReply> running=session.RunAsync(adapter,p.Config,SessionMessage);UpdateEnabled();MillionResourceReply r=await running;if(IsDisposed)return;if(r.ExitCode!=0)Append("启动未完成，请保留当前游戏现场。"+r.ExplainFailure());UpdateEnabled();if(returnWhenDetached&&r.ExitCode==0&&MillionResourceService.Text(r.Data,"status")=="service-retained"){Action a=BackRequested;if(a!=null)a();else Close();}
    }
}
