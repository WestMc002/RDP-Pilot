// reitzner RDP Pilot – RDP-Sitzungen auf den richtigen Monitor lenken
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RDPilot {

static class Native {
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
 [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd,string app,string idList);
 static int ColorRef(Color c){return c.R|(c.G<<8)|(c.B<<16);}
 // Per-Monitor-DPI, damit die Monitorkoordinaten echte Pixel sind (wie sie mstsc erwartet)
 public static void EnableDpiAwareness() {
  try {if(SetProcessDpiAwarenessContext(new IntPtr(-4)))return;} catch {}
  try {SetProcessDPIAware();} catch {}
 }
 public static void DarkTitle(IntPtr handle) {
  try {
   int on=Ui.Dark?1:0;
   if(DwmSetWindowAttribute(handle,20,ref on,4)!=0)DwmSetWindowAttribute(handle,19,ref on,4);
   int caption=ColorRef(Ui.Bg);DwmSetWindowAttribute(handle,35,ref caption,4);
   int text=ColorRef(Ui.TextColor);DwmSetWindowAttribute(handle,36,ref text,4);
  } catch {}
 }
 public static void DarkScroll(Control control) {
  try {SetWindowTheme(control.Handle,Ui.Dark?"DarkMode_Explorer":"Explorer",null);} catch {}
 }
}

public static class Ui {
 public static float S=1f;
 public static int Px(float value){return (int)Math.Round(value*S);}
 public static bool Dark=true;
 public static Color Bg,Surface,Surface2,SurfaceHover,Border,TextColor,Muted,Accent,InfoText,Tint;
 public static Color SuccessColor,ErrorColor,WarningColor;
 // Akzentfarben: werden aus der Windows-Akzentfarbe abgeleitet (siehe ApplyAccent)
 public static Color Primary=Color.FromArgb(31,111,214);
 public static Color PrimaryHover=Color.FromArgb(45,124,226);
 public static Color PrimaryPressed=Color.FromArgb(24,94,184);
 // Schriftfarbe auf der Akzentfläche: Weiß im hellen, Schwarz im dunklen Modus (wie Windows 11)
 public static Color OnPrimary=Color.White;
 public static readonly Color Green=Color.FromArgb(34,197,94);

 // Windows-Einstellung "Standard-App-Modus": AppsUseLightTheme=0 bedeutet dunkel
 public static bool DetectDark() {
  try {
   using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
    object value=key!=null?key.GetValue("AppsUseLightTheme"):null;
    return value is int&&(int)value==0;
   }
  } catch {return false;}
 }
 // Farben nach dem Windows-11-Standarddesign (Fluent / WinUI 3), jeweils auf deckende Werte umgerechnet
 public static void ApplyPalette(bool dark) {
  Dark=dark;
  if(dark) {
   Bg=Color.FromArgb(32,32,32);            // Fensterhintergrund (Mica-Ersatzfarbe #202020)
   Surface=Color.FromArgb(43,43,43);       // Karten (CardBackgroundFillColorDefault)
   Surface2=Color.FromArgb(55,55,55);      // Steuerelemente (ControlFillColorDefault)
   SurfaceHover=Color.FromArgb(61,61,61);  // Steuerelemente bei Mouseover
   Border=Color.FromArgb(58,58,58);        // Rahmen (ControlStrokeColorDefault)
   TextColor=Color.FromArgb(255,255,255);  // TextFillColorPrimary
   Muted=Color.FromArgb(206,206,206);      // TextFillColorSecondary
   InfoText=Color.FromArgb(255,255,255);Tint=Color.White;
   SuccessColor=Color.FromArgb(108,203,95);ErrorColor=Color.FromArgb(255,153,164);WarningColor=Color.FromArgb(252,225,0);
   Accent=Color.FromArgb(153,235,255);
  } else {
   Bg=Color.FromArgb(243,243,243);         // Fensterhintergrund (Mica-Ersatzfarbe #F3F3F3)
   Surface=Color.FromArgb(251,251,251);    // Karten
   Surface2=Color.FromArgb(254,254,254);   // Steuerelemente
   SurfaceHover=Color.FromArgb(246,246,246);
   Border=Color.FromArgb(229,229,229);
   TextColor=Color.FromArgb(26,26,26);
   Muted=Color.FromArgb(96,96,96);
   InfoText=Color.FromArgb(26,26,26);Tint=Color.Black;
   SuccessColor=Color.FromArgb(15,123,15);ErrorColor=Color.FromArgb(196,43,28);WarningColor=Color.FromArgb(157,93,0);
   Accent=Color.FromArgb(0,62,146);
  }
  ApplyAccent(dark);
 }

 // Windows-Akzentfarbe: AccentPalette enthält 8 Abstufungen (je 4 Bytes RGBA),
 // 0-2 = heller, 3 = Grundfarbe, 4-6 = dunkler. Windows 11 nutzt im hellen Modus
 // dunklere, im dunklen Modus hellere Stufen.
 static Color[] ReadAccentPalette() {
  try {
   using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent")) {
    var data=key!=null?key.GetValue("AccentPalette") as byte[]:null;
    if(data!=null&&data.Length>=32) {
     var colors=new Color[8];
     for(int i=0;i<8;i++)colors[i]=Color.FromArgb(data[i*4],data[i*4+1],data[i*4+2]);
     return colors;
    }
   }
  } catch {}
  try {
   using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM")) {
    object value=key!=null?key.GetValue("AccentColor"):null;
    if(value is int) {
     int abgr=(int)value;
     Color baseColor=Color.FromArgb(abgr&0xFF,(abgr>>8)&0xFF,(abgr>>16)&0xFF);
     return new[]{Blend(baseColor,Color.White,0.6f),Blend(baseColor,Color.White,0.4f),Blend(baseColor,Color.White,0.2f),baseColor,
                  Blend(baseColor,Color.Black,0.2f),Blend(baseColor,Color.Black,0.4f),Blend(baseColor,Color.Black,0.6f),Blend(baseColor,Color.Black,0.7f)};
    }
   }
  } catch {}
  return null;
 }

 static double Luminance(Color c) {
  Func<double,double> lin=v=>{v/=255.0;return v<=0.03928?v/12.92:Math.Pow((v+0.055)/1.055,2.4);};
  return 0.2126*lin(c.R)+0.7152*lin(c.G)+0.0722*lin(c.B);
 }

 // Kontrast gegen Weiß (für weiße Schrift auf der Akzentfarbe)
 static Color EnsureWhiteContrast(Color c) {
  int guard=0;
  while((1.05/(Luminance(c)+0.05))<4.5&&guard++<20)c=Blend(c,Color.Black,0.08f);
  return c;
 }

 // Kontrast gegen einen Hintergrund (für Akzent-Text wie Links und Symbole)
 static Color EnsureContrast(Color c,Color background,bool lighten) {
  int guard=0;
  while(guard++<20) {
   double a=Luminance(c)+0.05,b=Luminance(background)+0.05;
   if(Math.Max(a,b)/Math.Min(a,b)>=4.5)break;
   c=Blend(c,lighten?Color.White:Color.Black,0.08f);
  }
  return c;
 }

 static void ApplyAccent(bool dark) {
  var palette=ReadAccentPalette();
  if(palette==null) {
   // Standard-Akzent von Windows 11 (Blau #0078D4)
   palette=new[]{Color.FromArgb(153,235,255),Color.FromArgb(96,205,255),Color.FromArgb(0,159,255),Color.FromArgb(0,120,212),
                 Color.FromArgb(0,95,184),Color.FromArgb(0,62,146),Color.FromArgb(0,26,104),Color.FromArgb(0,10,60)};
  }
  // Windows 11: hell = AccentDark1 mit weißer Schrift, dunkel = AccentLight2 mit schwarzer Schrift;
  // Mouseover und Gedrückt = Akzentfläche mit 90 % bzw. 80 % Deckkraft
  if(dark) {
   OnPrimary=Color.Black;
   Primary=EnsureContrast(palette[1],Color.Black,true);
   PrimaryHover=Blend(Primary,Bg,0.1f);
   PrimaryPressed=Blend(Primary,Bg,0.2f);
   Accent=EnsureContrast(palette[0],Surface,true);
  } else {
   OnPrimary=Color.White;
   Primary=EnsureWhiteContrast(palette[4]);
   PrimaryHover=Blend(Primary,Bg,0.1f);
   PrimaryPressed=Blend(Primary,Bg,0.2f);
   Accent=EnsureContrast(palette[5],Surface,false);
  }
 }

 public static Font UiFont,UiSemibold,TitleFont,SmallFont,NumberFont,IconFont,SmallIconFont;
 public static void Init() {
  ApplyPalette(DetectDark());
  using(var g=Graphics.FromHwnd(IntPtr.Zero)) S=g.DpiX/96f;
  if(S<1f)S=1f;
  // Windows 11 nutzt "Segoe UI Variable"; ältere Systeme fallen auf "Segoe UI" zurück
  UiFont=Pick(new[]{"Segoe UI Variable Text","Segoe UI"},10.5f)??new Font("Segoe UI",10.5f);
  SmallFont=Pick(new[]{"Segoe UI Variable Small","Segoe UI Variable Text","Segoe UI"},9f)??new Font("Segoe UI",9f);
  UiSemibold=Pick(new[]{"Segoe UI Variable Text Semibold","Segoe UI Semibold"},10.5f)??new Font("Segoe UI",10.5f,FontStyle.Bold);
  TitleFont=Pick(new[]{"Segoe UI Variable Display Semibold","Segoe UI Semibold"},17f)??new Font("Segoe UI",17f,FontStyle.Bold);
  NumberFont=Pick(new[]{"Segoe UI Variable Display Semibold","Segoe UI Semibold"},20f)??new Font("Segoe UI",20f,FontStyle.Bold);
  IconFont=Pick(new[]{"Segoe Fluent Icons","Segoe MDL2 Assets"},11f);
  SmallIconFont=Pick(new[]{"Segoe Fluent Icons","Segoe MDL2 Assets"},8f);
 }
 static Font Pick(string[] names,float size) {
  foreach(string name in names) {
   var font=new Font(name,size);
   if(String.Equals(font.Name,name,StringComparison.OrdinalIgnoreCase))return font;
   font.Dispose();
  }
  return null;
 }
 public static Color Blend(Color a,Color b,float t) {
  return Color.FromArgb((int)(a.R+(b.R-a.R)*t),(int)(a.G+(b.G-a.G)*t),(int)(a.B+(b.B-a.B)*t));
 }
 public static GraphicsPath Round(Rectangle r,int radius) {
  var path=new GraphicsPath();
  int d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
  if(d<=1){path.AddRectangle(r);return path;}
  path.AddArc(r.X,r.Y,d,d,180,90);
  path.AddArc(r.Right-d,r.Y,d,d,270,90);
  path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);
  path.AddArc(r.X,r.Bottom-d,d,d,90,90);
  path.CloseFigure();
  return path;
 }
 public static GraphicsPath Round(RectangleF r,float radius) {
  var path=new GraphicsPath();
  float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
  if(d<=1){path.AddRectangle(r);return path;}
  path.AddArc(r.X,r.Y,d,d,180,90);
  path.AddArc(r.Right-d,r.Y,d,d,270,90);
  path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);
  path.AddArc(r.X,r.Bottom-d,d,d,90,90);
  path.CloseFigure();
  return path;
 }
}

public enum ButtonKind {Primary,Secondary,Ghost}

public class ModernButton:Button {
 bool hover,down;
 readonly ButtonKind kind;
 readonly string glyph;
 public ModernButton(string text,ButtonKind kind,string glyph) {
  this.kind=kind;this.glyph=glyph;
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
  FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;
  Font=kind==ButtonKind.Ghost?Ui.UiFont:Ui.UiSemibold;
  Height=Ui.Px(38);Margin=new Padding(0,0,Ui.Px(8),0);
  Text=text;UpdateWidth();
 }
 bool HasGlyph{get{return Ui.IconFont!=null&&!String.IsNullOrEmpty(glyph);}}
 void UpdateWidth() {
  if(Font==null)return;
  if(String.IsNullOrEmpty(Text)){Width=Height;return;}
  int width=TextRenderer.MeasureText(Text,Font,Size.Empty,TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine).Width+Ui.Px(28);
  if(HasGlyph)width+=Ui.Px(26);
  Width=width;
 }
 protected override void OnTextChanged(EventArgs e){base.OnTextChanged(e);UpdateWidth();Invalidate();}
 protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);if(String.IsNullOrEmpty(Text)&&Width!=Height)Width=Height;}
 protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
 protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;down=false;Invalidate();}
 protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){down=true;Invalidate();}}
 protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);down=false;Invalidate();}
 protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);hover=false;down=false;Invalidate();}
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  Color parent=Parent!=null?Parent.BackColor:Ui.Bg;
  g.Clear(parent);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  Color fill,border,fore;
  if(kind==ButtonKind.Primary) {
   fill=!Enabled?Ui.Blend(Ui.Primary,parent,0.6f):down?Ui.PrimaryPressed:hover?Ui.PrimaryHover:Ui.Primary;
   border=fill;
   fore=Enabled?Ui.OnPrimary:Ui.Blend(Ui.OnPrimary,parent,0.5f);
  } else if(kind==ButtonKind.Secondary) {
   fill=!Enabled?Ui.Blend(Ui.Surface2,parent,0.5f):down?Ui.Blend(Ui.Surface2,Ui.Tint,0.12f):hover?Ui.Blend(Ui.Surface2,Ui.Tint,0.05f):Ui.Surface2;
   border=Enabled?Ui.Blend(Ui.Border,Ui.Tint,hover?0.08f:0f):Ui.Blend(Ui.Border,parent,0.5f);
   fore=Enabled?Ui.TextColor:Ui.Blend(Ui.Muted,parent,0.4f);
  } else {
   fill=Enabled&&(hover||down)?Ui.Blend(parent,Ui.Tint,down?0.10f:0.06f):parent;
   border=fill;
   fore=Enabled?Ui.Accent:Ui.Blend(Ui.Accent,parent,0.5f);
  }
  var r=new Rectangle(0,0,Width-1,Height-1);
  using(var path=Ui.Round(r,Ui.Px(4)))
  using(var brush=new SolidBrush(fill))
  using(var pen=new Pen(border)) {g.FillPath(brush,path);g.DrawPath(pen,path);}
  if(Focused&&ShowFocusCues) {
   using(var path=Ui.Round(Rectangle.Inflate(r,-Ui.Px(2),-Ui.Px(2)),Ui.Px(6)))
   using(var pen=new Pen(Ui.Blend(Ui.Accent,Ui.Tint,0.3f),Math.Max(1f,Ui.S))) g.DrawPath(pen,path);
  }
  var flags=TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter;
  if(String.IsNullOrEmpty(Text)) {
   if(HasGlyph)TextRenderer.DrawText(g,glyph,Ui.IconFont,new Rectangle(0,0,Width,Height),fore,flags|TextFormatFlags.HorizontalCenter);
   return;
  }
  int textWidth=TextRenderer.MeasureText(Text,Font,Size.Empty,flags).Width;
  int total=textWidth+(HasGlyph?Ui.Px(26):0);
  int x=(Width-total)/2;
  if(HasGlyph) {
   TextRenderer.DrawText(g,glyph,Ui.IconFont,new Rectangle(x,0,Ui.Px(18),Height),fore,flags|TextFormatFlags.HorizontalCenter);
   x+=Ui.Px(26);
  }
  TextRenderer.DrawText(g,Text,Font,new Rectangle(x,0,textWidth+Ui.Px(4),Height),fore,flags|TextFormatFlags.Left);
 }
}

public class Card:Panel {
 public int Radius;
 public Card() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
  BackColor=Ui.Bg;Radius=Ui.Px(8);
 }
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(Parent!=null?Parent.BackColor:Ui.Bg);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  using(var path=Ui.Round(new Rectangle(0,0,Width-1,Height-1),Radius))
  using(var brush=new SolidBrush(Ui.Surface))
  using(var pen=new Pen(Ui.Border)) {g.FillPath(brush,path);g.DrawPath(pen,path);}
 }
}

public class HeaderPanel:Panel {
 public Bitmap Logo;
 public string Title="",Subtitle="",Badge="";
 // Klick auf die Versions-Plakette öffnet das Info-Fenster
 public event EventHandler BadgeClick;
 Rectangle pillRect;
 bool pillHover;
 public HeaderPanel() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
  BackColor=Ui.Bg;
 }
 protected override void OnMouseMove(MouseEventArgs e) {
  base.OnMouseMove(e);
  bool h=pillRect.Contains(e.Location);
  if(h!=pillHover){pillHover=h;Cursor=h?Cursors.Hand:Cursors.Default;Invalidate();}
 }
 protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);if(pillHover){pillHover=false;Cursor=Cursors.Default;Invalidate();}}
 protected override void OnMouseClick(MouseEventArgs e) {
  base.OnMouseClick(e);
  if(e.Button==MouseButtons.Left&&pillRect.Contains(e.Location)&&BadgeClick!=null)BadgeClick(this,EventArgs.Empty);
 }
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(BackColor);
  int x=0,size=Ui.Px(48);
  if(Logo!=null) {
   g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
   var dest=new Rectangle(0,(Height-size)/2,size,size);
   g.DrawImage(Logo,dest);
   x=dest.Right+Ui.Px(14);
  }
  var flags=TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix;
  Size title=TextRenderer.MeasureText(g,Title,Ui.TitleFont,Size.Empty,flags);
  Size sub=TextRenderer.MeasureText(g,Subtitle,Ui.UiFont,Size.Empty,flags);
  int gap=Ui.Px(3);
  int y=(Height-title.Height-gap-sub.Height)/2;
  TextRenderer.DrawText(g,Title,Ui.TitleFont,new Point(x,y),Ui.TextColor,flags);
  TextRenderer.DrawText(g,Subtitle,Ui.UiFont,new Point(x+1,y+title.Height+gap),Ui.Muted,flags);
  if(!String.IsNullOrEmpty(Badge)) {
   g.SmoothingMode=SmoothingMode.AntiAlias;
   Size b=TextRenderer.MeasureText(g,Badge,Ui.SmallFont,Size.Empty,flags);
   int h=b.Height+Ui.Px(6);
   var pill=new Rectangle(x+title.Width+Ui.Px(12),y+(title.Height-h)/2+Ui.Px(2),b.Width+Ui.Px(18),h);
   pillRect=pill;
   using(var path=Ui.Round(pill,h/2))
   using(var brush=new SolidBrush(Ui.Blend(Ui.Bg,Ui.Accent,pillHover?0.26f:0.14f)))
   using(var pen=new Pen(Ui.Blend(Ui.Bg,Ui.Accent,0.38f))) {g.FillPath(brush,path);g.DrawPath(pen,path);}
   TextRenderer.DrawText(g,Badge,Ui.SmallFont,pill,Ui.Blend(Ui.Accent,Ui.Tint,0.25f),flags|TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
  }
 }
}

public enum InfoKind {Info,Success,Warning,Error}

public class InfoBar:Control {
 InfoKind kind=InfoKind.Info;
 public InfoBar() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
  BackColor=Ui.Bg;
 }
 public InfoKind Kind{get{return kind;}set{kind=value;Invalidate();}}
 public void SetMessage(string text,InfoKind kind){this.kind=kind;Text=text;Invalidate();}
 protected override void OnTextChanged(EventArgs e){base.OnTextChanged(e);Invalidate();}
 Color KindColor() {
  switch(kind) {
   case InfoKind.Success:return Ui.SuccessColor;
   case InfoKind.Warning:return Ui.WarningColor;
   case InfoKind.Error:return Ui.ErrorColor;
   default:return Ui.Accent;
  }
 }
 string KindGlyph() {
  switch(kind) {
   case InfoKind.Success:return "";
   case InfoKind.Warning:return "";
   case InfoKind.Error:return "";
   default:return "";
  }
 }
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(BackColor);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  Color color=KindColor();
  var r=new Rectangle(0,0,Width-1,Height-1);
  using(var path=Ui.Round(r,Ui.Px(4)))
  using(var brush=new SolidBrush(Ui.Blend(Ui.Bg,color,0.08f)))
  using(var pen=new Pen(Ui.Blend(Ui.Bg,color,0.28f))) {g.FillPath(brush,path);g.DrawPath(pen,path);}
  int x=Ui.Px(16);
  if(Ui.IconFont!=null) {
   TextRenderer.DrawText(g,KindGlyph(),Ui.IconFont,new Rectangle(x,0,Ui.Px(20),Height),color,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding);
   x+=Ui.Px(34);
  }
  var flags=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.Left;
  var area=new Rectangle(x,0,Math.Max(10,Width-x-Ui.Px(16)),Height);
  Size size=TextRenderer.MeasureText(g,Text,Ui.UiFont,new Size(area.Width,int.MaxValue),flags);
  area.Y=Math.Max(0,(Height-size.Height)/2);area.Height=size.Height+2;
  TextRenderer.DrawText(g,Text,Ui.UiFont,area,Ui.InfoText,flags);
 }
}

// Moderne Checkbox im Stil der übrigen Steuerelemente
public class ModernCheck:Control {
 bool isChecked,hover;
 public event EventHandler CheckedChanged;
 public ModernCheck(string text) {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
  Text=text;Font=Ui.UiFont;Cursor=Cursors.Hand;TabStop=true;
  Height=Ui.Px(28);
  Width=Ui.Px(30)+TextRenderer.MeasureText(text,Ui.UiFont).Width+Ui.Px(4);
 }
 public bool Checked {
  get{return isChecked;}
  set{if(isChecked==value)return;isChecked=value;Invalidate();if(CheckedChanged!=null)CheckedChanged(this,EventArgs.Empty);}
 }
 protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
 protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;Invalidate();}
 protected override void OnMouseClick(MouseEventArgs e){base.OnMouseClick(e);if(Enabled&&e.Button==MouseButtons.Left){Focus();Checked=!Checked;}}
 protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(Enabled&&e.KeyCode==Keys.Space){Checked=!Checked;e.Handled=true;}}
 protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);Invalidate();}
 protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
 protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  Color parent=Parent!=null?Parent.BackColor:Ui.Bg;
  g.Clear(parent);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  int box=Ui.Px(20);
  var r=new Rectangle(1,(Height-box)/2,box,box);
  bool on=isChecked&&Enabled;
  Color fill=on?(hover?Ui.PrimaryHover:Ui.Primary):(hover&&Enabled?Ui.SurfaceHover:parent);
  Color border=on?fill:Enabled?Ui.Blend(Ui.Border,Ui.Tint,hover?0.25f:0.15f):Ui.Border;
  if(isChecked&&!Enabled){fill=Ui.Blend(Ui.Primary,parent,0.6f);border=fill;}
  using(var path=Ui.Round(r,Ui.Px(4)))
  using(var brush=new SolidBrush(fill))
  using(var pen=new Pen(border,Math.Max(1f,Ui.S))) {g.FillPath(brush,path);g.DrawPath(pen,path);}
  if(isChecked) {
   if(Ui.IconFont!=null) {
    TextRenderer.DrawText(g,"",Ui.SmallIconFont,r,Ui.OnPrimary,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
   } else {
    using(var pen=new Pen(Ui.OnPrimary,Ui.Px(2))) g.DrawLines(pen,new[]{new Point(r.X+box/4,r.Y+box/2),new Point(r.X+box*2/5,r.Y+box*7/10),new Point(r.X+box*3/4,r.Y+box*3/10)});
   }
  }
  if(Focused&&ShowFocusCues) {
   using(var path=Ui.Round(Rectangle.Inflate(r,Ui.Px(2),Ui.Px(2)),Ui.Px(7)))
   using(var pen=new Pen(Ui.Accent)) g.DrawPath(pen,path);
  }
  var text=new Rectangle(box+Ui.Px(10),0,Width-box-Ui.Px(10),Height);
  TextRenderer.DrawText(g,Text,Font,text,Enabled?Ui.TextColor:Ui.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
 }
}

// Auswahlfeld mit modernem Aufklappmenü (ersetzt die klassische ComboBox)
public class ModernSelect:Control {
 public class Item {
  public string Title,Detail;
  public object Tag;
 }
 public readonly System.Collections.Generic.List<Item> Items=new System.Collections.Generic.List<Item>();
 int selected=-1;
 bool hover,open;
 public string Placeholder="";
 public event EventHandler SelectedIndexChanged;
 public ModernSelect() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
  Cursor=Cursors.Hand;TabStop=true;Height=Ui.Px(44);
 }
 public int SelectedIndex {
  get{return selected;}
  set {
   if(value<-1||value>=Items.Count)value=-1;
   if(selected==value)return;
   selected=value;Invalidate();
   if(SelectedIndexChanged!=null)SelectedIndexChanged(this,EventArgs.Empty);
  }
 }
 public Item SelectedItem{get{return selected>=0&&selected<Items.Count?Items[selected]:null;}}
 public void Clear(){Items.Clear();selected=-1;Invalidate();}
 protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
 protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;Invalidate();}
 protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
 protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
 protected override bool IsInputKey(Keys keyData) {
  if(keyData==Keys.Up||keyData==Keys.Down)return true;
  return base.IsInputKey(keyData);
 }
 protected override void OnKeyDown(KeyEventArgs e) {
  base.OnKeyDown(e);
  if(Items.Count==0)return;
  if(e.KeyCode==Keys.Down)SelectedIndex=Math.Min(Items.Count-1,selected+1);
  else if(e.KeyCode==Keys.Up)SelectedIndex=Math.Max(0,selected-1);
  else if(e.KeyCode==Keys.Space||e.KeyCode==Keys.F4||(e.Alt&&e.KeyCode==Keys.Down))OpenMenu();
  else return;
  e.Handled=true;
 }
 protected override void OnMouseClick(MouseEventArgs e){base.OnMouseClick(e);if(e.Button==MouseButtons.Left&&Enabled){Focus();OpenMenu();}}
 // Für die Demo-Screenshots: Liste aufklappen und das Menü zurückgeben
 internal ContextMenuStrip OpenMenuForDemo(){return OpenMenu();}
 // Für die Demo-Screenshots: Auswahlfeld im aufgeklappten Zustand zeichnen, ohne Menü
 internal void SetOpenForDemo(bool value){open=value;Invalidate();Update();}
 ContextMenuStrip OpenMenu() {
  if(Items.Count==0)return null;
  var menu=new ContextMenuStrip();
  menu.Renderer=new SelectRenderer();
  menu.ShowImageMargin=false;menu.ShowCheckMargin=false;
  menu.Padding=new Padding(Ui.Px(4));
  menu.BackColor=Ui.Surface;
  for(int i=0;i<Items.Count;i++) {
   var item=new SelectMenuItem(Items[i],i==selected,Width-Ui.Px(10));
   int index=i;
   item.Click+=(s,a)=>SelectedIndex=index;
   menu.Items.Add(item);
  }
  // Aufklappmenü genauso breit wie das Auswahlfeld
  menu.MinimumSize=new Size(Width,0);
  menu.Closed+=(s,a)=>{open=false;Invalidate();BeginInvoke(new Action(menu.Dispose));};
  open=true;Invalidate();
  menu.Show(this,new Point(0,Height+Ui.Px(4)));
  return menu;
 }
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  Color parent=Parent!=null?Parent.BackColor:Ui.Surface;
  g.Clear(parent);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  var r=new Rectangle(0,0,Width-1,Height-1);
  Color fill=hover||open?Ui.Blend(Ui.Surface2,Ui.Tint,0.04f):Ui.Surface2;
  Color border=open||Focused?Ui.Accent:Ui.Blend(Ui.Border,Ui.Tint,hover?0.1f:0f);
  using(var path=Ui.Round(r,Ui.Px(4)))
  using(var brush=new SolidBrush(fill))
  using(var pen=new Pen(border)) {g.FillPath(brush,path);g.DrawPath(pen,path);}
  int x=Ui.Px(14);
  if(Ui.IconFont!=null) {
   TextRenderer.DrawText(g,"",Ui.IconFont,new Rectangle(x,0,Ui.Px(20),Height),Ui.Accent,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
   x+=Ui.Px(32);
  }
  int chevron=Ui.Px(36);
  var flags=TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter;
  var item=SelectedItem;
  var area=new Rectangle(x,0,Width-x-chevron,Height);
  if(item==null) {
   TextRenderer.DrawText(g,Placeholder,Ui.UiFont,area,Ui.Muted,flags);
  } else {
   int maxTitle=TitleWidth(g,item.Title,item.Detail,area.Width);
   TextRenderer.DrawText(g,item.Title,Ui.UiSemibold,new Rectangle(area.X,0,maxTitle,Height),Ui.TextColor,flags);
   if(!String.IsNullOrEmpty(item.Detail)) {
    int dx=area.X+maxTitle+Ui.Px(8);
    TextRenderer.DrawText(g,item.Detail,Ui.UiFont,new Rectangle(dx,0,Math.Max(0,area.Right-dx),Height),Ui.Muted,flags);
   }
  }
  if(Ui.IconFont!=null)
   TextRenderer.DrawText(g,"",Ui.SmallIconFont,new Rectangle(Width-chevron,0,chevron-Ui.Px(8),Height),Ui.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
 }

 // Breite des Titels ohne Ellipsen-Flag messen, sonst wird kurzer Text abgeschnitten
 internal static int TitleWidth(Graphics g,string title,string detail,int available) {
  var measure=TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding;
  int width=TextRenderer.MeasureText(g,title??"",Ui.UiSemibold,new Size(int.MaxValue,int.MaxValue),measure).Width+Ui.Px(8);
  if(String.IsNullOrEmpty(detail))return available;
  return Math.Min(width,available*2/3);
 }

 class SelectMenuItem:ToolStripMenuItem {
  public readonly Item Data;
  public readonly bool IsSelected;
  readonly int itemWidth;
  public SelectMenuItem(Item data,bool isSelected,int width):base(data.Title) {
   Data=data;IsSelected=isSelected;itemWidth=width;
  }
  // Feste Größe, damit das Aufklappmenü so breit wie das Auswahlfeld ist
  public override Size GetPreferredSize(Size constrainingSize){return new Size(itemWidth,Ui.Px(40));}
 }

 class SelectRenderer:ToolStripRenderer {
  protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
   e.Graphics.Clear(Ui.Surface);
  }
  protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) {
   var r=new Rectangle(0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);
   using(var pen=new Pen(Ui.Border)) e.Graphics.DrawRectangle(pen,r);
  }
  protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
   var item=e.Item as SelectMenuItem;
   var g=e.Graphics;
   g.SmoothingMode=SmoothingMode.AntiAlias;
   var r=new Rectangle(Ui.Px(2),1,e.Item.Width-Ui.Px(4),e.Item.Height-2);
   Color fill=e.Item.Selected?Ui.SurfaceHover:Ui.Surface;
   if(item!=null&&item.IsSelected)fill=Ui.Blend(Ui.Surface,Ui.Accent,e.Item.Selected?0.2f:0.12f);
   using(var path=Ui.Round(r,Ui.Px(6)))
   using(var brush=new SolidBrush(fill)) g.FillPath(brush,path);
   if(item!=null&&item.IsSelected) {
    using(var brush=new SolidBrush(Ui.Accent)) g.FillRectangle(brush,r.X+Ui.Px(1),r.Y+Ui.Px(10),Ui.Px(3),r.Height-Ui.Px(20));
   }
  }
  protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
   var item=e.Item as SelectMenuItem;
   if(item==null){base.OnRenderItemText(e);return;}
   var flags=TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter;
   int x=Ui.Px(14);
   var area=new Rectangle(x,0,item.Width-x-Ui.Px(12),item.Height);
   int maxTitle=TitleWidth(e.Graphics,item.Data.Title,item.Data.Detail,area.Width);
   TextRenderer.DrawText(e.Graphics,item.Data.Title,Ui.UiSemibold,new Rectangle(area.X,0,maxTitle,item.Height),Ui.TextColor,flags);
   if(!String.IsNullOrEmpty(item.Data.Detail)) {
    int dx=area.X+maxTitle+Ui.Px(8);
    TextRenderer.DrawText(e.Graphics,item.Data.Detail,Ui.UiFont,new Rectangle(dx,0,Math.Max(0,area.Right-dx),item.Height),Ui.Muted,flags);
   }
  }
 }
}

}