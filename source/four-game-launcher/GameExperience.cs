using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

// Game identity and brightness are independent. A daytime palette belongs to a game.
internal static class GameExperience {
    internal const int Gakumas=0,Shiny=1,Cgss=2,Million=3;
    internal static int ReadGame(string root) {
        try { string value=File.ReadAllText(Path.Combine(root,"selected-game.txt"),Encoding.UTF8).Trim(); return value=="scsp"?Shiny:value=="cgss"?Cgss:value=="million"?Million:Gakumas; }
        catch(IOException) { return Gakumas; } catch(UnauthorizedAccessException) { return Gakumas; }
    }
    internal static void WriteGame(string root,int game) {
        string file=Path.Combine(root,"selected-game.txt"),temporary=file+".tmp";
        try {
            File.WriteAllText(temporary,game==Million?"million":game==Cgss?"cgss":game==Shiny?"scsp":"gakumas",new UTF8Encoding(false));
            if(File.Exists(file))File.Replace(temporary,file,null);else File.Move(temporary,file);
        } finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
    internal static bool ReadShiny(string root) { return ReadGame(root)==Shiny; }
    internal static void WriteShiny(string root,bool shiny) { WriteGame(root,shiny?Shiny:Gakumas); }
}

internal static class BrandPalette {
    internal static Color HeaderInk{get{return UI.IsDark?UI.Accent:UI.IsBauhaus?UI.C("244E86"):UI.Accent;}}
    internal static Color GakumasTextInk{get{return UI.Text;}}
    internal static Color GakumasBandInk{get{return UI.Accent;}}
    internal static Color GakumasCrestInk{get{return UI.C(UI.IsDark?"956B2D":"B18B2C");}}
    internal static Color ShinyFeatherInk{get{return UI.C(UI.IsDark?"8A703E":"93BAD6");}}
    internal static Color ShinyFeatherDetailInk{get{return UI.C(UI.IsDark?"A98D4D":"5C87B0");}}
    // Dark bronze stays readable against the gold ticket's paper.
    internal static Color TicketInk{get{return UI.IsDark?UI.C("493916"):HeaderInk;}}
}

// Rasterize native vector outlines only when size, game or palette changes.
// A normal page slide leaves this control and its pixels untouched.
internal sealed class GameLogo:Label {
    readonly GraphicsPath gakumas,shiny,gakumasBand,gakumasCrest,shinyFeather,shinyFeatherDetail;
    Bitmap raster;bool isShiny,isCgss,isMillion;Size rasterBox;Color rasterInk,rasterPaper,rasterBand,rasterCrest,rasterFeather,rasterFeatherDetail;
    internal bool Shiny {get{return isShiny;}set{if(isShiny==value)return;isShiny=value;ClearRaster();Invalidate();}}
    internal bool Cgss {get{return isCgss;}set{if(isCgss==value)return;isCgss=value;ClearRaster();Invalidate();}}
    internal bool Million {get{return isMillion;}set{if(isMillion==value)return;isMillion=value;ClearRaster();Invalidate();}}
    internal GameLogo(){
        AutoSize=false;UseMnemonic=false;TabStop=true;Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Selectable,true);
        gakumas=GameWordmarks.Create(false,0);shiny=GameWordmarks.Create(true,0);gakumasBand=GameWordmarks.Create(false,1);gakumasCrest=GameWordmarks.Create(false,2);
        shinyFeather=GameWordmarks.Create(true,1);shinyFeatherDetail=GameWordmarks.Create(true,2);
    }
    internal void PerformClick(){if(Enabled&&!IsDisposed)OnClick(EventArgs.Empty);}
    protected override bool IsInputKey(Keys keyData){Keys key=keyData&Keys.KeyCode;return key==Keys.Space||key==Keys.Enter||base.IsInputKey(keyData);}
    protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left)Focus();base.OnMouseDown(e);}
    protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter)e.Handled=e.SuppressKeyPress=true;}
    protected override void OnKeyUp(KeyEventArgs e){base.OnKeyUp(e);if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){e.Handled=e.SuppressKeyPress=true;PerformClick();}}
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    protected override AccessibleObject CreateAccessibilityInstance(){return new LogoAccessibility(this);}
    sealed class LogoAccessibility:ControlAccessibleObject {
        readonly GameLogo logo;
        internal LogoAccessibility(GameLogo logo):base(logo){this.logo=logo;}
        public override string DefaultAction{get{return "切换游戏";}}
        public override void DoDefaultAction(){logo.PerformClick();}
    }    void ClearRaster(){if(raster!=null){raster.Dispose();raster=null;}}
    protected override void OnResize(EventArgs e){ClearRaster();base.OnResize(e);}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.Clear(BackColor);DrawArtwork(e.Graphics,ClientRectangle);
        if(Focused&&ShowFocusCues){Rectangle focus=ClientRectangle;focus.Inflate(-2,-2);ControlPaint.DrawFocusRectangle(e.Graphics,focus,ForeColor,BackColor);}
    }
    internal void DrawArtwork(Graphics drawing,Rectangle bounds){
        if(bounds.Width<1||bounds.Height<1)return;
        if(isMillion){DrawMillionArtwork(drawing,bounds);return;}
        if(isCgss){DrawCgssArtwork(drawing,bounds);return;}
        GraphicsPath source=isShiny?shiny:gakumas;SizeF canvas=GameWordmarks.Canvas(isShiny);Color ink=isShiny?BrandPalette.HeaderInk:BrandPalette.GakumasTextInk,band=BrandPalette.GakumasBandInk,crest=BrandPalette.GakumasCrestInk;
        if(bounds.Width<1||bounds.Height<1)return;
        Color feather=BrandPalette.ShinyFeatherInk,featherDetail=BrandPalette.ShinyFeatherDetailInk;
        if(raster!=null&&(rasterBox!=bounds.Size||rasterInk!=ink||rasterPaper!=UI.Bg||rasterBand!=band||rasterCrest!=crest||rasterFeather!=feather||rasterFeatherDetail!=featherDetail))ClearRaster();
        rasterBox=bounds.Size;rasterInk=ink;rasterPaper=UI.Bg;rasterBand=band;rasterCrest=crest;rasterFeather=feather;rasterFeatherDetail=featherDetail;
        if(raster==null){
            float scale=Math.Min(bounds.Width/canvas.Width,bounds.Height/canvas.Height);
            raster=new Bitmap(Math.Max(1,(int)Math.Ceiling(canvas.Width*scale)),Math.Max(1,(int)Math.Ceiling(canvas.Height*scale)),PixelFormat.Format32bppPArgb);
            using(Graphics g=Graphics.FromImage(raster))using(Brush fill=new SolidBrush(ink)){
                g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.ScaleTransform(scale,scale);
                if(!isShiny){using(Brush color=new SolidBrush(band))g.FillPath(color,gakumasBand);using(Brush color=new SolidBrush(crest))g.FillPath(color,gakumasCrest);}
                else {using(Brush color=new SolidBrush(feather))g.FillPath(color,shinyFeather);using(Brush color=new SolidBrush(featherDetail))g.FillPath(color,shinyFeatherDetail);}
                g.FillPath(fill,source);
            }
        }
        drawing.DrawImageUnscaled(raster,bounds.X,bounds.Y+(bounds.Height-raster.Height)/2);
    }
    static Bitmap millionWordmark;
    void DrawMillionArtwork(Graphics drawing,Rectangle bounds){
        if(millionWordmark==null)using(Stream stream=typeof(GameLogo).Assembly.GetManifestResourceStream("million-logo.png"))if(stream!=null)using(Image source=Image.FromStream(stream))millionWordmark=new Bitmap(source);
        if(millionWordmark==null)return;
        if(raster!=null&&(rasterBox!=bounds.Size||rasterPaper!=UI.Bg))ClearRaster();rasterBox=bounds.Size;rasterPaper=UI.Bg;
        if(raster==null){
            float scale=Math.Min(bounds.Width/(float)millionWordmark.Width,bounds.Height/(float)millionWordmark.Height);
            int w=Math.Max(1,(int)Math.Round(millionWordmark.Width*scale)),h=Math.Max(1,(int)Math.Round(millionWordmark.Height*scale));
            raster=new Bitmap(w,h,PixelFormat.Format32bppPArgb);
            using(Graphics g=Graphics.FromImage(raster)){
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                if(UI.IsDark){
                    float[][] values={new[]{.134f,.117f,.072f,0f,0f},new[]{.451f,.393f,.243f,0f,0f},new[]{.045f,.040f,.025f,0f,0f},new[]{0f,0f,0f,1f,0f},new[]{.22f,.17f,.08f,0f,1f}};
                    using(ImageAttributes tone=new ImageAttributes()){tone.SetColorMatrix(new ColorMatrix(values));g.DrawImage(millionWordmark,new Rectangle(0,0,w,h),0,0,millionWordmark.Width,millionWordmark.Height,GraphicsUnit.Pixel,tone);}
                }else g.DrawImage(millionWordmark,new Rectangle(0,0,w,h));
            }
        }
        drawing.DrawImageUnscaled(raster,bounds.X,bounds.Y+(bounds.Height-raster.Height)/2);
    }
    List<CgssWordmark.Part> cgssVector;
    void DrawCgssArtwork(Graphics drawing,Rectangle bounds){
        Color ink=UI.Text,accent=UI.Accent,paper=UI.Bg;
        if(raster!=null&&(rasterBox!=bounds.Size||rasterInk!=ink||rasterPaper!=paper||rasterBand!=accent))ClearRaster();
        rasterBox=bounds.Size;rasterInk=ink;rasterPaper=paper;rasterBand=accent;
        if(raster==null){
            if(cgssVector==null)cgssVector=CgssWordmark.Create();
            float scale=Math.Min(bounds.Width/900f,bounds.Height/308f);
            raster=new Bitmap(Math.Max(1,(int)Math.Ceiling(900*scale)),Math.Max(1,(int)Math.Ceiling(308*scale)),PixelFormat.Format32bppPArgb);
            using(Graphics g=Graphics.FromImage(raster)){
                g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.ScaleTransform(scale,scale);
                foreach(CgssWordmark.Part part in cgssVector)using(Brush fill=new SolidBrush(UI.IsDark?(part.Ink.GetBrightness()>.9f?UI.Bg:part.Ink.R>150?UI.C("F1DDA7"):UI.Accent):part.Ink))g.FillPath(fill,part.Path);
            }
        }
        drawing.DrawImageUnscaled(raster,bounds.X,bounds.Y+(bounds.Height-raster.Height)/2);
    }
    protected override void Dispose(bool disposing){if(disposing){ClearRaster();if(cgssVector!=null){foreach(CgssWordmark.Part part in cgssVector)part.Dispose();cgssVector=null;}gakumas.Dispose();shiny.Dispose();gakumasBand.Dispose();gakumasCrest.Dispose();shinyFeather.Dispose();shinyFeatherDetail.Dispose();}base.Dispose(disposing);}
}

internal sealed class ResourceLink {
    internal readonly string Name,Url,Caption,Domain;
    internal ResourceLink(string name,string url,string caption,string domain) { Name=name;Url=url;Caption=caption;Domain=domain; }
}

internal sealed class ResourceContent {
    internal string Name,Kicker,Headline,Description,Footer;
    internal ResourceLink[] Links;
    internal string[] FeatureTitles,FeatureDetails;
    internal bool Shiny;internal int Game;
    internal static ResourceContent For(int kind,bool shiny) { return For(kind,shiny?GameExperience.Shiny:GameExperience.Gakumas); }
    internal static ResourceContent For(int kind,int game) {
        ResourceContent content=game==GameExperience.Million?ForMillion(kind):game==GameExperience.Cgss?ForCgss(kind):ForOriginal(kind,game==GameExperience.Shiny);
        content.Game=game;content.Shiny=game==GameExperience.Shiny;return content;
    }
    static ResourceContent ForMillion(int kind) {
        ResourceContent c=new ResourceContent();
        string[] names={"萌娘百科","bilibili","百万现场官网","同人交流站"};
        string[] kick={"MOEGIRL / MILLION LIVE!","BILIBILI / TWO PERSPECTIVES","765 THEATER / OFFICIAL","IMSWeb / PRODUCER COMMUNITY"};
        string[] heads={"从一段故事，\n认识一颗星。","舞台的热爱，\n一起分享。","从剧场出发，\n向未来开演。","让应援，\n在这里相遇。"};
        string[] descriptions={"从萌娘百科了解剧场时光。\n认识偶像与她们的故事。","官方资讯与制作人视角。\n一起寻找舞台之外的精彩。","认识偶像与作品。\n从这里走进百万现场的世界。","连接制作人与同好。\n一起发现活动和更多故事。"};
        string[][] features={new[]{"作品介绍","偶像资料","剧场故事"},new[]{"官方频道","制作人视角","视频与动态"},new[]{"作品与偶像","官方资讯","765 剧场"},new[]{"站点导航","活动资讯","一起应援"}};
        string[][] details={new[]{"了解剧场时光的作品背景","认识舞台上的偶像","探索更多故事与相关资料"},new[]{"偶像大师官方账号","十六夜カズヤP 的个人空间","前往 bilibili 继续探索"},new[]{"认识 765 剧场的偶像","前往官网查看作品资讯","走进百万现场的舞台"},new[]{"发现相关站点入口","探索制作人活动与资讯","和同好一起分享热爱"}};
        kind=Math.Max(0,Math.Min(3,kind));c.Name=names[kind];c.Kicker=kick[kind];c.Headline=heads[kind];c.Description=descriptions[kind];
        if(kind==0)c.Links=new[]{new ResourceLink("前往萌娘百科","https://zh.moegirl.org.cn/%E5%81%B6%E5%83%8F%E5%A4%A7%E5%B8%88_%E7%99%BE%E4%B8%87%E7%8E%B0%E5%9C%BA_%E5%89%A7%E5%9C%BA%E6%97%B6%E5%85%89","百万现场剧场时光资料","ZH.MOEGIRL.ORG.CN")};
        else if(kind==1)c.Links=ForCgss(1).Links;
        else if(kind==2)c.Links=new[]{new ResourceLink("百万现场官网","https://millionlive-theaterdays.idolmaster-official.jp/","作品、偶像与官方资讯","MILLIONLIVE-THEATERDAYS.IDOLMASTER-OFFICIAL.JP")};
        else c.Links=ForCgss(3).Links;
        c.Footer="THEATER / CONNECTIONS";c.FeatureTitles=features[kind];c.FeatureDetails=details[kind];return c;
    }

    static ResourceContent ForCgss(int kind) {
        ResourceContent content=new ResourceContent();
        if(kind==2) {
            content.Name="灰姑娘女孩官网";content.Kicker="CINDERELLA GIRLS / OFFICIAL";
            content.Headline="让梦想，\n站上舞台。";
            content.Description="作品资讯与偶像资料。\n从这里走进灰姑娘的世界。";
            content.Links=new[]{new ResourceLink("灰姑娘女孩官网","https://cinderellagirls.idolmaster-official.jp/","作品、偶像与官方资讯","CINDERELLAGIRLS.IDOLMASTER-OFFICIAL.JP")};
            content.Footer="OFFICIAL CONNECTIONS";
        } else if(kind==1) {
            content.Name="bilibili";content.Kicker="BILIBILI / TWO PERSPECTIVES";
            content.Headline="一边是舞台，\n一边是热爱。";
            content.Description="官方的消息，制作人的视角。\n去 bilibili，继续这场相遇。";
            content.Links=new[]{
                new ResourceLink("偶像大师官方","https://space.bilibili.com/3546925024414310?spm_id_from=333.337.0.0","官方账号 · 视频与动态","BILIBILI / OFFICIAL"),
                new ResourceLink("十六夜カズヤP","https://space.bilibili.com/1357137?spm_id_from=333.337.search-card.all.click","制作人视角 · 视频与动态","BILIBILI / IZAYOI P")
            };
            content.Footer="OFFICIAL & PRODUCER";
        } else if(kind==0) {
            content.Name="剧情站";content.Kicker="CINDERELLA STORY / ARCHIVE";
            content.Headline="从一段剧情，\n遇见灰姑娘。";
            content.Description="按话数翻阅剧情资料。\n把还没看过的故事补回来。";
            content.Links=new[]{new ResourceLink("灰姑娘剧情站","https://derestory.icedn.com/list/","剧情列表与资料","DERESTORY.ICEDN.COM")};
            content.Footer="THE STORY ARCHIVE";
        } else {
            content.Name="同人交流站";content.Kicker="IMSWeb / 制作人社区";
            content.Headline="让热爱，\n在此相连。";content.Description="从站点导航，到活动与偶像生日。\n在中文制作人社区，找到同路的人。";
            content.Links=new[]{new ResourceLink("前往同人交流站","https://idol-master.top/","欢迎您，Producer！","IDOL-MASTER.TOP / IMSWeb")};
            content.Footer="THE PRODUCER COMMUNITY";
        }
        if(kind==2){
            content.FeatureTitles=new[]{"作品与偶像","官方资讯","灰姑娘的世界"};
            content.FeatureDetails=new[]{"走近 Cinderella Girls 的世界","前往官网查看作品资讯","从 346 Production 开始"};
        }else if(kind==1){
            content.FeatureTitles=new[]{"官方频道","制作人视角","视频与动态"};
            content.FeatureDetails=new[]{"偶像大师官方账号","十六夜カズヤP 的个人空间","前往 bilibili 继续探索"};
        }else if(kind==0){
            content.FeatureTitles=new[]{"剧情档案","按话翻阅","补完故事"};
            content.FeatureDetails=new[]{"前往灰姑娘剧情站","查看剧情列表与资料","把没看过的章节补回来"};
        }else{
            content.FeatureTitles=new[]{"站点导航","活动资讯","生日日历"};
            content.FeatureDetails=new[]{"发现剧情与相关站点入口","探索制作人活动与同人资讯","查看站点收录的偶像生日"};
        }
        return content;
    }
    static ResourceContent ForOriginal(int kind,bool shiny) {
        ResourceContent content=new ResourceContent();content.Shiny=shiny;
        if(kind==2) {
            content.Name="游戏官网";content.Kicker=shiny?"283 PRODUCTION / OFFICIAL":"初星学园 / OFFICIAL";
            content.Headline=shiny?"让每一种光，\n都有自己的色彩。":"下一场心动，\n从初星开始。";
            content.Description=shiny?"从作品世界出发，或直接走进 enza。\n两个官方入口，在这里相遇。":"认识初星学园与登场偶像。\n作品资讯与 PC 版下载，各有入口。";
            content.Links=shiny?new[]{
                new ResourceLink("闪耀色彩官网","https://shinycolors.idolmaster.jp/","作品、偶像与官方资讯","SHINYCOLORS.IDOLMASTER.JP"),
                new ResourceLink("闪耀色彩页游","https://shinycolors.enza.fun/","前往 enza 网页游戏","SHINYCOLORS.ENZA.FUN")
            }:new[]{
                new ResourceLink("学园偶像大师官网","https://gakuen.idolmaster-official.jp/","作品、偶像与官方资讯","GAKUEN.IDOLMASTER-OFFICIAL.JP"),
                new ResourceLink("PC 版下载官网","https://dmg-gakuen.idolmaster-official.jp/","前往 DMM GAMES 版官网","DMG-GAKUEN.IDOLMASTER-OFFICIAL.JP")
            };
            content.Footer="OFFICIAL CONNECTIONS";
        } else if(kind==1) {
            content.Name="bilibili";content.Kicker="BILIBILI / TWO PERSPECTIVES";
            content.Headline="一边是舞台，\n一边是热爱。";
            content.Description="官方的消息，制作人的视角。\n去 bilibili，继续这场相遇。";
            content.Links=new[]{
                new ResourceLink("偶像大师官方","https://space.bilibili.com/3546925024414310?spm_id_from=333.337.0.0","官方账号 · 视频与动态","BILIBILI / OFFICIAL"),
                new ResourceLink("十六夜カズヤP","https://space.bilibili.com/1357137?spm_id_from=333.337.search-card.all.click","制作人视角 · 视频与动态","BILIBILI / IZAYOI P")
            };
            content.Footer="OFFICIAL & PRODUCER";
        } else if(kind==0&&shiny) {
            content.Name="剧情站";content.Kicker="IM@S STORY / FIRST CHAPTER";
            content.Headline="从一个故事，\n认识一束光。";
            content.Description="从剧情出发，慢慢认识偶像大师。\n把下一章，留给你的好奇心。";
            content.Links=new[]{new ResourceLink("探索偶像大师剧情站","https://qzymz.github.io/imas-story/","从这里开始你的故事","QZYMZ.GITHUB.IO / IMAS-STORY")};
            content.Footer="YOUR FIRST CHAPTER";
        } else if(kind==0) {
            content.Name="偶像大师 Wiki";content.Kicker="中文 WIKI / 资料索引";
            content.Headline="把热爱，\n变成知识。";content.Description="前往学园偶像大师的中文资料入口。\n查阅角色、游戏内容与攻略。";
            content.Links=new[]{new ResourceLink("打开偶像大师 Wiki","https://www.gamekee.com/gakumas/622223.html","学园偶像大师中文资料","GAMEKEE.COM / GAKUMAS")};
            content.Footer="THE KNOWLEDGE ROOM";
        } else {
            content.Name="同人交流站";content.Kicker="IMSWeb / 制作人社区";
            content.Headline="让热爱，\n在此相连。";content.Description="从站点导航，到活动与偶像生日。\n在中文制作人社区，找到同路的人。";
            content.Links=new[]{new ResourceLink("前往同人交流站","https://idol-master.top/","欢迎您，Producer！","IDOL-MASTER.TOP / IMSWeb")};
            content.Footer="THE PRODUCER COMMUNITY";
        }
        if(kind==2){
            content.FeatureTitles=new[]{"作品与偶像",shiny?"闪耀色彩页游":"游戏下载","官方消息"};
            content.FeatureDetails=new[]{shiny?"走近 283 Production 的世界":"认识初星学园与登场偶像",shiny?"前往 enza 游戏入口":"前往 DMM GAMES 版官网","前往官网查看作品资讯"};
        }else if(kind==1){
            content.FeatureTitles=new[]{"官方频道","制作人视角","视频与动态"};
            content.FeatureDetails=new[]{"偶像大师官方账号","十六夜カズヤP 的个人空间","前往 bilibili 继续探索"};
        }else if(kind==0){
            content.FeatureTitles=shiny?new[]{"从故事开始","邂逅偶像","下一章见"}:new[]{"中文资料","角色与攻略","同好共建"};
            content.FeatureDetails=shiny?new[]{"前往偶像大师剧情站","从剧情认识新的担当","探索属于你的入坑路线"}:new[]{"从资料入口查阅相关信息","探索角色与游戏内容","了解 Wiki 与编辑组"};
        }else{
            content.FeatureTitles=new[]{"站点导航","活动资讯","生日日历"};
            content.FeatureDetails=new[]{"发现剧情与相关站点入口","探索制作人活动与同人资讯","查看站点收录的偶像生日"};
        }
        return content;
    }
}

