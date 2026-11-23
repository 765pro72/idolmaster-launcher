using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Both games use one responsive grid; business controls remain owned by their page.
internal sealed class HomeLayout {
    internal readonly float Width,Height,Divider,Compression,LeftWidth;
    internal readonly SizeF Viewport;
    internal HomeLayout(float width,float height){
        Width=width;Height=height;Viewport=UI.IsWabi?new SizeF((width-96)*5/12,StagePass.PageViewport(width,height).Height):StagePass.PageViewport(width,height);
        Divider=Math.Max(height-150,164+Viewport.Height);
        Compression=Math.Min(1,(Divider-174)/412f);
        LeftWidth=UI.IsWabi?(width-96)*5/12:Math.Min(440,width-96-Viewport.Width-36);
    }
    internal float Y(float value){return 174+(value-174)*Compression;}
    internal float H(float value){return value*Compression;}
    internal RectangleF Ticket{get{return new RectangleF(Width-48-Viewport.Width,164,Viewport.Width,Viewport.Height);}}
    internal RectangleF PathBox{get{return new RectangleF(48,Y(346),LeftWidth,H(42));}}
    internal RectangleF Play{get{return new RectangleF(48,Y(406),LeftWidth,H(62));}}
    internal RectangleF Tools{get{return new RectangleF(48,Y(526),LeftWidth,H(40));}}
    internal RectangleF Log{get{return new RectangleF(60,Divider+75,Width-120,Math.Max(30,Height-Divider-91));}}
    internal RectangleF CopyLog{get{return new RectangleF(Width-122,Divider+43,32,28);}}
    internal RectangleF ClearLog{get{return new RectangleF(Width-84,Divider+43,32,28);}}
    internal void Paint(Graphics g){
        using(Pen line=new Pen(UI.Border,1))g.DrawLine(line,48,Divider,Width-48,Divider);
        if(!UI.IsWabi)using(Brush accent=new SolidBrush(UI.Accent))g.FillRectangle(accent,31,Y(206),5,H(121));
        using(GraphicsPath shape=UI.Round(PathBox,UI.Corner))using(Brush fill=new SolidBrush(UI.Card))g.FillPath(fill,shape);
        RectangleF logBox=Log;logBox.Inflate(12,7);
        using(GraphicsPath shape=UI.Round(logBox,UI.Corner))using(Brush fill=new SolidBrush(UI.Blend(UI.Bg,UI.Card,.62f)))g.FillPath(fill,shape);
    }
}

// Delay only menu intent/closing; animation stays on the existing frame clock.
internal sealed class HoverDisclosure:System.IDisposable {
    readonly Control source;readonly Action open;readonly Func<ToolStripDropDown> current;
    readonly Timer intent=new Timer{Interval=65},close=new Timer{Interval=160};bool disposed;
    internal HoverDisclosure(Control source,Action open,Func<ToolStripDropDown> current){
        this.source=source;this.open=open;this.current=current;
        source.MouseEnter+=Enter;source.MouseLeave+=Leave;source.MouseDown+=Press;
        intent.Tick+=delegate{intent.Stop();if(!disposed&&Inside(source,Cursor.Position)&&(current()==null||!current().Visible))open();};
        close.Tick+=delegate{close.Stop();ToolStripDropDown menu=current();if(menu!=null&&!Inside(source,Cursor.Position)&&!InsideTree(menu,Cursor.Position))menu.Close();};
    }
    void Enter(object sender,EventArgs e){close.Stop();intent.Stop();intent.Start();}
    void Leave(object sender,EventArgs e){intent.Stop();close.Start();}
    void Press(object sender,MouseEventArgs e){intent.Stop();}
    internal void Watch(ToolStripDropDown menu){menu.MouseEnter+=delegate{if(!disposed)close.Stop();};menu.MouseLeave+=delegate{if(!disposed)close.Start();};}
    static bool Inside(Control c,Point p){return !c.IsDisposed&&c.Visible&&c.RectangleToScreen(c.ClientRectangle).Contains(p);}
    static bool InsideTree(ToolStripDropDown menu,Point p){if(Inside(menu,p))return true;foreach(ToolStripItem item in menu.Items){ToolStripDropDownItem branch=item as ToolStripDropDownItem;if(branch!=null&&branch.HasDropDownItems&&InsideTree(branch.DropDown,p))return true;}return false;}
    public void Dispose(){if(disposed)return;disposed=true;intent.Dispose();close.Dispose();source.MouseEnter-=Enter;source.MouseLeave-=Leave;source.MouseDown-=Press;}
}

// Keep native selection, caret and keyboard scrolling, without visible scroll chrome.
internal sealed class ScrollableLogTextBox:TextBox {
    int wheelRemainder;
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wParam,IntPtr lParam);
    internal ScrollableLogTextBox(){Multiline=true;ReadOnly=true;BorderStyle=BorderStyle.None;ScrollBars=ScrollBars.None;}
    protected override void WndProc(ref Message message){
        if(message.Msg==0x020A){
            int delta=unchecked((short)(message.WParam.ToInt64()>>16));wheelRemainder+=delta;
            int steps=wheelRemainder/120;wheelRemainder-=steps*120;
            int lines=SystemInformation.MouseWheelScrollLines;
            if(lines<0)lines=Math.Max(1,ClientSize.Height/Math.Max(1,Font.Height)-1);
            if(steps!=0&&lines!=0)SendMessage(Handle,0x00B6,IntPtr.Zero,new IntPtr(-steps*lines));
            Point point=PointToClient(Cursor.Position);base.OnMouseWheel(new MouseEventArgs(MouseButtons.None,0,point.X,point.Y,delta));
            message.Result=IntPtr.Zero;return;
        }
        base.WndProc(ref message);
    }
}

// Small vector tools share RoundBtn's existing hover and press animation.
internal sealed class LogActionButton:RoundBtn {
    internal readonly bool ClearsLog;
    internal LogActionButton(bool clears){ClearsLog=clears;Text="";AccessibleName=clears?"清空日志":"复制日志";AccessibleDescription=clears?"清空当前游戏的操作记录":"复制当前游戏的全部操作记录";}
    protected override void PaintLabel(Graphics drawing,Rectangle bounds,Color color){
        GraphicsState saved=drawing.Save();drawing.TranslateTransform(bounds.X+bounds.Width/2f,bounds.Y+bounds.Height/2f);drawing.ScaleTransform(Density,Density);
        using(Pen ink=new Pen(color,1.45f)){
            ink.StartCap=ink.EndCap=LineCap.Round;ink.LineJoin=LineJoin.Round;
            if(ClearsLog){
                drawing.DrawLine(ink,-6,-4.5f,6,-4.5f);drawing.DrawLines(ink,new[]{new PointF(-2.5f,-4.5f),new PointF(-2.5f,-6.5f),new PointF(2.5f,-6.5f),new PointF(2.5f,-4.5f)});
                drawing.DrawLines(ink,new[]{new PointF(-4.6f,-2.5f),new PointF(-3.7f,6),new PointF(3.7f,6),new PointF(4.6f,-2.5f)});drawing.DrawLine(ink,-1.6f,-1,-1.6f,3.7f);drawing.DrawLine(ink,1.6f,-1,1.6f,3.7f);
            }else{
                using(GraphicsPath paper=UI.Round(new RectangleF(-2.5f,-3,8.5f,10),1.4f))drawing.DrawPath(ink,paper);
                drawing.DrawLines(ink,new[]{new PointF(2.5f,-5.5f),new PointF(2.5f,-7),new PointF(-6.5f,-7),new PointF(-6.5f,3),new PointF(-5,3)});
            }
        }
        drawing.Restore(saved);
    }
}
