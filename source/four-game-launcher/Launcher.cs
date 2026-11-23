using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class UI {
    public const float Axis=-14f;
    public static bool IsDark,IsBauhaus,IsWabi,IsMillion;
    public static Color Bg,Card,Border,Text,Dim,Accent,AccentH,GhostH,Ok,Bad,DotOff;
    public static string BodyFace="Noto Sans SC",TitleFace="Noto Sans SC",LatinFace="Bahnschrift";
    public static string UprightLatin{get{return Available("Bahnschrift","Segoe UI");}}
    public static string Available(string requested,string fallback){
        try{using(FontFamily family=new FontFamily(requested))return family.Name;}catch(ArgumentException){return fallback;}
    }
    public static Color C(string hex){return ColorTranslator.FromHtml("#"+hex);}
    public static float Corner{get{return IsMillion?7:IsWabi?6:IsDark?12:IsBauhaus?7:9;}}
    public static string Face(string text,bool headline){
        bool chinese=false;foreach(char c in text)if(c>0x2ff){chinese=true;break;}
        return chinese?(headline?TitleFace:BodyFace):LatinFace;
    }
    public static void Theme(bool dark,bool bauhaus,bool wabi=false,bool million=false){
        IsDark=dark;IsWabi=wabi;IsMillion=million;IsBauhaus=bauhaus&&!dark&&!wabi&&!million;
        Bg=C(dark?"181716":"EFEADB");Card=C(dark?"282622":"EBE7DB");
        Border=C(dark?"57513F":"D5D0C1");Text=C(dark?"F6E8C9":"1E2726");
        Dim=C(dark?"B8AA8C":"667068");Accent=C(dark?"C7A963":"DE3923");
        AccentH=dark?Blend(Accent,Color.Black,.18f):C("BF2A19");
        GhostH=dark?Blend(Card,Text,.09f):C("DCD7C9");
        Ok=C(dark?"9AD8B1":"237651");Bad=C(dark?"FFA38B":"B1301E");DotOff=C(dark?"B8AA8C":"8F9788");
        if(IsBauhaus){Bg=C("F1E8D3");Card=C("E7D8BA");Border=C("A49A82");Text=C("182A36");Dim=C("53636C");Accent=C("244E86");AccentH=Blend(Accent,Color.Black,.18f);GhostH=Blend(Card,Text,.09f);}
        if(IsWabi&&!dark){
            Bg=C("F7F2E9");Card=C("EEE9DF");Border=C("D3C7B4");Text=C("253B57");Dim=C("687889");
            Accent=C("38689C");AccentH=C("2C547F");GhostH=C("E6E9E8");Ok=C("44735D");Bad=C("A44D46");DotOff=Dim;
        }

        if(IsMillion&&!dark){Bg=C("F5F0E7");Card=C("EAE3D6");Border=C("CEC4B4");Text=C("213D50");Dim=C("687783");Accent=C("C37729");AccentH=C("A96320");GhostH=C("E1D8C9");Ok=C("46765B");Bad=C("AD4D42");DotOff=Dim;}

        BodyFace=Available("Noto Sans SC","Microsoft YaHei UI");
        TitleFace=Available((dark||wabi&&!million)?"Noto Serif SC":"Noto Sans SC",BodyFace);
        LatinFace=Available((dark||wabi&&!million)?"Georgia":"Bahnschrift","Segoe UI");
    }
    public static GraphicsPath Round(RectangleF r,float radius){
        GraphicsPath p=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
        if(d<=0){p.AddRectangle(r);return p;}
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);
        p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    public static Font PixelFont(float points,bool bold,float scale){
        return new Font(BodyFace,points*96f/72f*scale,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);
    }
    public static Color Blend(Color a,Color b,float t){
        t=Math.Max(0,Math.Min(1,t));return Color.FromArgb((int)(a.A+(b.A-a.A)*t),(int)(a.R+(b.R-a.R)*t),(int)(a.G+(b.G-a.G)*t),(int)(a.B+(b.B-a.B)*t));
    }
}
static class WindowsAppearance {
    public static bool ReadDark(){
        try{
            using(RegistryKey key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",false)){
                object value=key==null?null:key.GetValue("AppsUseLightTheme");
                return value!=null&&Convert.ToInt32(value)==0;
            }
        }catch{return false;}
    }
    public static bool Valid(string value){return value=="system"||value=="light"||value=="dark";}
    public static string ReadPreference(string file,out string day){
        day="red";
        try{
            string content=File.ReadAllText(file,Encoding.UTF8).Trim().ToLowerInvariant();
            if(Valid(content))return content;
            string mode="system";
            foreach(string line in content.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){
                string[] part=line.Split(new[]{'='},2);if(part.Length!=2)continue;
                if(part[0].Trim()=="mode"&&Valid(part[1].Trim()))mode=part[1].Trim();
                if(part[0].Trim()=="day"&&(part[1].Trim()=="red"||part[1].Trim()=="bauhaus"))day=part[1].Trim();
            }
            return mode;
        }catch{return "system";}
    }
    public static void WritePreference(string file,string value,string day){
        if(!Valid(value)||day!="red"&&day!="bauhaus")throw new ArgumentException("Unknown appearance preference.");
        string temporary=file+".tmp";
        try{
            File.WriteAllText(temporary,"mode="+value+Environment.NewLine+"day="+day+Environment.NewLine,new UTF8Encoding(false));
            if(File.Exists(file)){
                try{File.Replace(temporary,file,null);}
                catch(IOException){File.Copy(temporary,file,true);File.Delete(temporary);}
            }else File.Move(temporary,file);
        }finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    public static void TitleBar(IntPtr window){TitleBar(window,UI.IsDark,UI.Bg,UI.Text);}
    public static void TitleBar(IntPtr window,bool isDark,Color paper,Color ink){
        try{
            int dark=isDark?1:0;
            if(DwmSetWindowAttribute(window,20,ref dark,4)!=0)DwmSetWindowAttribute(window,19,ref dark,4);
            int background=paper.R|(paper.G<<8)|(paper.B<<16),text=ink.R|(ink.G<<8)|(ink.B<<16);
            DwmSetWindowAttribute(window,35,ref background,4);DwmSetWindowAttribute(window,36,ref text,4);
        }catch(EntryPointNotFoundException){}catch(DllNotFoundException){}
    }
    public static bool? TitleBarDark(IntPtr window){
        try{int value;if(DwmGetWindowAttribute(window,20,out value,4)==0)return value!=0;}catch(EntryPointNotFoundException){}catch(DllNotFoundException){}
        return null;
    }
}
static class MotionProfile {
    public static double Coast(double value){double t=Math.Max(0,Math.Min(1,value)),r=1-t;return 1-r*r*r*r*(1+4*t);}
    public static double Slide(double value){double t=Math.Max(0,Math.Min(1,value));return t*t*(3-2*t);}
    public static double Passage(double value){double t=Math.Max(0,Math.Min(1,value));return t*t*t*(10+t*(-15+6*t));}
}
// One native, opaque copy of a static scene. Geometry and text remain native-DPI;
// the moving vector scene is drawn separately. No frame allocates another bitmap.
sealed class RasterFrame:IDisposable {
    readonly Size size;Bitmap fallback;IntPtr bitmap,dc,previous;bool disposed;int owners=1;
    public Size Size{get{return size;}}
    public RasterFrame Retain(){if(disposed)throw new ObjectDisposedException("RasterFrame");owners++;return this;}
    public bool IsNative{get{return fallback==null&&!disposed;}}
    internal sealed class Canvas:IDisposable {
        readonly Graphics graphics;readonly int saved;internal readonly IntPtr Target;
        public Canvas(Graphics g){
            graphics=g;RectangleF visible=g.VisibleClipBounds;
            Rectangle clip=Rectangle.FromLTRB((int)Math.Floor(visible.Left),(int)Math.Floor(visible.Top),(int)Math.Ceiling(visible.Right),(int)Math.Ceiling(visible.Bottom));
            Target=g.GetHdc();saved=SaveDC(Target);IntersectClipRect(Target,clip.Left,clip.Top,clip.Right,clip.Bottom);
        }
        public void Dispose(){RestoreDC(Target,saved);graphics.ReleaseHdc(Target);}
    }
    [StructLayout(LayoutKind.Sequential)]struct Blend {public byte Operation,Flags,Alpha,Format;}
    [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]static extern IntPtr SelectObject(IntPtr dc,IntPtr item);
    [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")]static extern bool RestoreDC(IntPtr dc,int saved);
    [DllImport("gdi32.dll")]static extern int IntersectClipRect(IntPtr dc,int l,int t,int r,int b);
    [DllImport("gdi32.dll")]static extern IntPtr CreateEllipticRgn(int l,int t,int r,int b);
    [DllImport("gdi32.dll")]static extern int ExtSelectClipRgn(IntPtr dc,IntPtr region,int mode);
    [DllImport("gdi32.dll")]static extern bool BitBlt(IntPtr target,int x,int y,int w,int h,IntPtr source,int sx,int sy,uint operation);
    [DllImport("msimg32.dll")]static extern bool AlphaBlend(IntPtr target,int x,int y,int w,int h,IntPtr source,int sx,int sy,int sw,int sh,Blend blend);
    public RasterFrame(Bitmap image){
        size=image.Size;fallback=image;
        try{
            bitmap=image.GetHbitmap();dc=CreateCompatibleDC(IntPtr.Zero);
            if(bitmap!=IntPtr.Zero&&dc!=IntPtr.Zero)previous=SelectObject(dc,bitmap);
            if(previous!=IntPtr.Zero&&previous!=new IntPtr(-1)){fallback=null;image.Dispose();}else ReleaseNative();
        }catch(ExternalException){ReleaseNative();}catch(OutOfMemoryException){ReleaseNative();}
    }
    public void Draw(Graphics g,int x,int y,float opacity){Draw(g,x,y,opacity,Rectangle.Empty);}
    public void Draw(Graphics g,int x,int y,float opacity,Rectangle circle){
        if(disposed||opacity<=0)return;
        if(fallback!=null){
            GraphicsState state=g.Save();try{
                if(!circle.IsEmpty)using(GraphicsPath mask=new GraphicsPath()){mask.AddEllipse(circle);g.SetClip(mask,CombineMode.Intersect);}
                if(opacity>=1){CompositingMode old=g.CompositingMode;g.CompositingMode=CompositingMode.SourceCopy;g.DrawImageUnscaled(fallback,x,y);g.CompositingMode=old;}
                else using(System.Drawing.Imaging.ImageAttributes alpha=new System.Drawing.Imaging.ImageAttributes()){
                    System.Drawing.Imaging.ColorMatrix matrix=new System.Drawing.Imaging.ColorMatrix();matrix.Matrix33=opacity;alpha.SetColorMatrix(matrix);
                    g.DrawImage(fallback,new Rectangle(x,y,size.Width,size.Height),0,0,size.Width,size.Height,GraphicsUnit.Pixel,alpha);
                }
            }finally{g.Restore(state);}return;
        }
        using(Canvas canvas=new Canvas(g))Draw(canvas,x,y,opacity,circle);
    }
    public void Draw(Canvas canvas,int x,int y,float opacity,Rectangle circle){
        if(disposed||opacity<=0)return;IntPtr target=canvas.Target;int saved=0;
        try{
            if(!circle.IsEmpty){saved=SaveDC(target);IntPtr region=CreateEllipticRgn(circle.Left,circle.Top,circle.Right,circle.Bottom);try{ExtSelectClipRgn(target,region,1);}finally{DeleteObject(region);}}
            if(opacity>=1)BitBlt(target,x,y,size.Width,size.Height,dc,0,0,0x00CC0020);
            else AlphaBlend(target,x,y,size.Width,size.Height,dc,0,0,size.Width,size.Height,new Blend{Alpha=(byte)Math.Round(Math.Min(1,opacity)*255)});
        }finally{if(saved!=0)RestoreDC(target,saved);}
    }
    void ReleaseNative(){if(dc!=IntPtr.Zero&&previous!=IntPtr.Zero&&previous!=new IntPtr(-1))SelectObject(dc,previous);previous=IntPtr.Zero;if(bitmap!=IntPtr.Zero){DeleteObject(bitmap);bitmap=IntPtr.Zero;}if(dc!=IntPtr.Zero){DeleteDC(dc);dc=IntPtr.Zero;}}
    public void Dispose(){if(disposed||--owners>0)return;disposed=true;ReleaseNative();if(fallback!=null){fallback.Dispose();fallback=null;}}
}
sealed class MenuColors:ProfessionalColorTable {
    public override Color ToolStripDropDownBackground{get{return UI.Card;}}
    public override Color ImageMarginGradientBegin{get{return UI.Card;}}
    public override Color ImageMarginGradientMiddle{get{return UI.Card;}}
    public override Color ImageMarginGradientEnd{get{return UI.Card;}}
    public override Color MenuBorder{get{return UI.Border;}}
    public override Color MenuItemBorder{get{return UI.Border;}}
    public override Color MenuItemSelected{get{return UI.GhostH;}}
    public override Color MenuItemSelectedGradientBegin{get{return UI.GhostH;}}
    public override Color MenuItemSelectedGradientEnd{get{return UI.GhostH;}}
    public override Color SeparatorDark{get{return UI.Border;}}
    public override Color SeparatorLight{get{return UI.Border;}}
    public override Color CheckBackground{get{return UI.Card;}}
    public override Color CheckSelectedBackground{get{return UI.GhostH;}}
    public override Color CheckPressedBackground{get{return UI.GhostH;}}
}
sealed class ThemeMenuRenderer:ToolStripProfessionalRenderer {
    public ThemeMenuRenderer():base(new MenuColors()){RoundedEdges=false;}
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e){
        float p=MenuMotion.Progress(e.Item);Rectangle rect=e.TextRectangle;rect.Offset((int)Math.Round((1-p)*6*e.Graphics.DpiX/96f),0);e.TextRectangle=rect;
        e.TextColor=UI.Blend(UI.Card,e.Item.Enabled?UI.Text:UI.DotOff,p);base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e){e.ArrowColor=UI.Blend(UI.Card,e.Item.Enabled?UI.Text:UI.DotOff,MenuMotion.Progress(e.Item));base.OnRenderArrow(e);}
}
// Keep the menu's hit targets in place from the first frame. Only row ink glides in.
sealed class MenuMotion {
    static readonly Dictionary<ToolStrip,MenuMotion> motions=new Dictionary<ToolStrip,MenuMotion>();
    readonly ToolStripDropDown menu;readonly Stopwatch age=new Stopwatch();FramePump clock;
    MenuMotion(ToolStripDropDown value){menu=value;motions.Add(menu,this);menu.Opened+=Opened;menu.Closed+=Closed;menu.Disposed+=Disposed;}
    public static void Attach(ToolStripDropDown menu){if(!motions.ContainsKey(menu))new MenuMotion(menu);}
    public static float Progress(ToolStripItem item){
        MenuMotion motion;if(item.Owner==null||!motions.TryGetValue(item.Owner,out motion)||!motion.age.IsRunning)return 1;
        int index=Math.Max(0,item.Owner.Items.IndexOf(item));double t=(motion.age.Elapsed.TotalSeconds-Math.Min(4,index)*.009)/.13;
        return (float)MotionProfile.Coast(t);
    }
    void Opened(object sender,EventArgs e){
        Stop();age.Restart();clock=new FramePump(menu,delegate(double dt){if(age.Elapsed.TotalSeconds>=.18){age.Stop();Stop();}menu.Invalidate();});
        clock.Rate=DisplayInfo.Refresh(menu);clock.SetActive(true);menu.Invalidate();
    }
    void Closed(object sender,ToolStripDropDownClosedEventArgs e){age.Stop();Stop();}
    void Stop(){FramePump old=clock;clock=null;if(old!=null)old.Dispose();}
    void Disposed(object sender,EventArgs e){Stop();motions.Remove(menu);menu.Opened-=Opened;menu.Closed-=Closed;menu.Disposed-=Disposed;}
}
sealed class QuickMenuItem:ToolStripMenuItem {
    readonly System.Windows.Forms.Timer intent=new System.Windows.Forms.Timer();bool inside;
    public QuickMenuItem(string text):base(text){intent.Interval=65;intent.Tick+=delegate{intent.Stop();if(inside&&Enabled&&HasDropDownItems&&!IsDisposed)ShowDropDown();};}
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);inside=true;if(HasDropDownItems)intent.Start();}
    protected override void OnMouseLeave(EventArgs e){inside=false;intent.Stop();base.OnMouseLeave(e);}
    protected override void Dispose(bool disposing){if(disposing)intent.Dispose();base.Dispose(disposing);}
}
static class FontStore{
    static readonly System.Drawing.Text.PrivateFontCollection fonts=new System.Drawing.Text.PrivateFontCollection();
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)]static extern int AddFontResourceEx(string file,uint flags,IntPtr reserved);
    public static void Load(string root){
        string dir=Path.Combine(root,@"src\ui\fonts");if(!Directory.Exists(dir))return;
        foreach(string file in Directory.GetFiles(dir,"*.ttf"))try{fonts.AddFontFile(file);AddFontResourceEx(file,0x10,IntPtr.Zero);}catch{}
    }
}

static class DisplayInfo {
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string device;
        public ushort spec,driver,size,extra;public uint fields;
        public int x,y;public uint orientation,fixedOutput;
        public short color,duplex,yResolution,tt,collate;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string form;
        public ushort logPixels;public uint bits,width,height,flags,frequency,icmMethod,icmIntent,media,dither,reserved1,reserved2,panningWidth,panningHeight;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool EnumDisplaySettingsEx(string name,int mode,ref DEVMODE dm,uint flags);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]static extern IntPtr MonitorFromPoint(Point point,uint flags);
    [DllImport("shcore.dll")]static extern int GetDpiForMonitor(IntPtr monitor,int kind,out uint x,out uint y);
    [DllImport("user32.dll")]static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("shcore.dll")]static extern int SetProcessDpiAwareness(int awareness);
    [DllImport("user32.dll")]static extern bool SetProcessDPIAware();
    public static void Initialize() {
        // The embedded manifest establishes PMv2 before any window exists.
        try{if(SetProcessDpiAwarenessContext(new IntPtr(-4)))return;}catch(EntryPointNotFoundException){}catch(DllNotFoundException){}
        try{if(SetProcessDpiAwareness(2)==0)return;}catch(EntryPointNotFoundException){}catch(DllNotFoundException){}
        try{SetProcessDPIAware();}catch(EntryPointNotFoundException){}
    }
    public static int Dpi(Point point) {
        try{uint x,y;if(GetDpiForMonitor(MonitorFromPoint(point,2),0,out x,out y)==0)return (int)x;}catch(EntryPointNotFoundException){}catch(DllNotFoundException){}
        using(Graphics g=Graphics.FromHwnd(IntPtr.Zero))return (int)Math.Round(g.DpiX);
    }
    public static int Dpi(Control c){try{return (int)GetDpiForWindow(c.Handle);}catch(EntryPointNotFoundException){return Dpi(c.PointToScreen(Point.Empty));}}
    public static int Refresh(Control c) {
        DEVMODE dm=new DEVMODE();dm.size=(ushort)Marshal.SizeOf(typeof(DEVMODE));
        try{if(EnumDisplaySettingsEx(Screen.FromHandle(c.Handle).DeviceName,-1,ref dm,0)&&dm.frequency>=24&&dm.frequency<=1000)return (int)dm.frequency;}catch{}
        return 60;
    }
}
// A single, monitor-rate clock; only one UI callback can be pending at a time.
// Animation uses real elapsed time and drops stale frames instead of queuing them.
sealed class FramePump:IDisposable {
    readonly object lifecycle=new object();bool eventsClosed;int disposeStarted;
    readonly Control host;readonly Action<double> frame;readonly ManualResetEvent wake=new ManualResetEvent(false);
    readonly AutoResetEvent frameReady=new AutoResetEvent(false);
    readonly Thread worker;volatile bool running=true,active;int pending,hz=60;volatile int effectiveHz=60;int slowFrames,fastFrames,observedFrames;double delivered,paintAverage;
    public int Rate{get{return hz;}set{
        int next=Math.Max(24,Math.Min(1000,value));
        if(Interlocked.Exchange(ref hz,next)!=next){effectiveHz=next;paintAverage=0;slowFrames=fastFrames=observedFrames=0;}
    }}
    public double EffectiveRate{get{return Math.Min(hz,effectiveHz);}}
    // Reserve UI time for input and composition. Hysteresis avoids rate oscillation.
    public void ObserveWork(double milliseconds){
        if(milliseconds<0||double.IsNaN(milliseconds)||double.IsInfinity(milliseconds))return;
        double sample=Math.Min(100,milliseconds);
        if(++observedFrames<=3){paintAverage=sample;return;}
        paintAverage=paintAverage==0?sample:paintAverage+(sample-paintAverage)*(sample<paintAverage?.18:.06);
        // Adjust a few Hz at a time instead of abruptly halving the monitor rate.
        int target=Math.Max(24,Math.Min(hz,(int)Math.Floor(820/Math.Max(.1,paintAverage)))),step=Math.Max(2,hz/12);
        if(target<effectiveHz-2){
            fastFrames=0;
            if(++slowFrames>=8){
                effectiveHz=Math.Max(target,effectiveHz-step);
                slowFrames=0;
            }
        }else{
            slowFrames=0;
            if(target>effectiveHz+2||target==hz&&effectiveHz<hz){
                if(++fastFrames>=4){effectiveHz=Math.Min(target,effectiveHz+step);fastFrames=0;}
            }else fastFrames=0;
        }
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateWaitableTimerEx(IntPtr attrs,string name,uint flags,uint access);
    [DllImport("kernel32.dll")]static extern bool SetWaitableTimer(IntPtr timer,ref long due,int period,IntPtr completion,IntPtr arg,bool resume);
    [DllImport("kernel32.dll")]static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    [DllImport("winmm.dll")]static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]static extern uint timeEndPeriod(uint period);
    public FramePump(Control host,Action<double> frame){this.host=host;this.frame=frame;worker=new Thread(Loop);worker.IsBackground=true;worker.Name="StagePass animation clock";worker.Start();}
    public bool WorkerStopped{get{return !worker.IsAlive;}}
    public void SetActive(bool value){
        lock(lifecycle){
            if(disposeStarted!=0)return;
            active=value;delivered=0;if(value)wake.Set();else wake.Reset();
        }
    }
    void CompleteFrame(){
        Interlocked.Exchange(ref pending,0);
        lock(lifecycle){if(running&&!eventsClosed)frameReady.Set();}
    }
    void Loop() {
        IntPtr timer=IntPtr.Zero;bool fallback=false;
        try {
            try{timer=CreateWaitableTimerEx(IntPtr.Zero,null,2,0x1F0003);}catch(EntryPointNotFoundException){}
            while(running) {
                wake.WaitOne();if(!running)break;
                if(timer==IntPtr.Zero&&!fallback)fallback=timeBeginPeriod(1)==0;
                long next=Stopwatch.GetTimestamp();
                while(running&&active) {
                    while(pending!=0&&running&&active)frameReady.WaitOne(60);if(!running||!active)break;
                    if(Interlocked.CompareExchange(ref pending,1,0)==0) {
                        try{host.BeginInvoke(new Action(delegate{
                            try{if(running&&active&&!host.IsDisposed&&!host.Disposing){
                                double now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
                                double elapsed=delivered==0?1.0/EffectiveRate:Math.Min(.1,now-delivered);delivered=now;frame(elapsed);
                            }}finally{CompleteFrame();}
                        }));}catch(InvalidOperationException){Interlocked.Exchange(ref pending,0);}
                    }
                    next+=Math.Max(1,(long)(Stopwatch.Frequency/EffectiveRate));long remaining=next-Stopwatch.GetTimestamp();
                    if(remaining<=0){next=Stopwatch.GetTimestamp()+Math.Max(1,(long)(Stopwatch.Frequency/EffectiveRate));remaining=next-Stopwatch.GetTimestamp();}
                    long due=-Math.Max(1,(long)(remaining*(10000000.0/Stopwatch.Frequency)));
                    if(timer!=IntPtr.Zero&&SetWaitableTimer(timer,ref due,0,IntPtr.Zero,IntPtr.Zero,false))WaitForSingleObject(timer,60);
                    else Thread.Sleep(Math.Max(1,(int)(remaining*1000/Stopwatch.Frequency)));
                }
                if(fallback){timeEndPeriod(1);fallback=false;}
            }
        }finally{
            if(timer!=IntPtr.Zero)CloseHandle(timer);if(fallback)timeEndPeriod(1);
            // Only the worker releases its wait handles, after its last wait.
            lock(lifecycle){
                running=false;active=false;disposeStarted=1;
                if(!eventsClosed){eventsClosed=true;wake.Dispose();frameReady.Dispose();}
            }
        }
    }
    public void Dispose(){
        lock(lifecycle){
            if(disposeStarted!=0)return;
            disposeStarted=1;running=false;active=false;wake.Set();frameReady.Set();
        }
        if(Thread.CurrentThread!=worker)worker.Join(250);
    }
}
class RoundBtn:Button {
    public float Density=1,Radius=9;
    public Color Fill,FillHover,Stroke;
    public bool HasStroke=true,DirectionalArrow,ExternalLink;
    readonly Stopwatch navigation=new Stopwatch();System.Windows.Forms.Timer linkTimer;
    bool hover,down;float blend,press,pressVelocity;
    public RoundBtn() {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.PushButton;
        MouseEnter+=delegate{hover=true;Invalidate();};
        MouseLeave+=delegate{hover=false;down=false;Invalidate();};
        MouseDown+=delegate(object s,MouseEventArgs e){if(e.Button==MouseButtons.Left){down=true;Invalidate();}};
        MouseUp+=delegate{down=false;Invalidate();};
        KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){down=true;Invalidate();}};
        KeyUp+=delegate{down=false;Invalidate();};
        LostFocus+=delegate{down=false;};
    }
    public void Advance(double dt) {
        float target=hover&&Enabled?1:0;bool changed=Math.Abs(target-blend)>.001f;
        blend+=(target-blend)*(float)(1-Math.Exp(-dt/.065));
        float destination=down&&Enabled?1:0;
        int steps=Math.Max(1,(int)Math.Ceiling(Math.Min(.1,dt)/.004));float step=(float)Math.Min(.1,dt)/steps;
        for(int i=0;i<steps;i++){pressVelocity+=(420*(destination-press)-28*pressVelocity)*step;press+=pressVelocity*step;}
        if(changed||Math.Abs(destination-press)>.0005f||Math.Abs(pressVelocity)>.002f||navigation.IsRunning&&navigation.Elapsed.TotalSeconds<.85)Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g=e.Graphics;g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.Half;
        float inset=press*(DirectionalArrow?2.4f:1.3f)*Density;
        float stroke=Math.Max(1,Density),edge=HasStroke?stroke/2f:.5f;
        RectangleF r=new RectangleF(edge+inset,edge+inset*.55f,Width-edge*2-inset*2,Height-edge*2-inset*1.1f);Color fill=Enabled?UI.Blend(Fill,FillHover,blend):UI.Card;
        using(GraphicsPath p=UI.Round(r,Radius*Density)) {
            using(Brush b=new SolidBrush(fill))g.FillPath(b,p);
            if(HasStroke)using(Pen pen=new Pen(Stroke,stroke))g.DrawPath(pen,p);
        }
        Color fg=Enabled?ForeColor:UI.Dim;Rectangle text=Rectangle.Round(r);
        if(ExternalLink){text.X+=(int)(4*Density);text.Width-=(int)(26*Density);}
        text.Y+=(int)Math.Round(press*1.8f*Density);
        PaintLabel(g,text,fg);
        if(DirectionalArrow) {
            GraphicsState state=g.Save();g.TranslateTransform(Width-46*Density+blend*5*Density,Height/2f);g.RotateTransform(UI.Axis);
            using(Pen p=new Pen(fg,2*Density)){g.DrawLine(p,-12*Density,0,12*Density,0);g.DrawLine(p,12*Density,0,5*Density,-7*Density);g.DrawLine(p,12*Density,0,5*Density,7*Density);}g.Restore(state);
        }
        if(ExternalLink){
            double t=navigation.IsRunning?navigation.Elapsed.TotalSeconds:2;
            float launch=t<.85?(float)Math.Sin(Math.Min(1,t/.85)*Math.PI):0;
            float move=blend*2+launch*3;
            GraphicsState arrow=g.Save();g.TranslateTransform(Width-16*Density+move*Density,Height/2f-move*Density);
            using(Pen pen=new Pen(fg,1.25f*Density)){g.DrawLine(pen,-4*Density,4*Density,4*Density,-4*Density);g.DrawLine(pen,-2*Density,-4*Density,4*Density,-4*Density);g.DrawLine(pen,4*Density,-4*Density,4*Density,2*Density);}g.Restore(arrow);
            if(launch>.001f){
                GraphicsState clip=g.Save();using(GraphicsPath shape=UI.Round(r,Radius*Density))g.SetClip(shape,CombineMode.Intersect);
                Color glow=Fill==UI.Accent?UI.C("FFF5DC"):UI.Accent;
                float u=(float)Math.Min(1,t/.30),y=Height-3*Density;
                if(UI.IsWabi){using(Brush b=new SolidBrush(glow))g.FillRectangle(b,0,y,Width*u,2*Density);}
                else if(UI.IsBauhaus){
                    Color[] colors={UI.Accent,UI.C("D6AE35"),UI.C("244E86")};
                    for(int i=0;i<3;i++)using(Brush b=new SolidBrush(Color.FromArgb((int)(230*launch),colors[i])))g.FillRectangle(b,(Width*u)-Width*.34f+i*Width*.115f,y,Width*.10f,3*Density);
                }else if(UI.IsDark){
                    using(LinearGradientBrush beam=new LinearGradientBrush(new RectangleF(0,0,Width,Height),Color.Transparent,Color.FromArgb((int)(210*launch),glow),0f))g.FillRectangle(beam,0,y,Width*u,2*Density);
                }else{
                    using(Brush b=new SolidBrush(Color.FromArgb((int)(230*launch),glow)))g.FillPolygon(b,new[]{new PointF(0,y),new PointF(Width*u,y),new PointF(Width*u-5*Density,Height),new PointF(0,Height)});
                }
                g.Restore(clip);
            }
        }
        if(Focused&&ShowFocusCues){Rectangle focus=ClientRectangle;focus.Inflate(-(int)(5*Density),-(int)(5*Density));ControlPaint.DrawFocusRectangle(g,focus,fg,fill);}
    }
    protected virtual void PaintLabel(Graphics g,Rectangle bounds,Color color){bool left=UI.IsWabi&&!DirectionalArrow;if(left){bounds.X+=(int)(10*Density);bounds.Width-=(int)(10*Density);}TextRenderer.DrawText(g,Text,Font,bounds,color,(left?TextFormatFlags.Left:TextFormatFlags.HorizontalCenter)|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);}
    public void Navigate(Action open){
        if(linkTimer!=null)return;navigation.Restart();Invalidate();
        linkTimer=new System.Windows.Forms.Timer();linkTimer.Interval=150;
        linkTimer.Tick+=delegate{
            System.Windows.Forms.Timer delay=linkTimer;linkTimer=null;delay.Stop();delay.Dispose();
            if(!IsDisposed&&!Disposing)open();
        };linkTimer.Start();
    }
    protected override void Dispose(bool disposing){
        if(disposing&&linkTimer!=null){linkTimer.Stop();linkTimer.Dispose();linkTimer=null;}base.Dispose(disposing);
    }

}
class Dot:Control {
    public Color DotColor=Color.Gray;
    public Dot(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    protected override void OnPaint(PaintEventArgs e){e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(Brush b=new SolidBrush(DotColor))e.Graphics.FillEllipse(b,0,0,Width-1,Height-1);}
}
// All lettering, perforations and barcodes remain vector paths at every DPI.
// Perspective transforms cached points, with no fixed-size bitmap intermediate.
struct HeroPose {public double Time,Angle,Velocity,Activity;public PointF Pointer;}
sealed class EntryLayer:IDisposable {
    internal readonly Rectangle Bounds;readonly RasterFrame image;readonly double delay,duration;readonly float lift;int disposed;
    public EntryLayer(Bitmap source,Rectangle bounds,double delay,double duration,float lift){
        Bounds=TrimPaper(source,bounds);this.delay=delay;this.duration=duration;this.lift=lift;
        image=new RasterFrame(source.Clone(Bounds,System.Drawing.Imaging.PixelFormat.Format32bppPArgb));
    }
    static Rectangle TrimPaper(Bitmap source,Rectangle area){
        int paper=source.GetPixel(0,0).ToArgb(),left=area.Width,right=-1,top=area.Height,bottom=-1;int[] row=new int[area.Width];
        System.Drawing.Imaging.BitmapData bits=source.LockBits(area,System.Drawing.Imaging.ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try{for(int y=0;y<area.Height;y++){
            Marshal.Copy(IntPtr.Add(bits.Scan0,y*bits.Stride),row,0,row.Length);
            for(int x=0;x<row.Length;x++)if(row[x]!=paper){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=y;}
        }}finally{source.UnlockBits(bits);}
        if(right<0)return new Rectangle(area.Left,area.Top,1,1);
        return new Rectangle(area.Left+left,area.Top+top,right-left+1,bottom-top+1);
    }
    public void Paint(Graphics g,double elapsed){
        int y;float opacity;Sample(elapsed,out y,out opacity);image.Draw(g,Bounds.X,y,opacity);
    }
    internal bool IsNative{get{return image.IsNative;}}
    internal bool IsMoving(double elapsed){return elapsed<delay+duration;}
    internal void Paint(RasterFrame.Canvas canvas,double elapsed){int y;float opacity;Sample(elapsed,out y,out opacity);image.Draw(canvas,Bounds.X,y,opacity,Rectangle.Empty);}
    void Sample(double elapsed,out int y,out float opacity){
        double x=Math.Max(0,Math.Min(1,(elapsed-delay)/duration));y=Bounds.Y;opacity=(float)(1-Math.Pow(1-x,3));
        if(x>0&&x<1){double spring=1-Math.Exp(-7*x)*(Math.Cos(7*x)+Math.Sin(7*x));y+=(int)Math.Round(lift*(1-spring));}
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)image.Dispose();}
}
static class WindowHandoff {
    [DllImport("user32.dll")]internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)]static extern IntPtr SetOwner64(IntPtr window,int index,IntPtr owner);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW",SetLastError=true)]static extern IntPtr SetOwner32(IntPtr window,int index,IntPtr owner);
    [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    public static void Link(IntPtr loading,IntPtr main){
        if(IntPtr.Size==8)SetOwner64(loading,-8,main);else SetOwner32(loading,-8,main);
        SetWindowPos(main,loading,0,0,0,0,0x0001|0x0002|0x0010|0x0200);
    }
    public static void Complete(IntPtr loading,IntPtr main){
        IntPtr foreground=GetForegroundWindow();
        // Only pass our current foreground to our own main window. Respect an Alt+Tab away.
        if(foreground==loading||foreground==main)SetForegroundWindow(main);
    }
}
sealed class HeroHandoff:IDisposable {
    public Rectangle ScreenTarget;public IntPtr MainWindow;public EntryLayer[] Layers;
    public StagePass.TicketScene Ticket;
    public Action<HeroPose,Action> Land;public Action Resume;
    public void Dispose(){if(Layers!=null)foreach(EntryLayer layer in Layers)layer.Dispose();if(Ticket!=null)Ticket.Dispose();}
}
class StagePass:Control {
    internal const double Revolutions=2.0;
    internal const double ClickSpinSeconds=1.55;
    internal static double SpinTurns(double progress){return Revolutions*MotionProfile.Coast(progress);}
    internal const float SceneWidth=420,SceneHeight=420;
    internal const float TicketHeaderSize=12,TicketPrimarySize=46,TicketSecondarySize=47,TicketNoteSize=12,TicketNumberSize=101;
    internal static float SceneScale(float width,float height){return Math.Min(width/SceneWidth,height/SceneHeight);}
    internal static SizeF PageViewport(float width,float height){return new SizeF(Math.Min(440,width*.43f),Math.Min(430,height-300)+16);}
    internal bool LeftSide;internal bool GameShiny;
    internal float TransitionTilt=float.NaN;
    internal float RestTilt{get{return (ResourceKind>=12&&ResourceKind<=16?8:ResourceKind>=7&&ResourceKind<=11?4:UI.IsDark?5:UI.IsBauhaus?11:14)*(LeftSide?1:-1);}}
    internal float VisibleTilt{get{return float.IsNaN(TransitionTilt)?RestTilt:TransitionTilt;}}
    internal sealed class Layer:IDisposable {
        public GraphicsPath Path;public PointF[] Points,Projected;public byte[] Types;public Color Color;public SolidBrush Brush;
        public Layer(GraphicsPath p,Color c){Path=p;Color=c;Brush=new SolidBrush(c);Cache();}
        void Cache(){Path.Flatten(null,.35f);Points=Path.PathPoints;Types=Path.PathTypes;Projected=new PointF[Points.Length];}
        public void Append(GraphicsPath p){Path.AddPath(p,false);p.Dispose();Cache();}
        public void Dispose(){Brush.Dispose();Path.Dispose();}
    }
    readonly List<Layer> front=new List<Layer>(),back=new List<Layer>();
    internal sealed class TicketScene:IDisposable {
        internal readonly List<Layer> Front=new List<Layer>(),Back=new List<Layer>();
        public void Dispose(){foreach(Layer layer in Front)layer.Dispose();foreach(Layer layer in Back)layer.Dispose();Front.Clear();Back.Clear();}
    }
    internal TicketScene CopyTicketScene(){
        TicketScene scene=new TicketScene();try{foreach(Layer layer in front)scene.Front.Add(new Layer((GraphicsPath)layer.Path.Clone(),layer.Color));foreach(Layer layer in back)scene.Back.Add(new Layer((GraphicsPath)layer.Path.Clone(),layer.Color));return scene;}catch{scene.Dispose();throw;}
    }
    internal void AdoptTicketScene(TicketScene scene){
        foreach(Layer layer in front)layer.Dispose();foreach(Layer layer in back)layer.Dispose();front.Clear();back.Clear();front.AddRange(scene.Front);back.AddRange(scene.Back);scene.Front.Clear();scene.Back.Clear();Invalidate();
    }
    GraphicsPath grid;
    readonly Stopwatch clock=Stopwatch.StartNew();
    double lastTime,spinStart=-10,animationOffset;PointF pointer;bool reduced,held,suppressClear;
    bool busy;double orbitOrigin=-.6,orbitVelocity=.26,orbitChangedAt,visualOrigin,visualChangedAt;
    double AnimationTime{get{return PreviewTime>=0?PreviewTime:clock.Elapsed.TotalSeconds+animationOffset;}}
    internal double OrbitAt(double time){
        double dt=Math.Max(0,time-orbitChangedAt),target=busy?1.5:.26;
        return orbitOrigin+target*dt+(orbitVelocity-target)*.25*(1-Math.Exp(-dt/.25));
    }
    double ActivityAt(double t){
        double target=busy?1:0;return target+(visualOrigin-target)*Math.Exp(-Math.Max(0,t-visualChangedAt)/.18);
    }
    public bool Busy{
        get{return busy;}
        set{
            if(busy==value)return;double now=AnimationTime,dt=Math.Max(0,now-orbitChangedAt),target=busy?1.5:.26;
            orbitOrigin=OrbitAt(now);orbitVelocity=target+(orbitVelocity-target)*Math.Exp(-dt/.25);
            orbitChangedAt=now;visualOrigin=ActivityAt(now);visualChangedAt=now;busy=value;
        }
    }
    double previewTime=-1;
    public double PreviewTime{
        get{return previewTime;}
        set{
            if(value>=0&&(previewTime<0||value<previewTime)){orbitOrigin=-.6;orbitVelocity=.26;orbitChangedAt=value;visualOrigin=busy?1:0;visualChangedAt=value;}
            previewTime=value;
        }
    }
    public double SpinProgress=-1;internal double SpinPhase=-1;public int ResourceKind;
    public long PaintCount;public double PaintMilliseconds;public Action<double> PaintCompleted;
    public bool Reduced{get{return reduced;}set{reduced=value;pointer=PointF.Empty;Invalidate();}}
    public StagePass():this(0){}
    public StagePass(int resourceKind){
        ResourceKind=resourceKind;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        Cursor=Cursors.Hand;AccessibleName="舞台通行证，点击翻面";AccessibleRole=AccessibleRole.Graphic;
        Rebuild();MouseClick+=delegate(object s,MouseEventArgs e){if(e.Button==MouseButtons.Left)Spin();};
    }
    public void Advance() {
        double now=clock.Elapsed.TotalSeconds,dt=Math.Min(.05,now-lastTime);lastTime=now;if(reduced||held)return;
        Form owner=FindForm();if(owner==null)return;
        Point p=owner.PointToClient(Cursor.Position);PointF target=PointF.Empty;
        if(owner.ClientRectangle.Contains(p))target=new PointF((p.X/(float)owner.ClientSize.Width-.5f)*2,(p.Y/(float)owner.ClientSize.Height-.5f)*2);
        TrackPointer(target,dt);Invalidate();
    }
    internal void TrackPointer(PointF target,double dt){
        if(reduced||held)return;
        float alpha=(float)(1-Math.Exp(-Math.Max(0,dt)/.11));
        pointer.X+=(target.X-pointer.X)*alpha;pointer.Y+=(target.Y-pointer.Y)*alpha;
    }
    public void RestartEntrance(){
        clock.Restart();lastTime=0;spinStart=-10;animationOffset=0;held=false;orbitOrigin=-.6;orbitVelocity=.26;orbitChangedAt=0;pointer=PointF.Empty;Invalidate();
    }
    internal HeroPose CapturePose(){
        double t=AnimationTime,dt=Math.Max(0,t-orbitChangedAt),target=busy?1.5:.26;
        return new HeroPose{Time=t,Angle=OrbitAt(t),Velocity=target+(orbitVelocity-target)*Math.Exp(-dt/.25),Pointer=pointer,Activity=ActivityAt(t)};
    }
    internal void HoldPose(HeroPose pose){
        held=true;previewTime=pose.Time;orbitOrigin=pose.Angle;orbitVelocity=pose.Velocity;orbitChangedAt=pose.Time;
        pointer=pose.Pointer;visualOrigin=pose.Activity;visualChangedAt=pose.Time;busy=false;SpinProgress=-1;Invalidate();
    }
    internal void ResumePose(){
        if(!held)return;HeroPose pose=CapturePose();clock.Restart();lastTime=0;spinStart=-10;
        animationOffset=pose.Time;previewTime=-1;orbitOrigin=pose.Angle;orbitVelocity=pose.Velocity;orbitChangedAt=pose.Time;visualOrigin=pose.Activity;visualChangedAt=pose.Time;held=false;Invalidate();
    }
    internal void RenderOverlay(Graphics g,RectangleF destination,float rotation){
        GraphicsState state=g.Save();bool previous=suppressClear;
        try{
            suppressClear=true;g.TranslateTransform(destination.X+destination.Width/2,destination.Y+destination.Height/2);
            g.RotateTransform(rotation);g.ScaleTransform(destination.Width/Width,destination.Height/Height);
            g.TranslateTransform(-Width/2f,-Height/2f);RenderFrame(g);
        }finally{suppressClear=previous;g.Restore(state);}
    }
    public void Spin(){if(!reduced&&clock.Elapsed.TotalSeconds-spinStart>ClickSpinSeconds){spinStart=clock.Elapsed.TotalSeconds;Invalidate();}}
    static void Shape(List<Layer> list,GraphicsPath p,Color c){
        p.FillMode=FillMode.Winding;
        if(list.Count>0&&list[list.Count-1].Color.ToArgb()==c.ToArgb())list[list.Count-1].Append(p);
        else list.Add(new Layer(p,c));
    }
    static void Rect(List<Layer> list,RectangleF r,Color c,float radius){Shape(list,UI.Round(r,radius),c);}
    static void TextPath(List<Layer> list,string text,float x,float y,float size,bool bold,Color color,bool latin,float maxWidth=0,string faceOverride=null){
        GraphicsPath p=new GraphicsPath();
        using(FontFamily family=new FontFamily(faceOverride??(latin?UI.LatinFace:UI.BodyFace)))
        using(StringFormat format=(StringFormat)StringFormat.GenericTypographic.Clone())
            p.AddString(text,family,(int)(bold?FontStyle.Bold:FontStyle.Regular),size,new PointF(x,y),format);
        if(maxWidth>0&&p.GetBounds().Width>maxWidth){
            float fit=maxWidth/p.GetBounds().Width;
            using(Matrix transform=new Matrix()){transform.Translate(x,y);transform.Scale(fit,fit);transform.Translate(-x,-y);p.Transform(transform);}
        }
        p.FillMode=FillMode.Winding;Shape(list,p,color);
    }
    static GraphicsPath Outline(){
        GraphicsPath p=new GraphicsPath();
        const float corner=14,top=-164,bottom=164;
        p.AddArc(-130-corner,top-corner,corner*2,corner*2,90,-90);
        p.AddLine(-130+corner,top,130-corner,top);p.AddArc(130-corner,top-corner,corner*2,corner*2,180,-90);
        p.AddLine(130,top+corner,130,91);p.AddArc(122,91,16,16,270,-180);
        p.AddLine(130,107,130,bottom-corner);p.AddArc(130-corner,bottom-corner,corner*2,corner*2,270,-90);
        p.AddLine(130-corner,bottom,-130+corner,bottom);p.AddArc(-130-corner,bottom-corner,corner*2,corner*2,0,-90);
        p.AddLine(-130,bottom-corner,-130,107);p.AddArc(-138,91,16,16,90,-180);
        p.AddLine(-130,91,-130,top+corner);p.CloseFigure();return p;
    }
    public void Rebuild(){
        foreach(Layer l in front)l.Dispose();foreach(Layer l in back)l.Dispose();front.Clear();back.Clear();
        if(ResourceKind>=12&&ResourceKind<=16){BuildMillionTicket();Invalidate();return;}
        if(ResourceKind>=7&&ResourceKind<=11){BuildWabiTicket();Invalidate();return;}
        Color cream=Color.FromArgb(255,245,220),ink=Color.FromArgb(29,39,37);
        for(int face=0;face<2;face++){
            List<Layer> list=face==0?front:back;bool reverse=face==1;
            GraphicsPath shadow=Outline();using(Matrix m=new Matrix()){m.Translate(7,10);shadow.Transform(m);}Shape(list,shadow,Color.FromArgb(UI.IsDark?65:22,0,0,0));
            Color paper=reverse?ink:UI.IsDark?UI.Accent:cream;
            Color type=reverse?cream:ink;
            Shape(list,Outline(),paper);
            TextPath(list,ResourceKind==7?"CINDERELLA GIRLS":ResourceKind>=8&&ResourceKind<=11?"346 PRODUCTION":ResourceKind==5?"SONG FOR PRISM":GameShiny?"SHINY COLORS":ResourceKind>0?"STAGE / CONNECTIONS":"GAKUEN IDOLM@STER",-109,-134,TicketHeaderSize,true,type,true,218);
            Rect(list,new RectangleF(-109,-105,36,3),type,0);
            if(ResourceKind==7){
                if(!reverse){
                    TextPath(list,"STARLIGHT",-114,-88,TicketPrimarySize,true,type,true,218);
                    TextPath(list,"STAGE",-112,-29,TicketSecondarySize,true,type,true,218);
                    TextPath(list,"346 的舞台，等你登场。",-108,60,TicketNoteSize,true,type,false,216);
                    CgssEmblem.Append(list,new RectangleF(58,-139,58,42),BrandPalette.TicketInk);
                }else{
                    TextPath(list,"03",-114,-100,TicketNumberSize,true,type,true,218);
                    TextPath(list,"STAGE / PASS",-112,20,34,true,type,true,218);
                }
            } else if(ResourceKind>=8&&ResourceKind<=11){
                int route=ResourceKind-8;
                string[] top={"STORY","BILIBILI","OFFICIAL","COMMUNITY"},bottom={"ARCHIVE","CHANNEL","PORTAL","IMS"};
                string[] notes={"从 346 的日常，走进灰姑娘的故事。","舞台之外，也有 346 制作人的声音。","346 PRODUCTION 的官方讯息。","和灰姑娘的制作人，坐在同一排。"};
                TextPath(list,reverse?(route+1).ToString("00"):top[route],-114,reverse?-100:-88,reverse?TicketNumberSize:TicketPrimarySize,true,type,true,218);
                TextPath(list,reverse?"STAGE / 346":bottom[route],-112,reverse?20:-29,reverse?34:TicketSecondarySize,true,type,true,218);
                TextPath(list,notes[route],-108,60,TicketNoteSize,true,type,false,216);
            } else if(ResourceKind>0){
                string[] top={"WIKI","IM@S",GameShiny?"SHINY":"FIRST","IMS","SHINY","FIRST"},bottom={"GUIDE","CHANNEL",GameShiny?"WORLD":"STAR","WEB","COLORS","STORY"},notes={"热爱，自有答案。","官方 / 十六夜カズヤP",GameShiny?"每一束光，都有自己的色彩。":"下一站，初星学园。","欢迎，制作人。","让闪耀，在此相连。","下一章，从这里开始。"};
                int resource=Math.Max(0,Math.Min(5,ResourceKind-1));
                TextPath(list,reverse?(resource+1).ToString("00"):top[resource],-114,reverse?-100:-88,reverse?TicketNumberSize:TicketPrimarySize,true,type,true,218);
                TextPath(list,reverse?"EXPLORE":bottom[resource],-112,reverse?20:-29,reverse?34:TicketSecondarySize,true,type,true,218);
                TextPath(list,notes[resource],-108,60,TicketNoteSize,true,type,false,216);
                if(!reverse){
                    Color signal=BrandPalette.TicketInk;
                    if(resource==4){TicketEmblems.Append(true,list,new RectangleF(63,-114,47,47),BrandPalette.TicketInk);}
                    else if(resource==0||resource==5){
                        Rect(list,new RectangleF(65,-105,41,31),signal,2);
                        Rect(list,new RectangleF(68,-101,15,22),paper,0);Rect(list,new RectangleF(87,-101,15,22),paper,0);
                        for(int row=0;row<3;row++){Rect(list,new RectangleF(71,-97+row*6,9,1.5f),signal,0);Rect(list,new RectangleF(90,-97+row*6,9,1.5f),signal,0);}
                    }else if(resource==1){
                        Rect(list,new RectangleF(62,-102,47,31),signal,4);
                        Rect(list,new RectangleF(72,-92,4,9),paper,0);Rect(list,new RectangleF(94,-92,4,9),paper,0);
                        GraphicsPath antenna=new GraphicsPath();antenna.AddPolygon(new[]{new PointF(69,-112),new PointF(72,-114),new PointF(82,-103),new PointF(79,-101)});antenna.AddPolygon(new[]{new PointF(99,-114),new PointF(102,-112),new PointF(92,-101),new PointF(89,-103)});Shape(list,antenna,signal);
                    }else if(resource==2){
                        GraphicsPath star=new GraphicsPath();star.AddPolygon(new[]{new PointF(85,-115),new PointF(91,-99),new PointF(107,-93),new PointF(91,-87),new PointF(85,-71),new PointF(79,-87),new PointF(63,-93),new PointF(79,-99)});Shape(list,star,signal);
                    }else if(resource==3){
                        Rect(list,new RectangleF(65,-108,42,36),signal,3);Rect(list,new RectangleF(68,-97,36,22),paper,0);
                        Rect(list,new RectangleF(74,-112,3,9),signal,0);Rect(list,new RectangleF(95,-112,3,9),signal,0);
                        for(int row=0;row<2;row++)for(int col=0;col<3;col++)Rect(list,new RectangleF(73+col*10,-92+row*9,4,4),signal,0);
                    }else{
                        GraphicsPath prism=new GraphicsPath();prism.AddPolygon(new[]{new PointF(85,-116),new PointF(110,-75),new PointF(60,-75)});Shape(list,prism,signal);
                        GraphicsPath facet=new GraphicsPath();facet.AddPolygon(new[]{new PointF(85,-106),new PointF(94,-82),new PointF(76,-82)});Shape(list,facet,paper);
                    }
                }
            }else if(!reverse){
                TextPath(list,"LIVE",-114,-88,TicketPrimarySize,true,type,true,218);TextPath(list,"PASS",-112,-29,TicketSecondarySize,true,type,true,218);
                TextPath(list,"下一站，聚光灯下。",-108,60,TicketNoteSize,true,type,false,216);
                TicketEmblems.Append(false,list,new RectangleF(63,-114,47,47),BrandPalette.TicketInk);
            }else{
                TextPath(list,"01",-114,-100,TicketNumberSize,true,type,true,218);TextPath(list,"STAGE / PASS",-112,20,34,true,type,true,218);
            }
            for(int x=-124;x<124;x+=8)Rect(list,new RectangleF(x,98,4,1),Color.FromArgb(150,type),0);
            for(int x=-108;x<12;x+=4)Rect(list,new RectangleF(x,116,x%3==0?2.5f:1,20),type,0);
            TextPath(list,ResourceKind==7?"03 / ALL ACCESS":ResourceKind>=8&&ResourceKind<=11?(ResourceKind-7).ToString("00")+" / 346":"01 / ALL ACCESS",23,116,10,true,type,true);
        }
        Invalidate();
    }
    void BuildMillionTicket(){
        Color paper=UI.C(UI.IsDark?"C7A963":"FFF8E9"),ink=UI.C(UI.IsDark?"1D2725":"1E4058"),orange=UI.C(UI.IsDark?"493916":"DE9A25"),pink=UI.C("C95F77"),night=UI.C(UI.IsDark?"1D2725":"244C6C");
        int route=ResourceKind-13;int[] numbers={3,2,1,4};
        string[] top={"STORY","BILIBILI","OFFICIAL","COMMUNITY"};string[] bottom={"ARCHIVE","CHANNEL","STAGE","CIRCLE"};
        string[] notes={"从故事认识每一颗星。","一起分享舞台的热爱。","走进 765 的剧场。","和制作人一同应援。"};
        for(int face=0;face<2;face++){
            List<Layer> list=face==0?front:back;bool reverse=face==1;Color color=reverse?(UI.IsDark?UI.C("F6E8C9"):paper):ink;
            GraphicsPath shadow=Outline();using(Matrix m=new Matrix()){m.Translate(7,10);shadow.Transform(m);}Shape(list,shadow,Color.FromArgb(26,21,43,59));
            Shape(list,Outline(),reverse?night:paper);
            TextPath(list,ResourceKind==12?"765 MILLIONSTARS":"765 / CONNECTIONS",-108,-134,11,true,color,true,207);
            Rect(list,new RectangleF(-108,-105,36,3),reverse?paper:orange,0);
            if(reverse){
                TextPath(list,ResourceKind==12?"04":numbers[route].ToString("00"),-110,-100,95,true,color,true,205);
                TextPath(list,ResourceKind==12?"STAGE / PASS":"EXPLORE",-108,20,29,true,color,true,205);
                TextPath(list,"下一场，我们一起见。",-108,65,11,false,color,false,205);
            }else{
                TextPath(list,ResourceKind==12?"MILLION":top[route],-110,-78,38,true,ink,true,158);
                TextPath(list,ResourceKind==12?"LIVE!":bottom[route],-110,-26,40,true,ink,true,207);
                TextPath(list,ResourceKind==12?"每一份心动，都有舞台。":notes[route],-108,60,11,false,ink,false,210);
                if(ResourceKind==12)MillionEmblem.Append(list,new RectangleF(65,-104,46,44),orange);
                else MillionRouteIcon(list,route,ink,paper,pink);
            }
            for(int x=-124;x<124;x+=8)Rect(list,new RectangleF(x,98,4,1),Color.FromArgb(150,color),0);
            for(int x=-108;x<12;x+=4)Rect(list,new RectangleF(x,116,x%3==0?2.5f:1,20),color,0);
            TextPath(list,ResourceKind==12?"04 / ALL ACCESS":numbers[route].ToString("00")+" / 765",23,116,10,true,color,true);
        }
    }
    static void MillionRouteIcon(List<Layer> list,int route,Color ink,Color paper,Color pink){
        if(route==2){GraphicsPath star=new GraphicsPath();star.AddPolygon(new[]{new PointF(87,-110),new PointF(93,-96),new PointF(107,-90),new PointF(93,-84),new PointF(87,-70),new PointF(81,-84),new PointF(67,-90),new PointF(81,-96)});Shape(list,star,ink);}
        else if(route==1){Rect(list,new RectangleF(65,-96,44,27),ink,4);Rect(list,new RectangleF(75,-88,4,9),paper,1);Rect(list,new RectangleF(95,-88,4,9),paper,1);GraphicsPath a=new GraphicsPath();a.AddPolygon(new[]{new PointF(70,-107),new PointF(73,-109),new PointF(83,-98),new PointF(80,-96)});a.AddPolygon(new[]{new PointF(100,-109),new PointF(103,-107),new PointF(93,-96),new PointF(90,-98)});Shape(list,a,ink);}
        else if(route==0){Rect(list,new RectangleF(65,-100,42,30),ink,2);Rect(list,new RectangleF(68,-97,16,24),paper,0);Rect(list,new RectangleF(88,-97,16,24),paper,0);for(int i=0;i<3;i++){Rect(list,new RectangleF(71,-93+i*6,10,1.5f),ink,0);Rect(list,new RectangleF(91,-93+i*6,10,1.5f),ink,0);}}
        else{GraphicsPath heads=new GraphicsPath();heads.AddEllipse(81,-102,12,12);heads.AddEllipse(65,-96,9,9);heads.AddEllipse(99,-96,9,9);Shape(list,heads,ink);Rect(list,new RectangleF(77,-87,20,14),ink,5);Rect(list,new RectangleF(63,-84,13,11),ink,4);Rect(list,new RectangleF(98,-84,13,11),ink,4);}
    }
    void BuildWabiTicket(){
        Color paper=UI.C(UI.IsDark?"C7A963":"FFF9ED"),ink=UI.C(UI.IsDark?"1D2725":"253B57"),blue=UI.C(UI.IsDark?"493916":"657F91"),gold=UI.C(UI.IsDark?"493916":"BC965E"),reversePaper=UI.C(UI.IsDark?"1D2725":"30577F");
        int[] numbers={3,2,1,4};int route=ResourceKind-8;
        string[] top={"STORY","BILIBILI","OFFICIAL","COMMUNITY"};string[] bottom={"ARCHIVE","CHANNEL","PORTAL","CIRCLE"};
        string[] notes={"把喜欢的故事，再翻开一页。","和制作人一起分享热爱。","从这里遇见 346 的世界。","和同路的人，再次相遇。"};
        for(int face=0;face<2;face++){
            List<Layer> list=face==0?front:back;bool reverse=face==1;Color baseColor=reverse?reversePaper:paper,type=reverse?(UI.IsDark?UI.C("F6E8C9"):paper):ink;
            GraphicsPath shadow=Outline();using(Matrix m=new Matrix()){m.Translate(6,9);shadow.Transform(m);}Shape(list,shadow,Color.FromArgb(27,37,59,87));
            Shape(list,Outline(),baseColor);
            Rect(list,new RectangleF(-110,-141,220,1.5f),gold,0);
            Rect(list,new RectangleF(-110,-135,220,1),gold,0);
            TextPath(list,ResourceKind==7?"CINDERELLA GIRLS":"346 / CONNECTIONS",-108,-123,10,true,type,true,198,ResourceKind==7?null:UI.UprightLatin);
            Rect(list,new RectangleF(-108,-102,33,3),gold,0);
            if(reverse){
                TextPath(list,ResourceKind==7?"03":numbers[route].ToString("00"),-110,-88,90,true,type,true,205);
                TextPath(list,ResourceKind==7?"STAGE / PASS":"EXPLORE",-108,18,28,true,type,true,205);
                TextPath(list,"下一次相遇，仍然闪耀。",-108,63,11,false,type,false,210);
            }else{
                TextPath(list,ResourceKind==7?"STARLIGHT":top[route],-110,-73,34,true,ink,true,160);
                TextPath(list,ResourceKind==7?"STAGE":bottom[route],-110,-24,37,true,ink,true,210);
                TextPath(list,ResourceKind==7?"星光依旧，今晚为你亮起。":notes[route],-108,60,11,false,ink,false,210);
                if(ResourceKind==7)CgssEmblem.Append(list,new RectangleF(65,-101,44,32),blue);
                else DrawDecoIcon(list,route,blue,paper,gold);
            }
            for(int x=-124;x<124;x+=8)Rect(list,new RectangleF(x,98,4,1),Color.FromArgb(150,type),0);
            for(int x=-108;x<12;x+=4)Rect(list,new RectangleF(x,116,x%3==0?2.5f:1,20),type,0);
            TextPath(list,ResourceKind==7?"03 / ALL ACCESS":numbers[route].ToString("00")+" / 346",23,116,10,true,type,true,0,UI.UprightLatin);
        }
    }
    static void DrawDecoIcon(List<Layer> list,int route,Color ink,Color paper,Color gold){
        if(route==0){
            Rect(list,new RectangleF(66,-100,42,29),ink,2);Rect(list,new RectangleF(69,-97,16,23),paper,0);Rect(list,new RectangleF(89,-97,16,23),paper,0);
            for(int i=0;i<3;i++){Rect(list,new RectangleF(72,-93+i*6,10,1.5f),ink,0);Rect(list,new RectangleF(92,-93+i*6,10,1.5f),ink,0);}
        }else if(route==1){
            Rect(list,new RectangleF(65,-97,45,28),ink,4);Rect(list,new RectangleF(75,-88,4,9),paper,1);Rect(list,new RectangleF(96,-88,4,9),paper,1);
            GraphicsPath antenna=new GraphicsPath();antenna.AddPolygon(new[]{new PointF(72,-107),new PointF(75,-109),new PointF(83,-99),new PointF(80,-97)});antenna.AddPolygon(new[]{new PointF(101,-109),new PointF(104,-107),new PointF(94,-97),new PointF(91,-99)});Shape(list,antenna,ink);
        }else if(route==2){
            GraphicsPath star=new GraphicsPath();star.AddPolygon(new[]{new PointF(87,-110),new PointF(93,-95),new PointF(108,-89),new PointF(93,-83),new PointF(87,-68),new PointF(81,-83),new PointF(66,-89),new PointF(81,-95)});Shape(list,star,ink);
            GraphicsPath spark=new GraphicsPath();spark.AddEllipse(105,-105,5,5);Shape(list,spark,gold);
        }else{
            GraphicsPath heads=new GraphicsPath();heads.AddEllipse(82,-102,12,12);heads.AddEllipse(66,-96,9,9);heads.AddEllipse(100,-96,9,9);Shape(list,heads,ink);
            Rect(list,new RectangleF(78,-87,20,14),ink,5);
            Rect(list,new RectangleF(64,-84,13,11),ink,4);Rect(list,new RectangleF(99,-84,13,11),ink,4);
        }
    }
    static double Ease(double x){x=Math.Max(0,Math.Min(1,x));return 1-Math.Pow(1-x,4);}
    static PointF Project(PointF p,double yaw,double pitch,bool reverse){
        double x=reverse?-p.X:p.X,y=p.Y,z=-Math.Sin(yaw)*x+Math.Sin(pitch)*y,k=850/(850+z);
        return new PointF((float)(x*Math.Cos(yaw)*k),(float)(y*Math.Cos(pitch)*k));
    }
    void PaintTicket(Graphics g,double yaw,double pitch){
        bool reverse=Math.Cos(yaw)<0;List<Layer> layers=reverse?back:front;
        double sinYaw=Math.Sin(yaw),cosYaw=Math.Cos(yaw),sinPitch=Math.Sin(pitch),cosPitch=Math.Cos(pitch);
        foreach(Layer layer in layers){
            if(Math.Abs(yaw)<.00001&&Math.Abs(pitch)<.00001)g.FillPath(layer.Brush,layer.Path);
            else{
                PointF[] points=layer.Projected;for(int i=0;i<points.Length;i++){
                    PointF p=layer.Points[i];double x=reverse?-p.X:p.X,y=p.Y,k=850/(850-sinYaw*x+sinPitch*y);
                    points[i]=new PointF((float)(x*cosYaw*k),(float)(y*cosPitch*k));
                }
                using(GraphicsPath p=new GraphicsPath(points,layer.Types,layer.Path.FillMode))g.FillPath(layer.Brush,p);
            }
        }
        double activity=ActivityAt(AnimationTime);
        if(activity>.001&&!reduced){
            float y=(float)(Math.Sin((PreviewTime>=0?PreviewTime:clock.Elapsed.TotalSeconds+animationOffset)*2.4)*110);
            PointF[] points={Project(new PointF(-126,y),yaw,pitch,reverse),Project(new PointF(126,y),yaw,pitch,reverse),Project(new PointF(126,y+4),yaw,pitch,reverse),Project(new PointF(-126,y+4),yaw,pitch,reverse)};
            using(Brush b=new SolidBrush(UI.IsWabi?Color.FromArgb((int)(85*activity),255,249,237):Color.FromArgb((int)(95*activity),255,245,220)))g.FillPolygon(b,points);
        }
    }
    static void DrawFrameHighlight(Graphics g,Pen pen,PointF[] frame,double end,double length){
        // Split at each corner and wrap, so the line remains attached to the diamond.
        double start=end-length;
        for(int edge=-4;edge<8;edge++){
            double lo=Math.Max(start,edge/4.0),hi=Math.Min(end,(edge+1)/4.0);if(hi<=lo)continue;
            int index=((edge%4)+4)%4;float a=(float)(lo*4-edge),b=(float)(hi*4-edge);
            PointF p=frame[index],q=frame[index+1];
            g.DrawLine(pen,p.X+(q.X-p.X)*a,p.Y+(q.Y-p.Y)*a,p.X+(q.X-p.X)*b,p.Y+(q.Y-p.Y)*b);
        }
    }
    GraphicsPath caption;
    public void RenderFrame(Graphics g){
        GraphicsState state=g.Save();try{OnPaint(new PaintEventArgs(g,ClientRectangle));}finally{g.Restore(state);}
    }
    void DrawCaption(Graphics g){
        if(caption==null){caption=new GraphicsPath();using(FontFamily family=new FontFamily(UI.Available("Georgia","Segoe UI")))caption.AddString("THE NEXT ACT",family,(int)FontStyle.Bold,13,new PointF(-101,182),StringFormat.GenericTypographic);caption.FillMode=FillMode.Winding;}
        using(Brush b=new SolidBrush(UI.Accent))g.FillPath(b,caption);
    }
    protected override void OnPaint(PaintEventArgs e){
        long start=Stopwatch.GetTimestamp();Graphics g=e.Graphics;if(!suppressClear)g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
        double t=PreviewTime>=0?PreviewTime:clock.Elapsed.TotalSeconds+animationOffset;
        double intro=reduced?1:Ease(t/1.05),reveal=reduced?1:Ease((t-.22)/.9);
        float density=SceneScale(Width,Height);g.TranslateTransform(Width/2f,Height/2f);g.ScaleTransform(density,density);
        double orbit=reduced?-.6:OrbitAt(t),activity=reduced?0:ActivityAt(t);
        if(!UI.IsDark&&ResourceKind>=12&&ResourceKind<=16){
            // A rising stage: a single ochre disc and diagonal bands form one
            // constructivist composition, breathing together behind the ticket.
            float phase=reduced?0:(float)orbit,energy=(float)activity;
            float breathe=reduced?0:(float)Math.Sin(phase*2.2);
            GraphicsState composition=g.Save();
            g.RotateTransform(breathe*(4f+energy));
            float rise=reduced?0:(float)Math.Sin(phase*1.9)*(13+energy*5);
            using(Brush sun=new SolidBrush(UI.C(UI.IsDark?"B57C2D":"E1AA3F")))g.FillEllipse(sun,-196,-139+rise,278,278);
            // The two parallel blue planes make a stage-like diagonal, rather
            // than separate decorative pieces around the corners.
            GraphicsState stage=g.Save();g.RotateTransform(-34);
            float slide=reduced?0:(float)Math.Sin(phase*2.1)*(18+energy*8);
            using(Brush blue=new SolidBrush(UI.C(UI.IsDark?"729DB4":"365F77"))){
                g.FillRectangle(blue,-201+slide,-19,402,33);
                g.FillRectangle(blue,-181+slide,20,362,5);
            }
            using(Brush red=new SolidBrush(UI.C(UI.IsDark?"C87569":"B95548")))g.FillRectangle(red,71-slide,32,109,12);
            g.Restore(stage);
            // Compact ascending blocks echo the diagonal stage instead of
            // drawing an enclosing outline around the composition.
            GraphicsState accents=g.Save();g.TranslateTransform(110,-142);g.RotateTransform(-34);
            using(Brush accent=new SolidBrush(UI.C(UI.IsDark?"729DB4":"365F77"))){
                g.FillRectangle(accent,-12,-4,14,14);
                g.FillRectangle(accent,8,-4,9,9);
                g.FillRectangle(accent,23,-4,5,5);
            }
            g.Restore(accents);
            g.Restore(composition);
        }else if(!UI.IsDark&&ResourceKind>=7&&ResourceKind<=11){
            // A symmetric proscenium opens and settles as one Art Deco composition.
            float phase=reduced?0:(float)orbit;
            float breath=reduced?0:(float)Math.Sin(phase*.95);
            float fieldScale=1+breath*.025f,frameScale=1+breath*.012f;
            GraphicsState field=g.Save();g.ScaleTransform(fieldScale,fieldScale);
            PointF[] backdrop={new PointF(0,-174),new PointF(172,0),new PointF(0,174),new PointF(-172,0)};
            using(Brush fill=new SolidBrush(UI.C(UI.IsDark?"344D62":"E3EDF1")))g.FillPolygon(fill,backdrop);
            g.Restore(field);
            GraphicsState frameState=g.Save();g.ScaleTransform(frameScale,frameScale);
            PointF[] frame={new PointF(0,-190),new PointF(186,0),new PointF(0,190),new PointF(-186,0),new PointF(0,-190)};
            using(Pen gold=new Pen(UI.C("BD9B69"),1.2f)){
                g.DrawPolygon(gold,new[]{frame[0],frame[1],frame[2],frame[3]});
                g.DrawPolygon(gold,new[]{new PointF(0,-181),new PointF(177,0),new PointF(0,181),new PointF(-177,0)});
            }
            // A gold highlight travels on the frame, never detaching from its geometry.
            double cycle=(reduced?-.6:orbit)*1.4/(Math.PI*2);cycle-=Math.Floor(cycle);
            using(Pen glint=new Pen(UI.C("C79542"),2.3f+(float)activity*.6f)){
                glint.StartCap=glint.EndCap=LineCap.Round;DrawFrameHighlight(g,glint,frame,cycle,.065+(float)activity*.025f);
            }
            g.Restore(frameState);
            float offset=breath*8;
            using(Brush accent=new SolidBrush(UI.C(UI.IsDark?"A67F6B":"D8B9A4"))){
                g.FillPolygon(accent,new[]{new PointF(-182-offset,0),new PointF(-160-offset,-20),new PointF(-146-offset,-6),new PointF(-168-offset,14)});
                g.FillPolygon(accent,new[]{new PointF(182+offset,0),new PointF(160+offset,-20),new PointF(146+offset,-6),new PointF(168+offset,14)});
            }
        }else if(UI.IsDark){
            using(Pen p=new Pen(UI.Border,1))for(int i=0;i<5;i++)g.DrawEllipse(p,-172+i*9,-172+i*9,344-i*18,344-i*18);
            float phase=(float)(orbit*180/Math.PI);
            using(Pen p=new Pen(UI.Accent,2))g.DrawArc(p,-173,-173,346,346,-90+phase,(float)(intro*210));
            // Theatre spotlights: a restrained orbit at rest, opposing chases while preparing.
            int lights=3;
            for(int i=0;i<lights;i++){
                float intensity=i==0?1:(float)activity;if(intensity<.001f)continue;
                double angle=orbit*(i==1?-.82:1)+i*Math.PI*2/3-Math.PI/2;
                float radius=173-i*18,x=(float)Math.Cos(angle)*radius,y=(float)Math.Sin(angle)*radius;
                using(Brush halo=new SolidBrush(Color.FromArgb((int)((15+12*activity)*intensity),UI.Accent)))g.FillEllipse(halo,x-11,y-11,22,22);
                using(Brush glow=new SolidBrush(Color.FromArgb((int)(255*intensity),UI.Accent)))g.FillEllipse(glow,x-2.6f,y-2.6f,5.2f,5.2f);
                if(i>0)using(Pen trail=new Pen(Color.FromArgb((int)(185*intensity),UI.Accent),1.4f))g.DrawArc(trail,-radius,-radius,radius*2,radius*2,(float)(angle*180/Math.PI-68),56);
            }
        }else if(UI.IsBauhaus){
            // Colour planes share a phase, with no drawn orbit, dial or extra linework.
            // A counter-moving disk, rocking band and floating circle form the rhythm.
            double phase=reduced?0:orbit;
            float energy=(float)activity;
            float drift=reduced?0:1;
            float yellowX=-63+drift*(float)Math.Cos(phase*.8)*(3+energy*5);
            float yellowY=-18+drift*(float)Math.Sin(phase*.8)*(2+energy*4);
            using(Brush b=new SolidBrush(UI.C("D6AE35")))g.FillEllipse(b,yellowX-120,yellowY-120,240,240);
            GraphicsState band=g.Save();
            g.TranslateTransform(0,drift*(float)Math.Sin(phase+.8)*(2+energy*4));
            g.RotateTransform(-24+drift*(float)Math.Sin(phase)*(2+energy*4));
            using(Brush b=new SolidBrush(UI.C("244E86")))g.FillRectangle(b,-168,-36,(float)(336*intro),60);
            g.Restore(band);
            float redX=99+drift*(float)Math.Sin(phase)*(8+energy*14);
            float redY=103+drift*(float)Math.Cos(phase)*(6+energy*10);
            float radius=61*(1+drift*(float)Math.Sin(phase*2)*(.008f+energy*.022f));
            using(Brush b=new SolidBrush(UI.C("C43F2D")))g.FillEllipse(b,redX-radius,redY-radius,radius*2,radius*2);
        }else{
            if(grid==null){grid=new GraphicsPath();for(int x=-192;x<200;x+=24)for(int y=-180;y<190;y+=24)grid.AddRectangle(new RectangleF(x,y,1,1));}
            using(Brush dot=new SolidBrush(Color.FromArgb(70,UI.Dim)))g.FillPath(dot,grid);
            GraphicsState bar=g.Save();g.RotateTransform(UI.Axis);
            using(Brush b=new SolidBrush(UI.Accent))g.FillRectangle(b,-190,-20,(float)(380*intro),40);
            using(Pen p=new Pen(UI.Border,12))g.DrawArc(p,-146,-146,292,292,-120,(float)(intro*280));g.Restore(bar);
            using(Pen p=new Pen(UI.Border,1))g.DrawEllipse(p,-188,-112,376,224);
            double phase=orbit;
            using(Brush b=new SolidBrush(UI.Accent))g.FillEllipse(b,(float)Math.Cos(phase)*188-4,(float)Math.Sin(phase)*112-4,8,8);
        }
        GraphicsState card=g.Save();
        // Counter the portrait's perspective drift without shrinking its new height.
        g.TranslateTransform(UI.IsWabi?0:UI.IsBauhaus?15:0,UI.IsWabi?0:UI.IsBauhaus?-9:UI.IsDark?0:-6-pointer.Y*8);
        g.RotateTransform(VisibleTilt+(reduced?0:pointer.X*1.8f));
        g.TranslateTransform(reduced?0:pointer.X*7,reduced?0:(float)Math.Sin(t*.8)*2+pointer.Y*5);
        double progress=SpinProgress>=0?SpinProgress:(clock.Elapsed.TotalSeconds-spinStart)/ClickSpinSeconds;
        double yaw=reduced?0:pointer.X*.13,pitch=reduced?0:-pointer.Y*.08;
        if(!reduced&&SpinPhase>=0)yaw+=Math.PI*2*SpinPhase;
        else if(!reduced&&progress>=0&&progress<1)yaw+=Math.PI*2*SpinTurns(progress);
        float size=ResourceKind>=12&&ResourceKind<=16?.83f:ResourceKind>=7&&ResourceKind<=11?.83f:UI.IsDark?.78f:UI.IsBauhaus?.71f:.99f;
        g.ScaleTransform(size,size*(float)Math.Max(.001,reveal));PaintTicket(g,yaw,pitch);g.Restore(card);
        if(UI.IsDark)DrawCaption(g);
        double work=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;PaintCount++;PaintMilliseconds+=work;if(PaintCompleted!=null)PaintCompleted(work);
    }
    protected override void Dispose(bool disposing){
        if(disposing){foreach(Layer l in front)l.Dispose();foreach(Layer l in back)l.Dispose();if(caption!=null)caption.Dispose();if(grid!=null)grid.Dispose();}
        base.Dispose(disposing);
    }
}
sealed class StartupScreen:IDisposable {
    readonly object sync=new object();readonly string root;readonly Point anchor;readonly bool hidden;
    readonly System.Threading.Tasks.TaskCompletionSource<bool> shown=new System.Threading.Tasks.TaskCompletionSource<bool>();
    readonly Thread thread;StartupWindow window;Action cancelMain;HeroHandoff pendingHandoff;Rectangle bounds;FormWindowState state;
    volatile bool cancelled,finishRequested;int stage,disposeStarted;Exception failure;
    public bool Cancelled{get{return cancelled;}}
    public Exception Failure{get{lock(sync)return failure;}}
    public int Stage{get{return Interlocked.CompareExchange(ref stage,0,0);}}
    public Rectangle WindowBounds{get{lock(sync)return bounds;}}
    public FormWindowState WindowState{get{lock(sync)return state;}}
    public bool WorkerStopped{get{return !thread.IsAlive;}}
    public StartupScreen(string root):this(root,false){}
    internal StartupScreen(string root,bool hidden){
        this.root=root;this.hidden=hidden;anchor=Cursor.Position;
        thread=new Thread(Run);thread.IsBackground=true;thread.Name="StagePass loading screen";thread.SetApartmentState(ApartmentState.STA);
        thread.Start();shown.Task.Wait(1000);
    }
    void Run(){
        try{
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException,true);
            string day,mode=WindowsAppearance.ReadPreference(Path.Combine(root,"appearance-mode.txt"),out day);
            bool dark=mode=="dark"||mode=="system"&&WindowsAppearance.ReadDark();
            bool reduced=false;UI.Theme(dark,GameExperience.ReadGame(root)==GameExperience.Shiny,GameExperience.ReadGame(root)==GameExperience.Cgss,GameExperience.ReadGame(root)==GameExperience.Million);
            using(StartupWindow screen=new StartupWindow(this,root,anchor,dark,GameExperience.ReadShiny(root),reduced,hidden)){
                lock(sync)window=screen;
                if(Interlocked.CompareExchange(ref disposeStarted,0,0)!=0)return;
                screen.Shown+=delegate{
                    screen.Refresh();shown.TrySetResult(true);
                    if(Interlocked.CompareExchange(ref disposeStarted,0,0)!=0)screen.CloseCompleted();
                    else if(finishRequested){HeroHandoff request;lock(sync)request=pendingHandoff;if(request!=null)screen.BeginHandoff(request);else screen.Finish();}
                };
                Application.Run(screen);
            }
        }catch(Exception ex){lock(sync)failure=ex;shown.TrySetResult(false);}
        finally{lock(sync)window=null;shown.TrySetResult(false);}
    }
    internal void RecordPlacement(Rectangle value,FormWindowState windowState){lock(sync){bounds=value;state=windowState;}}
    internal void UserClosed(){
        Action close;lock(sync){cancelled=true;close=cancelMain;}
        if(close!=null)close();
    }
    void Post(Action<StartupWindow> action){
        StartupWindow screen;lock(sync)screen=window;
        if(screen==null||screen.IsDisposed||!screen.IsHandleCreated)return;
        try{screen.BeginInvoke(new Action(delegate{if(!screen.IsDisposed&&!screen.Disposing)action(screen);}));}catch(InvalidOperationException){}
    }
    public void SetStage(int value){Interlocked.Exchange(ref stage,value);Post(delegate(StartupWindow screen){screen.Invalidate();});}
    public void Attach(MainForm form){
        Action close=delegate{
            if(form.IsDisposed||!form.IsHandleCreated)return;
            try{form.BeginInvoke(new Action(delegate{if(!form.IsDisposed)form.Close();}));}catch(InvalidOperationException){}
        };
        lock(sync)cancelMain=close;
        if(cancelled)close();
    }
    public void Finish(){
        finishRequested=true;SetStage(3);Post(delegate(StartupWindow screen){screen.Finish();});
    }
    public void Finish(MainForm form){
        HeroHandoff request=new HeroHandoff();
        request.ScreenTarget=form.HeroScreenBounds;request.MainWindow=form.Handle;request.Layers=form.CaptureStartupLayers();request.Ticket=form.CopyTicketScene();
        request.Land=delegate(HeroPose pose,Action accepted){
            if(form.IsDisposed||!form.IsHandleCreated)return;
            try{form.BeginInvoke(new Action(delegate{
                if(form.IsDisposed||form.Disposing)return;if(WindowState==FormWindowState.Minimized)form.WindowState=FormWindowState.Minimized;form.AcceptHeroPose(pose);accepted();
            }));}catch(InvalidOperationException){}
        };
        request.Resume=delegate{
            if(form.IsDisposed||!form.IsHandleCreated)return;
            try{form.BeginInvoke(new Action(delegate{if(!form.IsDisposed&&!form.Disposing)form.ResumeHero();}));}catch(InvalidOperationException){}
        };
        lock(sync)pendingHandoff=request;finishRequested=true;SetStage(3);
        if(Failure!=null||WorkerStopped){request.Dispose();form.ResumeHero();return;}
        Post(delegate(StartupWindow screen){screen.BeginHandoff(request);});
    }
    public void Dispose(){
        if(Interlocked.Exchange(ref disposeStarted,1)!=0)return;
        lock(sync)cancelMain=null;
        Post(delegate(StartupWindow screen){screen.CloseCompleted();});
        if(Thread.CurrentThread!=thread&&thread.Join(500)){HeroHandoff request;lock(sync)request=pendingHandoff;if(request!=null)request.Dispose();}
    }
}
sealed class StartupWindow:Form {
    readonly StartupScreen owner;readonly bool dark,bauhaus,reduced,hidden;
    readonly Color paper,ink,muted,accent,line;
    readonly Font nameFont,bodyFont;readonly StagePass hero;
    const double FlightSeconds=1.42;
    readonly Stopwatch time=Stopwatch.StartNew(),fade=new Stopwatch(),travel=new Stopwatch(),settle=new Stopwatch();
    HeroHandoff handoff;RectangleF flightFrom,flightTarget;Rectangle lastHeroDirty,lastEntryDirty;bool waitingLanding,landed,lastCaptionVisible=true;
    FramePump pump;bool completed,stopping,released;long paintCount;internal long PaintCount{get{return Interlocked.Read(ref paintCount);}}
    public StartupWindow(StartupScreen owner,string root,Point anchor,bool dark,bool bauhaus,bool reduced,bool hidden){
        this.owner=owner;this.dark=dark;this.bauhaus=bauhaus;this.reduced=reduced;this.hidden=hidden;
        int game=GameExperience.ReadGame(root);bool cgss=game==GameExperience.Cgss,million=game==GameExperience.Million;
        paper=UI.C(dark?"181716":million?"F5F0E7":cgss?"F7F2E9":bauhaus?"F1E8D3":"EFEADB");ink=UI.C(dark?"F6E8C9":million?"213D50":cgss?"253B57":bauhaus?"182A36":"1E2726");
        muted=UI.C(dark?"B8AA8C":million?"687783":cgss?"687889":"667068");accent=UI.C(dark?"C7A963":million?"C37729":cgss?"BD9B69":bauhaus?"244E86":"DE3923");line=UI.C(dark?"57513F":million?"CEC4B4":cgss?"D3C7B4":"D5D0C1");
        AutoScaleMode=AutoScaleMode.None;DoubleBuffered=true;BackColor=paper;Text="偶像大师 · STAGE PASS";StartPosition=FormStartPosition.Manual;
        SetStyle(ControlStyles.ResizeRedraw,true);
        try{Icon=MainForm.LoadGameIcon(GameExperience.ReadGame(root))??Icon.ExtractAssociatedIcon(Path.Combine(root,"学マス启动器.exe"));}catch{}
        nameFont=new Font(UI.Available(dark?"Georgia":"Bahnschrift","Segoe UI"),18,FontStyle.Bold,GraphicsUnit.Pixel);
        bodyFont=new Font("Microsoft YaHei UI",13,FontStyle.Regular,GraphicsUnit.Pixel);
        hero=new StagePass(game==GameExperience.Million?12:game==GameExperience.Cgss?7:game==GameExperience.Shiny?5:0);hero.LeftSide=false;hero.BackColor=paper;hero.Size=new Size((int)StagePass.SceneWidth,(int)StagePass.SceneHeight);hero.Reduced=reduced;hero.PreviewTime=2.4;hero.Busy=!reduced;
        Rectangle area=Screen.FromPoint(anchor).WorkingArea;float d=DisplayInfo.Dpi(anchor)/96f;
        ClientSize=new Size(Math.Min((int)(1100*d),area.Width-40),Math.Min((int)(760*d),area.Height-(int)(64*d)));
        Location=new Point(area.X+(area.Width-Width)/2,area.Y+(area.Height-Height)/2);
        if(hidden){ShowInTaskbar=false;Opacity=0;}
        MinimumSize=SizeFromClientSize(new Size(Math.Min((int)(960*d),area.Width-40),Math.Min((int)(680*d),area.Height-(int)(64*d))));owner.RecordPlacement(WindowState==FormWindowState.Normal?Bounds:RestoreBounds,WindowState);
        Shown+=delegate{
            time.Restart();WindowsAppearance.TitleBar(Handle,dark,paper,ink);
            if(!reduced){pump=new FramePump(this,delegate(double dt){TickFrame();});pump.Rate=DisplayInfo.Refresh(this);pump.SetActive(true);}
        };
    }
    protected override bool ShowWithoutActivation{get{return hidden;}}
    RectangleF LoadingRectangle(){
        float d=Math.Min(ClientSize.Width/1100f,ClientSize.Height/760f),width=280*d,height=width*hero.Height/hero.Width;
        return new RectangleF((ClientSize.Width-width)/2,ClientSize.Height/2f-55*d-height/2,width,height);
    }
    internal RectangleF CurrentHeroRectangle{
        get{
            if(handoff==null)return LoadingRectangle();
            double x=Math.Max(0,Math.Min(1,travel.Elapsed.TotalSeconds/FlightSeconds)),u=MotionProfile.Coast(x);
            return new RectangleF((float)(flightFrom.X+(flightTarget.X-flightFrom.X)*u),(float)(flightFrom.Y+(flightTarget.Y-flightFrom.Y)*u-10*Math.Min(ClientSize.Width/1100f,ClientSize.Height/760f)*Math.Pow(Math.Sin(Math.PI*u),2)),
                (float)(flightFrom.Width+(flightTarget.Width-flightFrom.Width)*u),(float)(flightFrom.Height+(flightTarget.Height-flightFrom.Height)*u));
        }
    }
    internal bool Landed{get{return landed;}}
    void TickFrame(){
        if(stopping||IsDisposed||Disposing)return;
        if(!waitingLanding&&!landed)hero.PreviewTime=2.4+time.Elapsed.TotalSeconds;
        if(handoff!=null&&!waitingLanding&&!landed){
            double progress=Math.Min(1,travel.Elapsed.TotalSeconds/FlightSeconds);hero.SpinProgress=progress<1?progress:-1;
            if(progress>=1)SendLanding();
        }
        if(landed&&settle.IsRunning&&settle.Elapsed.TotalSeconds>.055){CloseCompleted();return;}
        if(fade.IsRunning){
            double alpha=1-fade.Elapsed.TotalSeconds/.18;
            if(alpha<=0){CloseCompleted();return;}if(!hidden)Opacity=Math.Max(.01,alpha);
            return;
        }
        // Repaint the animated region, keeping the large HiDPI background out of the frame budget.
        float d=Math.Min(ClientSize.Width/1100f,ClientSize.Height/760f);
        Rectangle current=Rectangle.Ceiling(CurrentHeroRectangle);current.Inflate((int)(42*d),(int)(42*d));
        Rectangle caption=new Rectangle((int)(ClientSize.Width/2-280*d),(int)(ClientSize.Height/2-235*d),(int)(560*d),(int)(470*d));
        Rectangle dirty=current,entryDirty=Rectangle.Empty;bool captionVisible=handoff==null||travel.Elapsed.TotalSeconds<.24;
        if(captionVisible||lastCaptionVisible)dirty=Rectangle.Union(dirty,caption);lastCaptionVisible=captionVisible;
        if(handoff!=null&&handoff.Layers!=null)foreach(EntryLayer layer in handoff.Layers)if(layer.IsMoving(travel.Elapsed.TotalSeconds)){Rectangle area=layer.Bounds;area.Inflate(0,(int)(24*d));entryDirty=entryDirty.IsEmpty?area:Rectangle.Union(entryDirty,area);}
        if(!entryDirty.IsEmpty)dirty=Rectangle.Union(dirty,entryDirty);
        if(!lastEntryDirty.IsEmpty)dirty=Rectangle.Union(dirty,lastEntryDirty);lastEntryDirty=entryDirty;
        if(!lastHeroDirty.IsEmpty)dirty=Rectangle.Union(dirty,lastHeroDirty);
        lastHeroDirty=current;dirty=Rectangle.Intersect(ClientRectangle,dirty);if(!dirty.IsEmpty)Invalidate(dirty);
    }
    public void BeginHandoff(HeroHandoff request){
        if(stopping||handoff!=null){request.Dispose();return;}
        if(!hidden)WindowHandoff.Link(Handle,request.MainWindow);
        RectangleF start=LoadingRectangle();
        float worldScale=Math.Min(start.Width/hero.Width,start.Height/hero.Height)*StagePass.SceneScale(hero.Width,hero.Height);
        Point target=PointToClient(request.ScreenTarget.Location);
        flightTarget=new RectangleF(target,request.ScreenTarget.Size);
        if(request.Ticket!=null)hero.AdoptTicketScene(request.Ticket);else hero.Rebuild();hero.Busy=false;hero.Size=request.ScreenTarget.Size;
        float scale=worldScale/StagePass.SceneScale(hero.Width,hero.Height),width=hero.Width*scale,height=hero.Height*scale;
        flightFrom=new RectangleF(start.X+start.Width/2-width/2,start.Y+start.Height/2-height/2,width,height);
        handoff=request;travel.Restart();Invalidate();
        if(reduced||WindowState==FormWindowState.Minimized){flightFrom=flightTarget;SendLanding();}
    }
    void SendLanding(){
        if(waitingLanding||landed||handoff==null)return;
        waitingLanding=true;hero.SpinProgress=-1;HeroPose pose=hero.CapturePose();hero.HoldPose(pose);
        handoff.Land(pose,delegate{
            if(IsDisposed||!IsHandleCreated)return;
            try{BeginInvoke(new Action(delegate{
                if(stopping||IsDisposed)return;waitingLanding=false;landed=true;Refresh();
                if(reduced||WindowState==FormWindowState.Minimized)CloseCompleted();else settle.Restart();
            }));}catch(InvalidOperationException){}
        });
    }
    public void Finish(){
        if(stopping)return;
        if(reduced||WindowState==FormWindowState.Minimized)CloseCompleted();else fade.Restart();
    }
    public void CloseCompleted(){
        if(stopping)return;completed=true;
        if(!hidden&&handoff!=null&&WindowState!=FormWindowState.Minimized)WindowHandoff.Complete(Handle,handoff.MainWindow);
        Close();
    }
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);WindowsAppearance.TitleBar(Handle,dark,paper,ink);}
    protected override void OnResize(EventArgs e){
        base.OnResize(e);
        if(owner!=null)owner.RecordPlacement(WindowState==FormWindowState.Normal?Bounds:RestoreBounds,WindowState);
        if(!stopping&&handoff!=null&&WindowState==FormWindowState.Minimized&&!waitingLanding&&!landed)SendLanding();
        if(!stopping&&pump!=null){pump.Rate=DisplayInfo.Refresh(this);pump.SetActive(WindowState!=FormWindowState.Minimized);}
    }
    protected override void OnLocationChanged(EventArgs e){
        base.OnLocationChanged(e);
        if(owner!=null)owner.RecordPlacement(WindowState==FormWindowState.Normal?Bounds:RestoreBounds,WindowState);
        if(!stopping&&pump!=null)pump.Rate=DisplayInfo.Refresh(this);
    }
    [StructLayout(LayoutKind.Sequential)]struct PlacementRect{public int Left,Top,Right,Bottom;}
    protected override void WndProc(ref Message m){
        if(m.Msg==0x02E0&&m.LParam!=IntPtr.Zero){
            PlacementRect r=(PlacementRect)Marshal.PtrToStructure(m.LParam,typeof(PlacementRect));
            SetBounds(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top);Invalidate();
            if(!stopping&&pump!=null)pump.Rate=DisplayInfo.Refresh(this);
            m.Result=IntPtr.Zero;return;
        }
        base.WndProc(ref m);
        if(m.Msg==0x007E&&!stopping&&pump!=null)pump.Rate=DisplayInfo.Refresh(this);
    }
    protected override void OnFormClosing(FormClosingEventArgs e){
        base.OnFormClosing(e);if(e.Cancel)return;
        stopping=true;FramePump clock=pump;pump=null;if(clock!=null)clock.Dispose();
        if(!completed)owner.UserClosed();else if(handoff!=null)handoff.Resume();
    }
    static void Fill(Graphics g,Color color,float x,float y,float w,float h){
        using(Brush brush=new SolidBrush(color))g.FillRectangle(brush,x,y,w,h);
    }
    static void Disk(Graphics g,Color color,float x,float y,float radius){
        using(Brush brush=new SolidBrush(color))g.FillEllipse(brush,x-radius,y-radius,radius*2,radius*2);
    }
    protected override void OnPaint(PaintEventArgs e){
        long start=Stopwatch.GetTimestamp();Graphics g=e.Graphics;using(Brush background=new SolidBrush(paper))g.FillRectangle(background,e.ClipRectangle);
        g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
        float density=Math.Min(ClientSize.Width/1100f,ClientSize.Height/760f);
        g.TranslateTransform(ClientSize.Width/2f,ClientSize.Height/2f-30*density);g.ScaleTransform(density,density);
        double t=reduced?0:time.Elapsed.TotalSeconds,raw=handoff==null?0:Math.Min(1,travel.Elapsed.TotalSeconds/FlightSeconds);
        float caption=(float)(handoff==null?1:Math.Pow(Math.Max(0,1-travel.Elapsed.TotalSeconds/.24),2));
        // The complete vector composition travels as one scene, including its satellites.
        g.ResetTransform();
        if(handoff!=null&&handoff.Layers!=null){
            bool native=true;foreach(EntryLayer layer in handoff.Layers)native&=layer.IsNative;
            if(native)using(RasterFrame.Canvas canvas=new RasterFrame.Canvas(g))foreach(EntryLayer layer in handoff.Layers)layer.Paint(canvas,travel.Elapsed.TotalSeconds);
            else foreach(EntryLayer layer in handoff.Layers)layer.Paint(g,travel.Elapsed.TotalSeconds);
        }
        float turn=handoff==null?0:(float)(-3*Math.Pow(Math.Sin(MotionProfile.Coast(raw)*Math.PI),2));
        hero.RenderOverlay(g,CurrentHeroRectangle,turn);
        g.TranslateTransform(ClientSize.Width/2f,ClientSize.Height/2f-30*density);g.ScaleTransform(density,density);
        if(caption>0){using(Pen rule=new Pen(Color.FromArgb((int)(255*caption),line),1)){
            g.DrawLine(rule,-260,-192,260,-192);g.DrawLine(rule,-260,222,260,222);
        }
        DrawCenter(g,"STAGE PASS",nameFont,Color.FromArgb((int)(255*caption),ink),new RectangleF(-240,142,480,28));
        string[] messages={"正在准备舞台…","正在整理通行证…","正在布置启动台…","准备就绪"};
        int stage=Math.Max(0,Math.Min(3,owner.Stage));
        DrawCenter(g,messages[stage],bodyFont,Color.FromArgb((int)(255*caption),muted),new RectangleF(-240,178,480,24));
        for(int i=0;i<3;i++){
            int alpha=i<stage?230:i==stage?(reduced?150:(int)(135+80*(.5+.5*Math.Sin(t*4)))):45;
            Fill(g,Color.FromArgb((int)(alpha*caption),accent),-58+i*42,244,32,3);
        }
        }
        Interlocked.Increment(ref paintCount);if(pump!=null)pump.ObserveWork((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);
    }
    static void DrawCenter(Graphics g,string text,Font font,Color color,RectangleF rect){
        using(Brush brush=new SolidBrush(color))
        using(StringFormat format=new StringFormat()){format.Alignment=StringAlignment.Center;format.LineAlignment=StringAlignment.Center;format.FormatFlags|=StringFormatFlags.NoWrap;g.DrawString(text,font,brush,rect,format);}
    }
    protected override void Dispose(bool disposing){
        if(disposing&&!released){
            released=true;stopping=true;FramePump clock=pump;pump=null;if(clock!=null)clock.Dispose();
            hero.Dispose();nameFont.Dispose();bodyFont.Dispose();if(handoff!=null)handoff.Dispose();
        }
        base.Dispose(disposing);
    }
}
sealed class ThemeReveal:Control {
    public int Direction;
    public StagePass TravelingHero,ArrivalHero;public RectangleF HeroFrom,HeroTo;public HeroPose StartPose;public Action<HeroPose> LandHero;
    readonly RasterFrame before,after;readonly Point origin;readonly Color edge;readonly double radius;
    readonly Stopwatch time=new Stopwatch();FramePump clock;Action finished;bool ending;internal Action<double> ChromeFrame;
    internal double Duration{get{return Direction==0?.56:1.48;}}
    internal float TiltAt(double easedProgress){float from=TravelingHero.RestTilt,to=ArrivalHero==null?from:ArrivalHero.RestTilt;return (float)(from+(to-from)*easedProgress);}
    public long PaintCount;public double PaintMilliseconds;public readonly List<double> PaintTimes=new List<double>();
    public ThemeReveal(Bitmap before,Bitmap after,Point origin,Color edge):this(new RasterFrame(before),new RasterFrame(after),origin,edge){}
    internal ThemeReveal(RasterFrame before,RasterFrame after,Point origin,Color edge){
        this.origin=origin;this.edge=edge;
        radius=Math.Sqrt(Math.Pow(Math.Max(Math.Abs(origin.X),Math.Abs(after.Size.Width-origin.X)),2)+Math.Pow(Math.Max(Math.Abs(origin.Y),Math.Abs(after.Size.Height-origin.Y)),2))+4;
        this.before=before;this.after=after;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Opaque,true);TabStop=false;
    }
    public void Start(Action finished){
        this.finished=finished;time.Restart();clock=new FramePump(this,delegate(double dt){if(time.Elapsed.TotalSeconds>=Duration){Invalidate();Update();Complete();return;}if(ChromeFrame!=null)ChromeFrame(dt);Invalidate();});
        clock.Rate=DisplayInfo.Refresh(this);clock.SetActive(true);
    }
    protected override void OnPaintBackground(PaintEventArgs e){}
    protected override void OnPaint(PaintEventArgs e){
        long start=Stopwatch.GetTimestamp();Graphics g=e.Graphics;double x=Math.Min(1,time.Elapsed.TotalSeconds/Duration),ease=MotionProfile.Coast(x);
        if(Direction==0){
            float r=(float)(radius*MotionProfile.Coast(x));
            if(before.IsNative&&after.IsNative)using(RasterFrame.Canvas canvas=new RasterFrame.Canvas(g)){
                before.Draw(canvas,0,0,1,Rectangle.Empty);if(r>.01f)after.Draw(canvas,0,0,1,Rectangle.Ceiling(new RectangleF(origin.X-r,origin.Y-r,r*2,r*2)));
            }else{before.Draw(g,0,0,1);if(r>.01f)after.Draw(g,0,0,1,Rectangle.Ceiling(new RectangleF(origin.X-r,origin.Y-r,r*2,r*2)));}
            if(r>.01f){
                if(x<.99){g.SmoothingMode=SmoothingMode.AntiAlias;using(Pen rim=new Pen(Color.FromArgb(36,edge),Math.Max(1,Width/1100f)))g.DrawEllipse(rim,origin.X-r,origin.Y-r,r*2,r*2);}
            }
        }else{
            int shift=(int)Math.Round(Width*ease);
            if(before.IsNative&&after.IsNative)using(RasterFrame.Canvas canvas=new RasterFrame.Canvas(g)){before.Draw(canvas,-Direction*shift,0,1,Rectangle.Empty);after.Draw(canvas,Direction*(Width-shift),0,1,Rectangle.Empty);}
            else{before.Draw(g,-Direction*shift,0,1);after.Draw(g,Direction*(Width-shift),0,1);}
            if(TravelingHero!=null){
                double turns=StagePass.Revolutions*ease;
                double edgeOn=.25-StartPose.Pointer.X*.13/(Math.PI*2);StagePass chosen=turns<edgeOn||ArrivalHero==null?TravelingHero:ArrivalHero;
                float tilt=TiltAt(ease);TravelingHero.TransitionTilt=tilt;
                TravelingHero.PreviewTime=StartPose.Time+time.Elapsed.TotalSeconds;TravelingHero.SpinPhase=turns;
                if(ArrivalHero!=null){ArrivalHero.TransitionTilt=tilt;ArrivalHero.PreviewTime=StartPose.Time+time.Elapsed.TotalSeconds;ArrivalHero.SpinPhase=turns;}
                double from=StagePass.SceneScale(HeroFrom.Width,HeroFrom.Height),to=StagePass.SceneScale(HeroTo.Width,HeroTo.Height);
                float density=(float)((from+(to-from)*ease)/StagePass.SceneScale(chosen.Width,chosen.Height));
                float cx=(float)(HeroFrom.X+HeroFrom.Width/2+(HeroTo.X+HeroTo.Width/2-HeroFrom.X-HeroFrom.Width/2)*ease);
                float cy=(float)(HeroFrom.Y+HeroFrom.Height/2+(HeroTo.Y+HeroTo.Height/2-HeroFrom.Y-HeroFrom.Height/2)*ease);
                float lift=(float)(2*(from+(to-from)*ease)*Math.Pow(Math.Sin(Math.PI*ease),2));
                chosen.RenderOverlay(g,new RectangleF(cx-chosen.Width*density/2,cy-lift-chosen.Height*density/2,chosen.Width*density,chosen.Height*density),0);
            }
        }
        double cost=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;PaintCount++;PaintMilliseconds+=cost;PaintTimes.Add(time.Elapsed.TotalMilliseconds);if(clock!=null)clock.ObserveWork(cost);
    }
    public void Complete(){
        if(ending)return;ending=true;FramePump pump=clock;clock=null;if(pump!=null)pump.Dispose();
        if(TravelingHero!=null){TravelingHero.SpinProgress=-1;TravelingHero.SpinPhase=-1;TravelingHero.TransitionTilt=float.NaN;}
        if(ArrivalHero!=null){ArrivalHero.SpinProgress=-1;ArrivalHero.SpinPhase=-1;ArrivalHero.TransitionTilt=float.NaN;}
        if(TravelingHero!=null&&LandHero!=null){StagePass target=ArrivalHero??TravelingHero;HeroPose pose=target.CapturePose();Action<HeroPose> accept=LandHero;LandHero=null;accept(pose);}
        Action done=finished;finished=null;ChromeFrame=null;Hide();if(done!=null)done();Dispose();
    }
    protected override void Dispose(bool disposing){
        if(disposing){FramePump pump=clock;clock=null;if(pump!=null)pump.Dispose();before.Dispose();after.Dispose();}
        base.Dispose(disposing);
    }
}
sealed class NavigationChrome:Control {
    public float Density=1,Offset,LogicalWidth=1100;public int PageKind=-1;public bool Shiny;public int Game;
    public NavigationChrome(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    protected override void OnPaint(PaintEventArgs e){
        Graphics g=e.Graphics;g.Clear(UI.Bg);g.TranslateTransform(Offset*Density,0);g.ScaleTransform(Density,Density);g.SmoothingMode=SmoothingMode.AntiAlias;
        using(Pen line=new Pen(UI.Border,1))g.DrawLine(line,48,82,LogicalWidth-48,82);
        using(GraphicsPath track=UI.Round(new RectangleF(48,99,UI.IsWabi?(LogicalWidth-96)*5/12:558,48),UI.Corner+3))using(Brush fill=new SolidBrush(UI.Card))g.FillPath(fill,track);
        using(Font number=new Font(UI.IsWabi?UI.UprightLatin:UI.LatinFace,12,FontStyle.Bold,GraphicsUnit.Pixel))
        using(Font caption=new Font(UI.BodyFace,11,FontStyle.Regular,GraphicsUnit.Pixel))
        using(Brush accent=new SolidBrush(UI.Accent))using(Brush dim=new SolidBrush(UI.Dim)){
            int page=PageKind>=30?PageKind-30:PageKind>=20?PageKind-20:PageKind>=10?PageKind-10:PageKind;bool home=PageKind<0||page==4||page==6||page==7;
            float numberX=UI.IsWabi?48+(LogicalWidth-96)*10/12:LogicalWidth-155;
            g.DrawString(home?"STAGE / "+(Game==3?"04":Game==2?"03":Game==1?"02":"01"):(page==2?1:page==1?2:page==0?3:4).ToString("00")+" / 04",number,accent,numberX,105);
            g.DrawString(UI.IsMillion?"765 THEATER / PREVIEW":UI.IsWabi?"2026 / 346 PRODUCTION":home?"你的下一场演出":Game==2?"探索灰姑娘的世界":Shiny?"探索闪耀的世界":"探索学马仕的世界",caption,dim,numberX,127);
        }
    }
}
class ResourcePage:Control {
    internal virtual bool IncludeHeroInSnapshot{get{return true;}}
    internal virtual bool CanNavigate{get{return true;}}
    internal virtual bool HandleNavigationKey(Keys key){return false;}
    internal readonly int Kind;internal StagePass Hero;internal readonly ResourceContent Content;
    readonly RoundBtn open,download;readonly List<RoundBtn> pageButtons=new List<RoundBtn>();
    readonly List<Font> fonts=new List<Font>();Font title,body,small,latin,buttonFont;
    float density=1,logicalWidth=1100,logicalHeight=760,offset,textX,textWidth;int pageDpi=96;bool disposed,themedOnce;
    internal static readonly string[] Names={"偶像大师 Wiki","bilibili","游戏官网","同人交流站","游戏下载官网"};
    internal static readonly string[] Urls={"https://www.gamekee.com/gakumas/622223.html","https://space.bilibili.com/3546925024414310?spm_id_from=333.337.0.0","https://gakuen.idolmaster-official.jp/","https://idol-master.top/","https://dmg-gakuen.idolmaster-official.jp/"};
    readonly Action<RoundBtn,int> navigate;string previewNotice="";System.Windows.Forms.Timer previewTimer;
    // Shared landing-page contract keeps snapshots, ticket ownership and transitions in MainForm.
    protected ResourcePage(int kind){
        Kind=kind;AccessibleRole=AccessibleRole.Pane;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
    }
    public ResourcePage(int kind,Action<RoundBtn,int> navigate):this(kind,false,navigate){}
    internal ResourcePage(int kind,bool shiny,Action<RoundBtn,int> navigate):this(kind,shiny?GameExperience.Shiny:GameExperience.Gakumas,navigate){}
    internal ResourcePage(int kind,int game,Action<RoundBtn,int> navigate):this(kind){
        bool shiny=game==GameExperience.Shiny;
        this.navigate=navigate;Content=ResourceContent.For(kind,game);AccessibleName=Content.Name+"资源着陆页";
        open=Button(Content.Links[0].Name,delegate{Hero.Busy=true;Hero.Spin();this.navigate(open,Kind);});open.ExternalLink=true;
        if(Content.Links.Length==2){
            open.Text=Kind==1?"偶像大师官方":shiny?"闪耀色彩官网":"访问游戏官网";
            download=Button(Kind==1?"十六夜カズヤP":shiny?"闪耀色彩页游":"下载游戏",delegate{Hero.Busy=true;Hero.Spin();this.navigate(download,4);});download.ExternalLink=true;
            download.AccessibleDescription="在默认浏览器打开 "+Content.Links[1].Name;
        }
        Hero=new StagePass(game==GameExperience.Million?13+Kind:game==GameExperience.Cgss?8+Kind:(shiny&&kind==0?6:Kind+1));Hero.GameShiny=shiny;Hero.Rebuild();Hero.PreviewTime=2.4;Hero.HoldPose(Hero.CapturePose());Controls.Add(Hero);
        Hero.AccessibleName=Content.Name+"通行证，点击翻面";ApplyTheme();
    }
    internal void PreviewAction(){
        previewNotice="界面预览 · 此功能尚未接入";Invalidate();
        if(previewTimer==null){previewTimer=new System.Windows.Forms.Timer{Interval=2400};previewTimer.Tick+=delegate{previewTimer.Stop();Hero.Busy=false;};}
        previewTimer.Stop();previewTimer.Start();
    }
    internal string DestinationUrl(int index){return Content.Links[index==4?1:0].Url;}
    internal string DestinationName(int index){return Content.Links[index==4?1:0].Name;}
    RoundBtn Button(string name,EventHandler action){RoundBtn b=new RoundBtn();b.Text=name;b.Click+=action;Controls.Add(b);pageButtons.Add(b);return b;}
    Font CachedFont(string face,float size,bool bold){foreach(Font f in fonts)if(f.Name==face&&Math.Abs(f.Size-size)<.001&&f.Bold==bold)return f;Font next=new Font(face,size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);fonts.Add(next);return next;}
    Font DrawFont(string face,float size,bool bold){return CachedFont(face,size,bold);}
    Font NewFont(string face,float size,bool bold){return CachedFont(face,size*density,bold);}
    void Put(RoundBtn b,float x,float y,float w,float h){b.SetBounds((int)Math.Round((x+offset)*density),(int)Math.Round(y*density),(int)Math.Round(w*density),(int)Math.Round(h*density));b.Density=density;b.Font=buttonFont;}
    internal virtual void ApplyTheme(){
        BackColor=UI.Bg;if(Hero!=null){Hero.BackColor=UI.Bg;if(themedOnce)Hero.Rebuild();themedOnce=true;}
        foreach(RoundBtn b in pageButtons){
            b.BackColor=UI.Bg;b.ForeColor=UI.Text;b.Fill=UI.Bg;b.FillHover=UI.GhostH;b.Stroke=UI.Border;b.HasStroke=false;b.Radius=UI.Corner;
        }
        open.Fill=UI.IsWabi&&!UI.IsDark?UI.C("BD9B69"):UI.Accent;open.FillHover=UI.IsWabi&&!UI.IsDark?UI.C("C7A574"):UI.AccentH;open.ForeColor=UI.IsWabi&&!UI.IsDark?UI.C("1B304C"):UI.IsDark?UI.Bg:UI.C("FFF5DC");
        if(download!=null){download.Fill=UI.Card;download.HasStroke=false;}
        LayoutPage();Invalidate(true);
    }
    internal virtual void Advance(double dt){Hero.Advance();foreach(RoundBtn b in pageButtons)b.Advance(dt);}
    internal void Hold(){Hero.HoldPose(Hero.CapturePose());}
    internal void Resume(){Hero.ResumePose();}
    internal void NavigationFinished(){Hero.Busy=false;}
    internal virtual void UpdateDpi(int dpi){pageDpi=dpi;LayoutPage();}
    protected override void OnResize(EventArgs e){base.OnResize(e);if(Hero!=null&&Kind<4)LayoutPage();}
    void LayoutPage(){
        if(Width<1||Height<1)return;
        float dpi=pageDpi/96f;density=dpi*Math.Min(1,Math.Min(Width/(960*dpi),Height/(680*dpi)));
        logicalWidth=Math.Min(1400,Width/density);logicalHeight=Height/density;offset=(Width/density-logicalWidth)/2;
        title=DrawFont(UI.TitleFace,UI.IsDark?43:UI.IsBauhaus?49:47,true);body=DrawFont(UI.BodyFace,14,false);small=DrawFont(UI.BodyFace,11,false);latin=DrawFont(UI.LatinFace,12,true);buttonFont=NewFont(UI.BodyFace,14,false);
        float artWidth=Math.Min(440,logicalWidth*.43f),artHeight=Math.Min(430,logicalHeight-300);
        bool artLeft=Kind!=1&&Kind!=3;float col=(logicalWidth-96)/12;
        if(UI.IsWabi)artWidth=5*col;
        float artX=UI.IsWabi?(artLeft?48:48+7*col):artLeft?32:logicalWidth-artWidth-44;
        Hero.LeftSide=artLeft;
        SizeF viewport=UI.IsWabi?new SizeF(artWidth,Math.Min(430,logicalHeight-300)+16):StagePass.PageViewport(logicalWidth,logicalHeight);
        Hero.SetBounds((int)Math.Round((artX+(artWidth-viewport.Width)/2+offset)*density),(int)Math.Round((164+(artHeight+16-viewport.Height)/2)*density),(int)Math.Round(viewport.Width*density),(int)Math.Round(viewport.Height*density));
        textX=UI.IsWabi?(artLeft?48+6*col:48):artLeft?logicalWidth*.52f:51;textWidth=UI.IsWabi?5*col:artLeft?logicalWidth-textX-48:logicalWidth-artWidth-126;
        float firstWidth=UI.IsWabi?(download!=null?2*col:4*col):download!=null?Math.Min(195,(textWidth-12)/2):Math.Min(356,textWidth);
        Put(open,textX,Math.Max(454,logicalHeight-280),firstWidth,58);open.Font=NewFont(UI.BodyFace,download!=null?16:18,true);
        if(download!=null)Put(download,textX+firstWidth+(UI.IsWabi?0:12),Math.Max(454,logicalHeight-280),UI.IsWabi?2*col:Math.Min(195,(textWidth-12)/2),58);
        PruneFonts();
        Invalidate(true);
    }
    void PruneFonts(){
        for(int i=fonts.Count-1;i>=0;i--){Font value=fonts[i];bool used=ReferenceEquals(value,title)||ReferenceEquals(value,body)||ReferenceEquals(value,small)||ReferenceEquals(value,latin)||ReferenceEquals(value,buttonFont);
            foreach(RoundBtn button in pageButtons)used|=ReferenceEquals(value,button.Font);
            if(!used){fonts.RemoveAt(i);value.Dispose();}
        }
    }
    void TextAt(Graphics g,string text,Font font,Color color,RectangleF bounds){using(Brush b=new SolidBrush(color))using(StringFormat format=new StringFormat()){format.FormatFlags=StringFormatFlags.NoClip;g.DrawString(text,font,b,bounds,format);}}
    internal void RenderSurface(Graphics g,Rectangle clip){OnPaint(new PaintEventArgs(g,clip));}
    protected override void OnPaint(PaintEventArgs e){
        Graphics g=e.Graphics;g.Clear(UI.Bg);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if(title==null)return;g.TranslateTransform(offset*density,0);g.ScaleTransform(density,density);
        float w=logicalWidth,h=logicalHeight;
        using(Pen line=new Pen(UI.Border,1))g.DrawLine(line,48,h-120,w-48,h-120);
        if(UI.IsMillion){
            using(Brush mark=new SolidBrush(UI.Accent))g.FillRectangle(mark,textX-18,180,5,90);
        }else if(UI.IsWabi){
            using(Pen rule=new Pen(UI.C("BC965E"),1))g.DrawLine(rule,textX,177,textX+textWidth,177);
        }else if(UI.IsBauhaus){
            using(Brush red=new SolidBrush(UI.Accent))g.FillRectangle(red,textX-18,180,7,90);
            using(Brush yellow=new SolidBrush(UI.C("D6AE35")))g.FillRectangle(yellow,48,h-88,20,20);
            using(Brush blue=new SolidBrush(UI.C("244E86")))g.FillRectangle(blue,76,h-88,20,20);
            using(Brush red=new SolidBrush(UI.Accent))g.FillEllipse(red,104,h-88,20,20);
        }else if(UI.IsDark){
            using(Pen gold=new Pen(UI.Accent,1))g.DrawLine(gold,textX,177,textX+66,177);
        }else{
            using(Brush red=new SolidBrush(UI.Accent))g.FillPolygon(red,new[]{new PointF(31,184),new PointF(39,180),new PointF(39,h-250),new PointF(31,h-246)});
        }
        TextAt(g,Content.Kicker,latin,UI.Accent,new RectangleF(textX,184,textWidth,24));
        TextAt(g,Content.Headline,title,UI.Text,new RectangleF(UI.IsWabi?textX:textX-3,222,textWidth+(UI.IsWabi?0:4),134));
        TextAt(g,Content.Description,body,UI.Dim,new RectangleF(textX,372,textWidth,70));
        TextAt(g,previewNotice.Length>0?previewNotice:Content.Links[0].Domain,small,UI.Dim,new RectangleF(textX,open.Bottom/density+15,textWidth+20,24));
        string[] foot=Content.FeatureTitles,detail=Content.FeatureDetails;
        Color[] accents=UI.IsBauhaus?new[]{UI.Accent,UI.C("244E86"),UI.C("B69121")}:new[]{UI.Accent,UI.Accent,UI.Accent};
        float cardWidth=UI.IsWabi?(w-96)/3:(w-112)/3;
        for(int i=0;i<3;i++){
            float x=48+i*(cardWidth+(UI.IsWabi?0:8)),y=h-104;
            if(UI.IsWabi){
                using(Pen rule=new Pen(UI.Border,1))g.DrawLine(rule,x,y,x+cardWidth,y);
                TextAt(g,(i+1).ToString("00"),latin,UI.Accent,new RectangleF(x,y+12,35,22));
                TextAt(g,foot[i],body,UI.Text,new RectangleF(x+42,y+12,cardWidth-50,25));
                TextAt(g,detail[i],small,UI.Dim,new RectangleF(x+42,y+40,cardWidth-50,24));
            }else{
                using(GraphicsPath card=UI.Round(new RectangleF(x,y,cardWidth,82),UI.Corner))using(Brush fill=new SolidBrush(UI.Blend(UI.Bg,UI.Card,.65f)))g.FillPath(fill,card);
                using(Brush stamp=new SolidBrush(UI.Blend(UI.Card,accents[i],.12f)))g.FillRectangle(stamp,x+14,y+17,44,44);
                DrawFeature(g,x+24,y+27,accents[i],i);
                TextAt(g,foot[i],body,UI.Text,new RectangleF(x+72,y+15,cardWidth-83,25));
                TextAt(g,detail[i],small,UI.Dim,new RectangleF(x+72,y+43,cardWidth-82,24));
            }
        }

    }
    void DrawFeature(Graphics g,float x,float y,Color color,int icon){
        using(Pen p=new Pen(color,1.8f)){
            p.LineJoin=LineJoin.Round;p.StartCap=p.EndCap=LineCap.Round;
            if(Kind==3&&icon==2){g.DrawRectangle(p,x,y+3,24,21);g.DrawLine(p,x,y+9,x+24,y+9);g.DrawLine(p,x+6,y,x+6,y+5);g.DrawLine(p,x+18,y,x+18,y+5);using(Brush b=new SolidBrush(color))for(int r=0;r<2;r++)for(int c=0;c<3;c++)g.FillRectangle(b,x+4+c*7,y+13+r*6,3,3);}
            else if(icon==0){g.DrawRectangle(p,x,y+2,24,21);g.DrawLine(p,x+8,y+2,x+8,y+23);g.DrawLine(p,x+12,y+8,x+20,y+8);g.DrawLine(p,x+12,y+13,x+20,y+13);g.DrawLine(p,x+12,y+18,x+18,y+18);}
            else if(icon==1){g.DrawRectangle(p,x,y+3,25,19);using(Brush b=new SolidBrush(color))g.FillPolygon(b,new[]{new PointF(x+9,y+7),new PointF(x+9,y+18),new PointF(x+18,y+12.5f)});}
            else {g.DrawEllipse(p,x+1,y+1,23,23);g.DrawLine(p,x+12.5f,y+6,x+12.5f,y+13);g.DrawLine(p,x+12.5f,y+13,x+18,y+16);}
        }
    }
    internal virtual List<string> CheckLayout(){
        List<string> errors=new List<string>();
        foreach(Control c in Controls){if(!ClientRectangle.Contains(c.Bounds))errors.Add("outside "+c.Text);if(c is RoundBtn){Size measured=TextRenderer.MeasureText(c.Text,c.Font);if(measured.Width+(c==open?50*density:8*density)>c.Width)errors.Add("text "+c.Text);}}
        if(open.Top<(442*density))errors.Add("open button overlaps description");return errors;
    }
    protected override void Dispose(bool disposing){bool release=disposing&&!disposed;if(release){disposed=true;if(previewTimer!=null)previewTimer.Dispose();}base.Dispose(disposing);if(release){foreach(Font f in fonts)f.Dispose();fonts.Clear();}}
}
class MainForm:Form {
    internal static readonly int[] PageOrder={-1,2,1,0,3};
    static int PagePosition(int kind){if(kind>=30&&kind<40)kind-=30;else if(kind>=20&&kind<30)kind-=20;else if(kind>=10&&kind<20)kind-=10;return Array.IndexOf(PageOrder,kind==4||kind==6||kind==7?-1:kind);}
    string Root,Mitm,Py,preferencePath,appearanceMode,dayStyle,accountAction="切到官方账号";
    const string YAHA="Cysharp.Net.Http.YetAnotherHttpHandler.Native";
    Dot dotSrv,dotGame;Label lblSrv,lblGame,lblSave;
    RoundBtn btnPlay,btnUpdate,navHome,wiki,community,official,fan,more,brightness,homeDirectory,homeFind,copyLog,clearLog;NavigationChrome chrome;
    readonly ToolTip hints=new ToolTip{InitialDelay=450,ReshowDelay=100,AutoPopDelay=3500};
    TextBox txtOut,homePath;Label actionState,brand,edition,masthead,headline,logTitle;
    System.Windows.Forms.Timer timer;Process cur;volatile bool stoppingAllServices;int stopGeneration;StagePass stage;ContextMenuStrip activeMenu;
    FramePump pump;ThemeReveal themeReveal;ResourcePage resourcePage;bool startupPending;RasterFrame resourceBackdrop;System.Drawing.Icon gakumasIcon,shinyIcon,cgssIcon,millionIcon;
    readonly Dictionary<int,ResourcePage> resourcePages=new Dictionary<int,ResourcePage>();
    const long BackdropCacheLimit=128L*1024*1024;
    readonly Dictionary<int,RasterFrame> resourceBackdropCache=new Dictionary<int,RasterFrame>();
    readonly List<int> backdropRecency=new List<int>();long backdropCacheBytes;Size backdropSize;int backdropDpi,homeScroll,homeSelectionStart,homeSelectionLength;bool backdropDark,backdropBauhaus;
    int pendingResource=int.MinValue,navigationVersion;bool shinyGame,cgssGame,millionGame;int? pendingGame;
    internal int Game{get{return millionGame?GameExperience.Million:cgssGame?GameExperience.Cgss:shinyGame?GameExperience.Shiny:GameExperience.Gakumas;}}
    internal int HomePage{get{return Game==GameExperience.Million?7:Game==GameExperience.Cgss?6:Game==GameExperience.Shiny?4:-1;}}
    readonly System.Windows.Forms.Timer hoverDelay=new System.Windows.Forms.Timer(),hoverClose=new System.Windows.Forms.Timer();
    Control hoverTarget;Action hoverAction;bool openingByHover,menuIsHover;
    readonly List<RoundBtn> buttons=new List<RoundBtn>();
    readonly Dictionary<Control,Font> fonts=new Dictionary<Control,Font>();
    bool layoutReady,layingOut,preparing,subscribed,shuttingDown,resourcesDisposed;
    int displayDpi=96,themeUpdatePending;float scale=1,lw=1100,lh=760,ox,divider=570,mainX=48,right=1052;
    readonly Stopwatch entrance=Stopwatch.StartNew();
    Func<bool> readSystemDark=WindowsAppearance.ReadDark;
    public static string RootOverride;public static bool RenderMode;internal static Action<string> ExternalNavigationProbe=null;
    internal event Action GakumasDirectorySelectionRequested;
    public Action StartupReady;public Func<Rectangle> StartupBounds;public Func<FormWindowState> StartupState;internal bool SuppressReveal;
    protected override bool ShowWithoutActivation{get{return SuppressReveal||startupPending;}}
    void SetFont(Control c,float points,bool bold){
        float pixels=points*96f/72f*scale;Font previous;
        string face=c==edition&&Game==GameExperience.Cgss?UI.UprightLatin:UI.Face(c.Text,c==headline);
        if(fonts.TryGetValue(c,out previous)&&Math.Abs(previous.Size-pixels)<.05f&&previous.Bold==bold&&previous.Name==face)return;
        Font next=new Font(face,pixels,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);c.Font=next;fonts[c]=next;if(previous!=null)previous.Dispose();
    }
    void Put(Control c,float x,float y,float w,float h,float points,bool bold){
        c.SetBounds((int)Math.Round((x+ox)*scale),(int)Math.Round(y*scale),Math.Max(1,(int)Math.Round(w*scale)),Math.Max(1,(int)Math.Round(h*scale)));
        if(points>0)SetFont(c,points,bold);RoundBtn b=c as RoundBtn;if(b!=null)b.Density=scale;
    }
    Label AddText(string text){Label l=new Label();l.Text=text;l.AutoSize=false;l.UseMnemonic=false;l.UseCompatibleTextRendering=false;Controls.Add(l);return l;}
    RoundBtn Action(string text,EventHandler handler){RoundBtn b=new RoundBtn();b.Text=text;b.Click+=handler;Controls.Add(b);buttons.Add(b);return b;}
    public MainForm(){
        GkmEnvironmentRepair.Repair();
        Root=RootOverride??Path.GetDirectoryName(Application.ExecutablePath);Mitm=Path.Combine(Root,"mitm");Py=Path.Combine(Root,"python\\python.exe");
        preferencePath=Path.Combine(Root,"appearance-mode.txt");appearanceMode=WindowsAppearance.ReadPreference(preferencePath,out dayStyle);
        int savedGame=GameExperience.ReadGame(Root);shinyGame=savedGame==GameExperience.Shiny;cgssGame=savedGame==GameExperience.Cgss;millionGame=savedGame==GameExperience.Million;
        dayStyle=Game==GameExperience.Gakumas?"red":"bauhaus";UI.Theme(ResolveAppearance(),Game==GameExperience.Shiny,Game==GameExperience.Cgss,Game==GameExperience.Million);
        AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(1100,760);Text="偶像大师 · STAGE PASS";KeyPreview=true;
        KeyDown+=delegate(object sender,KeyEventArgs e){
            if(resourcePage==null)return;
            if(resourcePage.HandleNavigationKey(e.KeyCode)){e.Handled=e.SuppressKeyPress=true;return;}
            if(e.KeyCode==Keys.Escape){ShowResource(HomePage);e.Handled=e.SuppressKeyPress=true;}
            else if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right){if(ActiveControl is TextBoxBase)return;foreach(Control input in resourcePage.Controls)if(input.Focused&&(input is TextBoxBase||input is TrackBar))return;int position=PagePosition(resourcePage.Kind)+(e.KeyCode==Keys.Right?1:-1);if(position>=0&&position<PageOrder.Length)ShowResource(PageOrder[position]<0?HomePage:PageOrder[position]);e.Handled=e.SuppressKeyPress=true;}
        };
        FormBorderStyle=FormBorderStyle.Sizable;DoubleBuffered=true;StartPosition=FormStartPosition.Manual;SetStyle(ControlStyles.ResizeRedraw,true);
        gakumasIcon=LoadEmbeddedIcon("gakumas.ico");shinyIcon=LoadEmbeddedIcon("shiny.ico");cgssIcon=LoadEmbeddedIcon("cgss.ico");millionIcon=LoadEmbeddedIcon("million.ico");
        if(gakumasIcon==null)try{gakumasIcon=Icon.ExtractAssociatedIcon(Path.Combine(Root,"学マス启动器.exe"));}catch{}
        ApplyGameIcon();
        chrome=new NavigationChrome();Controls.Add(chrome);
        brand=new GameLogo();brand.Click+=delegate{CycleGame();};Controls.Add(brand);edition=AddText("STAGE / 01");
        navHome=Action("主界面",delegate{ShowResource(HomePage);});
        wiki=Action("Wiki",delegate{ShowResource(0);});
        community=Action("bilibili",delegate{ShowResource(1);});
        official=Action("官网",delegate{ShowResource(2);});
        fan=Action("同人站",delegate{ShowResource(3);});
        foreach(RoundBtn link in new[]{wiki,community,official,fan}){link.ExternalLink=true;link.AccessibleDescription="打开"+link.Text+"独立资源页";}
        more=Action("更多  ⋯",delegate{ShowUtilities();});
        stage=new StagePass();stage.PaintCompleted=delegate(double work){if(pump!=null)pump.ObserveWork(work);};Controls.Add(stage);
        stage.Reduced=false;
        masthead=AddText("GAKUEN IDOLM@STER  /  LAUNCHER");headline=AddText("");
        homePath=new TextBox{ReadOnly=true,BorderStyle=BorderStyle.None,Text="选择 gakumas.exe 所在目录",AccessibleName="学园偶像大师游戏目录"};Controls.Add(homePath);homeDirectory=Action("选择",delegate{Action choose=GakumasDirectorySelectionRequested;if(choose!=null)choose();else if(!RenderMode)ChooseGameDirectory();});homeDirectory.AccessibleName="选择学园偶像大师游戏目录";hints.SetToolTip(homeDirectory,homeDirectory.AccessibleName);
        homeFind=Action("自动寻找",delegate{FindGameDirectory();});homeFind.AccessibleName="自动寻找学园偶像大师游戏目录";hints.SetToolTip(homeFind,"自动寻找并保存学园游戏目录");
        btnPlay=Action("开始游戏",delegate{PrepareAndRun("launch","launch_game.ps1",!IsOfficialMode());});btnPlay.DirectionalArrow=true;
        dotSrv=new Dot();dotGame=new Dot();Controls.Add(dotSrv);Controls.Add(dotGame);
        lblSrv=AddText("服务未启动");lblGame=AddText("游戏未运行");
        btnUpdate=Action("更新游戏内容   ↓",delegate{PrepareAndRun("update","grab_master.ps1",false);});
        actionState=AddText("准备就绪");logTitle=AddText("操作记录");
        txtOut=new ScrollableLogTextBox();txtOut.AccessibleName="学园偶像大师操作记录";
        txtOut.Text="点击「开始游戏」进入学园。操作记录会显示在这里。";Controls.Add(txtOut);
        copyLog=new LogActionButton(false);copyLog.Click+=delegate{if(txtOut.TextLength==0)return;try{Clipboard.SetText(txtOut.Text);}catch(System.Runtime.InteropServices.ExternalException ex){Say("无法复制日志："+ex.Message);}};
        clearLog=new LogActionButton(true);clearLog.Click+=delegate{txtOut.Clear();};
        foreach(RoundBtn tool in new[]{copyLog,clearLog}){Controls.Add(tool);buttons.Add(tool);hints.SetToolTip(tool,tool.AccessibleName);}
        lblSave=AddText("");lblSave.TextAlign=ContentAlignment.MiddleRight;
        brightness=Action("◐",delegate{ToggleAppearance();});brightness.AccessibleName="明暗模式";
        foreach(Control header in new Control[]{brand,edition,navHome,official,community,wiki,fan,brightness,lblSave})chrome.Controls.Add(header);
        chrome.BringToFront();
        foreach(Control child in Controls)if(child!=chrome&&child!=stage){child.TextChanged+=delegate{InvalidateHomeBackdrop();};child.ForeColorChanged+=delegate{InvalidateHomeBackdrop();};child.EnabledChanged+=delegate{InvalidateHomeBackdrop();};}
        timer=new System.Windows.Forms.Timer();timer.Interval=2500;timer.Tick+=delegate{if(!startupPending&&themeReveal==null&&resourcePage==null){RefreshState();UpdateHomePath();}};
        Load+=delegate{if(RenderMode){accountAction="切到官方账号";lblSave.Text="STAGE PASS  /  READY";}else{RefreshState();UpdateHomePath();timer.Start();}};
        Shown+=delegate{
            FollowSystemAppearance();
            if(!RenderMode){
                displayDpi=DisplayInfo.Dpi(this);pump=new FramePump(this,Animate);UpdateDisplay();UpdatePlayback();ApplyLayout();
                try{SystemEvents.UserPreferenceChanged+=SystemPreferenceChanged;subscribed=true;}catch(InvalidOperationException){}catch(System.Runtime.InteropServices.ExternalException){}
            }
            if(StartupReady!=null)BeginInvoke(new Action(delegate{
                if(shuttingDown||IsDisposed)return;
                if(StartupBounds!=null){Rectangle placement=StartupBounds();if(!placement.IsEmpty)Bounds=placement;}
                if(StartupState!=null)WindowState=StartupState();
                entrance.Restart();ActiveHero.HoldPose(new HeroPose{Time=2.4,Angle=-.6,Velocity=.26});Refresh();
                foreach(Control child in Controls)child.Refresh();
                Opacity=SuppressReveal?0:1;Action ready=StartupReady;StartupReady=null;ready();
            }));
        };
        Activated+=delegate{if(layoutReady)FollowSystemAppearance();UpdatePlayback();};
        Deactivate+=delegate{pendingResource=int.MinValue;pendingGame=null;navigationVersion++;if(themeReveal!=null)themeReveal.Complete();UpdatePlayback();};LocationChanged+=delegate{if(pump!=null)UpdateDisplay();};
        hoverDelay.Interval=65;hoverClose.Interval=160;
        hoverDelay.Tick+=delegate{hoverDelay.Stop();TryHoverOpen(Cursor.Position);};
        hoverClose.Tick+=delegate{hoverClose.Stop();TryHoverClose(Cursor.Position);};WireHover(more,ShowUtilities);
        layoutReady=true;ApplyPalette();if(HomePage>=0)ActivatePage(HomePage);if(!RenderMode)InitialBounds();ApplyLayout();
    }
    internal StagePass ActiveHero{get{return resourcePage==null?stage:resourcePage.Hero;}}
    internal StagePass.TicketScene CopyTicketScene(){return ActiveHero.CopyTicketScene();}
    internal Rectangle HeroScreenBounds{get{return new Rectangle(ActiveHero.PointToScreen(Point.Empty),ActiveHero.Size);}}
    internal void AcceptHeroPose(HeroPose pose){if(shuttingDown)return;ActiveHero.HoldPose(pose);ActiveHero.Refresh();}
    internal EntryLayer[] CaptureStartupLayers(){
        using(Bitmap scene=SnapshotAppearance(false)){
            int top=Math.Max(1,(int)Math.Round(164*scale)),action=btnPlay.Top-(int)(14*scale),bottom=(int)Math.Round(divider*scale);
            int left=stage.Left,gutter=(int)Math.Round(42*scale);
            if(resourcePage!=null){return new[]{new EntryLayer(scene,new Rectangle(0,0,ClientSize.Width,top),.06,.50,10*scale),new EntryLayer(scene,new Rectangle(0,top,ClientSize.Width,ClientSize.Height-top),.15,.65,14*scale)};}
            return new[]{
                new EntryLayer(scene,new Rectangle(0,0,ClientSize.Width,top),.06,.50,10*scale),
                new EntryLayer(scene,new Rectangle(0,top,gutter,bottom-top),.08,.72,0),
                new EntryLayer(scene,new Rectangle(gutter,top,left-gutter,action-top),.15,.61,18*scale),
                new EntryLayer(scene,new Rectangle(gutter,action,left-gutter,bottom-action),.28,.60,14*scale),
                new EntryLayer(scene,new Rectangle(0,bottom,ClientSize.Width,ClientSize.Height-bottom),.40,.61,12*scale)
            };
        }
    }
    internal void ResumeHero(){if(shuttingDown)return;startupPending=false;ActiveHero.ResumePose();UpdatePlayback();}
    bool ResolveAppearance(){return appearanceMode=="dark"||appearanceMode=="system"&&readSystemDark();}
    internal int ContentTop{get{return (int)Math.Round(160*scale);}}
    internal Bitmap SnapshotContent(bool includeHero){
        int top=ContentTop;Rectangle body=new Rectangle(0,top,ClientSize.Width,ClientSize.Height-top);
        Bitmap canvas=new Bitmap(body.Width,body.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try{using(Graphics g=Graphics.FromImage(canvas)){
            g.Clear(UI.Bg);GraphicsState saved=g.Save();g.TranslateTransform(0,-top);
            if(resourcePage!=null)resourcePage.RenderSurface(g,body);else OnPaintBackground(new PaintEventArgs(g,body));g.Restore(saved);
            if(includeHero&&(resourcePage==null||resourcePage.IncludeHeroInSnapshot)){StagePass ticket=resourcePage==null?stage:resourcePage.Hero;Rectangle bounds=ticket.Bounds;bounds.Offset(0,-top);ticket.RenderOverlay(g,bounds,0);}
            Control surface=resourcePage==null?(Control)this:resourcePage;
            foreach(Control child in surface.Controls){
                if(child is StagePass||child==chrome||child is ThemeReveal||child is ResourcePage||!child.Visible||!body.IntersectsWith(child.Bounds))continue;
                using(Bitmap image=new Bitmap(child.Width,child.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb)){
                    if(child is TextBoxBase)using(Graphics drawing=Graphics.FromImage(image)){
                        drawing.Clear(child.BackColor);IntPtr dc=drawing.GetHdc();try{PrintControl(child.Handle,0x0317,dc,new IntPtr(14));}finally{drawing.ReleaseHdc(dc);}
                    }else child.DrawToBitmap(image,child.ClientRectangle);
                    g.DrawImageUnscaled(image,child.Left,child.Top-top);
                }
            }
        }return canvas;}catch{canvas.Dispose();throw;}
    }
    [DllImport("user32.dll",EntryPoint="SendMessageW")]static extern IntPtr PrintControl(IntPtr window,int message,IntPtr dc,IntPtr flags);
    Bitmap SnapshotAppearance(bool includeHero=true){
        Bitmap image=new Bitmap(ClientSize.Width,ClientSize.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try{using(Graphics g=Graphics.FromImage(image))using(Bitmap body=SnapshotContent(includeHero))using(Bitmap header=new Bitmap(chrome.Width,chrome.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb)){
            g.Clear(UI.Bg);g.CompositingMode=CompositingMode.SourceCopy;g.DrawImageUnscaled(body,0,ContentTop);chrome.DrawToBitmap(header,chrome.ClientRectangle);g.DrawImageUnscaled(header,0,0);
        }return image;}catch{image.Dispose();throw;}
    }
    internal Bitmap SnapshotScene(){
        Bitmap canvas;
        if(resourcePage!=null){canvas=new Bitmap(ClientSize.Width,ClientSize.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);resourcePage.DrawToBitmap(canvas,ClientRectangle);}
        else using(Bitmap window=new Bitmap(Width,Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb)){
            DrawToBitmap(window,new Rectangle(Point.Empty,Size));Point client=PointToScreen(Point.Empty);client.Offset(-Left,-Top);
            canvas=window.Clone(new Rectangle(client,ClientSize),System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        }
        try{using(Graphics g=Graphics.FromImage(canvas)){
            if(themeReveal!=null&&!themeReveal.IsDisposed&&themeReveal.Visible)using(Bitmap body=new Bitmap(themeReveal.Width,themeReveal.Height)){themeReveal.DrawToBitmap(body,new Rectangle(Point.Empty,body.Size));g.DrawImageUnscaled(body,themeReveal.Location);}
            using(Bitmap header=new Bitmap(chrome.Width,chrome.Height)){chrome.DrawToBitmap(header,new Rectangle(Point.Empty,header.Size));g.DrawImageUnscaled(header,chrome.Location);}
        }return canvas;}catch{canvas.Dispose();throw;}
    }
    void UpdateChrome(){
        chrome.PageKind=resourcePage==null?-1:resourcePage.Kind;chrome.Shiny=shinyGame;chrome.Game=Game;chrome.BackColor=UI.Bg;
        RoundBtn[] navigation={navHome,official,community,wiki,fan};int selected=PagePosition(resourcePage==null?-1:resourcePage.Kind);if(resourcePage!=null&&(resourcePage.Kind==6||resourcePage.Kind==7))selected=PagePosition(-1);
        for(int i=0;i<navigation.Length;i++){
            RoundBtn item=navigation[i];item.TabIndex=i;item.ExternalLink=false;item.BackColor=UI.Card;item.Fill=i==selected?(UI.IsWabi&&!UI.IsDark?UI.C("BD9B69"):UI.IsBauhaus?UI.C("244E86"):UI.Accent):UI.Card;item.FillHover=i==selected?(UI.IsWabi&&!UI.IsDark?UI.C("C7A574"):UI.Blend(item.Fill,UI.Text,.12f)):UI.GhostH;
            item.HasStroke=false;item.ForeColor=i==selected?(UI.IsWabi&&!UI.IsDark?UI.C("1B304C"):UI.IsDark?UI.Bg:UI.C("FFF5DC")):UI.Text;item.Radius=UI.Corner;
        }
        brightness.TabIndex=5;brand.TabIndex=6;brightness.Fill=UI.Bg;brightness.FillHover=UI.GhostH;brightness.HasStroke=false;brightness.ForeColor=UI.Text;
        ((GameLogo)brand).Shiny=shinyGame;((GameLogo)brand).Cgss=cgssGame;((GameLogo)brand).Million=millionGame;brand.AccessibleName=millionGame?"百万现场 Logo":cgssGame?"灰姑娘女孩 STARLIGHT STAGE Logo":shinyGame?"闪耀色彩 SONG FOR PRISM Logo":"学园偶像大师 Logo";wiki.Text=Game!=GameExperience.Gakumas?"剧情站":"Wiki";
        brand.BackColor=edition.BackColor=lblSave.BackColor=UI.Bg;brand.ForeColor=UI.Text;edition.ForeColor=UI.Dim;edition.Text=millionGame?"765 / MILLION LIVE!":cgssGame?"346 / CINDERELLA GIRLS":shinyGame?"283 / SONG FOR PRISM":"STAGE / 01";lblSave.Visible=resourcePage==null;chrome.Invalidate();
    }
    void FollowSystemAppearance(){
        if(!layoutReady||shuttingDown||IsDisposed||Disposing)return;
        bool dark=ResolveAppearance();
        if(UI.IsDark!=dark||UI.IsBauhaus!=(!dark&&Game==GameExperience.Shiny)||UI.IsWabi!=(Game==GameExperience.Cgss)||UI.IsMillion!=(Game==GameExperience.Million)){
            if(themeReveal!=null)themeReveal.Complete();
            bool animate=!RenderMode&&!startupPending&&Visible&&Opacity>.9&&Form.ActiveForm==this;
            bool wasBusy=ActiveHero.Busy;Bitmap previous=null;HeroPose pose=new HeroPose();Point origin=PointToClient(Cursor.Position);
            if(!ClientRectangle.Contains(origin))origin=new Point(brightness.Left+brightness.Width/2,brightness.Top+brightness.Height/2);
            if(animate){
                if(activeMenu!=null)activeMenu.Close();
                pose=ActiveHero.CapturePose();ActiveHero.HoldPose(pose);if(resourcePage!=null)resourcePage.Hold();previous=SnapshotAppearance();
            }
            DropAllResourceBackdrops();DropHiddenResourcePages();UI.Theme(dark,Game==GameExperience.Shiny,Game==GameExperience.Cgss,Game==GameExperience.Million);stage.Rebuild();ApplyPalette();if(resourcePage!=null)resourcePage.ApplyTheme();ApplyLayout();if(!RenderMode&&resourcePage==null)RefreshState();
            if(animate){
                ActiveHero.HoldPose(pose);Bitmap next=SnapshotAppearance();
                ThemeReveal reveal=new ThemeReveal(previous,next,origin,UI.Accent);reveal.ChromeFrame=AnimateChrome;themeReveal=reveal;
                reveal.Bounds=ClientRectangle;Controls.Add(reveal);reveal.BringToFront();UpdatePlayback();
                reveal.Start(delegate{
                    if(themeReveal==reveal)themeReveal=null;Controls.Remove(reveal);
                    if(!shuttingDown){if(resourcePage!=null)resourcePage.Resume();else stage.ResumePose();ActiveHero.Busy=wasBusy;UpdatePlayback();DrainNavigation();}
                });
            }
        }
        UpdateSwitchHint();
        if(IsHandleCreated)WindowsAppearance.TitleBar(Handle);
        if(activeMenu!=null&&!activeMenu.IsDisposed)RecolorMenu(activeMenu);
    }
    void SetAppearanceMode(string mode,string day){
        if(!WindowsAppearance.Valid(mode))return;
        appearanceMode=mode;dayStyle=Game==GameExperience.Gakumas?"red":"bauhaus";
        try{WindowsAppearance.WritePreference(preferencePath,mode,dayStyle);}catch(Exception ex){Say("[外观] 无法保存选择："+ex.Message);}
        FollowSystemAppearance();
    }
    // One click on the header icon is the whole interaction; the three-way setting stays in More.
    void ToggleAppearance(){
        if(shuttingDown||!layoutReady)return;
        SetAppearanceMode(UI.IsDark?"light":"dark",null);
    }
    void UpdateSwitchHint(){
        string target=millionGame?"学园偶像大师":cgssGame?"百万现场":shinyGame?"灰姑娘女孩":"闪耀色彩";
        brand.AccessibleDescription="点击切换到"+target+"启动器，保留当前运行会话";
        hints.SetToolTip(brand,"切换到"+target);
        if(brightness!=null){string next=UI.IsDark?"当前夜间模式，点击切换到日间模式":"当前日间模式，点击切换到夜间模式";brightness.AccessibleDescription=next;hints.SetToolTip(brightness,next);}
    }
    internal static System.Drawing.Icon LoadEmbeddedIcon(string name){
        try{using(Stream stream=typeof(MainForm).Assembly.GetManifestResourceStream(name)){return stream==null?null:new System.Drawing.Icon(stream);}}
        catch{return null;}
    }
    // Shared by the loading window and the main window so both modes use the right icon.
    internal static System.Drawing.Icon LoadGameIcon(int game){
        System.Drawing.Icon icon=LoadEmbeddedIcon(game==GameExperience.Million?"million.ico":game==GameExperience.Cgss?"cgss.ico":game==GameExperience.Shiny?"shiny.ico":"gakumas.ico");
        if(icon!=null)return icon;
        try{return System.Drawing.Icon.ExtractAssociatedIcon(Path.Combine(RootOverride??Path.GetDirectoryName(Application.ExecutablePath),"学マス启动器.exe"));}catch{return null;}
    }
    internal static System.Drawing.Icon LoadGameIcon(bool shiny){return LoadGameIcon(shiny?GameExperience.Shiny:GameExperience.Gakumas);}
    // The window and taskbar icon follow the selected game, like the logo and palette.
    void ApplyGameIcon(){
        System.Drawing.Icon wanted=millionGame?millionIcon:cgssGame?cgssIcon:shinyGame?shinyIcon:gakumasIcon;
        if(wanted!=null&&!ReferenceEquals(Icon,wanted))Icon=wanted;
    }
    internal void UpdateHomePath(){if(homePath!=null)homePath.Text=ReadGameDir()??"选择 gakumas.exe 所在目录";}
    void SystemPreferenceChanged(object sender,UserPreferenceChangedEventArgs e){QueueAppearanceUpdate();}
    void QueueAppearanceUpdate(){
        if(shuttingDown||IsDisposed||Disposing||!IsHandleCreated||Interlocked.CompareExchange(ref themeUpdatePending,1,0)!=0)return;
        try{BeginInvoke(new Action(delegate{Interlocked.Exchange(ref themeUpdatePending,0);FollowSystemAppearance();}));}
        catch(InvalidOperationException){Interlocked.Exchange(ref themeUpdatePending,0);}
    }
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);if(layoutReady)WindowsAppearance.TitleBar(Handle);}
    void InitialBounds(){
        Point point=Cursor.Position;Rectangle area=Screen.FromPoint(point).WorkingArea;displayDpi=DisplayInfo.Dpi(point);float d=displayDpi/96f;
        ClientSize=new Size(Math.Min((int)(1100*d),area.Width-40),Math.Min((int)(760*d),area.Height-(int)(64*d)));
        Location=new Point(area.X+(area.Width-Width)/2,area.Y+(area.Height-Height)/2);UpdateMinimum();
    }
    void UpdateMinimum(){
        Rectangle area=Screen.FromHandle(Handle).WorkingArea;float d=displayDpi/96f;
        MinimumSize=SizeFromClientSize(new Size(Math.Min((int)(960*d),area.Width-40),Math.Min((int)(680*d),area.Height-(int)(64*d))));
    }
    void UpdateDisplay(){if(!shuttingDown&&pump!=null)pump.Rate=DisplayInfo.Refresh(this);}
    void UpdatePlayback(){if(!shuttingDown&&!IsDisposed&&!Disposing&&pump!=null)pump.SetActive(themeReveal==null&&Visible&&WindowState!=FormWindowState.Minimized&&(Form.ActiveForm==this||Game==GameExperience.Million));}
    void Animate(double dt){if(shuttingDown||IsDisposed||Disposing)return;if(resourcePage!=null)resourcePage.Advance(dt);else stage.Advance();foreach(RoundBtn b in buttons)b.Advance(dt);if(entrance.Elapsed.TotalSeconds<1.4)Invalidate();}
    void AnimateChrome(double dt){foreach(Control child in chrome.Controls){RoundBtn button=child as RoundBtn;if(button!=null)button.Advance(dt);}}
    static bool ContainsScreen(Control c,Point point){return c!=null&&!c.IsDisposed&&c.Visible&&c.RectangleToScreen(c.ClientRectangle).Contains(point);}
    static bool ContainsMenu(ToolStripDropDown menu,Point point){
        if(menu==null||menu.IsDisposed||!menu.Visible)return false;
        if(ContainsScreen(menu,point))return true;
        foreach(ToolStripItem item in menu.Items){ToolStripDropDownItem branch=item as ToolStripDropDownItem;if(branch!=null&&branch.HasDropDownItems&&ContainsMenu(branch.DropDown,point))return true;}
        return false;
    }
    void WireHover(Control source,Action open){
        source.MouseEnter+=delegate{if(shuttingDown)return;hoverClose.Stop();hoverDelay.Stop();hoverTarget=source;hoverAction=open;if(activeMenu==null||!activeMenu.Visible||activeMenu.SourceControl!=source)hoverDelay.Start();};
        source.MouseLeave+=delegate{if(shuttingDown)return;hoverDelay.Stop();if(menuIsHover&&activeMenu!=null&&activeMenu.SourceControl==source)hoverClose.Start();};
        source.MouseDown+=delegate{if(shuttingDown)return;openingByHover=false;hoverDelay.Stop();};
        source.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Down){hoverDelay.Stop();openingByHover=false;open();e.Handled=e.SuppressKeyPress=true;}};
    }
    void TryHoverOpen(Point point){
        if(!ContainsScreen(hoverTarget,point)||hoverAction==null)return;
        if(activeMenu!=null&&activeMenu.Visible&&activeMenu.SourceControl==hoverTarget)return;
        openingByHover=true;try{hoverAction();}finally{openingByHover=false;}
    }
    void TryHoverClose(Point point){
        if(activeMenu==null||!menuIsHover)return;
        if(ContainsScreen(activeMenu.SourceControl,point)||ContainsMenu(activeMenu,point))return;
        activeMenu.Close();
    }
    void WatchMenuHover(ToolStripDropDown menu){
        menu.MouseEnter+=delegate{if(shuttingDown)return;hoverClose.Stop();};
        menu.MouseLeave+=delegate{if(!shuttingDown&&menuIsHover)hoverClose.Start();};
    }
    void RecolorMenu(ToolStripDropDown menu){
        menu.BackColor=UI.Card;menu.ForeColor=UI.Text;
        foreach(ToolStripItem item in menu.Items){
            item.ForeColor=item.Enabled?UI.Text:UI.DotOff;
            string mode=item.Tag as string;ToolStripMenuItem option=item as ToolStripMenuItem;
            if(option!=null&&mode!=null)option.Checked=mode=="game:scsp"?shinyGame:mode=="game:gakumas"?!shinyGame:mode==appearanceMode;
            ToolStripDropDownItem branch=item as ToolStripDropDownItem;
            if(branch!=null&&branch.HasDropDownItems)RecolorMenu(branch.DropDown);
        }
        menu.Invalidate();
    }
    ContextMenuStrip CreateMenu(){
        if(activeMenu!=null)activeMenu.Close();
        ContextMenuStrip menu=new ContextMenuStrip();activeMenu=menu;menuIsHover=openingByHover;if(RenderMode||SuppressReveal)menu.Opacity=0;
        WatchMenuHover(menu);if(!RenderMode)MenuMotion.Attach(menu);menu.Font=UI.PixelFont(10,false,scale);menu.ShowImageMargin=false;menu.Renderer=new ThemeMenuRenderer();
        menu.BackColor=UI.Card;menu.ForeColor=UI.Text;
        menu.Closed+=delegate{
            if(!shuttingDown&&!IsDisposed&&!Disposing)try{BeginInvoke(new Action(delegate{if(activeMenu==menu)activeMenu=null;Font f=menu.Font;menu.Dispose();f.Dispose();}));}catch(InvalidOperationException){}
        };return menu;
    }
    void ShowUtilities(){
        bool busy=stoppingAllServices||preparing||cur!=null&&!cur.HasExited;bool game=GameRunning();
        ContextMenuStrip menu=CreateMenu();
        ToolStripMenuItem dir=new ToolStripMenuItem("打开游戏文件夹");dir.Click+=delegate{OpenGameDir();};menu.Items.Add(dir);
        ToolStripMenuItem doc=new ToolStripMenuItem("使用说明");doc.Click+=delegate{OpenDoc("使用说明.md");};menu.Items.Add(doc);
        ToolStripMenuItem errors=new ToolStripMenuItem("错误提示与解决方法");
        errors.Click+=delegate{OpenDoc("错误提示与解决方法.txt");};menu.Items.Add(errors);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BrightnessMenu(menu));
        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem service=new ToolStripMenuItem("重启本地服务");service.Enabled=!busy;service.Click+=delegate{RestartService();};menu.Items.Add(service);
        ToolStripMenuItem stopSvc=new ToolStripMenuItem("停止本地服务");stopSvc.Enabled=true;stopSvc.Click+=delegate{StopAllLocalServices();};menu.Items.Add(stopSvc);
        ToolStripMenuItem account=new ToolStripMenuItem(accountAction);account.Enabled=!busy;account.Click+=delegate{RunUtilityOperation(ToggleOfficial);};menu.Items.Add(account);
        ToolStripMenuItem uninstall=new ToolStripMenuItem("卸载解锁组件");uninstall.Enabled=!busy&&!game;uninstall.Click+=delegate{RunUtilityOperation(Uninstall);};menu.Items.Add(uninstall);
        menu.Items.Add(new ToolStripSeparator());
        if(directorySearch!=null){ToolStripMenuItem cancelSearch=new ToolStripMenuItem("取消目录搜索");cancelSearch.Click+=delegate{if(directorySearch!=null)directorySearch.Cancel();};menu.Items.Add(cancelSearch);}
        if(File.Exists(Path.Combine(Root,"out","local-master-activation","state.json"))){ToolStripMenuItem rollback=new ToolStripMenuItem("回退未确认的更新");rollback.Enabled=!busy&&!game;rollback.Click+=delegate{RunJob("rollback-master",ScriptArg("grab_master.ps1")+" -RollbackMaster");};menu.Items.Add(rollback);}
        RecolorMenu(menu);menu.Show(more,new Point(0,more.Height));
    }
    void ApplyPalette(){
        BackColor=UI.Bg;ForeColor=UI.Text;foreach(Control c in Controls){c.BackColor=UI.Bg;c.ForeColor=UI.Text;}
        foreach(RoundBtn b in buttons){b.Fill=UI.Card;b.FillHover=UI.GhostH;b.Stroke=UI.Border;b.HasStroke=false;b.Radius=UI.Corner;}
        foreach(RoundBtn link in new[]{navHome,wiki,community,official,fan}){link.HasStroke=false;link.Fill=UI.Bg;}navHome.Fill=UI.Card;navHome.ForeColor=UI.Accent;
        btnPlay.Fill=UI.Accent;btnPlay.FillHover=UI.AccentH;btnPlay.ForeColor=UI.IsDark?UI.Bg:Color.FromArgb(255,248,230);btnPlay.HasStroke=false;
        homePath.BackColor=UI.Card;homePath.ForeColor=UI.Dim;
        foreach(Label l in new[]{edition,masthead,lblSave,actionState,logTitle})l.ForeColor=UI.Dim;
        lblSrv.ForeColor=UI.Bad;dotSrv.DotColor=UI.Bad;lblGame.ForeColor=UI.Dim;dotGame.DotColor=UI.DotOff;
        txtOut.BackColor=UI.Blend(UI.Bg,UI.Card,.62f);txtOut.ForeColor=UI.Dim;headline.Text=UI.IsDark?"让这一刻，\n成为序章。":UI.IsBauhaus?"让热爱，\n有形可见。":"让下一场，\n立即开演。";
        UpdateSwitchHint();UpdateChrome();Invalidate(true);
    }
    void ApplyLayout(){
        if(!layoutReady||shuttingDown||layingOut||ClientSize.Width<1||ClientSize.Height<1)return;layingOut=true;SuspendLayout();
        try{
            float d=displayDpi/96f;scale=d*Math.Min(1,Math.Min(ClientSize.Width/(960f*d),ClientSize.Height/(680f*d)));
            lw=Math.Min(1400,ClientSize.Width/scale);lh=ClientSize.Height/scale;ox=(ClientSize.Width/scale-lw)/2;
            HomeLayout home=new HomeLayout(lw,lh);mainX=48;right=lw-48;divider=home.Divider;float w=right-mainX;
            Put(brand,48,3,220,58,25,true);Put(edition,51,64,200,16,8,true);
            if(UI.IsWabi){float col=w/12;Put(navHome,48,103,col,40,10.5f,false);Put(official,48+col,103,col,40,10.5f,false);Put(community,48+2*col,103,col,40,10.5f,false);Put(wiki,48+3*col,103,col,40,10.5f,false);Put(fan,48+4*col,103,col,40,10.5f,false);}
            else{Put(navHome,52,103,105,40,10.5f,false);Put(official,163,103,105,40,10.5f,false);Put(community,274,103,105,40,10.5f,false);Put(wiki,385,103,105,40,10.5f,false);Put(fan,496,103,105,40,10.5f,false);}
            Put(brightness,right-38,20,38,42,13,false);
            chrome.Density=scale;chrome.Offset=ox;chrome.LogicalWidth=lw;chrome.SetBounds(0,0,ClientSize.Width,ContentTop);chrome.Invalidate();
            Put(masthead,48,174,home.LeftWidth,22,8.5f,true);Put(lblSave,right-260,143,260,16,8,false);
            Put(headline,48,home.Y(206),home.LeftWidth,home.H(138),(UI.IsDark?31:33)*home.Compression,true);
            RectangleF pathBox=home.PathBox;Put(homePath,pathBox.X+12,pathBox.Y+12,home.LeftWidth-208,home.H(22),9,false);Put(homeFind,pathBox.Right-188,pathBox.Y,92,pathBox.Height,9,false);Put(homeDirectory,pathBox.Right-88,pathBox.Y,88,pathBox.Height,9,false);
            RectangleF play=home.Play;Put(btnPlay,play.X,play.Y,play.Width,play.Height,17*home.Compression,true);
            float sy=home.Y(486);Put(dotSrv,54,sy+8,7,7,0,false);Put(lblSrv,70,sy,148,24,9,false);Put(dotGame,235,sy+8,7,7,0,false);Put(lblGame,251,sy,170,24,9,false);
            RectangleF art=home.Ticket;Put(stage,art.X,art.Y,art.Width,art.Height,0,false);
            Put(btnUpdate,48,home.Tools.Y,214,home.Tools.Height,9.5f,false);Put(more,274,home.Tools.Y,116,home.Tools.Height,9.5f,false);
            Put(actionState,48,divider+17,w,25,9,false);Put(logTitle,48,divider+47,80,20,8,true);
            RectangleF copy=home.CopyLog,clear=home.ClearLog;Put(copyLog,copy.X,copy.Y,copy.Width,copy.Height,0,false);Put(clearLog,clear.X,clear.Y,clear.Width,clear.Height,0,false);
            RectangleF logBox=home.Log;Put(txtOut,logBox.X,logBox.Y,logBox.Width,logBox.Height,8.5f,false);
            if(resourcePage!=null){resourcePage.Bounds=ClientRectangle;resourcePage.UpdateDpi(displayDpi);}chrome.BringToFront();
            Invalidate(true);
        }finally{ResumeLayout(false);layingOut=false;}
    }
    protected override void OnResize(EventArgs e){base.OnResize(e);pendingResource=int.MinValue;pendingGame=null;navigationVersion++;if(themeReveal!=null)themeReveal.Complete();DropAllResourceBackdrops();if(layoutReady){ApplyLayout();UpdatePlayback();}}
    [StructLayout(LayoutKind.Sequential)]struct NativeRect{public int Left,Top,Right,Bottom;}
    protected override void WndProc(ref Message m){
        if(m.Msg==0x02E0&&layoutReady&&!RenderMode){
            displayDpi=(int)(m.WParam.ToInt64()&0xffff);NativeRect r=(NativeRect)Marshal.PtrToStructure(m.LParam,typeof(NativeRect));
            MinimumSize=Size.Empty;SetBounds(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top);UpdateMinimum();ApplyLayout();UpdateDisplay();m.Result=IntPtr.Zero;return;
        }
        base.WndProc(ref m);
        if(m.Msg==0x007E&&pump!=null)UpdateDisplay();
        if(layoutReady&&(m.Msg==0x001A||m.Msg==0x031A||m.Msg==0x0015))QueueAppearanceUpdate();
    }
    protected override void OnPaintBackground(PaintEventArgs e){
        Graphics g=e.Graphics;g.Clear(UI.Bg);if(!layoutReady)return;
        g.TranslateTransform(ox*scale,0);g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;
        new HomeLayout(lw,lh).Paint(g);
    }
    void SaveClient(string path){
        using(Bitmap b=SnapshotAppearance()){b.Save(path,System.Drawing.Imaging.ImageFormat.Png);}
    }
    void StopUi(){
        if(shuttingDown)return;shuttingDown=true;if(directorySearch!=null)directorySearch.Cancel();
        pendingResource=int.MinValue;navigationVersion++;
        if(themeReveal!=null)themeReveal.Complete();
        if(subscribed){SystemEvents.UserPreferenceChanged-=SystemPreferenceChanged;subscribed=false;}
        hoverDelay.Stop();hoverClose.Stop();if(timer!=null)timer.Stop();
        if(stage!=null)stage.PaintCompleted=null;
        FramePump clock=pump;pump=null;if(clock!=null)clock.Dispose();
    }
    protected override void OnFormClosing(FormClosingEventArgs e){
        base.OnFormClosing(e);if(!e.Cancel)StopUi();
    }
    protected override void Dispose(bool disposing){
        if(disposing&&!resourcesDisposed){
            resourcesDisposed=true;StopUi();hints.Dispose();StartupReady=null;StartupBounds=null;StartupState=null;
            hoverDelay.Dispose();hoverClose.Dispose();if(timer!=null)timer.Dispose();
            if(activeMenu!=null){Font f=activeMenu.Font;activeMenu.Dispose();activeMenu=null;f.Dispose();}
            DropAllResourceBackdrops();foreach(ResourcePage page in resourcePages.Values)page.Dispose();resourcePages.Clear();
            foreach(Font f in fonts.Values)f.Dispose();fonts.Clear();
        }
        base.Dispose(disposing);
    }
    ToolStripMenuItem BrightnessMenu(ContextMenuStrip menu){
        ToolStripMenuItem branch=new QuickMenuItem("明暗模式");
        string[] modes={"system","light","dark"},names={"跟随系统","日间模式","暗金夜间"};
        for(int i=0;i<modes.Length;i++){
            string mode=modes[i];ToolStripMenuItem item=new ToolStripMenuItem(names[i]){Tag=mode};
            item.Click+=delegate{SetAppearanceMode(mode,null);};branch.DropDownItems.Add(item);
        }
        branch.DropDown.Renderer=new ThemeMenuRenderer();branch.DropDown.Font=menu.Font;
        if(RenderMode||SuppressReveal)branch.DropDown.Opacity=0;
        ((ToolStripDropDownMenu)branch.DropDown).ShowImageMargin=false;((ToolStripDropDownMenu)branch.DropDown).ShowCheckMargin=true;
        WatchMenuHover(branch.DropDown);if(!RenderMode)MenuMotion.Attach(branch.DropDown);return branch;
    }
    void CycleGame(){SelectGame((Game+1)%4);}
    internal void SelectGame(bool shiny){SelectGame(shiny?GameExperience.Shiny:GameExperience.Gakumas);}
    internal void SelectGame(int next){
        if(shuttingDown||startupPending||resourcePage!=null&&!resourcePage.CanNavigate)return;navigationVersion++;
        if(themeReveal!=null){pendingGame=next;pendingResource=int.MinValue;return;}
        if(next==Game){pendingGame=null;return;}
        pendingGame=null;pendingResource=int.MinValue;
        if(activeMenu!=null)activeMenu.Close();
        bool animate=!RenderMode&&!SuppressReveal&&Visible&&Opacity>.9&&Form.ActiveForm==this;
        HeroPose pose=ActiveHero.CapturePose();ActiveHero.HoldPose(pose);
        Bitmap before=animate?SnapshotAppearance():null;
        Point origin=PointToClient(brand.PointToScreen(new Point(brand.Width/2,brand.Height/2)));
        shinyGame=next==GameExperience.Shiny;cgssGame=next==GameExperience.Cgss;millionGame=next==GameExperience.Million;dayStyle=Game==GameExperience.Gakumas?"red":"bauhaus";ApplyGameIcon();
        try{GameExperience.WriteGame(Root,Game);WindowsAppearance.WritePreference(preferencePath,appearanceMode,dayStyle);}
        catch(Exception ex){Say("[游戏选择] 无法保存选择："+ex.Message);}
        DropAllResourceBackdrops();UI.Theme(ResolveAppearance(),Game==GameExperience.Shiny,Game==GameExperience.Cgss,Game==GameExperience.Million);stage.Rebuild();
        ActivatePage(HomePage);DropHiddenResourcePages();ApplyPalette();ApplyLayout();if(!RenderMode&&resourcePage==null)RefreshState();
        ActiveHero.HoldPose(pose);if(IsHandleCreated)WindowsAppearance.TitleBar(Handle);
        if(animate){
            Bitmap after=SnapshotAppearance();ThemeReveal reveal=new ThemeReveal(before,after,origin,UI.Accent);themeReveal=reveal;
            reveal.Bounds=ClientRectangle;Controls.Add(reveal);reveal.BringToFront();UpdatePlayback();
            reveal.Start(delegate{if(themeReveal==reveal)themeReveal=null;Controls.Remove(reveal);if(!shuttingDown){ActiveHero.ResumePose();UpdatePlayback();DrainNavigation();}});
        }else{ActiveHero.ResumePose();UpdatePlayback();}
    }
    int ResourceKey(int kind){return kind>=0&&kind<4?(Game==GameExperience.Million?kind+30:Game==GameExperience.Shiny?kind+10:Game==GameExperience.Cgss?kind+20:kind):kind;}
    void ActivatePage(int kind){
        ResourcePage retiring=resourcePage;resourcePage=null;
        if(retiring!=null){Controls.Remove(retiring);retiring.Hide();}
        foreach(Control child in Controls)if(child!=chrome&&child!=themeReveal)child.Visible=kind<0;
        if(kind>=0){
            int key=ResourceKey(kind);
            if(!resourcePages.TryGetValue(key,out resourcePage)){
                if(kind==4){
                    ScspPage shinyPage=new ScspPage(Root,RenderMode);
                    shinyPage.SurfaceChanged+=delegate{RemoveCachedBackdrop(4);if(resourcePage==shinyPage)DropResourceBackdrop();};resourcePage=shinyPage;
                }else if(kind==7){
                    MillionPage millionPage=new MillionPage();millionPage.SurfaceChanged+=delegate{RemoveCachedBackdrop(7);if(resourcePage==millionPage)DropResourceBackdrop();};resourcePage=millionPage;
                }else if(kind==6){
                    CgssPage cgssPage=new CgssPage(Root,RenderMode);cgssPage.SetAppearanceHandler(delegate(string mode){SetAppearanceMode(mode,null);});
                    cgssPage.SurfaceChanged+=delegate{RemoveCachedBackdrop(6);if(resourcePage==cgssPage)DropResourceBackdrop();};resourcePage=cgssPage;
                }else{
                    ResourcePage page=null;page=new ResourcePage(kind,Game,delegate(RoundBtn trigger,int index){OpenExternal(trigger,page.DestinationName(index),page.DestinationUrl(index));});resourcePage=page;
                }
                resourcePages.Add(key,resourcePage);
            }
            if(resourcePage.BackColor!=UI.Bg)resourcePage.ApplyTheme();resourcePage.Visible=true;
            resourcePage.Hero.PaintCompleted=delegate(double work){if(pump!=null)pump.ObserveWork(work);};
            resourcePage.Bounds=ClientRectangle;resourcePage.UpdateDpi(displayDpi);Controls.Add(resourcePage);resourcePage.BringToFront();
        }
        UpdateChrome();chrome.BringToFront();
    }
    void DrainNavigation(){
        if(shuttingDown)return;
        if(pendingGame.HasValue){int next=pendingGame.Value;int gameVersion=navigationVersion;pendingGame=null;pendingResource=int.MinValue;BeginInvoke(new Action(delegate{if(!shuttingDown&&gameVersion==navigationVersion)SelectGame(next);}));return;}
        int page=pendingResource,version=navigationVersion;pendingResource=int.MinValue;
        if(page!=int.MinValue&&page!=(resourcePage==null?-1:resourcePage.Kind))BeginInvoke(new Action(delegate{if(!shuttingDown&&version==navigationVersion)ShowResource(page);}));
    }
    internal void ShowResource(int kind){
        if(shuttingDown||startupPending||kind>7||kind< -1||resourcePage!=null&&!resourcePage.CanNavigate)return;navigationVersion++;
        if(kind==4&&Game!=GameExperience.Shiny){SelectGame(GameExperience.Shiny);return;}
        if(kind==6&&Game!=GameExperience.Cgss){SelectGame(GameExperience.Cgss);return;}
        if(kind==7&&Game!=GameExperience.Million){SelectGame(GameExperience.Million);return;}
        if(kind<0&&Game!=GameExperience.Gakumas){SelectGame(GameExperience.Gakumas);return;}
        if(themeReveal!=null){pendingResource=kind;pendingGame=null;return;}
        pendingResource=int.MinValue;if(kind<0&&resourcePage==null||kind>=0&&resourcePage!=null&&resourcePage.Kind==kind)return;
        if(themeReveal!=null)themeReveal.Complete();if(activeMenu!=null)activeMenu.Close();
        ValidateResourceBackdrops();
        StagePass source=resourcePage==null?stage:resourcePage.Hero;HeroPose pose=source.CapturePose();source.HoldPose(pose);
        Rectangle origin=new Rectangle(PointToClient(source.PointToScreen(Point.Empty)),source.Size);
        RasterFrame before=resourcePage!=null&&resourceBackdrop!=null?resourceBackdrop.Retain():CaptureContentBackdrop();
        int previous=resourcePage==null?-1:resourcePage.Kind;ActivatePage(kind);
        UpdateChrome();chrome.BringToFront();StagePass destination=resourcePage==null?stage:resourcePage.Hero;destination.HoldPose(pose);
        Rectangle target=new Rectangle(PointToClient(destination.PointToScreen(Point.Empty)),destination.Size);
        RasterFrame after=CaptureContentBackdrop();DropResourceBackdrop();if(resourcePage!=null)resourceBackdrop=after.Retain();
        ThemeReveal transition=new ThemeReveal(before,after,Point.Empty,UI.Accent);
        transition.Direction=PagePosition(kind)>=PagePosition(previous)?1:-1;
        origin.Offset(0,-ContentTop);target.Offset(0,-ContentTop);
        transition.TravelingHero=source;transition.ArrivalHero=destination;transition.HeroFrom=origin;transition.HeroTo=target;transition.StartPose=pose;
        transition.LandHero=delegate(HeroPose landed){if(!shuttingDown&&!destination.IsDisposed)destination.HoldPose(landed);};
        transition.ChromeFrame=AnimateChrome;themeReveal=transition;transition.Bounds=new Rectangle(0,ContentTop,ClientSize.Width,ClientSize.Height-ContentTop);Controls.Add(transition);transition.BringToFront();chrome.BringToFront();UpdatePlayback();
        transition.Start(delegate{
            if(themeReveal==transition)themeReveal=null;Controls.Remove(transition);
            if(!shuttingDown){
                if(resourcePage!=null)resourcePage.Resume();else stage.ResumePose();UpdatePlayback();
                DrainNavigation();
            }
        });
    }
    void DropResourceBackdrop(){RasterFrame old=resourceBackdrop;resourceBackdrop=null;if(old!=null)old.Dispose();}
    void RemoveCachedBackdrop(int kind){
        RasterFrame old;if(!resourceBackdropCache.TryGetValue(kind,out old))return;
        backdropCacheBytes-=old.Size.Width*(long)old.Size.Height*4;resourceBackdropCache.Remove(kind);backdropRecency.Remove(kind);old.Dispose();
    }
    void InvalidateHomeBackdrop(){RemoveCachedBackdrop(-1);}
    void DropAllResourceBackdrops(){
        DropResourceBackdrop();foreach(RasterFrame frame in resourceBackdropCache.Values)frame.Dispose();resourceBackdropCache.Clear();backdropRecency.Clear();backdropCacheBytes=0;backdropSize=Size.Empty;
    }
    void ValidateResourceBackdrops(){
        if(backdropSize!=ClientSize||backdropDpi!=displayDpi||backdropDark!=UI.IsDark||backdropBauhaus!=UI.IsBauhaus){
            DropAllResourceBackdrops();backdropSize=ClientSize;backdropDpi=displayDpi;backdropDark=UI.IsDark;backdropBauhaus=UI.IsBauhaus;
        }
    }
    RasterFrame CaptureContentBackdrop(){
        ValidateResourceBackdrops();int kind=ResourceKey(resourcePage==null?-1:resourcePage.Kind);RasterFrame frame;
        if(kind<0){
            int scroll=txtOut.IsHandleCreated?PrintControl(txtOut.Handle,0x00CE,IntPtr.Zero,IntPtr.Zero).ToInt32():0;
            if(scroll!=homeScroll||homeSelectionStart!=txtOut.SelectionStart||homeSelectionLength!=txtOut.SelectionLength)InvalidateHomeBackdrop();
            homeScroll=scroll;homeSelectionStart=txtOut.SelectionStart;homeSelectionLength=txtOut.SelectionLength;
        }
        if(resourceBackdropCache.TryGetValue(kind,out frame)){backdropRecency.Remove(kind);backdropRecency.Add(kind);return frame.Retain();}
        frame=new RasterFrame(SnapshotContent(false));long bytes=frame.Size.Width*(long)frame.Size.Height*4;
        if(bytes<=BackdropCacheLimit){
            while(resourceBackdropCache.Count>=5||backdropCacheBytes+bytes>BackdropCacheLimit){
                RemoveCachedBackdrop(backdropRecency[0]);
            }
            resourceBackdropCache.Add(kind,frame.Retain());backdropRecency.Add(kind);backdropCacheBytes+=bytes;
        }
        return frame;
    }
    void DropHiddenResourcePages(){List<int> remove=new List<int>();foreach(KeyValuePair<int,ResourcePage> entry in resourcePages)if(entry.Value!=resourcePage&&entry.Key!=4){entry.Value.Dispose();remove.Add(entry.Key);}foreach(int kind in remove)resourcePages.Remove(kind);}
    void OpenExternal(RoundBtn source,string label,string url){
        if(shuttingDown)return;actionState.Text="正在前往"+label+"…";
        source.Navigate(delegate{
            ThreadPool.QueueUserWorkItem(delegate{
                string result;
                try{if(ExternalNavigationProbe!=null)ExternalNavigationProbe(url);else{ProcessStartInfo browser=new ProcessStartInfo(url);browser.UseShellExecute=true;Process.Start(browser);}result="已请求在浏览器打开"+label;}
                catch(Exception ex){result="无法打开"+label+"："+ex.Message;}
                if(!IsDisposed&&IsHandleCreated)try{BeginInvoke(new Action(delegate{if(!shuttingDown){actionState.Text=result;if(resourcePage!=null)resourcePage.NavigationFinished();}}));}catch(InvalidOperationException){}
            });
        });
    }
    void PrepareAndRun(string name,string script,bool hook){
        if(stoppingAllServices||preparing||cur!=null&&!cur.HasExited)return;
        int generation=stopGeneration;stage.Spin();preparing=true;CancellationTokenSource searchSource=new CancellationTokenSource();directorySearch=searchSource;RefreshState();
        ThreadPool.QueueUserWorkItem(delegate{
            bool ready=false;try{ready=EnsureGamePath(searchSource.Token);if(ready&&!searchSource.IsCancellationRequested&&generation==stopGeneration&&!stoppingAllServices&&hook)EnsureHook();}
            catch(Exception ex){try{if(!IsDisposed)Say("[准备] "+ex.Message);}catch(InvalidOperationException){}}
            try{if(!IsDisposed&&IsHandleCreated)BeginInvoke(new Action(delegate{if(ReferenceEquals(directorySearch,searchSource))directorySearch=null;if(generation==stopGeneration)preparing=false;bool cancelled=searchSource.IsCancellationRequested;searchSource.Dispose();if(ready&&!cancelled&&generation==stopGeneration&&!stoppingAllServices)RunJob(name,ScriptArg(script)+(IsOfficialMode()&&script=="launch_game.ps1"?" -Official":""));RefreshState();}));}catch(InvalidOperationException){}
        });
    }
    string ScriptArg(string script)
    {
        return "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(Mitm, script) + "\"";
    }

    static bool PortOpen(int port)
    {
        try
        {
            System.Net.NetworkInformation.IPGlobalProperties props =
                System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            foreach (System.Net.IPEndPoint ep in props.GetActiveTcpListeners())
                if (ep.Port == port) return true;
            return false;
        }
        catch { return false; }
    }

    static bool GameRunning()
    {
        try {
            Process[] games=Process.GetProcessesByName("gakumas");
            bool running=games.Length>0;
            foreach(Process game in games)game.Dispose();
            return running;
        } catch { return false; }
    }
    string ReadGameDir()
    {
        try
        {
            string f = Path.Combine(Root, "game_path.txt");
            if (!File.Exists(f)) return null;
            string s = File.ReadAllText(f, Encoding.UTF8).Trim().Trim('"').TrimEnd('\\');
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    static bool SameFile(string a, string b)
    {
        try
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] ha, hb;
                using (FileStream fa = File.OpenRead(a)) { ha = sha.ComputeHash(fa); }
                using (FileStream fb = File.OpenRead(b)) { hb = sha.ComputeHash(fb); }
                if (ha.Length != hb.Length) return false;
                for (int i = 0; i < ha.Length; i++) if (ha[i] != hb[i]) return false;
                return true;
            }
        }
        catch { return false; }
    }

    CancellationTokenSource directorySearch;
    bool EnsureGamePath(CancellationToken token){
        string saved=ReadGameDir(),valid,error;
        if(GameDirectoryFinder.TryValidate(GameDirectoryKind.Gakumas,saved,out valid,out error))return true;
        GameDirectoryResult result=GameDirectoryFinder.Find(new GameDirectoryRequest{Game=GameDirectoryKind.Gakumas,SavedPath=saved,PackageRoot=Root},token,delegate(string message){Say("[路径] "+message);});
        if(result.UniquePath!=null){GameDirectoryFinder.SaveGakumasPath(Root,result.UniquePath);Say("[路径] 已识别并保存："+result.UniquePath);return true;}
        Say(result.Matches.Count>1?"[路径] 找到多份有效安装，请从更多菜单手动选择：":"[路径] 未找到完整游戏，请从更多菜单手动选择游戏目录。");
        foreach(GameDirectoryMatch match in result.Matches)Say("  "+match.Path+"（"+match.Source+"）");return false;
    }
    void ChooseGameDirectory(){
        if(preparing||cur!=null&&!cur.HasExited||GameRunning())return;
        using(FolderBrowserDialog dialog=new FolderBrowserDialog()){
            dialog.Description="选择包含 gakumas.exe 的 gakumas 文件夹，也可选择它的直接上级目录";dialog.ShowNewFolderButton=false;
            string saved=ReadGameDir();if(saved!=null&&Directory.Exists(saved))dialog.SelectedPath=saved;
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;string selected=dialog.SelectedPath;preparing=true;RefreshState();
            ThreadPool.QueueUserWorkItem(delegate{
                try{GameDirectoryFinder.SaveGakumasPath(Root,selected);Say("[路径] 手动选择已保存："+ReadGameDir());}
                catch(Exception ex){Say("[路径] "+ex.Message);}
                if(!IsDisposed&&IsHandleCreated)try{BeginInvoke(new Action(delegate{preparing=false;RefreshState();}));}catch(InvalidOperationException){}
            });
        }
    }
    void FindGameDirectory(){
        if(preparing||cur!=null&&!cur.HasExited||GameRunning())return;
        int generation=stopGeneration;preparing=true;directorySearch=new CancellationTokenSource();CancellationTokenSource source=directorySearch;RefreshState();Say("[路径] 正在自动寻找学园游戏目录…");
        ThreadPool.QueueUserWorkItem(delegate{
            GameDirectoryResult result=GameDirectoryFinder.Find(new GameDirectoryRequest{Game=GameDirectoryKind.Gakumas,SavedPath=ReadGameDir(),PackageRoot=Root},source.Token,delegate(string message){Say("[路径] "+message);});
            try{if(result.UniquePath!=null){GameDirectoryFinder.SaveGakumasPath(Root,result.UniquePath);Say("[路径] 已识别并保存："+result.UniquePath);}else{
                Say(result.Cancelled?"[路径] 搜索已取消，原路径未改动。":result.Matches.Count>1?"[路径] 找到多份有效安装，请手动选择。":result.TimedOut?"[路径] 限时搜索结束，请手动选择游戏目录。":"[路径] 未找到完整游戏，请手动选择目录。");
                foreach(GameDirectoryMatch match in result.Matches)Say("  "+match.Path+"（"+match.Source+"）");
            }}catch(Exception ex){Say("[路径] "+ex.Message);}
            if(!IsDisposed&&IsHandleCreated)try{BeginInvoke(new Action(delegate{if(ReferenceEquals(directorySearch,source))directorySearch=null;source.Dispose();if(generation==stopGeneration)preparing=false;RefreshState();}));}catch(InvalidOperationException){}
        });
    }

    bool EnsureHook()
    {
        try
        {
            string gd = ReadGameDir();
            if (gd == null) return false;
            string plug = Path.Combine(gd, "gakumas_Data\\Plugins\\x86_64");
            string target = Path.Combine(plug, YAHA + ".dll");
            string orig = Path.Combine(plug, YAHA + ".orig.dll");
            string src = Path.Combine(Root, "hook\\yaha_proxy.dll");
            if (!Directory.Exists(plug) || !File.Exists(src) || !File.Exists(target)) return false;
            if (SameFile(target, src)) return true;
            if (!File.Exists(orig)) File.Copy(target, orig, true);
            File.Copy(src, target, true);
            Say("[补丁] 已自动装好解锁补丁。");
            return true;
        }
        catch (Exception ex)
        {
            Say("[补丁] 自动安装失败：" + ex.Message + "（游戏可能仍会以未解锁状态启动）");
            return false;
        }
    }

    void InstallHook()
    {
        string gd = ReadGameDir();
        if (gd == null) { Say("还没有游戏目录记录。先点一次「开始游戏」，让脚本自动认一下路径。"); return; }
        string plug = Path.Combine(gd, "gakumas_Data\\Plugins\\x86_64");
        string target = Path.Combine(plug, YAHA + ".dll");
        string orig = Path.Combine(plug, YAHA + ".orig.dll");
        string src = Path.Combine(Root, "hook\\yaha_proxy.dll");
        if (!Directory.Exists(plug)) { Say("找不到游戏的插件目录：" + plug); return; }
        if (!File.Exists(src)) { Say("包里缺 hook\\yaha_proxy.dll，重新解压一次完整包。"); return; }
        try
        {
            if (!File.Exists(orig))
            {
                if (!File.Exists(target)) { Say("游戏里没有原始 DLL，放弃。"); return; }
                if (SameFile(target, src))
                {
                    Say("文件已经是解锁版，但找不到原版备份，这次不动它。");
                    Say("想修复的话：先从 DMM 启动一次游戏（它会还原原版），再回来点这里。");
                    return;
                }
                File.Copy(target, orig, true);
                Say("已备份原版 → " + Path.GetFileName(orig));
            }
            File.Copy(src, target, true);
            Say("补丁已装好，可以点「开始游戏」了。");
        }
        catch (Exception ex) { Say("装补丁失败：" + ex.Message); }
    }

    void StopServiceAndGame()
    {
        try
        {
            ProcessStartInfo k = new ProcessStartInfo("taskkill", "/F /IM gakumas.exe");
            k.UseShellExecute = false; k.CreateNoWindow = true;
            Process.Start(k).WaitForExit(8000);
        }
        catch { }
        try
        {
            ProcessStartInfo ps = new ProcessStartInfo("powershell",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(Mitm, "stop_server.ps1") + "\"");
            ps.UseShellExecute = false; ps.CreateNoWindow = true; ps.WorkingDirectory = Root;
            Process.Start(ps).WaitForExit(20000);
        }
        catch { }
        try { KillCache(); } catch { }
    }

    void KillCache()
    {
        ProcessStartInfo ps = new ProcessStartInfo("powershell",
            "-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='python.exe'\\\" | " +
            "Where-Object { $_.CommandLine -match 'cache_server' } | " +
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }\"");
        ps.UseShellExecute = false; ps.CreateNoWindow = true;
        Process.Start(ps).WaitForExit(15000);
    }

    bool IsOfficialMode()
    {
        // A fresh installation has no selected game yet and starts in pass mode.
        if (ReadGameDir() == null) return false;
        // 官方账号模式 = 游戏目录里没有我们的代理 DLL
        return !IsHookInstalled();
    }

    bool IsHookInstalled()
    {
        string gd = ReadGameDir();
        if (gd == null) return false;
        string target = Path.Combine(Path.Combine(gd, "gakumas_Data\\Plugins\\x86_64"), YAHA + ".dll");
        string src = Path.Combine(Root, "hook\\yaha_proxy.dll");
        return SameFile(target, src);
    }

    void ToggleOfficial()
    {
        string gd = ReadGameDir();
        if (gd == null) { Say("没有游戏目录记录，先点一次「开始游戏」。"); return; }
        string plug = Path.Combine(gd, "gakumas_Data\\Plugins\\x86_64");
        string target = Path.Combine(plug, YAHA + ".dll");
        string orig = Path.Combine(plug, YAHA + ".orig.dll");
        string hk = Path.Combine(Root, "hook\\yaha_proxy.dll");
        bool hooked = !IsOfficialMode();

        Say("正在关游戏、停服务 ...");
        int utilityGeneration=stopGeneration;StopServiceAndGame();
        if(stoppingAllServices||utilityGeneration!=stopGeneration)return;
        try
        {
            if (hooked)
            {
                if (!File.Exists(orig))
                {
                    Say("找不到原版备份（.orig.dll），无法切到官方。");
                    Say("解决办法：从 DMM 启动一次游戏，它会自动还原原版，再回来点这里。");
                    return;
                }
                File.Copy(orig, target, true);
                try { File.WriteAllText(Path.Combine(Root, "official-mode.txt"), "official"); } catch (Exception) { }
                Say("已切到官方账号。点「开始游戏」即可跳过 DMM 直接登录官服。");
            }
            else
            {
                if (!File.Exists(target)) { Say("游戏里找不到网络库：" + target); return; }
                if (!File.Exists(orig)) File.Copy(target, orig, true);
                if (!File.Exists(hk)) { Say("包里缺 hook\\yaha_proxy.dll。"); return; }
                File.Copy(hk, target, true);
                try { string om = Path.Combine(Root, "official-mode.txt"); if (File.Exists(om)) File.Delete(om); } catch (Exception) { }
                Say("已切回解锁版。点「开始游戏」即可进游戏。");
            }
            RefreshToggle();
        }
        catch (Exception ex) { Say("切换失败：" + ex.Message); }
    }

    void CaptureArgs()
    {
        string gd = ReadGameDir();
        if (gd == null) { Say("还没有游戏目录记录，先点一次「开始游戏」让面板认一下路径。"); return; }

        Say("[1/3] 安装解锁补丁 ...");
        EnsureHook();

        Say("[2/3] 备份旧参数，准备重新抓取 ...");
        string af = Path.Combine(Root, "launch-args.json");
        try
        {
            if (File.Exists(af))
            {
                string bak = af + ".old-" + DateTime.Now.ToString("HHmmss");
                File.Move(af, bak);
                Say("        旧参数已备份为 " + Path.GetFileName(bak));
            }
        }
        catch (Exception ex) { Say("        备份失败：" + ex.Message); }

        Say("[3/3] 抓取参数 + 启动游戏 ...");
        Say("        抓取窗口会弹出来，按里面的提示从 DMM 启动一次游戏。");
        Say("        抓到后会自动接着启动，不用再点按钮。");
        RunJob("capture", ScriptArg("launch_game.ps1"));
    }

    void RefreshToggle()
    {
        if(InvokeRequired){if(IsHandleCreated)try{BeginInvoke(new Action(RefreshToggle));}catch(InvalidOperationException){}return;}

        accountAction = IsOfficialMode() ? "切回通行证" : "切到官方账号";
    }

    void Uninstall()
    {
        if (MessageBox.Show("会还原游戏文件、恢复系统代理、删除证书，并清掉路径记录，确定？", "卸载",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        string gd = ReadGameDir();
        Say("正在关游戏、停服务 ...");
        int utilityGeneration=stopGeneration;StopServiceAndGame();
        if(stoppingAllServices||utilityGeneration!=stopGeneration)return;
        // 完整卸载：还原游戏 DLL + 恢复系统代理 + 删除本机证书 + 清运行时残留。
        // 这一步是「点完卸载之后网络和游戏都不留问题」的关键。
        Say("正在还原游戏文件、恢复网络、删除证书 ...");
        try
        {
            ProcessStartInfo ps = new ProcessStartInfo("powershell",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(Mitm, "full_uninstall.ps1") + "\"");
            ps.UseShellExecute = false; ps.CreateNoWindow = true; ps.WorkingDirectory = Root;
            Process.Start(ps).WaitForExit(60000);
        }
        catch { }
        if(stoppingAllServices||utilityGeneration!=stopGeneration)return;
        if (gd != null)
        {
            string plug = Path.Combine(gd, "gakumas_Data\\Plugins\\x86_64");
            string target = Path.Combine(plug, YAHA + ".dll");
            string orig = Path.Combine(plug, YAHA + ".orig.dll");
            try
            {
                if (File.Exists(orig))
                {
                    File.Copy(orig, target, true);
                    File.Delete(orig);
                    Say("已还原官方文件。");
                }
                else Say("没有备份，跳过还原。");
            }
            catch (Exception ex) { Say("还原失败：" + ex.Message); }
        }
        try
        {
            string gp = Path.Combine(Root, "game_path.txt");
            if (File.Exists(gp)) { File.Delete(gp); Say("已删除路径记录。"); }
        }
        catch { }
        Say("卸载完成，游戏已恢复原样。");
    }

    void OpenGameDir()
    {
        string gd = ReadGameDir();
        if (gd == null) { Say("没有游戏目录记录。"); return; }
        try { Process.Start("explorer.exe", "\"" + gd + "\""); }
        catch (Exception ex) { Say("打不开：" + ex.Message); }
    }

    void OpenDoc(string name)
    {
        string p = Path.Combine(Root, name);
        if (!File.Exists(p)) { Say("找不到 " + name); return; }
        try { Process.Start("notepad.exe", "\"" + p + "\""); }
        catch (Exception ex) { Say("打不开：" + ex.Message); }
    }

    void RefreshState()
    {
        if(InvokeRequired){if(IsHandleCreated)try{BeginInvoke(new Action(RefreshState));}catch(InvalidOperationException){}return;}

        bool srv = PortOpen(18443);
        bool game = GameRunning();

        lblSrv.Text = srv ? "服务运行中" : "服务未启动";
        lblSrv.ForeColor = srv ? UI.Ok : UI.Bad;
        dotSrv.DotColor = srv ? UI.Ok : UI.Bad;
        dotSrv.Invalidate();

        lblGame.Text = game ? "游戏运行中" : "游戏未运行";
        lblGame.ForeColor = game ? UI.Ok : UI.Dim;
        dotGame.DotColor = game ? UI.Ok : UI.DotOff;
        dotGame.Invalidate();

        try
        {
            string stat = Path.Combine(Mitm, "static_userget.bin");
            string mt = Path.Combine(Mitm, "masterTrans");
            string saveTxt = "";
            if (File.Exists(stat)) saveTxt = (new FileInfo(stat).Length / 1024) + " KB";
            DateTime newest = DateTime.MinValue;
            if (Directory.Exists(mt))
                foreach (string f in Directory.GetFiles(mt, "*.json"))
                {
                    DateTime wt = File.GetLastWriteTime(f);
                    if (wt > newest) newest = wt;
                }
            if (newest != DateTime.MinValue)
                saveTxt += (saveTxt.Length > 0 ? "  ·  " : "") + "索引 " + newest.ToString("MM-dd HH:mm");
            lblSave.Text = saveTxt;
        }
        catch { }

        bool busy = stoppingAllServices || preparing || cur != null && !cur.HasExited;
        stage.Busy = busy;
        btnPlay.Text = busy ? "正在处理…" : "开始游戏";
        actionState.Text = stoppingAllServices ? "正在停止全部本地服务…" : busy ? "正在处理，请查看下方记录" : "准备就绪";
        btnPlay.Enabled = !busy;
        btnUpdate.Enabled = !busy;
        homeDirectory.Enabled = homeFind.Enabled = !busy && !game;
        UpdateHomePath();





        RefreshToggle();
    }

    void Say(string line)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { try { BeginInvoke(new Action<string>(Say), new object[] { line }); } catch (InvalidOperationException) { } return; }
        if (txtOut.Text.Length > 0 && !txtOut.Text.EndsWith("\n")) txtOut.AppendText(Environment.NewLine);
        txtOut.AppendText(line + Environment.NewLine);
        txtOut.SelectionStart = txtOut.TextLength;
        txtOut.ScrollToCaret();
    }

    bool ClearStopRequest()
    {
        try{string directory=Path.Combine(Root,"out");Directory.CreateDirectory(directory);string signal=Path.Combine(directory,"stop-request.txt");if(File.Exists(signal))File.Delete(signal);return true;}
        catch(Exception ex){Say("[启动失败] 无法清理停止标记："+ex.Message);return false;}
    }
    void StopAllLocalServices()
    {
        if(stoppingAllServices){Say("[停止] 正在执行总停止，请稍候。");return;}
        stoppingAllServices=true;Interlocked.Increment(ref stopGeneration);
        try{string directory=Path.Combine(Root,"out");Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"stop-request.txt"),DateTime.UtcNow.ToString("o"));}catch(Exception ex){Say("[停止警告] 停止标记写入失败："+ex.Message);}
        if(directorySearch!=null)directorySearch.Cancel();
        preparing=false;cur=null;
        Say("[停止] 优先中断启动/更新任务，停止所有版本的学园本地服务及监视器…");
        RefreshState();
        ThreadPool.QueueUserWorkItem(delegate{
            int code=-1;string diagnosis=null;
            try{
                ProcessStartInfo info=new ProcessStartInfo("powershell.exe",ScriptArg("stop_all_services.ps1"));
                info.WorkingDirectory=Root;info.UseShellExecute=false;info.CreateNoWindow=true;
                info.RedirectStandardOutput=true;info.RedirectStandardError=true;
                info.StandardOutputEncoding=info.StandardErrorEncoding=Encoding.GetEncoding(936);
                using(Process stop=new Process()){
                    stop.StartInfo=info;
                    DataReceivedEventHandler output=delegate(object sender,DataReceivedEventArgs e){
                        if(e.Data==null)return;
                        string hint=LauncherErrors.Diagnostic(e.Data);
                        if(hint!=null){Interlocked.Exchange(ref diagnosis,hint);Say(hint+" [诊断] "+e.Data.Trim());}
                        else Say(e.Data);
                    };
                    stop.OutputDataReceived+=output;stop.ErrorDataReceived+=output;
                    stop.Start();stop.BeginOutputReadLine();stop.BeginErrorReadLine();stop.WaitForExit();code=stop.ExitCode;
                }
            }catch(Exception ex){Say(LauncherErrors.FailedToStart("stop",ex));}
            if(!IsDisposed&&IsHandleCreated)try{BeginInvoke(new Action(delegate{
                stoppingAllServices=false;Say(LauncherErrors.Finish("stop",code,diagnosis));RefreshState();
            }));}catch(InvalidOperationException){}
        });
    }
            void RunUtilityOperation(Action operation)
    {
        if(stoppingAllServices||preparing||cur!=null&&!cur.HasExited)return;
        if(!ClearStopRequest())return;
        int generation=stopGeneration;preparing=true;RefreshState();
        ThreadPool.QueueUserWorkItem(delegate{
            try{if(generation==stopGeneration&&!stoppingAllServices)operation();}catch(Exception ex){Say(ex.Message);}
            if(!IsDisposed&&IsHandleCreated)try{BeginInvoke(new Action(delegate{if(generation==stopGeneration)preparing=false;RefreshState();}));}catch(InvalidOperationException){}
        });
    }

            void RunJob(string name, string args)
    {
        if(stoppingAllServices||cur!=null&&!cur.HasExited)return;
        if(!ClearStopRequest())return;
        int generation=stopGeneration;txtOut.Clear();Say("正在"+LauncherErrors.JobName(name)+"…");
        ProcessStartInfo psi=new ProcessStartInfo("powershell.exe",args);
        psi.WorkingDirectory=Root;psi.UseShellExecute=false;psi.RedirectStandardOutput=true;psi.RedirectStandardError=true;psi.CreateNoWindow=true;
        bool utf8Job=args.IndexOf("grab_master.ps1",StringComparison.OrdinalIgnoreCase)>=0;
        psi.StandardOutputEncoding=psi.StandardErrorEncoding=utf8Job?new UTF8Encoding(false):Encoding.GetEncoding(936);
        if(utf8Job)psi.EnvironmentVariables["GKM_LAUNCHER_UTF8"]="1";
        Process p=new Process();p.StartInfo=psi;
        string diagnosis=null;int closedStreams=0;
        DataReceivedEventHandler output=delegate(object sender,DataReceivedEventArgs e){
            if(e.Data==null){Interlocked.Increment(ref closedStreams);return;}
            if(generation!=stopGeneration)return;
            string hint=LauncherErrors.Diagnostic(e.Data);
            if(hint!=null){Interlocked.Exchange(ref diagnosis,hint);Say(hint+" [诊断] "+e.Data.Trim());}
            else Say(e.Data);
        };
        p.OutputDataReceived+=output;
        p.ErrorDataReceived+=output;
        p.EnableRaisingEvents=true;p.Exited+=delegate{
            SpinWait.SpinUntil(delegate{return Volatile.Read(ref closedStreams)==2;},500);
            int code=-1;try{code=p.ExitCode;}catch(InvalidOperationException){}
            if(!IsDisposed&&IsHandleCreated)try{BeginInvoke(new Action(delegate{
                if(generation==stopGeneration&&ReferenceEquals(cur,p)){cur=null;Say(LauncherErrors.Finish(name,code,diagnosis));RefreshState();}
                p.Dispose();
            }));}catch(InvalidOperationException){}
        };
        try{cur=p;p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();RefreshState();}
        catch(Exception ex){if(ReferenceEquals(cur,p))cur=null;p.Dispose();Say(LauncherErrors.FailedToStart(name,ex));RefreshState();}
    }

    void RestartService()
    {
        if(stoppingAllServices)return;
        int generation=stopGeneration;
        RunUtilityOperation(delegate{
        try
        {
            KillCache();
            ProcessStartInfo psi = new ProcessStartInfo(Py, "cache_server.py");
            psi.WorkingDirectory = Mitm;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["GKM_MODE"] = "offline";
            psi.EnvironmentVariables["GKM_MATCH"] = "exact";
            psi.EnvironmentVariables["GKM_MITM_PORT"] = "18443";
            psi.EnvironmentVariables["GKM_H2C_PORT"] = "18444";
            psi.EnvironmentVariables["GKM_UPSTREAM_PROXY"] = "127.0.0.1:1";
            psi.EnvironmentVariables["GKM_STATIC_USER"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            if(stoppingAllServices||generation!=stopGeneration)return;
            Process.Start(psi);
            Say("已重启本地服务。");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(3000);
                try { BeginInvoke(new Action(RefreshState)); } catch { }
            });
        }
        catch (Exception ex) { Say(LauncherErrors.FailedToStart("restart",ex)); }
    });
    }

    List<string> CheckLayout(){
        List<string> errors=new List<string>();
        List<Control> visible=new List<Control>();foreach(Control c in Controls)visible.Add(c);foreach(Control c in chrome.Controls)visible.Add(c);
        foreach(Control c in visible){
            if(!c.Visible)continue;
            if(c.Left<0||c.Top<0||c.Right>c.Parent.ClientSize.Width+1||c.Bottom>c.Parent.ClientSize.Height+1)errors.Add("OUTSIDE "+c.Text+" "+c.Bounds);
            if(c==stage||c is TextBoxBase||c is Dot||c.Text.Length==0)continue;
            Size size=TextRenderer.MeasureText(c.Text,c.Font,Size.Empty,TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
            int reserve=c==btnPlay?(int)(70*scale):c is CheckBox?(int)(22*scale):0;
            if(size.Width>c.Width-reserve+2)errors.Add("TEXT WIDTH "+c.Text+" needs "+size.Width+" has "+(c.Width-reserve));
            if(size.Height>c.Height+2)errors.Add("TEXT HEIGHT "+c.Text.Replace("\n","/")+" needs "+size.Height+" has "+c.Height);
        }
        return errors;
    }
    static string FrameSignature(StagePass stage){
        using(Bitmap bitmap=new Bitmap(stage.Width,stage.Height))
        using(Graphics drawing=Graphics.FromImage(bitmap))
        using(MemoryStream data=new MemoryStream()){
            stage.RenderFrame(drawing);bitmap.Save(data,System.Drawing.Imaging.ImageFormat.Png);
            using(System.Security.Cryptography.SHA256 hash=System.Security.Cryptography.SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(data.ToArray()));
        }
    }
    static void Expect(bool condition,string name,List<string> report,ref bool passed){
        report.Add((condition?"PASS ":"FAIL ")+name);if(!condition)passed=false;
    }
    static void RenderSuite(MainForm form,string target,bool verify,bool film){
        Directory.CreateDirectory(target);List<string> report=new List<string>();bool passed=true;
        form.FormBorderStyle=FormBorderStyle.None;form.ShowInTaskbar=false;form.Opacity=0;
        form.MaximumSize=new Size(12000,12000);form.MinimumSize=Size.Empty;form.Location=new Point(-24000,-24000);
        form.Show();Application.DoEvents();form.ActiveHero.PreviewTime=2.4;form.ActiveHero.SpinProgress=-1;
        bool systemDark=false;form.readSystemDark=delegate{return systemDark;};
        string renderFixture=Path.Combine(target,"preference-fixture");Directory.CreateDirectory(renderFixture);form.Root=renderFixture;form.preferencePath=Path.Combine(renderFixture,"appearance-mode.txt");form.appearanceMode="system";form.SelectGame(false);
        form.appearanceMode="system";form.dayStyle="red";form.FollowSystemAppearance();
        string[] names={"day-red","night-gold","day-bauhaus"};
        report.Add("Only three styles: day red poster, night black-gold theatre, day Bauhaus.");
        report.Add("Native GDI/GDI+ rendering; scheduler callbacks exclude physical display presentation.");
        Expect(UI.Available("StagePass-Nonexistent-Font","Consolas")=="Consolas","missing-font fallback",report,ref passed);
        report.Add("Detected display refresh: "+DisplayInfo.Refresh(form)+" Hz");
        float[] densities=verify?new float[]{1,1.25f,1.5f,1.75f,2,2.5f,3}:new float[]{1};
        for(int style=0;style<3;style++){
            systemDark=style==1;form.SelectGame(style==2);form.FollowSystemAppearance();
            foreach(float d in densities){
                form.displayDpi=(int)(96*d);form.ClientSize=new Size((int)(1100*d),(int)(760*d));form.ApplyLayout();Application.DoEvents();
                List<string> errors=form.CheckLayout();Expect(errors.Count==0,names[style]+" @ "+d+"x layout "+string.Join("; ",errors.ToArray()),report,ref passed);
                if(d==1||d==2||d==3)form.SaveClient(Path.Combine(target,names[style]+"-"+(int)(d*100)+".png"));
            }
            if(verify)foreach(Size logical in new[]{new Size(960,680),new Size(1440,900),new Size(900,600)}){
                form.displayDpi=192;form.ClientSize=new Size(logical.Width*2,logical.Height*2);form.ApplyLayout();Application.DoEvents();
                List<string> errors=form.CheckLayout();Expect(errors.Count==0,names[style]+" compact "+logical+" "+string.Join("; ",errors.ToArray()),report,ref passed);
                form.SaveClient(Path.Combine(target,names[style]+"-compact-"+logical.Width+".png"));
            }
        }
        form.displayDpi=96;form.ClientSize=new Size(1100,760);form.ApplyLayout();
        for(int style=0;style<3;style++){
            systemDark=style==1;form.SelectGame(style==2);form.FollowSystemAppearance();
            form.ActiveHero.Busy=true;form.SaveClient(Path.Combine(target,names[style]+"-busy.png"));form.ActiveHero.Busy=false;
        }
        if(film){
            form.displayDpi=96;form.ClientSize=new Size(1100,760);form.ApplyLayout();
            for(int style=0;style<3;style++){
                form.appearanceMode="system";systemDark=style==1;form.SelectGame(style==2);form.FollowSystemAppearance();
                string dir=Path.Combine(target,"film-"+names[style]);Directory.CreateDirectory(dir);
                for(int frame=0;frame<180;frame++){double t=frame/60.0;form.ActiveHero.PreviewTime=t;form.ActiveHero.Busy=t>=.8;form.ActiveHero.SpinProgress=t>=1.5&&t<2.55?(t-1.5)/1.05:-1;form.SaveClient(Path.Combine(dir,frame.ToString("0000")+".png"));}
            }
        }
        form.ActiveHero.Busy=false;form.ActiveHero.SpinProgress=-1;form.ActiveHero.PreviewTime=2.4;
        if(verify){
            for(int style=0;style<3;style++){
                systemDark=style==1;form.SelectGame(style==2);form.FollowSystemAppearance();
                using(StagePass ticket=new StagePass()){
                    ticket.Size=new Size(452,421);ticket.BackColor=UI.Bg;ticket.PreviewTime=2.4;
                    ticket.TrackPointer(new PointF(-1,-1),1);
                    string left=FrameSignature(ticket);
                    ticket.TrackPointer(new PointF(1,1),1);
                    Expect(left!=FrameSignature(ticket),names[style]+" ticket follows mouse with translation and perspective",report,ref passed);
                    ticket.TrackPointer(PointF.Empty,1);
                    string still=FrameSignature(ticket);ticket.Spin();Thread.Sleep(110);
                    Expect(still!=FrameSignature(ticket),names[style]+" actual click-spin clock animates ticket",report,ref passed);
                    ticket.Reduced=true;string reduced=FrameSignature(ticket);
                    ticket.TrackPointer(new PointF(1,1),1);ticket.Spin();
                    Expect(reduced==FrameSignature(ticket),names[style]+" reduced motion disables pointer and spin",report,ref passed);
                }
            }
            using(StagePass transition=new StagePass()){
                transition.PreviewTime=1;double before=transition.OrbitAt(1);
                transition.Busy=true;double atChange=transition.OrbitAt(1);
                Expect(Math.Abs(before-atChange)<.000001,"busy acceleration preserves orbit position",report,ref passed);
                double firstStep=transition.OrbitAt(1.01)-atChange;
                Expect(firstStep>.0025&&firstStep<.004,"busy acceleration eases speed without a jump",report,ref passed);
                transition.PreviewTime=2;before=transition.OrbitAt(2);transition.Busy=false;
                Expect(Math.Abs(before-transition.OrbitAt(2))<.000001,"busy completion preserves orbit position while decelerating",report,ref passed);
            }
            using(FramePump adaptive=new FramePump(form,delegate(double dt){})){
                adaptive.Rate=240;
                for(int i=0;i<100;i++)adaptive.ObserveWork(12);
                Expect(adaptive.EffectiveRate<=70,"slow renderer lowers rate to its sustainable budget",report,ref passed);
                double limited=adaptive.EffectiveRate;
                for(int i=0;i<1500;i++)adaptive.ObserveWork(.5);
                Expect(adaptive.EffectiveRate>limited&&Math.Abs(adaptive.EffectiveRate-240)<1,"renderer recovers rate after workload falls",report,ref passed);
                adaptive.Rate=75;
                Expect(Math.Abs(adaptive.EffectiveRate-75)<.1,"monitor refresh change resets adaptive budget",report,ref passed);
            }
            // Verify game choice and brightness independently using an isolated root.
            systemDark=false;form.SelectGame(false);form.SetAppearanceMode("system",null);Application.DoEvents();
            Expect(!UI.IsDark&&!UI.IsBauhaus,"system light selects Gakumas red",report,ref passed);
            systemDark=true;Message change=Message.Create(form.Handle,0x001A,IntPtr.Zero,IntPtr.Zero);form.WndProc(ref change);Application.DoEvents();
            Expect(UI.IsDark,"WM_SETTINGCHANGE updates system mode live",report,ref passed);
            form.SetAppearanceMode("light",null);Expect(!UI.IsDark&&!UI.IsBauhaus,"manual day ignores system dark",report,ref passed);
            form.SelectGame(true);Expect(!UI.IsDark&&UI.IsBauhaus,"Shiny owns the Bauhaus day palette",report,ref passed);
            foreach(string setting in new[]{"system","light","dark"}){
                form.SetAppearanceMode(setting,null);string previousRoot=RootOverride;RootOverride=renderFixture;
                using(MainForm reopened=new MainForm())Expect(reopened.appearanceMode==setting&&reopened.shinyGame&&reopened.resourcePage is ScspPage,"reopen restores game and "+setting+" brightness",report,ref passed);
                RootOverride=previousRoot;
            }
            form.SetAppearanceMode("system",null);systemDark=false;form.FollowSystemAppearance();Expect(UI.IsBauhaus&&!UI.IsDark,"system light restores Shiny Bauhaus",report,ref passed);
            form.SelectGame(false);form.hoverTarget=form.more;form.hoverAction=form.ShowUtilities;
            Point buttonPoint=form.more.PointToScreen(new Point(form.more.Width/2,form.more.Height/2));form.TryHoverOpen(buttonPoint);
            Expect(form.activeMenu!=null&&form.activeMenu.Visible,"More opens on hover",report,ref passed);
            ToolStripMenuItem brightness=(ToolStripMenuItem)form.activeMenu.Items[3];brightness.ShowDropDown();Application.DoEvents();
            Expect(brightness.DropDown.Visible&&brightness.DropDownItems.Count==3,"brightness offers system, day and gold night",report,ref passed);
            Point childPoint=brightness.DropDown.PointToScreen(new Point(10,10));form.TryHoverClose(childPoint);
            Expect(form.activeMenu!=null&&form.activeMenu.Visible,"nested hover retains the parent menu",report,ref passed);
            ((ToolStripMenuItem)brightness.DropDownItems[2]).PerformClick();Application.DoEvents();Expect(UI.IsDark,"night menu action applies",report,ref passed);
            if(form.activeMenu!=null)form.activeMenu.Close();Application.DoEvents();
            ((GameLogo)form.brand).PerformClick();Application.DoEvents();
            Expect(form.shinyGame&&UI.IsDark,"game selector preserves gold night",report,ref passed);
            if(form.activeMenu!=null)form.activeMenu.Close();Application.DoEvents();
            form.SetAppearanceMode("system",null);systemDark=false;form.SelectGame(false);form.FollowSystemAppearance();form.ActiveHero.PreviewTime=2.4;
            int[] rates={60,75,90,100,120,144,165,180,240,360,144,240,144,240};
            int[] styles={0,0,0,0,0,0,0,0,0,0,1,1,2,2};float[] dpi={1,1,1,1,1,1,1,1,1,1,2,3,2,3};int index=0;
            List<double>[] times=new List<double>[rates.Length];double[] workStart=new double[rates.Length],workEnd=new double[rates.Length],effectiveRates=new double[rates.Length];long[] countStart=new long[rates.Length],countEnd=new long[rates.Length];
            for(int i=0;i<rates.Length;i++)times[i]=new List<double>();
            Stopwatch watch=Stopwatch.StartNew();Bitmap surface=null;Graphics drawing=null;
            Action configure=delegate{
                systemDark=styles[index]==1;form.SelectGame(styles[index]==2);form.FollowSystemAppearance();
                form.displayDpi=(int)(96*dpi[index]);form.ClientSize=new Size((int)(1100*dpi[index]),(int)(760*dpi[index]));form.ApplyLayout();
                if(drawing!=null)drawing.Dispose();if(surface!=null)surface.Dispose();
                surface=new Bitmap(form.ActiveHero.Width,form.ActiveHero.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);drawing=Graphics.FromImage(surface);
                workStart[index]=form.ActiveHero.PaintMilliseconds;countStart[index]=form.ActiveHero.PaintCount;
            };configure();
            FramePump measuredClock=null;
            using(FramePump pump=new FramePump(form,delegate(double dt){
                form.ActiveHero.PreviewTime=watch.Elapsed.TotalSeconds+2;form.ActiveHero.SpinProgress=(watch.Elapsed.TotalSeconds%1.05)/1.05;
                double beforeWork=form.ActiveHero.PaintMilliseconds;form.ActiveHero.RenderFrame(drawing);if(measuredClock!=null)measuredClock.ObserveWork(form.ActiveHero.PaintMilliseconds-beforeWork);times[index].Add(watch.Elapsed.TotalMilliseconds);
            }))
            using(ApplicationContext context=new ApplicationContext())
            using(System.Windows.Forms.Timer advance=new System.Windows.Forms.Timer()){
                measuredClock=pump;pump.Rate=rates[0];pump.SetActive(true);advance.Interval=1200;advance.Tick+=delegate{
                    workEnd[index]=form.ActiveHero.PaintMilliseconds;countEnd[index]=form.ActiveHero.PaintCount;effectiveRates[index]=pump.EffectiveRate;
                    if(index==rates.Length-1){pump.SetActive(false);advance.Stop();context.ExitThread();}
                    else{index++;configure();pump.Rate=rates[index];}
                };advance.Start();Application.Run(context);
            }
            if(drawing!=null)drawing.Dispose();if(surface!=null)surface.Dispose();
            for(int i=0;i<rates.Length;i++){
                List<double> intervals=new List<double>();for(int j=1;j<times[i].Count;j++)intervals.Add(times[i][j]-times[i][j-1]);intervals.Sort();
                double fps=times[i].Count<2?0:(times[i].Count-1)*1000/(times[i][times[i].Count-1]-times[i][0]);
                report.Add(names[styles[i]]+" @ "+dpi[i]+"x target "+rates[i]+" Hz; adapted "+effectiveRates[i].ToString("0.0")+" Hz: "+fps.ToString("0.0")+" render callbacks/s; P95 "+(intervals.Count==0?0:intervals[(int)((intervals.Count-1)*.95)]).ToString("0.00")+" ms; paint "+((workEnd[i]-workStart[i])/Math.Max(1,countEnd[i]-countStart[i])).ToString("0.00")+" ms/frame");
                Expect(fps>=Math.Min(rates[i],effectiveRates[i])*.8&&fps<=rates[i]*1.2,"scheduler follows measured capacity "+names[styles[i]]+" requested "+rates[i]+" Hz / adapted "+effectiveRates[i].ToString("0.0")+" Hz",report,ref passed);
            }
        }
        report.Add(passed?"OVERALL PASS":"OVERALL FAIL");File.WriteAllLines(Path.Combine(target,"verification.txt"),report.ToArray(),new UTF8Encoding(true));
        Environment.ExitCode=passed?0:2;form.Close();
    }
    internal static void RunDesktop(string root,Action<MainForm,StartupScreen> configured,bool hidden){
        using(StartupScreen loading=new StartupScreen(root,hidden)){
            if(loading.Cancelled)return;
            loading.SetStage(1);FontStore.Load(root);
            if(loading.Cancelled)return;
            loading.SetStage(2);
            using(MainForm form=new MainForm()){
                if(loading.Cancelled)return;
                form.Opacity=0;form.SuppressReveal=hidden;form.startupPending=true;if(hidden)form.ShowInTaskbar=false;loading.Attach(form);
                form.StartupBounds=delegate{return loading.WindowBounds;};
                form.StartupState=delegate{return loading.WindowState;};
                form.StartupReady=delegate{loading.Finish(form);};
                if(configured!=null)configured(form,loading);
                Application.Run(form);
            }
        }
    }
    [STAThread]
    static void Main(string[] args){
        DisplayInfo.Initialize();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string render=null;bool verify=false,film=false;
        for(int i=0;i<args.Length;i++){
            if(args[i]=="--root"&&i+1<args.Length)RootOverride=args[++i];
            else if(args[i]=="--render"&&i+1<args.Length)render=args[++i];
            else if(args[i]=="--verify")verify=true;else if(args[i]=="--film")film=true;
        }
        RenderMode=render!=null;string root=RootOverride??Path.GetDirectoryName(Application.ExecutablePath);
        try{
            if(RenderMode){FontStore.Load(root);using(MainForm form=new MainForm())RenderSuite(form,render,verify,film);}
            else RunDesktop(root,null,false);
        }catch(Exception ex){
            string dir=render??Path.GetDirectoryName(Application.ExecutablePath);try{Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"panel_crash.txt"),ex.ToString());}catch{}
            Environment.ExitCode=1;if(!RenderMode)MessageBox.Show(ex.Message,"启动器");
        }
    }
}
