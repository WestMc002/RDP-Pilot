// reitzner RDP Pilot – RDP-Sitzungen auf den richtigen Monitor lenken
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RDPilot {

// Info-Fenster nach Vorlage Monitor Dimmer: großes reitzner-Logo, Name, Version, Eckdaten, Knöpfe.
// Öffnet sich beim Klick auf die Versionsnummer im Kopfbereich.
public class AboutDialog:Form {
 static AboutDialog open;
 readonly string folder;

 public static void ShowFor(Form owner,string folder) {
  if(open!=null){open.Activate();return;}
  using(var d=new AboutDialog(folder)) {
   open=d;
   try {d.ShowDialog(owner);}
   finally {open=null;}
  }
 }

 // reitzner-Projekt: statt des GS-Logos das reitzner-Logo (Globale Richtlinien),
 // passend zum hellen bzw. dunklen Design; Resources\Logo-*.png neben der EXE, sonst eingebettet
 public static Image ReitznerLogo(bool dark) {
  string name=dark?"Logo-dunkel.png":"Logo-hell.png";
  try {
   string file=Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Resources"),name);
   if(File.Exists(file)) {
    using(var fs=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
    using(var img=Image.FromStream(fs)) return new Bitmap(img);
   }
   using(var rs=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
    if(rs!=null)using(var img=Image.FromStream(rs)) return new Bitmap(img);
  } catch {}
  return null;
 }

 internal AboutDialog(string folder) {
  this.folder=folder;
  Text="Info über "+AppInfo.FullName;
  try {Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);} catch {}
  // Normaler Rahmen wie beim Hauptfenster (Icon mit gleichem Abstand), Größe bleibt fest
  FormBorderStyle=FormBorderStyle.Sizable;
  SizeGripStyle=SizeGripStyle.Hide;
  MaximizeBox=false;MinimizeBox=false;
  ShowInTaskbar=false;
  StartPosition=FormStartPosition.CenterParent;
  AutoScaleMode=AutoScaleMode.None;
  BackColor=Ui.Bg;ForeColor=Ui.TextColor;Font=Ui.UiFont;

  int W=Ui.Px(480);
  int y=Ui.Px(24);

  Image logo=ReitznerLogo(Ui.Dark);
  var picture=new PictureBox{Image=logo,SizeMode=PictureBoxSizeMode.Zoom,Location=new Point(Ui.Px(64),y),Size=new Size(W-Ui.Px(128),Ui.Px(130)),BackColor=Ui.Bg};
  if(picture.Image==null)picture.Image=LoadAppIcon();
  Controls.Add(picture);
  y+=Ui.Px(146);

  Controls.Add(new Label{Text=AppInfo.FullName,Font=Ui.TitleFont,ForeColor=Ui.TextColor,BackColor=Ui.Bg,TextAlign=ContentAlignment.MiddleCenter,Location=new Point(0,y),Size=new Size(W,Ui.Px(38)),UseMnemonic=false});
  y+=Ui.Px(38);
  Controls.Add(new Label{Text="Version "+AppInfo.Version,Font=Ui.UiFont,ForeColor=Ui.Accent,BackColor=Ui.Bg,TextAlign=ContentAlignment.MiddleCenter,Location=new Point(0,y),Size=new Size(W,Ui.Px(26))});
  y+=Ui.Px(40);

  // Karte mit Eckdaten
  var card=new Card{Location=new Point(Ui.Px(24),y),Size=new Size(W-Ui.Px(48),Ui.Px(140)),Padding=new Padding(Ui.Px(14),Ui.Px(10),Ui.Px(14),Ui.Px(10))};
  var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,BackColor=Ui.Surface};
  table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,Ui.Px(118)));
  table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
  var tip=new ToolTip();
  string built;
  try {built=File.GetLastWriteTime(Application.ExecutablePath).ToString("dd.MM.yyyy");} catch {built="-";}
  AddRow(table,tip,"Entwickler",AppInfo.Company);
  AddRow(table,tip,"Erstellt",built);
  AddRow(table,tip,"Farbschema",(Ui.Dark?"dunkel":"hell")+" (folgt Windows)");
  // Bei den Demo-Screenshots kein privater Pfad im Bild
  AddRow(table,tip,"Programm",Demo.Active?Demo.DemoProgramFolder:AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
  card.Controls.Add(table);
  Controls.Add(card);
  y+=card.Height+Ui.Px(24);

  // Knöpfe
  var manual=new ModernButton("Anleitung",ButtonKind.Secondary,"");
  manual.Click+=(s,e)=>OpenManual();
  var connections=new ModernButton("Verbindungen",ButtonKind.Secondary,"");
  connections.Click+=(s,e)=>Open(this.folder);
  var close=new ModernButton("Schließen",ButtonKind.Primary,null){DialogResult=DialogResult.Cancel};
  foreach(var b in new[]{manual,connections,close})b.Margin=new Padding(0);
  int gap=Ui.Px(14);
  int total=manual.Width+connections.Width+close.Width+2*gap;
  int bx=(W-total)/2;
  manual.Location=new Point(bx,y);
  connections.Location=new Point(manual.Right+gap,y);
  close.Location=new Point(connections.Right+gap,y);
  Controls.Add(manual);Controls.Add(connections);Controls.Add(close);
  CancelButton=close;AcceptButton=close;
  y+=close.Height+Ui.Px(22);

  Controls.Add(new Label{Text="© "+DateTime.Now.Year+" · "+AppInfo.FullName,ForeColor=Ui.Muted,BackColor=Ui.Bg,Font=Ui.SmallFont,TextAlign=ContentAlignment.MiddleCenter,Location=new Point(0,y),Size=new Size(W,Ui.Px(24)),UseMnemonic=false});
  y+=Ui.Px(24)+Ui.Px(20);

  ClientSize=new Size(W,y);
  Shown+=(s,e)=>close.Focus();
 }

 static Image LoadAppIcon() {
  try {
   using(var rs=Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon.png"))
    if(rs!=null)using(var img=Image.FromStream(rs))return new Bitmap(img);
  } catch {}
  return null;
 }

 void AddRow(TableLayoutPanel t,ToolTip tip,string name,string value) {
  int row=t.RowCount;
  t.RowCount=row+1;
  t.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(28)));
  t.Controls.Add(new Label{Text=name,ForeColor=Ui.Muted,BackColor=Ui.Surface,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,row);
  var v=new Label{Text=value,ForeColor=Ui.TextColor,BackColor=Ui.Surface,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,UseMnemonic=false};
  tip.SetToolTip(v,value);
  t.Controls.Add(v,1,row);
 }

 // Anleitung: zuerst die ausführliche Anleitung (Word), sonst Liesmich.txt
 void OpenManual() {
  string dir=AppDomain.CurrentDomain.BaseDirectory;
  foreach(string name in new[]{AppInfo.FullName+" – Anleitung.docx","Liesmich.txt"}) {
   string file=Path.Combine(dir,name);
   if(File.Exists(file)){Open(file);return;}
  }
  MessageBox.Show(this,"Keine Anleitung im Programmordner gefunden.",AppInfo.FullName,MessageBoxButtons.OK,MessageBoxIcon.Information);
 }

 static void Open(string path) {
  try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true});} catch {}
 }

 protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Native.DarkTitle(Handle);}

 // Fensterrahmen nach Globalen Richtlinien: normaler Rahmen, feste Größe
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

}