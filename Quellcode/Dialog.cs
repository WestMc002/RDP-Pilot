// RDP Pilot – Meldungen und Rückfragen im Programmdesign
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RDPilot {

// Ersatz für System.Windows.Forms.MessageBox mit denselben Show-Überladungen.
// Innerhalb des Namensraums RDPilot nutzen alle Aufrufe automatisch das eigene Fenster.
public static class MessageBox {
 public static DialogResult Show(string text){return Show(null,text,null,MessageBoxButtons.OK,MessageBoxIcon.None,MessageBoxDefaultButton.Button1);}
 public static DialogResult Show(string text,string caption){return Show(null,text,caption,MessageBoxButtons.OK,MessageBoxIcon.None,MessageBoxDefaultButton.Button1);}
 public static DialogResult Show(string text,string caption,MessageBoxButtons buttons){return Show(null,text,caption,buttons,MessageBoxIcon.None,MessageBoxDefaultButton.Button1);}
 public static DialogResult Show(string text,string caption,MessageBoxButtons buttons,MessageBoxIcon icon){return Show(null,text,caption,buttons,icon,MessageBoxDefaultButton.Button1);}
 public static DialogResult Show(string text,string caption,MessageBoxButtons buttons,MessageBoxIcon icon,MessageBoxDefaultButton defaultButton){return Show(null,text,caption,buttons,icon,defaultButton);}
 public static DialogResult Show(IWin32Window owner,string text){return Show(owner,text,null,MessageBoxButtons.OK,MessageBoxIcon.None,MessageBoxDefaultButton.Button1);}
 public static DialogResult Show(IWin32Window owner,string text,string caption){return Show(owner,text,caption,MessageBoxButtons.OK,MessageBoxIcon.None,MessageBoxDefaultButton.Button1);}
 public static DialogResult Show(IWin32Window owner,string text,string caption,MessageBoxButtons buttons){return Show(owner,text,caption,buttons,MessageBoxIcon.None,MessageBoxDefaultButton.Button1);}
 public static DialogResult Show(IWin32Window owner,string text,string caption,MessageBoxButtons buttons,MessageBoxIcon icon){return Show(owner,text,caption,buttons,icon,MessageBoxDefaultButton.Button1);}

 public static DialogResult Show(IWin32Window owner,string text,string caption,MessageBoxButtons buttons,MessageBoxIcon icon,MessageBoxDefaultButton defaultButton) {
  // Rückfall nur, solange das Design noch nicht geladen ist (sehr frühe Fehler beim Start)
  if(Ui.UiFont==null)return System.Windows.Forms.MessageBox.Show(owner,text,caption??AppInfo.FullName,buttons,icon,defaultButton);
  using(var dialog=new MsgDialog(text,buttons,icon,defaultButton,null)) return dialog.ShowFor(owner);
 }

 // Wie Show, dazu unter dem Text ein Link (z. B. „Alle Änderungen“), der eine Gesamtliste als graues,
 // schreibgeschütztes Feld mit Bildlauf aufklappt (aufgeklappt heißt der Link „Weniger“). Die Liste wird erst beim
 // ersten Aufklappen erzeugt (details).
 public static DialogResult ShowDetails(IWin32Window owner,string text,Func<string> details,string detailsLink,MessageBoxButtons buttons,MessageBoxIcon icon,MessageBoxDefaultButton defaultButton) {
  if(Ui.UiFont==null)return System.Windows.Forms.MessageBox.Show(owner,text,AppInfo.FullName,buttons,icon,defaultButton);
  using(var dialog=new MsgDialog(text,buttons,icon,defaultButton,null,details,detailsLink)) return dialog.ShowFor(owner);
 }
}

// Das eigentliche Fenster: Symbol links, Text, optional Kästchen (z. B. „Nicht mehr fragen“), Schaltflächen rechts unten
public class MsgDialog:Form {
 readonly ModernCheck check;
 readonly DialogResult escapeResult;
 bool escape=true;

 public bool Checked{get{return check!=null&&check.Checked;}}

 readonly List<ModernButton> buttonList=new List<ModernButton>();
 TextBox detailsBox;
 Panel detailsPanel;
 Label detailsLabel;
 string detailsLinkText;
 Func<string> detailsSource;
 int detailsShift,detailsX,detailsY,detailsWidth;

 public MsgDialog(string text,MessageBoxButtons buttons,MessageBoxIcon icon,MessageBoxDefaultButton defaultButton,string checkText)
  :this(text,buttons,icon,defaultButton,checkText,null,null){}

 public MsgDialog(string text,MessageBoxButtons buttons,MessageBoxIcon icon,MessageBoxDefaultButton defaultButton,string checkText,Func<string> details,string detailsLink) {
  Text=AppInfo.FullName; // Fenstertitel = Programmname
  try {Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);} catch {}
  // Fensterrahmen wie bei allen Dialogen: normaler Rahmen, feste Größe
  FormBorderStyle=FormBorderStyle.Sizable;
  SizeGripStyle=SizeGripStyle.Hide;
  MaximizeBox=false;MinimizeBox=false;
  ShowInTaskbar=false;
  AutoScaleMode=AutoScaleMode.None;
  KeyPreview=true;
  BackColor=Ui.Bg;ForeColor=Ui.TextColor;Font=Ui.UiFont;

  int pad=Ui.Px(24),iconSize=Ui.Px(32),gap=Ui.Px(16);
  bool hasIcon=icon!=MessageBoxIcon.None;
  int textX=pad+(hasIcon?iconSize+gap:0);
  text=(text??"").Replace("\r\n","\n").Replace("\n","\r\n");

  // Breite nach Textlänge: mindestens 420, höchstens 600 × Skalierung
  var flags=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl;
  int natural=0;
  foreach(string line in text.Split(new[]{"\r\n"},StringSplitOptions.None))
   natural=Math.Max(natural,TextRenderer.MeasureText(line.Length==0?" ":line,Font,Size.Empty,TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine).Width);
  int width=Math.Max(Ui.Px(420),Math.Min(Ui.Px(600),textX+natural+pad+Ui.Px(2)));

  // Schaltflächen nach Art, Reihenfolge wie bei Windows
  var spec=new List<KeyValuePair<string,DialogResult>>();
  switch(buttons) {
   case MessageBoxButtons.OKCancel: spec.Add(Pair("OK",DialogResult.OK));spec.Add(Pair("Abbrechen",DialogResult.Cancel));break;
   case MessageBoxButtons.YesNo: spec.Add(Pair("Ja",DialogResult.Yes));spec.Add(Pair("Nein",DialogResult.No));break;
   case MessageBoxButtons.YesNoCancel: spec.Add(Pair("Ja",DialogResult.Yes));spec.Add(Pair("Nein",DialogResult.No));spec.Add(Pair("Abbrechen",DialogResult.Cancel));break;
   case MessageBoxButtons.RetryCancel: spec.Add(Pair("Wiederholen",DialogResult.Retry));spec.Add(Pair("Abbrechen",DialogResult.Cancel));break;
   case MessageBoxButtons.AbortRetryIgnore: spec.Add(Pair("Abbrechen",DialogResult.Abort));spec.Add(Pair("Wiederholen",DialogResult.Retry));spec.Add(Pair("Ignorieren",DialogResult.Ignore));break;
   default: spec.Add(Pair("OK",DialogResult.OK));break;
  }
  // Esc bzw. X: bei Ja/Nein wie „Nein“, bei OK wie „OK“, sonst wie „Abbrechen“
  escapeResult=buttons==MessageBoxButtons.YesNo?DialogResult.No:buttons==MessageBoxButtons.OK?DialogResult.OK:
   buttons==MessageBoxButtons.AbortRetryIgnore?DialogResult.Abort:DialogResult.Cancel;
  int defaultIndex=defaultButton==MessageBoxDefaultButton.Button3?2:defaultButton==MessageBoxDefaultButton.Button2?1:0;
  if(defaultIndex>=spec.Count)defaultIndex=0;

  var created=new List<ModernButton>();
  int buttonsWidth=0;
  for(int i=0;i<spec.Count;i++) {
   var b=new ModernButton(spec[i].Key,i==defaultIndex?ButtonKind.Primary:ButtonKind.Secondary,null);
   b.Width=Math.Max(b.Width,Ui.Px(110));
   var result=spec[i].Value;
   b.Click+=(s,e)=>{escape=false;DialogResult=result;};
   created.Add(b);
   buttonsWidth+=b.Width+(i>0?Ui.Px(8):0);
  }
  width=Math.Max(width,pad+buttonsWidth+pad);

  int textWidth=width-textX-pad;
  int textHeight=TextRenderer.MeasureText(text,Font,new Size(textWidth,0),flags).Height;
  int contentHeight=Math.Max(textHeight,hasIcon?iconSize:0);
  var label=new Label{Text=text,AutoSize=false,UseMnemonic=false,BackColor=Ui.Bg,ForeColor=Ui.TextColor,Font=Font,
   Location=new Point(textX,pad+(hasIcon&&textHeight<iconSize?(iconSize-textHeight)/2:0)),Size=new Size(textWidth,textHeight+Ui.Px(2))};
  Controls.Add(label);
  if(hasIcon)Controls.Add(new MsgIcon(icon){Location=new Point(pad,pad),Size=new Size(iconSize,iconSize)});

  int y=pad+contentHeight;
  if(!String.IsNullOrEmpty(checkText)) {
   check=new ModernCheck(checkText){Location=new Point(textX,y+Ui.Px(12))};
   Controls.Add(check);
   y=check.Bottom;
  }
  if(details!=null) {
   // Link wie „Details“ im Info-Fenster: Akzentfarbe, Handzeiger; klappt die Gesamtliste auf
   detailsSource=details;
   detailsLinkText=String.IsNullOrEmpty(detailsLink)?"Details":detailsLink;
   detailsLabel=new Label{Text=detailsLinkText,AutoSize=true,UseMnemonic=false,ForeColor=Ui.Accent,BackColor=Ui.Bg,Font=Font,
    Cursor=Cursors.Hand,Location=new Point(textX,y+Ui.Px(10))};
   detailsLabel.Click+=(s,e)=>ToggleDetails();
   Controls.Add(detailsLabel);
   y=detailsLabel.Bottom;
   detailsX=textX;detailsY=y+Ui.Px(8);detailsWidth=width-textX-pad;
  }
  y+=Ui.Px(24);
  int x=width-pad-buttonsWidth;
  foreach(var b in created) {
   b.Location=new Point(x,y);
   Controls.Add(b);
   buttonList.Add(b);
   x+=b.Width+Ui.Px(8);
  }
  ClientSize=new Size(width,y+created[0].Height+pad);

  AcceptButton=created[defaultIndex];
  ModernButton cancel=null;
  for(int i=0;i<spec.Count;i++)if(spec[i].Value==escapeResult)cancel=created[i];
  if(cancel!=null)CancelButton=cancel;
  created[defaultIndex].TabIndex=0;
  ActiveControl=created[defaultIndex];
 }

 // Gesamtliste auf- bzw. zuklappen: Knöpfe und Fenster rücken um die Feldhöhe; Größensperre vorher lösen
 void ToggleDetails() {
  if(detailsSource==null)return;
  if(detailsPanel==null)BuildDetails();
  var panel=detailsPanel;
  bool open=!panel.Visible;
  MinimumSize=Size.Empty;MaximumSize=Size.Empty;
  int shift=open?detailsShift:-detailsShift;
  foreach(var b in buttonList)b.Top+=shift;
  ClientSize=new Size(ClientSize.Width,ClientSize.Height+shift);
  panel.Visible=open;
  detailsLabel.Text=open?"Weniger":detailsLinkText;
  if(open){detailsBox.SelectionStart=0;detailsBox.SelectionLength=0;}
  MinimumSize=Size;MaximumSize=Size;
 }

 // Gesamtliste beim ersten Aufklappen erzeugen: graues Feld (höchstens 240 hoch, sonst Bildlauf)
 void BuildDetails() {
  string body;
  try {body=detailsSource()??"";} catch(Exception ex){body=ex.Message;}
  body=body.Replace("\r\n","\n").Replace("\n","\r\n");
  var flags=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl;
  int inner=TextRenderer.MeasureText(body,Font,new Size(detailsWidth-Ui.Px(16)-SystemInformation.VerticalScrollBarWidth,0),flags).Height;
  int boxHeight=Math.Min(Ui.Px(240),inner+Ui.Px(12));
  detailsPanel=new Panel{BackColor=Ui.Surface,Location=new Point(detailsX,detailsY),Size=new Size(detailsWidth,boxHeight),Visible=false};
  detailsBox=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=inner+Ui.Px(12)>boxHeight?ScrollBars.Vertical:ScrollBars.None,
   BorderStyle=BorderStyle.None,WordWrap=true,Text=body,BackColor=Ui.Surface,ForeColor=Ui.TextColor,Font=Font,TabStop=false,
   Location=new Point(Ui.Px(8),Ui.Px(6)),Size=new Size(detailsWidth-Ui.Px(8),boxHeight-Ui.Px(12))};
  detailsPanel.Controls.Add(detailsBox);
  Controls.Add(detailsPanel);
  detailsShift=boxHeight+Ui.Px(8);
 }

 static KeyValuePair<string,DialogResult> Pair(string text,DialogResult result){return new KeyValuePair<string,DialogResult>(text,result);}

 // Ohne Owner: aktives Programmfenster im selben Thread, sonst bildschirmmittig
 public DialogResult ShowFor(IWin32Window owner) {
  if(owner==null) {
   var active=Form.ActiveForm;
   if(active!=null&&!active.InvokeRequired&&active.Visible)owner=active;
  }
  if(owner==null){StartPosition=FormStartPosition.CenterScreen;ShowInTaskbar=true;return ShowDialog();}
  StartPosition=FormStartPosition.CenterParent;
  return ShowDialog(owner);
 }

 protected override void OnFormClosing(FormClosingEventArgs e) {
  if(escape)DialogResult=escapeResult; // X schließt wie Esc
  base.OnFormClosing(e);
 }

 protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Native.DarkTitle(Handle);}

 protected override void OnLoad(EventArgs e) {
  base.OnLoad(e);
  MinimumSize=Size;
  MaximumSize=Size;
 }

 // Keine Größenänderungs-Pfeile am Rand: Randbereiche (HTLEFT..HTBOTTOMRIGHT) als HTCLIENT melden
 const int WM_NCHITTEST=0x0084,HTCLIENT=1,HTLEFT=10,HTBOTTOMRIGHT=17;
 protected override void WndProc(ref Message m) {
  base.WndProc(ref m);
  if(m.Msg==WM_NCHITTEST) {
   int hit=m.Result.ToInt32();
   if(hit>=HTLEFT&&hit<=HTBOTTOMRIGHT)m.Result=(IntPtr)HTCLIENT;
  }
 }
}

// Tooltip im Programmdesign: Kartenfarbe, 1 px Rand, Segoe UI 9 pt, Innenabstand 8 × 5, Umbruch bei 400, 15 s sichtbar.
// Text mit NoPadding und einem Graphics messen und zeichnen, sonst rechnet Windows einen Zuschlag ein
// und der Rand rechts wird größer als links.
public class ModernToolTip:ToolTip {
 const TextFormatFlags Flags=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl|TextFormatFlags.NoPadding;
 Font font;
 public ModernToolTip() {
  OwnerDraw=true;
  AutoPopDelay=15000;
  Popup+=OnTipPopup;
  Draw+=OnTipDraw;
 }
 Font TipFont{get{if(font==null)font=new Font("Segoe UI",9f);return font;}}
 void OnTipPopup(object sender,PopupEventArgs e) {
  string text=e.AssociatedControl!=null?GetToolTip(e.AssociatedControl):null;
  if(String.IsNullOrEmpty(text)){e.Cancel=true;return;}
  Size size;
  using(var g=Graphics.FromHwnd(IntPtr.Zero)) size=TextRenderer.MeasureText(g,text,TipFont,new Size(Ui.Px(400),0),Flags);
  // 1 px Rand je Seite, Innenabstand 8 × 5, 1 px Reserve in der Breite
  e.ToolTipSize=new Size(size.Width+1+Ui.Px(16)+2,size.Height+Ui.Px(10)+2);
 }
 void OnTipDraw(object sender,DrawToolTipEventArgs e) {
  var g=e.Graphics;
  var r=e.Bounds;
  using(var back=new SolidBrush(Ui.Surface)) g.FillRectangle(back,r);
  using(var pen=new Pen(Ui.Border)) g.DrawRectangle(pen,r.X,r.Y,r.Width-1,r.Height-1);
  var text=new Rectangle(r.X+1+Ui.Px(8),r.Y+1+Ui.Px(5),r.Width-2-Ui.Px(16),r.Height-2-Ui.Px(10));
  TextRenderer.DrawText(g,e.ToolTipText,TipFont,text,Ui.TextColor,Flags);
 }
 protected override void Dispose(bool disposing) {
  if(disposing&&font!=null){font.Dispose();font=null;}
  base.Dispose(disposing);
 }
}

// Symbol als gefüllter Kreis mit weißem Zeichen: Frage/Info in Akzentfarbe, Warnung orange, Fehler rot
class MsgIcon:Control {
 readonly MessageBoxIcon icon;
 public MsgIcon(MessageBoxIcon icon) {
  this.icon=icon;
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
 }
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(Parent!=null?Parent.BackColor:Ui.Bg);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
  Color fill;string sign;
  switch(icon) {
   case MessageBoxIcon.Warning: fill=Ui.Dark?Color.FromArgb(247,99,12):Color.FromArgb(202,80,16);sign="!";break;      // MessageBoxIcon.Exclamation
   case MessageBoxIcon.Error: fill=Ui.Dark?Color.FromArgb(214,47,47):Color.FromArgb(196,43,28);sign="×";break;        // auch Hand/Stop
   case MessageBoxIcon.Question: fill=AccentFill();sign="?";break;
   default: fill=AccentFill();sign="i";break;                                                                         // Information/Asterisk
  }
  var r=new RectangleF(0.5f,0.5f,Width-1.5f,Height-1.5f);
  using(var brush=new SolidBrush(fill)) g.FillEllipse(brush,r);
  using(var font=new Font("Segoe UI Semibold",Height*0.42f,FontStyle.Bold,GraphicsUnit.Pixel))
  using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
  using(var white=new SolidBrush(Color.White))
   g.DrawString(sign,font,white,new RectangleF(0,-Height*0.03f,Width,Height),format);
 }
 // Im Dunkelmodus ist die Akzentfläche hell – für das weiße Zeichen eine dunklere Stufe verwenden
 static Color AccentFill(){return Ui.Dark?Ui.Blend(Ui.Primary,Color.Black,0.45f):Ui.Primary;}
}

}
