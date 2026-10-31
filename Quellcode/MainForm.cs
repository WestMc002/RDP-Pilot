// reitzner RDP Pilot – RDP-Sitzungen auf den richtigen Monitor lenken
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RDPilot {

// Fußzeile mit Ordnerhinweis links und dezentem Firmenlogo rechts
public class FooterPanel:Panel {
 public Bitmap LogoLight,LogoDark;
 public FooterPanel() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
  BackColor=Ui.Bg;
 }
 protected override void OnTextChanged(EventArgs e){base.OnTextChanged(e);Invalidate();}
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(BackColor);
  int right=Width;
  Bitmap logo=Ui.Dark?LogoDark:LogoLight;
  if(logo!=null) {
   int h=Ui.Px(30);
   int w=(int)Math.Round(logo.Width*(double)h/logo.Height);
   var dest=new Rectangle(Width-w,(Height-h)/2,w,h);
   g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
   using(var attributes=new System.Drawing.Imaging.ImageAttributes()) {
    var matrix=new System.Drawing.Imaging.ColorMatrix();
    matrix.Matrix33=Ui.Dark?0.8f:0.9f;
    attributes.SetColorMatrix(matrix);
    g.DrawImage(logo,dest,0,0,logo.Width,logo.Height,GraphicsUnit.Pixel,attributes);
   }
   right=dest.X-Ui.Px(16);
  }
  var flags=TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis;
  TextRenderer.DrawText(g,Text,Ui.SmallFont,new Rectangle(Ui.Px(2),0,Math.Max(0,right-Ui.Px(2)),Height),Ui.Muted,flags);
 }
}

public class MainForm:Form {
 readonly List<MonitorInfo> monitors;
 readonly List<string> paths=new List<string>();
 readonly string folder;
 RdpFile current;

 HeaderPanel header;
 FooterPanel footer;
 ModernSelect select=new ModernSelect();
 MonitorMap map=new MonitorMap();
 ModernCheck resolution=new ModernCheck("Auflösung an den Monitor anpassen (nur bei einem Monitor)");
 ModernCheck backupCheck=new ModernCheck("Sicherung erstellen (.rdp.bak)");
 InfoBar info=new InfoBar();
 Label connectionInfo=new Label(),selectionInfo=new Label();
 ModernButton connect,save,start,restore,openFolder,refresh,browse,ids;

 public MainForm(string startFile):this(startFile,null){}

 // folderOverride: anderer Ordner für die Verbindungen (nur für die Demo-Screenshots)
 public MainForm(string startFile,string folderOverride) {
  folder=folderOverride??AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
  monitors=Monitors.Load();

  Text=AppInfo.Product+" "+AppInfo.Version;
  try {Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);} catch {}
  AutoScaleMode=AutoScaleMode.None;Font=Ui.UiFont;BackColor=Ui.Bg;ForeColor=Ui.TextColor;
  StartPosition=FormStartPosition.CenterScreen;AllowDrop=true;KeyPreview=true;

  connect=new ModernButton("Speichern & verbinden",ButtonKind.Primary,"");
  save=new ModernButton("Speichern",ButtonKind.Secondary,"");
  start=new ModernButton("Nur verbinden",ButtonKind.Secondary,"");
  restore=new ModernButton("Original wiederherstellen",ButtonKind.Secondary,"");
  openFolder=new ModernButton("Ordner öffnen",ButtonKind.Secondary,"");
  refresh=new ModernButton("Aktualisieren",ButtonKind.Ghost,"");
  browse=new ModernButton("Andere Datei …",ButtonKind.Ghost,"");
  ids=new ModernButton("IDs prüfen",ButtonKind.Ghost,"");
  foreach(var b in new[]{refresh,browse,ids}){b.Height=Ui.Px(32);b.Margin=new Padding(0);}
  openFolder.Margin=new Padding(0);

  // Kopfbereich
  header=new HeaderPanel{Dock=DockStyle.Fill,Margin=new Padding(0,0,0,Ui.Px(12)),Title=AppInfo.Product,Subtitle="RDP-Sitzungen auf den richtigen Monitor lenken",Badge="Version "+AppInfo.Version};
  header.Logo=LoadBitmap("AppIcon.png");
  header.Controls.Add(openFolder);
  header.Resize+=(s,e)=>openFolder.Location=new Point(header.Width-openFolder.Width,(header.Height-openFolder.Height)/2);

  // Karte Verbindung
  var connectionCard=new Card{Dock=DockStyle.Fill,Margin=new Padding(0,0,0,Ui.Px(14)),Padding=new Padding(Ui.Px(8))};
  var connectionHead=MakeHead("Verbindung",connectionInfo,new[]{browse,refresh});
  var selectHost=new Panel{Dock=DockStyle.Fill,BackColor=Ui.Surface,Padding=new Padding(Ui.Px(8),Ui.Px(2),Ui.Px(8),Ui.Px(8))};
  select.Dock=DockStyle.Top;select.Placeholder="Keine .rdp-Datei gefunden – über „Andere Datei …“ auswählen";
  selectHost.Controls.Add(select);
  connectionCard.Controls.Add(selectHost);connectionCard.Controls.Add(connectionHead);selectHost.BringToFront();

  // Karte Monitore
  var monitorCard=new Card{Dock=DockStyle.Fill,Margin=new Padding(0,0,0,Ui.Px(14)),Padding=new Padding(Ui.Px(8))};
  var monitorHead=MakeHead("Monitore",selectionInfo,new[]{ids});
  var monitorFoot=new Panel{Dock=DockStyle.Bottom,Height=Ui.Px(44),BackColor=Ui.Surface,Padding=new Padding(Ui.Px(10),0,0,Ui.Px(4))};
  resolution.Location=new Point(Ui.Px(10),(Ui.Px(40)-resolution.Height)/2);
  resolution.Checked=true;
  monitorFoot.Controls.Add(resolution);
  // Sicherung ist standardmäßig aus
  backupCheck.Checked=false;
  monitorFoot.Controls.Add(backupCheck);
  monitorFoot.Resize+=(s,e)=>backupCheck.Location=new Point(Math.Max(resolution.Right+Ui.Px(24),monitorFoot.Width-backupCheck.Width-Ui.Px(12)),(Ui.Px(40)-backupCheck.Height)/2);
  var help=new HelpSteps{Dock=DockStyle.Bottom,Height=Ui.Px(96),BackColor=Ui.Surface};
  map.Dock=DockStyle.Fill;map.Monitors=monitors;
  monitorCard.Controls.Add(map);monitorCard.Controls.Add(help);monitorCard.Controls.Add(monitorFoot);monitorCard.Controls.Add(monitorHead);map.BringToFront();

  // Aktionen
  var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,Margin=new Padding(0,0,0,Ui.Px(14)),Padding=new Padding(0),BackColor=Ui.Bg,WrapContents=false};
  var separator=new ActionSeparator();
  actions.Controls.AddRange(new Control[]{connect,save,start,separator,restore});

  info.Dock=DockStyle.Fill;info.Margin=new Padding(0,0,0,Ui.Px(10));

  footer=new FooterPanel{Dock=DockStyle.Fill,Margin=new Padding(0)};
  footer.LogoLight=LoadBitmap("Logo-hell.png");
  footer.LogoDark=LoadBitmap("Logo-dunkel.png");
  footer.Text="© "+DateTime.Now.Year+" · "+AppInfo.FullName;
  header.BadgeClick+=(s,e)=>AboutDialog.ShowFor(this,folder);

  var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(Ui.Px(24),Ui.Px(18),Ui.Px(24),Ui.Px(16)),ColumnCount=1,RowCount=6,BackColor=Ui.Bg};
  layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(76)));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(44)+Ui.Px(54)+Ui.Px(16)+Ui.Px(14)));
  layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(52)));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(66)));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(38)));
  layout.Controls.Add(header,0,0);layout.Controls.Add(connectionCard,0,1);layout.Controls.Add(monitorCard,0,2);
  layout.Controls.Add(actions,0,3);layout.Controls.Add(info,0,4);layout.Controls.Add(footer,0,5);
  Controls.Add(layout);

  Rectangle work=Screen.PrimaryScreen.WorkingArea;
  // Fensterhöhe an die Monitoranordnung anpassen, damit die Karte ohne Leerraum gefüllt ist
  int width=Math.Min(Ui.Px(900),work.Width-Ui.Px(40));
  int mapHeight=Ui.Px(220);
  if(monitors.Count>0) {
   Rectangle u=monitors[0].Bounds;
   foreach(var m in monitors)u=Rectangle.Union(u,m.Bounds);
   int mapWidth=width-Ui.Px(48)-Ui.Px(16)-Ui.Px(32);
   mapHeight=(int)(mapWidth*(double)u.Height/u.Width)+Ui.Px(32);
   mapHeight=Math.Max(Ui.Px(200),Math.Min(Ui.Px(420),mapHeight));
  }
  int fixedHeight=Ui.Px(18)+Ui.Px(76)+Ui.Px(128)+Ui.Px(44)+Ui.Px(96)+Ui.Px(44)+Ui.Px(16)+Ui.Px(14)+Ui.Px(52)+Ui.Px(66)+Ui.Px(38)+Ui.Px(16);
  ClientSize=new Size(width,Math.Min(fixedHeight+mapHeight,work.Height-Ui.Px(40)));
  int chrome=Height-ClientSize.Height;
  MinimumSize=new Size(Math.Min(Ui.Px(820),work.Width),Math.Min(fixedHeight+Ui.Px(160)+chrome,work.Height));

  // Ereignisse
  select.SelectedIndexChanged+=(s,e)=>LoadCurrent();
  map.SelectionChanged+=(s,e)=>UpdateState(true);
  refresh.Click+=(s,e)=>{ReloadList(CurrentPath());Status("Ordner neu eingelesen: "+paths.Count+" Verbindung(en).",InfoKind.Info);};
  browse.Click+=(s,e)=>Browse();
  ids.Click+=(s,e)=>Run("mstsc.exe","/l");
  openFolder.Click+=(s,e)=>Run("explorer.exe",Quote(folder));
  save.Click+=(s,e)=>SaveCurrent(false);
  connect.Click+=(s,e)=>SaveCurrent(true);
  start.Click+=(s,e)=>{if(current!=null)Run("mstsc.exe",Quote(current.Path));};
  restore.Click+=(s,e)=>RestoreBackup();
  DragEnter+=(s,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;};
  DragDrop+=(s,e)=>{
   var files=e.Data.GetData(DataFormats.FileDrop) as string[];
   if(files==null)return;
   foreach(string f in files)if(f.EndsWith(".rdp",StringComparison.OrdinalIgnoreCase))AddPath(f,true);
  };

  // Hilfetexte bei Mouseover
  var tips=new ToolTip{AutoPopDelay=15000,InitialDelay=400,ReshowDelay=150,ShowAlways=true};
  tips.SetToolTip(openFolder,"Öffnet den Ordner mit den Verbindungen im Explorer,\nz.B. um neue .rdp-Dateien hineinzukopieren.");
  tips.SetToolTip(refresh,"Liest den Ordner neu ein, nachdem Verbindungen\nhinzugefügt oder gelöscht wurden.");
  tips.SetToolTip(browse,"Wählt eine .rdp-Datei aus einem anderen Ordner.\nAlternativ die Datei per Drag & Drop auf das Fenster ziehen.");
  tips.SetToolTip(ids,"Zeigt die Monitor-Nummern der Remotedesktopverbindung (mstsc /l).\nHilfreich, wenn die Sitzung auf einem anderen Monitor landet als erwartet.");
  tips.SetToolTip(select,"Verbindung auswählen. Angezeigt werden alle .rdp-Dateien\naus dem Ordner von RDP Pilot, jeweils mit Server und Benutzer.");
  tips.SetToolTip(resolution,"Nur bei einem Monitor: Die Sitzung übernimmt\ndie Auflösung des gewählten Bildschirms.");
  tips.SetToolTip(backupCheck,"Sichert vor dem Speichern die unveränderte Verbindung als .rdp.bak.\nEine vorhandene Sicherung bleibt unverändert, damit das Original erhalten bleibt.\nStandardmäßig ausgeschaltet.");
  tips.SetToolTip(connect,"Speichert die Auswahl, startet die Verbindung\nund schließt RDP Pilot.");
  tips.SetToolTip(save,"Speichert die Auswahl in der Verbindung, ohne zu verbinden.");
  tips.SetToolTip(start,"Startet die Verbindung mit der zuletzt gespeicherten Einstellung.\nNicht gespeicherte Änderungen werden nicht übernommen.");
  tips.SetToolTip(restore,"Stellt die Verbindung so wieder her, wie sie vor dem ersten Speichern war.\nNur verfügbar, wenn eine Sicherung (.rdp.bak) existiert.");
  tips.SetToolTip(footer,"Verbindungen aus: "+folder);
  tips.SetToolTip(header,"Klick auf die Versionsnummer: Info über "+AppInfo.FullName);
  tips.SetToolTip(selectionInfo,"Linksklick auf einen Monitor wählt ihn aus oder ab,\nRechtsklick legt den Hauptmonitor fest.");

  ApplyTheme();
  ReloadList(null);
  if(!String.IsNullOrEmpty(startFile)&&File.Exists(startFile))AddPath(startFile,true);
  if(paths.Count==0)Status("Keine .rdp-Dateien im Ordner gefunden. Lege deine Verbindungen neben die EXE oder wähle über „Andere Datei …“ eine aus.",InfoKind.Warning);
 }

 Panel MakeHead(string title,Label detail,ModernButton[] buttons) {
  var head=new Panel{Dock=DockStyle.Top,Height=Ui.Px(44),BackColor=Ui.Surface};
  var titleLabel=new Label{Text=title,AutoSize=true,Font=Ui.UiSemibold,ForeColor=Ui.TextColor,BackColor=Ui.Surface};
  detail.AutoSize=true;detail.Font=Ui.SmallFont;detail.ForeColor=Ui.Muted;detail.Tag="muted";detail.BackColor=Ui.Surface;detail.UseMnemonic=false;
  head.Controls.Add(titleLabel);head.Controls.Add(detail);
  foreach(var b in buttons)head.Controls.Add(b);
  EventHandler layoutHead=(s,e)=>{
   titleLabel.Location=new Point(Ui.Px(10),(head.Height-titleLabel.Height)/2);
   detail.Location=new Point(titleLabel.Right+Ui.Px(10),(head.Height-detail.Height)/2+1);
   int x=head.Width-Ui.Px(2);
   foreach(var b in buttons){x-=b.Width;b.Location=new Point(x,(head.Height-b.Height)/2);x-=Ui.Px(4);}
  };
  head.Resize+=layoutHead;detail.SizeChanged+=layoutHead;
  return head;
 }

 static Bitmap LoadBitmap(string name) {
  try {
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) {
    if(stream!=null)return new Bitmap(stream);
   }
   string file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name);
   if(File.Exists(file))using(var temp=new Bitmap(file))return new Bitmap(temp);
  } catch {}
  return null;
 }

 static string Quote(string s){return "\""+s+"\"";}

 void Run(string file,string args) {
  try {Process.Start(file,args);}
  catch(Exception ex){Status(file+" konnte nicht gestartet werden: "+ex.Message,InfoKind.Error);}
 }

 void Status(string text,InfoKind kind){info.SetMessage(text,kind);}

 // --- RDP-Liste ---------------------------------------------------------------
 string CurrentPath(){return select.SelectedIndex>=0&&select.SelectedIndex<paths.Count?paths[select.SelectedIndex]:null;}

 static string DescribeFile(string path) {
  try {
   var f=RdpFile.Load(path);
   string server=f.Server,user=f.User;
   if(server.Length>0&&user.Length>0)return server+"  ·  "+user;
   return server+user;
  } catch {return "";}
 }

 void AddPath(string path,bool selectIt) {
  string full=Path.GetFullPath(path);
  int index=paths.FindIndex(p=>String.Equals(p,full,StringComparison.OrdinalIgnoreCase));
  if(index<0) {
   paths.Add(full);
   string title=Path.GetFileNameWithoutExtension(full);
   if(!String.Equals(Path.GetDirectoryName(full),folder,StringComparison.OrdinalIgnoreCase))title=title+"  ("+Path.GetDirectoryName(full)+")";
   select.Items.Add(new ModernSelect.Item{Title=title,Detail=DescribeFile(full),Tag=full});
   index=paths.Count-1;
  }
  UpdateCount();
  if(selectIt){if(select.SelectedIndex==index)LoadCurrent();else select.SelectedIndex=index;}
 }

 void ReloadList(string keep) {
  paths.Clear();select.Clear();
  try {
   foreach(string f in Directory.GetFiles(folder,"*.rdp").OrderBy(f=>Path.GetFileName(f),StringComparer.CurrentCultureIgnoreCase))AddPath(f,false);
  } catch(Exception ex){Status("Ordner konnte nicht gelesen werden: "+ex.Message,InfoKind.Error);}
  if(keep!=null&&File.Exists(keep))AddPath(keep,true);
  else if(paths.Count>0)select.SelectedIndex=0;
  else LoadCurrent();
  UpdateCount();
 }

 void UpdateCount() {
  int inFolder=paths.Count(p=>String.Equals(Path.GetDirectoryName(p),folder,StringComparison.OrdinalIgnoreCase));
  connectionInfo.Text=inFolder==1?"1 Verbindung im Ordner":inFolder+" Verbindungen im Ordner";
 }

 void Browse() {
  using(var dialog=new OpenFileDialog()) {
   dialog.Filter="Remotedesktop-Verbindungen (*.rdp)|*.rdp|Alle Dateien (*.*)|*.*";
   string cur=CurrentPath();
   dialog.InitialDirectory=cur!=null?Path.GetDirectoryName(cur):folder;
   if(dialog.ShowDialog(this)==DialogResult.OK)AddPath(dialog.FileName,true);
  }
 }

 void LoadCurrent() {
  string path=CurrentPath();
  current=null;
  if(path!=null) {
   try {
    current=RdpFile.Load(path);
    map.SetSelection(current.CurrentMonitors(monitors),current.CurrentMain(monitors));
    var sel=map.Selection;
    Status("„"+Path.GetFileNameWithoutExtension(path)+"“ startet derzeit auf Monitor "+Monitors.Describe(sel)+(sel.Count>1?" (Hauptmonitor "+map.MainMonitor.Nr+")":"")+".",InfoKind.Info);
   } catch(Exception ex) {
    Status("Datei konnte nicht gelesen werden: "+ex.Message,InfoKind.Error);
   }
  } else {
   map.SetSelection(monitors.Where(m=>m.Primary));
  }
  UpdateState();
 }

 // selectionChanged: Auswahl wurde geändert -> Infozeile immer neu schreiben
 void UpdateState() {UpdateState(false);}
 void UpdateState(bool selectionChanged) {
  var sel=map.Selection;
  var main=map.MainMonitor;
  selectionInfo.Text=sel.Count+" von "+monitors.Count+" ausgewählt"+(main!=null?"  ·  Hauptmonitor "+main.Nr:"");
  resolution.Enabled=sel.Count<=1;
  bool ok=current!=null&&sel.Count>0;
  connect.Enabled=ok;save.Enabled=ok;start.Enabled=current!=null;
  restore.Enabled=current!=null&&File.Exists(current.Path+".bak");
  if(current!=null&&sel.Count>1&&!Monitors.Adjacent(sel))
   Status("Die gewählten Monitore liegen nicht direkt aneinander. Die Remotedesktopverbindung kann das ablehnen.",InfoKind.Warning);
  else if(current!=null&&sel.Count==0)
   Status("Bitte mindestens einen Monitor auswählen.",InfoKind.Warning);
  else if(current!=null&&(selectionChanged||info.Kind==InfoKind.Warning))
   Status("Auswahl: Monitor "+Monitors.Describe(sel)+(main!=null?" – Hauptmonitor der Sitzung: "+main.Nr:""),InfoKind.Info);
 }

 void SaveCurrent(bool andConnect) {
  if(current==null)return;
  var sel=map.Selection;
  if(sel.Count==0){Status("Bitte mindestens einen Monitor auswählen.",InfoKind.Warning);return;}
  if(!Monitors.Adjacent(sel)) {
   var answer=MessageBox.Show(this,"Die gewählten Monitore liegen nicht direkt aneinander.\nDie Remotedesktopverbindung kann das ablehnen.\n\nTrotzdem speichern?",AppInfo.Product,MessageBoxButtons.YesNo,MessageBoxIcon.Warning);
   if(answer!=DialogResult.Yes)return;
  }
  bool backupCreated=false;
  try {
   current.ApplyMonitors(sel,resolution.Checked,map.MainMonitor);
   backupCreated=current.Save(backupCheck.Checked);
  } catch(Exception ex) {
   Status("Fehler beim Speichern: "+ex.Message,InfoKind.Error);
   return;
  }
  string text="Gespeichert: Monitor "+Monitors.Describe(sel);
  if(sel.Count>1)text+=" (Hauptmonitor "+map.MainMonitor.Nr+")";
  else text+=" ("+sel[0].Bounds.Width+" x "+sel[0].Bounds.Height+")";
  if(backupCreated)text+=". Sicherung angelegt: "+Path.GetFileName(current.Path)+".bak";
  Status(text+".",InfoKind.Success);
  UpdateState();
  if(andConnect) {
   Run("mstsc.exe",Quote(current.Path));
   Close();
  }
 }

 void RestoreBackup() {
  if(current==null)return;
  string backup=current.Path+".bak";
  if(!File.Exists(backup))return;
  var answer=MessageBox.Show(this,"Die ursprüngliche Datei aus der Sicherung wiederherstellen?\n\n"+backup,AppInfo.Product,MessageBoxButtons.YesNo,MessageBoxIcon.Question);
  if(answer!=DialogResult.Yes)return;
  try {
   File.Copy(backup,current.Path,true);
   File.Delete(backup);
  } catch(Exception ex){Status("Wiederherstellen fehlgeschlagen: "+ex.Message,InfoKind.Error);return;}
  LoadCurrent();
  Status("Originaldatei wiederhergestellt.",InfoKind.Success);
 }

 // --- Farbschema ----------------------------------------------------------------
 protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Native.DarkTitle(Handle);}
 // Windows meldet einen Wechsel zwischen hellem und dunklem Modus mit WM_SETTINGCHANGE "ImmersiveColorSet"
 protected override void WndProc(ref Message m) {
  base.WndProc(ref m);
  // ImmersiveColorSet kommt bei Wechsel hell/dunkel und bei geänderter Akzentfarbe,
  // WM_DWMCOLORIZATIONCOLORCHANGED (0x0320) ebenfalls bei geänderter Akzentfarbe
  bool colorChange=m.Msg==0x0320||(m.Msg==0x001A&&m.LParam!=IntPtr.Zero&&Marshal.PtrToStringUni(m.LParam)=="ImmersiveColorSet");
  if(colorChange){Ui.ApplyPalette(Ui.DetectDark());ApplyTheme();}
 }
 void ApplyTheme() {
  BackColor=Ui.Bg;ForeColor=Ui.TextColor;
  ApplyTheme(this,false);
  if(IsHandleCreated)Native.DarkTitle(Handle);
  Invalidate(true);
 }
 static void ApplyTheme(Control parent,bool inCard) {
  foreach(Control control in parent.Controls) {
   bool card=inCard;
   if(control is Card){control.BackColor=Ui.Bg;card=true;}
   else if(!(control is ModernButton))control.BackColor=inCard?Ui.Surface:Ui.Bg;
   if(control is Label)control.ForeColor=(control.Tag as string)=="muted"?Ui.Muted:Ui.TextColor;
   ApplyTheme(control,card);
  }
 }
}

// Kurze Anleitung unter der Monitorauswahl: drei nummerierte Schritte nebeneinander
public class HelpSteps:Control {
 static readonly string[][] Steps={
  new[]{"Monitore auswählen","Bildschirme anklicken, auf denen die Sitzung erscheinen soll. Ausgewählte sind farbig markiert."},
  new[]{"Hauptmonitor festlegen","Bei mehreren Bildschirmen per Rechtsklick. Dort erscheinen Taskleiste und Startmenü."},
  new[]{"Speichern & verbinden","Die Verbindung startet ab dann immer auf diesen Bildschirmen."}
 };
 public HelpSteps() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
 }
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(BackColor);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  using(var pen=new Pen(Ui.Border)) g.DrawLine(pen,Ui.Px(10),0,Width-Ui.Px(10),0);
  int pad=Ui.Px(12),gap=Ui.Px(20),badge=Ui.Px(22);
  int col=(Width-pad*2-gap*2)/3;
  var center=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding;
  for(int i=0;i<Steps.Length;i++) {
   int x=pad+i*(col+gap),y=Ui.Px(14);
   var circle=new Rectangle(x,y,badge,badge);
   using(var brush=new SolidBrush(Ui.Blend(Ui.Surface,Ui.Primary,Ui.Dark?0.35f:0.14f))) g.FillEllipse(brush,circle);
   TextRenderer.DrawText(g,(i+1).ToString(),Ui.SmallFont,circle,Ui.Dark?Ui.TextColor:Ui.Primary,center);
   int tx=x+badge+Ui.Px(10),tw=Math.Max(10,col-badge-Ui.Px(10));
   var titleFlags=TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis;
   Size ts=TextRenderer.MeasureText(g,Steps[i][0],Ui.UiSemibold,new Size(tw,int.MaxValue),titleFlags);
   TextRenderer.DrawText(g,Steps[i][0],Ui.UiSemibold,new Rectangle(tx,y+(badge-ts.Height)/2,tw,ts.Height),Ui.TextColor,titleFlags);
   var bodyFlags=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis;
   int by=y+badge+Ui.Px(4);
   TextRenderer.DrawText(g,Steps[i][1],Ui.SmallFont,new Rectangle(tx,by,tw,Height-by-Ui.Px(6)),Ui.Muted,bodyFlags);
  }
 }
}

public class ActionSeparator:Control {
 public ActionSeparator() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
  BackColor=Ui.Bg;Width=Ui.Px(25);Height=Ui.Px(38);Margin=new Padding(0,0,Ui.Px(8),0);
 }
 protected override void OnPaint(PaintEventArgs e) {
  e.Graphics.Clear(BackColor);
  using(var pen=new Pen(Ui.Border)) e.Graphics.DrawLine(pen,Width/2,Ui.Px(6),Width/2,Height-Ui.Px(6));
 }
}

}