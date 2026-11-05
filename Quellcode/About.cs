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
 // Update-Prüfung des Hauptfensters (Stand, "Jetzt prüfen", automatisch prüfen); null = ohne Updates-Zeilen
 internal static UpdateHooks Updates;

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

  // Screenshots: keine Prüfung, Stand fest "Aktuell" (Globale Richtlinien)
  string today="Zuletzt geprüft "+DateTime.Now.ToString("dd.MM.yyyy");
  var updates=Demo.Active?new UpdateHooks{
   Status=()=>new UpdateStatus(UpdateKind.Current,"Aktuell",today),
   Auto=()=>true,
   SetAuto=on=>{},
   CheckNow=()=>System.Threading.Tasks.Task.FromResult(new UpdateStatus(UpdateKind.Current,"Aktuell",today)),
   CanInstall=()=>false
  }:Updates;
  int W=Ui.Px(updates!=null?540:480); // mit Updates-Zeilen breiter
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
  var card=new Card{Location=new Point(Ui.Px(24),y),Size=new Size(W-Ui.Px(48),Ui.Px(132)+(updates!=null?Ui.Px(60)+Ui.Px(32):0)),Padding=new Padding(Ui.Px(14),Ui.Px(10),Ui.Px(14),Ui.Px(10))};
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
  if(updates!=null)AddUpdateRows(table,updates);
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

 // Updates (Globale Richtlinien): Symbol und Stand, darunter die letzte Prüfung, rechts ein kleiner Knopf
 // ("Jetzt prüfen", bei neuer Version "Aktualisieren" bzw. "Release-Seite"), darunter "Automatisch prüfen"
 void AddUpdateRows(TableLayoutPanel t,UpdateHooks updates) {
  int row=t.RowCount;
  t.RowCount=row+2;
  t.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(60)));
  t.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(32)));
  t.Controls.Add(new Label{Text="Updates",ForeColor=Ui.Muted,BackColor=Ui.Surface,AutoSize=false,Size=new Size(Ui.Px(110),Ui.Px(22)),
   Anchor=AnchorStyles.Top|AnchorStyles.Left,Margin=new Padding(3,Ui.Px(9),3,0),TextAlign=ContentAlignment.MiddleLeft},0,row);

  var cell=new Panel{Dock=DockStyle.Fill,BackColor=Ui.Surface,Margin=new Padding(3,0,3,0)};
  var glyph=new StatusGlyph{BackColor=Ui.Surface,Size=new Size(Ui.Px(20),Ui.Px(22)),Location=new Point(0,Ui.Px(9))};
  var title=new Label{ForeColor=Ui.TextColor,BackColor=Ui.Surface,AutoSize=false,AutoEllipsis=true,UseMnemonic=false,TextAlign=ContentAlignment.MiddleLeft};
  var detail=new Label{ForeColor=Ui.Muted,BackColor=Ui.Surface,AutoSize=false,AutoEllipsis=true,UseMnemonic=false,TextAlign=ContentAlignment.TopLeft,Font=new Font("Segoe UI",8.75f)};
  var check=new ModernButton("Jetzt prüfen",ButtonKind.Secondary,Glyphs.Refresh);
  var install=new ModernButton("Aktualisieren",ButtonKind.Primary,Glyphs.Download);
  var page=new ModernButton("Release-Seite",ButtonKind.Secondary,Glyphs.Link);
  var tip=new ToolTip();
  tip.SetToolTip(install,"Programm beenden, auf die neue Version aktualisieren und neu starten");
  tip.SetToolTip(page,"Öffnet die Release-Seite auf GitHub");
  foreach(var b in new[]{check,install,page}) {
   b.Margin=Padding.Empty;
   b.Height=Ui.Px(30);
   cell.Controls.Add(b);
  }
  cell.Controls.Add(glyph);cell.Controls.Add(title);cell.Controls.Add(detail);

  Action layout=()=>{
   // Knopf rechtsbündig mit dem Schalter darunter und auf Höhe der ersten Zeile,
   // damit die zweite Zeile (letzte Prüfung) die volle Breite hat
   int right=cell.Width-Ui.Px(2);
   foreach(var b in new[]{check,install,page})b.Location=new Point(right-b.Width,Ui.Px(20)-b.Height/2);
   var shown=install.Visible?install:page.Visible?page:check;
   int tx=Ui.Px(28),tw=Math.Max(Ui.Px(40),shown.Left-Ui.Px(12)-tx);
   title.SetBounds(tx,Ui.Px(9),tw,Ui.Px(22));
   detail.SetBounds(tx,Ui.Px(33),Math.Max(Ui.Px(40),cell.Width-tx),Ui.Px(20));
  };
  cell.Resize+=(s,e)=>layout();

  Action<UpdateStatus> show=st=>{
   switch(st.Kind) {
    case UpdateKind.Current:glyph.Set(Glyphs.Completed,Ui.SuccessColor);break;
    case UpdateKind.Available:glyph.Set(Glyphs.Download,Ui.Accent);break;
    case UpdateKind.Failed:glyph.Set(Glyphs.Warning,Ui.ErrorColor);break;
    case UpdateKind.Checking:glyph.Set(Glyphs.Sync,Ui.Muted);break;
    default:glyph.Set(Glyphs.Info,Ui.Muted);break;
   }
   title.Text=st.Title;
   title.ForeColor=st.Kind==UpdateKind.Available?Ui.Accent:st.Kind==UpdateKind.Checking?Ui.Muted:Ui.TextColor;
   detail.Text=st.Detail;
   tip.SetToolTip(detail,st.Detail);
   bool available=st.Kind==UpdateKind.Available;
   bool canInstall=available&&updates.CanInstall!=null&&updates.CanInstall();
   install.Visible=canInstall;
   page.Visible=available&&!canInstall;
   check.Visible=!available;
   check.Enabled=st.Kind!=UpdateKind.Checking;
   layout();
  };

  check.Click+=async (s,e)=>{
   show(new UpdateStatus(UpdateKind.Checking,"Prüfe auf GitHub …",""));
   UpdateStatus st;
   try {st=await updates.CheckNow();}
   catch(Exception ex){st=new UpdateStatus(UpdateKind.Failed,"Prüfung nicht möglich",ex.Message);}
   if(!IsDisposed)show(st);
  };
  install.Click+=(s,e)=>{
   Close();
   if(updates.Install!=null)updates.Install();
  };
  page.Click+=(s,e)=>{if(updates.OpenPage!=null)updates.OpenPage();};
  show(updates.Status());
  t.Controls.Add(cell,1,row);

  // Automatisch prüfen: Text bündig mit den Werten darüber, Schalter rechts
  var autoCell=new Panel{Dock=DockStyle.Fill,BackColor=Ui.Surface,Margin=new Padding(3,0,3,0)};
  var auto=new ToggleSwitch{BackColor=Ui.Surface,Dock=DockStyle.Right};
  auto.Checked=updates.Auto();
  var autoText=new Label{Text="Automatisch prüfen",ForeColor=Ui.TextColor,BackColor=Ui.Surface,Dock=DockStyle.Fill,
   TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,Cursor=Cursors.Hand,UseMnemonic=false};
  autoText.Click+=(s,e)=>auto.Checked=!auto.Checked;
  auto.CheckedChanged+=(s,e)=>{if(updates.SetAuto!=null)updates.SetAuto(auto.Checked);};
  autoCell.Controls.Add(autoText);
  autoCell.Controls.Add(auto);
  t.Controls.Add(autoCell,1,row+1);
 }

 Label AddRow(TableLayoutPanel t,ToolTip tip,string name,string value) {
  int row=t.RowCount;
  t.RowCount=row+1;
  t.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(28)));
  t.Controls.Add(new Label{Text=name,ForeColor=Ui.Muted,BackColor=Ui.Surface,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,row);
  var v=new Label{Text=value,ForeColor=Ui.TextColor,BackColor=Ui.Surface,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,UseMnemonic=false};
  tip.SetToolTip(v,value);
  t.Controls.Add(v,1,row);
  return v;
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