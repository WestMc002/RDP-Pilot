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
 Card card;
 // Raster der Angaben-Karte: Zeilenabstand 26, Spalte der Beschriftungen 118 (× Skalierung)
 static int RowHeight{get{return Ui.Px(26);}}
 static int LabelWidth{get{return Ui.Px(118);}}
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
   AutoInstall=()=>false,
   SetAutoInstall=on=>{},
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

  // Karte mit Eckdaten: Zeilen im Raster von 26 × Skalierung, darunter die Updates-Zeilen
  int innerW=W-Ui.Px(48)-Ui.Px(28);
  var table=new TableLayoutPanel{ColumnCount=2,BackColor=Ui.Surface,Location=new Point(Ui.Px(14),Ui.Px(10)),Size=new Size(innerW,4*RowHeight)};
  table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,LabelWidth));
  table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
  var tip=new ModernToolTip();
  string built;
  try {built=File.GetLastWriteTime(Application.ExecutablePath).ToString("dd.MM.yyyy");} catch {built="-";}
  AddRow(table,tip,"Entwickler",AppInfo.Company);
  AddRow(table,tip,"Erstellt",built);
  AddRow(table,tip,"Farbschema",(Ui.Dark?"dunkel":"hell")+" (folgt Windows)");
  // Bei den Demo-Screenshots kein privater Pfad im Bild
  AddRow(table,tip,"Programm",Demo.Active?Demo.DemoProgramFolder:AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
  card=new Card{Location=new Point(Ui.Px(24),y)};
  card.Controls.Add(table);
  int cardH=Ui.Px(10)+table.Height+Ui.Px(10);
  if(updates!=null) {
   var panel=AddUpdateRows(updates,innerW);
   panel.Location=new Point(Ui.Px(14),table.Bottom);
   card.Controls.Add(panel);
   cardH+=panel.Height;
  }
  card.Size=new Size(W-Ui.Px(48),cardH);
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

 // Updates (Globale Richtlinien › Updates › Anzeige, Referenz Dock Pilot):
 // Raster wie die Zeilen darüber; Symbol und Überschrift bei uy + 2, zweite Zeile bei uy + 23,
 // Knopf rechts neben beiden Zeilen (Oberkante uy + 8), "Automatisch prüfen" bei uy + 49,
 // "Automatisch aktualisieren" direkt darunter (uy + 49 + 32).
 // Bei Fehlern kurze Meldung mit "Details", die ein graues Feld mit dem Originaltext aufklappen.
 Panel AddUpdateRows(UpdateHooks updates,int width) {
  int uy=0,x0=LabelWidth+3; // Beginn der Werte wie in der Tabelle darüber (Spalte + Zellenrand)
  bool hasAutoInstall=updates.AutoInstall!=null&&updates.SetAutoInstall!=null;
  var panel=new Panel{BackColor=Ui.Surface,Size=new Size(width,Ui.Px(49)+Ui.Px(32)+(hasAutoInstall?Ui.Px(32):0))};
  var titleFont=new Font("Segoe UI",9.5f);
  var smallFont=new Font("Segoe UI",8.75f);
  panel.Disposed+=(s,e)=>{titleFont.Dispose();smallFont.Dispose();};

  panel.Controls.Add(new Label{Text="Updates",ForeColor=Ui.Muted,BackColor=Ui.Surface,AutoSize=false,
   Location=new Point(3,uy+Ui.Px(2)),Size=new Size(LabelWidth-3,Ui.Px(22)),TextAlign=ContentAlignment.MiddleLeft,UseMnemonic=false});
  var glyph=new StatusGlyph{BackColor=Ui.Surface,Location=new Point(x0,uy+Ui.Px(2)),Size=new Size(Ui.Px(20),Ui.Px(22))};
  int tx=x0+Ui.Px(23);
  var title=new Label{ForeColor=Ui.TextColor,BackColor=Ui.Surface,AutoSize=false,AutoEllipsis=true,UseMnemonic=false,
   TextAlign=ContentAlignment.MiddleLeft,Font=titleFont,Location=new Point(tx,uy+Ui.Px(2))};
  var detail=new Label{ForeColor=Ui.Muted,BackColor=Ui.Surface,AutoSize=false,AutoEllipsis=true,UseMnemonic=false,
   TextAlign=ContentAlignment.TopLeft,Font=smallFont,Location=new Point(tx,uy+Ui.Px(23)),Padding=Padding.Empty};
  var more=new Label{Text="Details",ForeColor=Ui.Accent,BackColor=Ui.Surface,AutoSize=true,UseMnemonic=false,Font=smallFont,
   Cursor=Cursors.Hand,Visible=false,Padding=Padding.Empty,Margin=Padding.Empty};
  // Grau hinterlegtes Feld mit dem technischen Originaltext (markier- und kopierbar)
  var box=new Panel{BackColor=Ui.Bg,Visible=false,Padding=new Padding(Ui.Px(8),Ui.Px(6),Ui.Px(8),Ui.Px(6))};
  var boxText=new TextBox{Multiline=true,ReadOnly=true,WordWrap=true,BorderStyle=BorderStyle.None,BackColor=Ui.Bg,ForeColor=Ui.TextColor,
   Font=smallFont,Dock=DockStyle.Fill,ScrollBars=ScrollBars.None,TabStop=false};
  box.Controls.Add(boxText);
  var check=new ModernButton("Jetzt prüfen",ButtonKind.Secondary,Glyphs.Refresh);
  var install=new ModernButton("Aktualisieren",ButtonKind.Primary,Glyphs.Download);
  var page=new ModernButton("Release-Seite",ButtonKind.Secondary,Glyphs.Link);
  var tip=new ModernToolTip();
  tip.SetToolTip(install,"Programm beenden, auf die neue Version aktualisieren und neu starten");
  tip.SetToolTip(page,"Öffnet die Release-Seite auf GitHub");
  foreach(var b in new[]{check,install,page}){b.Margin=Padding.Empty;b.Height=Ui.Px(30);panel.Controls.Add(b);}
  panel.Controls.Add(glyph);panel.Controls.Add(title);panel.Controls.Add(detail);panel.Controls.Add(more);panel.Controls.Add(box);

  // Automatisch prüfen: Text bündig mit den Werten, Schalter rechtsbündig mit dem Knopf
  var auto=new ToggleSwitch{BackColor=Ui.Surface};
  auto.Checked=updates.Auto();
  var autoText=new Label{Text="Automatisch prüfen",ForeColor=Ui.TextColor,BackColor=Ui.Surface,AutoSize=false,
   TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,Cursor=Cursors.Hand,UseMnemonic=false};
  autoText.Click+=(s,e)=>auto.Checked=!auto.Checked;
  panel.Controls.Add(autoText);panel.Controls.Add(auto);

  // Automatisch aktualisieren: nur bedienbar, wenn "Automatisch prüfen" an ist (sonst gesperrt und grau)
  ToggleSwitch install2=null;Label install2Text=null;
  if(hasAutoInstall) {
   install2=new ToggleSwitch{BackColor=Ui.Surface};
   install2.Checked=updates.AutoInstall();
   install2Text=new Label{Text="Automatisch aktualisieren",ForeColor=Ui.TextColor,BackColor=Ui.Surface,AutoSize=false,
    TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,Cursor=Cursors.Hand,UseMnemonic=false};
   string hint="Eine neue Version ohne Rückfrage installieren, sobald "+AppInfo.FullName+" nicht beschäftigt ist. "+
    AppInfo.FullName+" startet danach von selbst neu.";
   tip.SetToolTip(install2,hint);
   tip.SetToolTip(install2Text,hint);
   var sw=install2;
   install2Text.Click+=(s,e)=>{if(sw.Enabled)sw.Checked=!sw.Checked;};
   install2.CheckedChanged+=(s,e)=>updates.SetAutoInstall(sw.Checked);
   panel.Controls.Add(install2Text);panel.Controls.Add(install2);
  }
  Action syncAutoInstall=()=>{
   if(install2==null)return;
   install2.Enabled=auto.Checked;
   install2Text.ForeColor=auto.Checked?Ui.TextColor:Ui.Muted;
   install2Text.Cursor=auto.Checked?Cursors.Hand:Cursors.Default;
  };
  syncAutoInstall();
  auto.CheckedChanged+=(s,e)=>{if(updates.SetAuto!=null)updates.SetAuto(auto.Checked);syncAutoInstall();};

  bool expanded=false;
  int boxHeight=0;
  var flags=TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix;
  Action layout=()=>{
   int right=panel.Width-Ui.Px(2);
   foreach(var b in new[]{check,install,page})b.Location=new Point(right-b.Width,uy+Ui.Px(8));
   var shown=install.Visible?install:page.Visible?page:check;
   int avail=Math.Max(Ui.Px(40),shown.Left-Ui.Px(6)-tx);
   title.Size=new Size(Math.Max(Ui.Px(40),shown.Left-Ui.Px(12)-tx),Ui.Px(22));
   // zweite Zeile endet vor dem Knopf; "Details" etwa ein Leerzeichen hinter dem Text
   int linkW=more.Visible?more.PreferredWidth:0;
   int textW=String.IsNullOrEmpty(detail.Text)?0:TextRenderer.MeasureText(detail.Text,smallFont,Size.Empty,flags).Width; // NoPadding
   int cap=Math.Max(0,avail-(more.Visible?linkW+Ui.Px(4):0));
   int shownW=Math.Min(textW,Math.Max(0,cap-Ui.Px(6)));             // gezeigte Textbreite, sonst mit "…" gekürzt
   detail.Size=new Size(textW>0?shownW+Ui.Px(6):0,Ui.Px(20));
   detail.SendToBack();
   detail.Visible=textW>0;
   // "Details" etwa ein Leerzeichen hinter dem Text (Vorgabe: Textanfang + Breite(NoPadding) − 3).
   // Beide Labels zeichnen mit demselben Textrand, deshalb genügt: Textende + Breite eines Leerzeichens.
   // Ohne Text bündig unter der Überschrift.
   if(more.Visible) {
    int space=TextRenderer.MeasureText("x x",smallFont,Size.Empty,flags).Width-TextRenderer.MeasureText("xx",smallFont,Size.Empty,flags).Width;
    more.Location=new Point(textW>0?tx+shownW+space:tx,uy+Ui.Px(23));
    more.BringToFront();
   }
   // aufgeklapptes Feld von x0 bis zum rechten Rand der Werte, Oberkante uy + 46
   box.SetBounds(x0,uy+Ui.Px(46),right-x0,boxHeight);
   int ay=uy+Ui.Px(49)+(expanded?boxHeight+Ui.Px(8):0);
   auto.SetBounds(right-auto.Width,ay+(Ui.Px(32)-auto.Height)/2,auto.Width,auto.Height);
   autoText.SetBounds(x0,ay,Math.Max(Ui.Px(40),auto.Left-Ui.Px(8)-x0),Ui.Px(32));
   if(install2!=null) {
    int ay2=ay+Ui.Px(32);
    install2.SetBounds(right-install2.Width,ay2+(Ui.Px(32)-install2.Height)/2,install2.Width,install2.Height);
    install2Text.SetBounds(x0,ay2,Math.Max(Ui.Px(40),install2.Left-Ui.Px(8)-x0),Ui.Px(32));
   }
  };

  // Details auf- und zuklappen: Karte, alles darunter und das Fenster wachsen um Feldhöhe + 8
  Action<bool> setExpanded=open=>{
   if(open==expanded)return;
   if(open) {
    int w=Math.Max(Ui.Px(60),panel.Width-Ui.Px(2)-x0-Ui.Px(16));
    int line=TextRenderer.MeasureText("Ag",smallFont,Size.Empty,flags).Height;
    int h=TextRenderer.MeasureText(boxText.Text,smallFont,new Size(w,0),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl).Height;
    h=Math.Min(h,line*6);           // höchstens sechs Zeilen
    boxHeight=h+Ui.Px(12)+Ui.Px(2);
    boxText.ScrollBars=h>=line*6?ScrollBars.Vertical:ScrollBars.None;
   }
   int delta=boxHeight+Ui.Px(8);
   expanded=open;
   box.Visible=open;
   more.Text=open?"Weniger":"Details";
   Grow(panel,open?delta:-delta);
   layout();
  };
  more.Click+=(s,e)=>setExpanded(!expanded);

  Action<UpdateStatus> show=st=>{
   switch(st.Kind) {
    case UpdateKind.Current:glyph.Set(Glyphs.Completed,Ui.SuccessColor);break;
    case UpdateKind.Available:glyph.Set(Glyphs.Download,Ui.Accent);break;
    case UpdateKind.Failed:glyph.Set(Glyphs.Warning,Ui.ErrorColor);break;
    case UpdateKind.Checking:glyph.Set(Glyphs.Sync,Ui.Muted);break;
    default:glyph.Set(Glyphs.Info,Ui.Muted);break; // noch nicht geprüft, nur Vorabversionen
   }
   title.Text=st.Title;
   title.ForeColor=st.Kind==UpdateKind.Available?Ui.Accent:st.Kind==UpdateKind.Checking?Ui.Muted:Ui.TextColor;
   detail.Text=st.Detail;
   tip.SetToolTip(detail,st.Detail);
   tip.SetToolTip(title,st.Title);
   boxText.Text=st.More.Replace("\r\n","\n").Replace("\n","\r\n");
   more.Visible=st.Kind==UpdateKind.Failed&&st.More.Length>0;
   if(!more.Visible)setExpanded(false); // neuer Stand ohne Detailtext: Feld schließen
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
   catch(Exception ex){st=UpdateStatus.FromError(ex);}
   if(!IsDisposed)show(st);
  };
  install.Click+=(s,e)=>{
   Close();
   if(updates.Install!=null)updates.Install();
  };
  page.Click+=(s,e)=>{if(updates.OpenPage!=null)updates.OpenPage();};
  panel.Resize+=(s,e)=>layout();
  show(updates.Status());
  return panel;
 }

 // Updates-Bereich, Karte, alles darunter und das Fenster um delta vergrößern bzw. verkleinern
 void Grow(Panel panel,int delta) {
  if(delta==0||card==null)return;
  bool shown=IsHandleCreated;
  if(shown){MinimumSize=Size.Empty;MaximumSize=Size.Empty;} // Sperre der festen Größe vorher lösen
  SuspendLayout();
  int cardBottom=card.Bottom;
  foreach(Control c in Controls)
   if(c!=card&&c.Top>=cardBottom)c.Top+=delta;
  panel.Height+=delta;
  card.Height+=delta;
  ClientSize=new Size(ClientSize.Width,ClientSize.Height+delta);
  ResumeLayout();
  if(shown){MinimumSize=Size;MaximumSize=Size;}
 }

 Label AddRow(TableLayoutPanel t,ToolTip tip,string name,string value) {
  int row=t.RowCount;
  t.RowCount=row+1;
  t.RowStyles.Add(new RowStyle(SizeType.Absolute,RowHeight));
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