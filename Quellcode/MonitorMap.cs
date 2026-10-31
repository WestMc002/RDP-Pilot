// reitzner RDP Pilot – RDP-Sitzungen auf den richtigen Monitor lenken
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace RDPilot {

// Zeigt die Monitore in ihrer echten Anordnung; Klick schaltet einen Monitor an oder aus
public class MonitorMap:Control {
 List<MonitorInfo> monitors=new List<MonitorInfo>();
 readonly HashSet<int> selected=new HashSet<int>();
 readonly Dictionary<int,RectangleF> rects=new Dictionary<int,RectangleF>();
 readonly ToolTip tip=new ToolTip{AutoPopDelay=15000,InitialDelay=400,ReshowDelay=150};
 int hover=-1,focusNr=1;
 public event EventHandler SelectionChanged;

 public MonitorMap() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
  TabStop=true;BackColor=Ui.Surface;
 }

 public List<MonitorInfo> Monitors {
  get{return monitors;}
  set{monitors=value??new List<MonitorInfo>();selected.Clear();Invalidate();}
 }

 public List<MonitorInfo> Selection {
  get{return monitors.Where(m=>selected.Contains(m.Nr)).OrderBy(m=>m.Nr).ToList();}
 }

 // Vom Benutzer gewählter Hauptmonitor (-1 = automatisch)
 int preferredMain=-1;

 public void SetSelection(IEnumerable<MonitorInfo> selection,MonitorInfo main=null) {
  selected.Clear();
  foreach(var m in selection)selected.Add(m.Nr);
  preferredMain=main!=null&&selected.Contains(main.Nr)?main.Nr:-1;
  Invalidate();
  Raise();
 }

 // Hauptmonitor der Sitzung, nur bei mehreren ausgewählten Monitoren
 public MonitorInfo MainMonitor {
  get {
   var selection=Selection;
   if(selection.Count<2)return null;
   return RDPilot.Monitors.SessionOrder(selection,monitors.FirstOrDefault(m=>m.Nr==preferredMain))[0];
  }
 }

 void Toggle(int nr) {
  if(!selected.Remove(nr))selected.Add(nr);
  else if(nr==preferredMain)preferredMain=-1;
  Invalidate();
  Raise();
 }

 // Rechtsklick: Monitor als Hauptmonitor festlegen (wird dabei mit ausgewählt)
 void SetMain(int nr) {
  selected.Add(nr);
  preferredMain=nr;
  Invalidate();
  Raise();
 }

 void Raise(){if(SelectionChanged!=null)SelectionChanged(this,EventArgs.Empty);}

 void LayoutRects() {
  rects.Clear();
  if(monitors.Count==0||Width<=0||Height<=0)return;
  Rectangle u=monitors[0].Bounds;
  foreach(var m in monitors)u=Rectangle.Union(u,m.Bounds);
  float pad=Ui.Px(16);
  float scale=Math.Min((Width-pad*2)/u.Width,(Height-pad*2)/u.Height);
  // Waagrecht mittig, senkrecht oben bündig (kein Leerraum über den Monitoren)
  float ox=(Width-u.Width*scale)/2,oy=Ui.Px(4);
  float gap=Ui.Px(5);
  foreach(var m in monitors) {
   var r=new RectangleF((m.Bounds.X-u.X)*scale+ox+gap/2,(m.Bounds.Y-u.Y)*scale+oy+gap/2,m.Bounds.Width*scale-gap,m.Bounds.Height*scale-gap);
   rects[m.Nr]=r;
  }
 }

 int HitTest(Point p) {
  foreach(var pair in rects)if(pair.Value.Contains(p.X,p.Y))return pair.Key;
  return -1;
 }

 protected override void OnResize(EventArgs e){base.OnResize(e);Invalidate();}
 protected override void OnMouseMove(MouseEventArgs e) {
  base.OnMouseMove(e);
  int nr=HitTest(e.Location);
  if(nr!=hover) {
   hover=nr;Invalidate();
   var m=monitors.FirstOrDefault(x=>x.Nr==nr);
   tip.SetToolTip(this,m!=null?
    "Monitor "+m.Nr+": "+m.Bounds.Width+" x "+m.Bounds.Height+(m.Primary?" (primär)":"")+
    "\nLinksklick: auswählen oder abwählen"+
    "\nRechtsklick: als Hauptmonitor festlegen (Taskleiste der Sitzung)"+
    "\n\nTechnik: "+m.Name+", Position "+m.Bounds.X+", "+m.Bounds.Y+", mstsc-ID "+m.Id:"");
  }
  Cursor=nr>0?Cursors.Hand:Cursors.Default;
 }
 protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=-1;Invalidate();}
 protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);Focus();}
 protected override void OnMouseClick(MouseEventArgs e) {
  base.OnMouseClick(e);
  if(!Enabled)return;
  int nr=HitTest(e.Location);
  if(nr<=0)return;
  focusNr=nr;
  if(e.Button==MouseButtons.Left)Toggle(nr);
  else if(e.Button==MouseButtons.Right)SetMain(nr);
 }
 protected override bool IsInputKey(Keys keyData) {
  if(keyData==Keys.Left||keyData==Keys.Right||keyData==Keys.Space)return true;
  return base.IsInputKey(keyData);
 }
 protected override void OnKeyDown(KeyEventArgs e) {
  base.OnKeyDown(e);
  if(monitors.Count==0)return;
  if(e.KeyCode==Keys.Left)focusNr=Math.Max(1,focusNr-1);
  else if(e.KeyCode==Keys.Right)focusNr=Math.Min(monitors.Count,focusNr+1);
  else if(e.KeyCode==Keys.Space)Toggle(focusNr);
  else if(e.KeyCode==Keys.H)SetMain(focusNr);
  else if(e.KeyCode>=Keys.D1&&e.KeyCode<=Keys.D9&&(int)(e.KeyCode-Keys.D0)<=monitors.Count){focusNr=(int)(e.KeyCode-Keys.D0);Toggle(focusNr);}
  else return;
  Invalidate();e.Handled=true;
 }
 protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
 protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}

 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(BackColor);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  LayoutRects();
  var selection=Selection;
  var main=MainMonitor;
  int mainNr=main!=null?main.Nr:-1;
  bool adjacent=RDPilot.Monitors.Adjacent(selection);

  foreach(var m in monitors) {
   RectangleF r;
   if(!rects.TryGetValue(m.Nr,out r))continue;
   bool on=selected.Contains(m.Nr),hot=hover==m.Nr;
   Color fill,border,fore,sub;
   if(on) {
    fill=hot?Ui.PrimaryHover:Ui.Primary;
    border=adjacent?fill:Ui.WarningColor;fore=Ui.OnPrimary;sub=Ui.Blend(Ui.OnPrimary,Ui.Primary,0.25f);
   } else {
    fill=hot?Ui.Blend(Ui.Surface2,Ui.Tint,0.05f):Ui.Surface2;
    border=Ui.Blend(Ui.Border,Ui.Tint,hot?0.12f:0f);
    fore=Ui.TextColor;sub=Ui.Muted;
   }
   float radius=Math.Min(Ui.Px(8),Math.Min(r.Width,r.Height)/4);
   using(var path=Ui.Round(r,radius))
   using(var brush=new SolidBrush(fill))
   using(var pen=new Pen(border,on&&!adjacent?Ui.Px(3):Math.Max(1f,Ui.S))) {g.FillPath(brush,path);g.DrawPath(pen,path);}

   if(Focused&&ShowFocusCues&&focusNr==m.Nr) {
    var fr=RectangleF.Inflate(r,Ui.Px(3),Ui.Px(3));
    using(var path=Ui.Round(fr,radius+Ui.Px(3)))
    using(var pen=new Pen(Ui.Accent,Math.Max(1f,Ui.S))){pen.DashStyle=DashStyle.Dot;g.DrawPath(pen,path);}
   }

   var rect=Rectangle.Round(r);
   var center=TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix;
   Size num=TextRenderer.MeasureText(g,m.Nr.ToString(),Ui.NumberFont,Size.Empty,center);
   string res=m.Bounds.Width+" x "+m.Bounds.Height;
   Size resSize=TextRenderer.MeasureText(g,res,Ui.SmallFont,Size.Empty,center);
   bool showRes=rect.Height>num.Height+resSize.Height+Ui.Px(16)&&rect.Width>resSize.Width+Ui.Px(8);
   int total=num.Height+(showRes?resSize.Height:0);
   int y=rect.Y+(rect.Height-total)/2;
   TextRenderer.DrawText(g,m.Nr.ToString(),Ui.NumberFont,new Rectangle(rect.X,y,rect.Width,num.Height),fore,center);
   if(showRes)TextRenderer.DrawText(g,res,Ui.SmallFont,new Rectangle(rect.X,y+num.Height,rect.Width,resSize.Height),sub,center);

   // Haken oben rechts bei ausgewählten Monitoren
   if(on&&rect.Width>Ui.Px(60)) {
    int c=Ui.Px(20);
    var cr=new Rectangle(rect.Right-c-Ui.Px(8),rect.Y+Ui.Px(8),c,c);
    using(var brush=new SolidBrush(Color.FromArgb(70,Ui.OnPrimary))) g.FillEllipse(brush,cr);
    if(Ui.SmallIconFont!=null)TextRenderer.DrawText(g,"",Ui.SmallIconFont,cr,Ui.OnPrimary,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
   }
   // Kennzeichnungen unten links
   var tags=new List<string>();
   if(m.Nr==mainNr)tags.Add("Hauptmonitor");
   if(m.Primary)tags.Add("Primär");
   int tx=rect.X+Ui.Px(8);
   foreach(string tag in tags) {
    Size ts=TextRenderer.MeasureText(g,tag,Ui.SmallFont,Size.Empty,center);
    int ph=ts.Height+Ui.Px(4),pw=ts.Width+Ui.Px(12);
    if(tx+pw>rect.Right-Ui.Px(6)||rect.Height<ph*3)break;
    var pill=new Rectangle(tx,rect.Bottom-ph-Ui.Px(8),pw,ph);
    bool isMain=tag=="Hauptmonitor";
    Color pf=on?(isMain?Ui.OnPrimary:Color.FromArgb(55,Ui.OnPrimary)):Ui.Blend(Ui.Surface2,Ui.Accent,0.14f);
    Color pt=on?(isMain?Ui.Primary:Ui.OnPrimary):Ui.Accent;
    using(var path=Ui.Round(pill,ph/2))
    using(var brush=new SolidBrush(pf)) g.FillPath(brush,path);
    TextRenderer.DrawText(g,tag,Ui.SmallFont,pill,pt,center|TextFormatFlags.VerticalCenter);
    tx=pill.Right+Ui.Px(6);
   }
  }
 }


 protected override void Dispose(bool disposing){if(disposing)tip.Dispose();base.Dispose(disposing);}
}

}