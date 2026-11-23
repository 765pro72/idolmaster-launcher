using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class MillionPackChoice {
    internal string Id,Title;internal long Bytes;public override string ToString(){return Title+"  ·  "+MillionResourceService.Size(Bytes);}
}
internal class MillionResourcesDialog:Form {
    readonly MillionResourceService service;readonly string adapter;readonly Action<string> notice;
    readonly ComboBox packs=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly TextBox media=new TextBox{ReadOnly=true,Dock=DockStyle.Fill},details=new TextBox{ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
    readonly Button selectMedia=new Button{Text="选择资源包文件夹",AutoSize=true},plan=new Button{Text="检查所选资源包",AutoSize=true},install=new Button{Text="安装所选资源包",AutoSize=true},diagnose=new Button{Text="检查最近缺失资源",AutoSize=true};bool busy;
    internal MillionResourcesDialog(MillionResourceService service,string adapter,Action<string> notice,bool initialDiagnostic=false){
        this.service=service;this.adapter=adapter;this.notice=notice;Text="MLTD · 本地资源";StartPosition=FormStartPosition.CenterParent;ClientSize=new Size(820,580);MinimumSize=new Size(740,520);MaximizeBox=false;Font=new Font(UI.BodyFace,10);BackColor=UI.Bg;ForeColor=UI.Text;
        TableLayoutPanel grid=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=6};for(int i=0;i<5;i++)grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(grid);
        grid.Controls.Add(new Label{Text="适用客户端 9.0.350 · 资源 1077830\n补充包独立安装；文件校验通过后，仍需在游戏里重试。",AutoSize=true,Margin=new Padding(0,0,0,14)});
        grid.Controls.Add(packs);grid.Controls.Add(new Label{Text="资源包文件夹中应包含按包名解压的子文件夹。",AutoSize=true,Margin=new Padding(0,12,0,4)});
        TableLayoutPanel paths=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=2,AutoSize=true};paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));paths.Controls.Add(media,0,0);paths.Controls.Add(selectMedia,1,0);grid.Controls.Add(paths);
        FlowLayoutPanel buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,Margin=new Padding(0,12,0,12)};foreach(Button b in new[]{plan,install,diagnose})buttons.Controls.Add(b);grid.Controls.Add(buttons);grid.Controls.Add(details);details.BackColor=UI.Card;details.ForeColor=UI.Text;
        foreach(Button b in new[]{selectMedia,plan,install,diagnose}){b.BackColor=UI.Card;b.ForeColor=UI.Text;b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=UI.Border;}packs.BackColor=UI.Card;packs.ForeColor=UI.Text;media.BackColor=UI.Card;media.ForeColor=UI.Text;
        selectMedia.Click+=delegate{using(FolderBrowserDialog d=new FolderBrowserDialog{Description="选择解压资源包所在的总文件夹"})if(d.ShowDialog(this)==DialogResult.OK)media.Text=d.SelectedPath;};
        plan.Click+=async delegate{await Selected(false);};install.Click+=async delegate{await Selected(true);};diagnose.Click+=async delegate{await Diagnose();};Shown+=async delegate{await LoadPacks();if(initialDiagnostic)await Diagnose();};
        FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy||service.Busy){e.Cancel=true;details.AppendText(Environment.NewLine+"当前资源任务仍在运行，请等待完成。");}};
    }
    void SetBusy(bool value){busy=value;foreach(Control b in new Control[]{packs,selectMedia,plan,install,diagnose})b.Enabled=!value;UseWaitCursor=value;}
    async Task<MillionResourceReply> Command(params string[] args){SetBusy(true);try{return await service.RunAsync(adapter,args);}finally{if(!IsDisposed)SetBusy(false);}}
    void Failed(MillionResourceReply r){details.Text="操作未完成，现有文件保留。\r\n"+r.ExplainFailure();notice("资源操作未完成，请查看本地资源窗口。");}
    internal async Task LoadPacks(){
        MillionResourceReply r=await Command("--list-resource-packs");if(IsDisposed)return;if(r.ExitCode!=0||r.Data==null){Failed(r);return;}packs.Items.Clear();foreach(Dictionary<string,object> p in MillionResourceService.Rows(r.Data,"packs")){object core;p.TryGetValue("requiredCore",out core);if(core is bool&&(bool)core)continue;string id=MillionResourceService.Text(p,"id");packs.Items.Add(new MillionPackChoice{Id=id,Title=MillionResourceService.Name(id),Bytes=MillionResourceService.Number(p,"bytes")});}if(packs.Items.Count>0)packs.SelectedIndex=packs.Items.Count-1;details.Text="已读取 "+packs.Items.Count+" 个可选资源包。\r\n这里显示资源清单；尚未安装的包需要提供匹配的本地文件。";
    }
    async Task Selected(bool write){
        MillionPackChoice p=packs.SelectedItem as MillionPackChoice;if(p==null)return;string[] args=media.Text.Length>0?new[]{write?"--install-resource-pack":"--plan-resource-pack",p.Id,media.Text}:new[]{write?"--install-resource-pack":"--plan-resource-pack",p.Id};
        MillionResourceReply r=await Command(args);if(IsDisposed)return;if(r.Data==null||r.ExitCode!=0&&r.ExitCode!=2){Failed(r);return;}string state=MillionResourceService.Text(r.Data,"status");
        if(state=="optional-resource-media-missing"){details.Text="缺少以下资源文件，尚未完成安装：\r\n"+String.Join("\r\n",MillionResourceService.Rows(r.Data,"missingMedia").Select(x=>"• "+MillionResourceService.Name(MillionResourceService.Text(x,"id"))).ToArray())+"\r\n请选择包含对应资源包的文件夹后重试。基础文件缺失时，请重新解压基础适配包。";notice("资源文件尚未齐全，已保留现有文件。");return;}
        if(state=="optional-resource-installed"){details.Text="本地资源已安装并校验。请回到游戏，重试对应操作。\r\n\r\n"+p.Title;notice(p.Title+"：本地文件安装并校验通过，待游戏重试。");}
        else{details.Text="所选资源及依赖检查通过，可安装：\r\n"+String.Join("\r\n",MillionResourceService.Rows(r.Data,"packs").Select(x=>"• "+MillionResourceService.Name(MillionResourceService.Text(x,"id"))+(MillionResourceService.Text(x,"action")=="verified"?"（已校验）":"（待安装）")).ToArray());}
    }
    internal async Task Diagnose(){
        string run=MillionResourceService.LatestRun(adapter);if(run==null){details.Text="尚未找到这个适配包的启动记录。请先使用同一适配包启动游戏，再检查缺失资源。";return;}
        MillionResourceReply r=await Command("--resource-status",run);if(IsDisposed)return;if(r.ExitCode!=0||r.Data==null){Failed(r);return;}
        List<string> rows=new List<string>{"历史缺失资源 "+MillionResourceService.Number(r.Data,"distinctMissingResources")+" 项；当前仍未验证通过 "+MillionResourceService.Number(r.Data,"unresolvedResources")+" 项。","文件已补齐时，请在游戏里重试；此处不代表播放或显示已成功。",""};
        foreach(Dictionary<string,object> item in MillionResourceService.Rows(r.Data,"resources")){object names;item.TryGetValue("logicalNames",out names);IEnumerable values=names as IEnumerable;string logical=values!=null&&!(names is string)?String.Join("、",values.Cast<object>().Select(Convert.ToString).ToArray()):MillionResourceService.Text(item,"name");rows.Add(logical+"："+MillionResourceService.State(MillionResourceService.Text(item,"localState")));string[] available=MillionResourceService.Rows(item,"matchingPacks").Select(p=>MillionResourceService.Name(MillionResourceService.Text(p,"id"))).ToArray();if(available.Length>0)rows.Add("可用补充包："+String.Join("、",available));rows.Add("");}
        details.Text=String.Join(Environment.NewLine,rows.ToArray());notice("已检查最近缺失资源，结果见本地资源窗口。");
    }
}
