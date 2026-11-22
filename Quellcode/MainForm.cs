// RDP Pilot – RDP-Sitzungen auf den richtigen Monitor lenken
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
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RDPilot {

// Fußzeile: links grau "© <Jahr> · <Programm>", rechts klein und grau "www.westmc002.de" (anklickbar).
// Abweichung von den Globalen Richtlinien: kein GS-Logo und kein Name (das GS-Logo steht im Info-Fenster)
public class FooterPanel:Panel {
 const string Url="https://www.westmc002.de";
 Rectangle link;
 bool hover;
 public FooterPanel() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
  BackColor=Ui.Bg;
 }
 protected override void OnTextChanged(EventArgs e){base.OnTextChanged(e);Invalidate();}
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(BackColor);
  var flags=TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding;
  var size=TextRenderer.MeasureText(g,AppInfo.Website,Ui.SmallFont,Size.Empty,flags);
  // Links und rechts gleich weit von der Kante: "©" bringt links etwa 1 × Skalierung Rand mit,
  // deshalb rechts um 1 × Skalierung einrücken
  link=new Rectangle(Width-Ui.Px(1)-size.Width,(Height-size.Height)/2,size.Width,size.Height);
  TextRenderer.DrawText(g,AppInfo.Website,Ui.SmallFont,link.Location,hover?Ui.Accent:Ui.Muted,flags);
  TextRenderer.DrawText(g,Text,Ui.SmallFont,new Rectangle(0,0,Math.Max(0,link.X-Ui.Px(18)),Height),Ui.Muted,
   flags|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
 }
 protected override void OnMouseMove(MouseEventArgs e) {
  bool h=link.Contains(e.Location);
  if(h!=hover){hover=h;Cursor=h?Cursors.Hand:Cursors.Default;Invalidate();}
  base.OnMouseMove(e);
 }
 protected override void OnMouseLeave(EventArgs e) {
  if(hover){hover=false;Cursor=Cursors.Default;Invalidate();}
  base.OnMouseLeave(e);
 }
 protected override void OnMouseClick(MouseEventArgs e) {
  if(e.Button==MouseButtons.Left&&link.Contains(e.Location)) {
   try {Process.Start(new ProcessStartInfo(Url){UseShellExecute=true});} catch {}
  }
  base.OnMouseClick(e);
 }
}

public class MainForm:Form {
 readonly List<MonitorInfo> monitors;
 readonly List<string> paths=new List<string>();
 readonly string folder;
 RdpFile current;

 HeaderPanel header;
 FooterPanel footer;
 TableLayoutPanel layout;
 ToolTip tips;
 // Update-Prüfung über GitHub (Globale Richtlinien, Abschnitt "Updates")
 UpdateBar updateBar;
 UpdateInfo update;        // neuere Version (null = keine)
 bool updateRunning,installing;
 // "Automatisch aktualisieren": ist das Programm beschäftigt, alle 5 Minuten erneut versuchen
 readonly System.Windows.Forms.Timer autoUpdateRetry=new System.Windows.Forms.Timer{Interval=5*60*1000};
 UpdateStatus updateFailure; // Ergebnis der letzten Prüfung von Hand bei Fehler bzw. "Nur Vorabversionen" (Info-Fenster)
 string installFailure;      // automatisches Aktualisieren gescheitert: Originaltext für "Details" (null = nein)
 // Neustart durch das Update-Skript (--nach-update): Ergebnis melden
 public static bool AfterUpdate;
 ModernSelect select=new ModernSelect();
 MonitorMap map=new MonitorMap();
 ModernCheck resolution=new ModernCheck("Auflösung an den Monitor anpassen (nur bei einem Monitor)");
 ModernCheck backupCheck=new ModernCheck("Sicherung erstellen (.rdp.bak)");
 InfoBar info=new InfoBar();
 Label connectionInfo=new Label(),selectionInfo=new Label();
 ModernButton connect,save,start,restore,openFolder,browse,identify;
 FaintLink ids;
 // Ordner und Bildschirme werden überwacht; Änderungen kurz sammeln, dann neu einlesen
 FileSystemWatcher watcher;
 readonly System.Windows.Forms.Timer folderTimer=new System.Windows.Forms.Timer{Interval=600};
 readonly System.Windows.Forms.Timer displayTimer=new System.Windows.Forms.Timer{Interval=1200};
 readonly bool live; // false bei den Demo-Screenshots: nichts überwachen

 public MainForm(string startFile):this(startFile,null){}

 // folderOverride: anderer Ordner für die Verbindungen (nur für die Demo-Screenshots)
 public MainForm(string startFile,string folderOverride) {
  folder=folderOverride??AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
  live=folderOverride==null&&!Demo.Active&&Monitors.Override==null;
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
  browse=new ModernButton("Andere Datei …",ButtonKind.Ghost,"");
  identify=new ModernButton("Identifizieren",ButtonKind.Ghost,"\uE7F4");
  foreach(var b in new[]{browse,identify}){b.Height=Ui.Px(32);b.Margin=new Padding(0);}
  openFolder.Margin=new Padding(0);

  // Kopfbereich
  header=new HeaderPanel{Dock=DockStyle.Fill,Margin=new Padding(0,0,0,Ui.Px(12)),Title=AppInfo.Product,Subtitle="RDP-Sitzungen auf den richtigen Monitor lenken",Badge="Version "+AppInfo.Version};
  header.Logo=LoadBitmap("AppIcon.png");
  header.Controls.Add(openFolder);
  header.Resize+=(s,e)=>openFolder.Location=new Point(header.Width-openFolder.Width,(header.Height-openFolder.Height)/2);

  // Karte Verbindung
  var connectionCard=new Card{Dock=DockStyle.Fill,Margin=new Padding(0,0,0,Ui.Px(14)),Padding=new Padding(Ui.Px(8))};
  var connectionHead=MakeHead("Verbindung",connectionInfo,new Control[]{browse});
  var selectHost=new Panel{Dock=DockStyle.Fill,BackColor=Ui.Surface,Padding=new Padding(Ui.Px(8),Ui.Px(2),Ui.Px(8),Ui.Px(8))};
  select.Dock=DockStyle.Top;select.Placeholder="Keine .rdp-Datei gefunden – über „Andere Datei …“ auswählen";
  selectHost.Controls.Add(select);
  connectionCard.Controls.Add(selectHost);connectionCard.Controls.Add(connectionHead);selectHost.BringToFront();

  // Karte Monitore
  var monitorCard=new Card{Dock=DockStyle.Fill,Margin=new Padding(0,0,0,Ui.Px(14)),Padding=new Padding(Ui.Px(8))};
  // Unauffälliger Link für den Support: Monitor-IDs der Remotedesktopverbindung
  ids=new FaintLink("IDs anzeigen");
  var monitorHead=MakeHead("Monitore",selectionInfo,new Control[]{identify,ids});
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
  actions.Controls.AddRange(new Control[]{connect,save,start,restore});
  // "Original wiederherstellen" rechtsbündig, getrennt von den Hauptaktionen links
  EventHandler alignRestore=(s,e)=>{
   int used=0;
   foreach(var b in new[]{connect,save,start})used+=b.Width+b.Margin.Horizontal;
   restore.Margin=new Padding(Math.Max(Ui.Px(8),actions.ClientSize.Width-used-restore.Width),0,0,0);
  };
  actions.Resize+=alignRestore;restore.SizeChanged+=alignRestore;

  info.Dock=DockStyle.Fill;info.Margin=new Padding(0,0,0,Ui.Px(10)); // Fußzeile: Abstand Hinweis → Text = Text → Fensterrand (unten 11, weil die Schrift in ihrer Zeile ca. 1 tiefer sitzt)

  footer=new FooterPanel{Dock=DockStyle.Fill,Margin=new Padding(0)};
  footer.Text="© "+DateTime.Now.Year+" · "+AppInfo.FullName;
  header.BadgeClick+=(s,e)=>AboutDialog.ShowFor(this,folder);

  // Hinweisleiste bei neuer Version (nur sichtbar, wenn es eine gibt)
  updateBar=new UpdateBar{Dock=DockStyle.Fill,Margin=new Padding(0,0,0,Ui.Px(12)),Visible=false};
  updateBar.Install.Click+=async (s,e)=>await InstallUpdate();
  updateBar.Page.Click+=(s,e)=>OpenReleasePage();
  updateBar.Later.Click+=(s,e)=>ShowUpdateBar(false);
  AboutDialog.Updates=MakeUpdateHooks();

  layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(Ui.Px(24),Ui.Px(18),Ui.Px(24),Ui.Px(11)),ColumnCount=1,RowCount=7,BackColor=Ui.Bg};
  layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(76)));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,0)); // Update-Hinweis
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(44)+Ui.Px(54)+Ui.Px(16)+Ui.Px(14)));
  layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(52)));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(66)));
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,Ui.Px(38)));
  layout.Controls.Add(header,0,0);layout.Controls.Add(updateBar,0,1);layout.Controls.Add(connectionCard,0,2);layout.Controls.Add(monitorCard,0,3);
  layout.Controls.Add(actions,0,4);layout.Controls.Add(info,0,5);layout.Controls.Add(footer,0,6);
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
  int fixedHeight=Ui.Px(18)+Ui.Px(76)+Ui.Px(128)+Ui.Px(44)+Ui.Px(96)+Ui.Px(44)+Ui.Px(16)+Ui.Px(14)+Ui.Px(52)+Ui.Px(66)+Ui.Px(38)+Ui.Px(11);
  ClientSize=new Size(width,Math.Min(fixedHeight+mapHeight,work.Height-Ui.Px(40)));
  int chrome=Height-ClientSize.Height;
  MinimumSize=new Size(Math.Min(Ui.Px(820),work.Width),Math.Min(fixedHeight+Ui.Px(160)+chrome,work.Height));

  // Ereignisse
  select.SelectedIndexChanged+=(s,e)=>LoadCurrent();
  map.SelectionChanged+=(s,e)=>UpdateState(true);
  identify.Click+=(s,e)=>IdentifyMonitors();
  folderTimer.Tick+=(s,e)=>{folderTimer.Stop();FolderChanged();};
  displayTimer.Tick+=(s,e)=>{displayTimer.Stop();ReloadMonitors(true);};
  browse.Click+=(s,e)=>Browse();
  openFolder.Click+=(s,e)=>Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),Quote(folder)); // fester Pfad
  save.Click+=(s,e)=>SaveCurrent(false);
  connect.Click+=(s,e)=>SaveCurrent(true);
  start.Click+=(s,e)=>StartOnly();
  restore.Click+=(s,e)=>RestoreBackup();
  ids.Click+=(s,e)=>ShowIds();
  DragEnter+=(s,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;};
  DragDrop+=(s,e)=>{
   var files=e.Data.GetData(DataFormats.FileDrop) as string[];
   if(files==null)return;
   foreach(string f in files)if(f.EndsWith(".rdp",StringComparison.OrdinalIgnoreCase))AddPath(f,true);
  };

  // Hilfetexte bei Mouseover
  tips=new ModernToolTip{AutoPopDelay=15000,InitialDelay=400,ReshowDelay=150,ShowAlways=true};
  tips.SetToolTip(ids,"Monitor-IDs der Remotedesktopverbindung (für den Support)");
  tips.SetToolTip(openFolder,"Öffnet den Ordner mit den Verbindungen im Explorer,\nz.B. um neue .rdp-Dateien hineinzukopieren.\nNeue Dateien erscheinen von selbst in der Liste.");
  tips.SetToolTip(identify,"Blendet auf jedem Bildschirm kurz groß seine Nummer ein.\nSo siehst du, welcher Bildschirm hier Monitor 1, 2, 3 … ist.");
  tips.SetToolTip(browse,"Wählt eine .rdp-Datei aus einem anderen Ordner.\nAlternativ die Datei per Drag & Drop auf das Fenster ziehen.");
  tips.SetToolTip(select,"Verbindung auswählen. Angezeigt werden alle .rdp-Dateien\naus dem Ordner von RDP Pilot, jeweils mit Server und Benutzer.\nNeue oder gelöschte Dateien werden von selbst erkannt (F5 liest neu ein).");
  tips.SetToolTip(resolution,"Nur bei einem Monitor: Die Sitzung übernimmt\ndie Auflösung des gewählten Bildschirms.");
  tips.SetToolTip(backupCheck,"Sichert vor dem Speichern die unveränderte Verbindung als .rdp.bak.\nEine vorhandene Sicherung bleibt unverändert, damit das Original erhalten bleibt.\nStandardmäßig ausgeschaltet.");
  tips.SetToolTip(connect,"Speichert die Auswahl, startet die Verbindung\nund schließt RDP Pilot.");
  tips.SetToolTip(save,"Speichert die Auswahl in der Verbindung, ohne zu verbinden.");
  tips.SetToolTip(start,"Startet die Verbindung auf den Monitoren, die gerade ausgewählt sind,\nohne die Verbindung zu ändern. Zum Merken „Speichern“.");
  tips.SetToolTip(restore,"Stellt die Verbindung so wieder her, wie sie vor dem ersten Speichern war.\nNur verfügbar, wenn eine Sicherung (.rdp.bak) existiert.");
  tips.SetToolTip(footer,"Verbindungen aus: "+folder);
  tips.SetToolTip(header,"Klick auf die Versionsnummer: Info über "+AppInfo.FullName);
  tips.SetToolTip(selectionInfo,"Linksklick auf einen Monitor wählt ihn aus oder ab,\nRechtsklick legt den Hauptmonitor fest.");

  ApplyTheme();
  // zuletzt gewählte Verbindung vor dem ersten Einlesen merken (das Einlesen wählt sonst die erste aus)
  string last=null;
  if(live){UpdateSettings.Load();last=UpdateSettings.LastFile;}
  ReloadList(null);
  if(live) {
   // gemerkte Verbindungen aus anderen Ordnern wieder in die Liste, zuletzt gewählte Verbindung wieder auswählen
   // Netzwerkpfade werden dabei nicht angefasst (kein Zugriff auf den Server beim Start), sondern erst gelesen,
   // wenn man sie auswählt; eine zuletzt gewählte Verbindung im Netz wird deshalb nicht automatisch ausgewählt.
   foreach(string f in UpdateSettings.Files.ToList()) {
    if(RdpFile.IsNetworkPath(f))AddPath(f,false,true);
    else if(File.Exists(f))AddPath(f,false);
   }
   if(String.IsNullOrEmpty(startFile)&&last!=null&&!RdpFile.IsNetworkPath(last)&&File.Exists(last))AddPath(last,true);
  }
  if(!String.IsNullOrEmpty(startFile)&&File.Exists(startFile))AddPath(startFile,true);
  if(paths.Count==0)Status("Keine .rdp-Dateien im Ordner gefunden. Lege deine Verbindungen neben die EXE oder wähle über „Andere Datei …“ eine aus.",InfoKind.Warning);
 }

 Panel MakeHead(string title,Label detail,Control[] buttons) {
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

 // Support: Monitor-IDs der Remotedesktopverbindung, wie das Programm sie ermittelt,
 // auf Wunsch zum Vergleich die Anzeige von mstsc /l
 void ShowIds() {
  var lines=new List<string>();
  foreach(var m in Monitors.Load().OrderBy(x=>x.Id)) {
   var b=m.Bounds;
   lines.Add("ID "+m.Id+" = Monitor "+m.Nr+(m.Primary?" (primär)":"")+":  "+b.Width+" × "+b.Height+
    "  ("+b.Left+", "+b.Top+" bis "+(b.Right-1)+", "+(b.Bottom-1)+")");
  }
  var answer=MessageBox.Show(this,"Monitor-IDs der Remotedesktopverbindung (selectedmonitors):\n\n"+String.Join("\n",lines)+
   "\n\nZum Vergleich die Anzeige der Remotedesktopverbindung (mstsc /l) öffnen?",
   AppInfo.FullName,MessageBoxButtons.YesNo,MessageBoxIcon.Information,MessageBoxDefaultButton.Button2);
  if(answer==DialogResult.Yes)Run(RdpLaunch.Mstsc,"/l");
 }

 // --- Bildschirme: identifizieren und Änderungen erkennen -----------------------------
 // Blendet auf jedem Bildschirm kurz seine Nummer ein
 void IdentifyMonitors() {
  if(!live)return;
  ReloadMonitors(true);
  IdentifyOverlay.ShowAll(monitors,map.Selection);
 }

 static bool SameMonitors(IList<MonitorInfo> a,IList<MonitorInfo> b) {
  if(a.Count!=b.Count)return false;
  for(int i=0;i<a.Count;i++)
   if(a[i].Bounds!=b[i].Bounds||a[i].Id!=b[i].Id||a[i].Primary!=b[i].Primary)return false;
  return true;
 }

 // Liest die Bildschirme neu ein, wenn sie sich geändert haben (z. B. Notebook an- oder abgedockt).
 // Die Auswahl wird dann wieder aus der Verbindung gelesen. Rückgabe: true, wenn sich etwas geändert hat.
 bool ReloadMonitors(bool report) {
  if(!live||IsDisposed)return false;
  var fresh=Monitors.Load();
  if(fresh.Count==0||SameMonitors(fresh,monitors))return false;
  IdentifyOverlay.CloseAll();
  monitors.Clear();
  monitors.AddRange(fresh);
  map.Monitors=monitors;
  LoadCurrent();
  // eine Warnung zu fehlenden Bildschirmen (aus LoadCurrent) bleibt stehen
  if(report&&info.Kind!=InfoKind.Warning)
   Status("Die Bildschirme haben sich geändert ("+monitors.Count+" erkannt). Die Anzeige wurde neu eingelesen.",InfoKind.Info);
  return true;
 }

 void OnDisplayChanged(object sender,EventArgs e) {
  if(IsDisposed||!IsHandleCreated)return;
  // Windows meldet eine Änderung oft mehrfach hintereinander: kurz warten, dann einmal einlesen
  try {BeginInvoke(new Action(()=>{displayTimer.Stop();displayTimer.Start();}));} catch {}
 }

 // --- Ordner überwachen -----------------------------------------------------------------
 void StartWatching() {
  Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=OnDisplayChanged;
  try {
   watcher=new FileSystemWatcher(folder,"*.rdp"){NotifyFilter=NotifyFilters.FileName,IncludeSubdirectories=false,SynchronizingObject=this};
   FileSystemEventHandler changed=(s,e)=>{folderTimer.Stop();folderTimer.Start();};
   watcher.Created+=changed;
   watcher.Deleted+=changed;
   watcher.Renamed+=(s,e)=>{folderTimer.Stop();folderTimer.Start();};
   watcher.EnableRaisingEvents=true;
  } catch {watcher=null;} // z. B. Laufwerk ohne Benachrichtigungen: F5 liest von Hand neu ein
 }

 bool InFolder(string path){return String.Equals(Path.GetDirectoryName(path),folder,StringComparison.OrdinalIgnoreCase);}

 // Im Ordner sind .rdp-Dateien dazugekommen oder verschwunden: Liste neu aufbauen.
 // Die gewählte Verbindung und eine noch nicht gespeicherte Monitorauswahl bleiben erhalten.
 void FolderChanged() {
  if(IsDisposed)return;
  List<string> files;
  try {files=Directory.GetFiles(folder,"*.rdp").Select(f=>Path.GetFullPath(f)).ToList();} catch {return;}
  var known=paths.Where(p=>InFolder(p)).ToList();
  if(files.Count==known.Count&&files.All(f=>known.Contains(f,StringComparer.OrdinalIgnoreCase)))return;
  string cur=CurrentPath();
  bool stays=cur!=null&&File.Exists(cur);
  var keepSelection=map.Selection;
  var keepMain=map.MainMonitor;
  ReloadList(cur);
  if(stays)map.SetSelection(keepSelection,keepMain);
  Status("Der Ordner hat sich geändert: "+(paths.Count==1?"1 Verbindung":paths.Count+" Verbindungen")+" in der Liste.",InfoKind.Info);
 }

 // F5: Ordner und Bildschirme von Hand neu einlesen
 protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
  if(keyData==Keys.F5) {
   ReloadMonitors(false);
   ReloadList(CurrentPath());
   if(info.Kind!=InfoKind.Warning&&info.Kind!=InfoKind.Error)
    Status("Neu eingelesen: "+(paths.Count==1?"1 Verbindung":paths.Count+" Verbindungen")+", "+(monitors.Count==1?"1 Bildschirm":monitors.Count+" Bildschirme")+".",InfoKind.Info);
   return true;
  }
  return base.ProcessCmdKey(ref msg,keyData);
 }

 protected override void Dispose(bool disposing) {
  if(disposing) {
   try {Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=OnDisplayChanged;} catch {}
   if(watcher!=null){try {watcher.EnableRaisingEvents=false;watcher.Dispose();} catch {} watcher=null;}
   folderTimer.Dispose();
   autoUpdateRetry.Dispose();
   displayTimer.Dispose();
   IdentifyOverlay.CloseAll();
  }
  base.Dispose(disposing);
 }

 // "Nur verbinden": verbindet mit der Auswahl, die gerade im Fenster zu sehen ist, ohne die Datei zu ändern.
 // Weicht sie von der gespeicherten ab, startet mstsc mit einer Kopie unter %TEMP%\RDP Pilot,
 // in die die Auswahl geschrieben wird. Sonst wird die Datei direkt gestartet (vorher Prüfung, ob die
 // gespeicherten Bildschirme gerade angeschlossen sind).
 void StartOnly() {
  if(current==null)return;
  var sel=map.Selection;
  var main=map.MainMonitor;
  if(sel.Count>0&&SelectionDiffers(sel,main)) {
   if(sel.Count>1&&!Monitors.Adjacent(sel)) {
    var answer=MessageBox.Show(this,"Die gewählten Monitore liegen nicht direkt aneinander.\nDie Remotedesktopverbindung kann das ablehnen.\n\nTrotzdem verbinden?",
     AppInfo.FullName,MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2);
    if(answer!=DialogResult.Yes)return;
   }
   if(LaunchChecked(current,f=>f.ApplyMonitors(sel,resolution.Checked,main)))
    Status("Verbunden auf Monitor "+Monitors.Describe(sel)+" – nur für diese Sitzung, die Verbindung selbst ist unverändert. Zum Merken „Speichern“.",InfoKind.Info);
   return;
  }
  string missing=current.MissingMonitors(monitors);
  if(missing!=null) {
   var answer=MessageBox.Show(this,"„"+Path.GetFileNameWithoutExtension(current.Path)+"“ ist auf "+missing+".\n"+
    "Die Sitzung startet dann auf einem anderen Bildschirm oder nicht auf allen gewünschten.\n\nTrotzdem verbinden?",
    AppInfo.FullName,MessageBoxButtons.YesNo,MessageBoxIcon.Warning);
   if(answer!=DialogResult.Yes)return;
  }
  LaunchChecked(current,null);
 }

 // Startet mstsc mit genau der Fassung, die geprüft wurde (RdpLaunch): Die Datei wird frisch eingelesen,
 // mehrdeutige Ziel- oder Gateway-Einträge werden abgelehnt, und weicht irgendeine Einstellung außer der
 // Monitorauswahl von der angezeigten Fassung (shown) ab, werden die Änderungen gezeigt und der Start muss
 // bestätigt werden.
 // modify ändert nur die Kopie, die mstsc bekommt.
 bool LaunchChecked(RdpFile shown,Action<RdpFile> modify) {
  string name=Path.GetFileNameWithoutExtension(shown.Path);
  try {
   string problem;
   var fresh=RdpLaunch.Read(shown.Path,out problem);
   if(problem!=null){Status(problem,InfoKind.Error);return false;}
   if(fresh.SettingsSignature!=shown.SettingsSignature) {
    var answer=MessageBox.Show(this,"„"+name+"“ wurde geändert, seit RDP Pilot die Verbindung angezeigt hat:\n\n"+
     shown.DescribeChanges(fresh)+"\n\nMit den geänderten Einstellungen verbinden?",
     AppInfo.FullName,MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2);
    if(answer!=DialogResult.Yes){Status("Nicht verbunden – die Verbindung wurde von einem anderen Programm geändert.",InfoKind.Warning);return false;}
   }
   RdpLaunch.Start(fresh,modify);
   return true;
  } catch(Exception ex){Status("Verbinden nicht möglich: "+ex.Message,InfoKind.Error);return false;}
 }


 // Weicht die Auswahl im Fenster (Monitore oder Hauptmonitor) von der gespeicherten ab?
 bool SelectionDiffers(IList<MonitorInfo> sel,MonitorInfo main) {
  var saved=current.CurrentMonitors(monitors);
  if(current.MissingMonitors(monitors)!=null)return true;
  if(saved.Count!=sel.Count||saved.Any(m=>!sel.Contains(m)))return true;
  if(sel.Count>1) {
   var savedMain=current.CurrentMain(monitors);
   if(savedMain!=null&&main!=null&&savedMain!=main)return true;
  }
  return false;
 }

 void Run(string file,string args) {
  try {Process.Start(file,args);}
  catch(Exception ex){Status(file+" konnte nicht gestartet werden: "+ex.Message,InfoKind.Error);}
 }

 void Status(string text,InfoKind kind){info.SetMessage(text,kind);}

 // --- Update-Prüfung über GitHub (Globale Richtlinien, Abschnitt "Updates") ----------
 // Automatisch still kurz nach dem Start, höchstens alle 20 Stunden; nicht bei Vorschaubildern und Screenshots
 protected override void OnShown(EventArgs e) {
  base.OnShown(e);
  if(live){StartWatching();RdpLaunch.CleanTemp(TimeSpan.FromMinutes(10));}
  if(UpdateCheck.Disabled||Demo.Active)return;
  UpdateSettings.Load();
  if(AfterUpdate) {
   // Neustart durch das Update-Skript: Ergebnis melden
   // Meldungen ohne Technik und ohne Pfade; Einzelheiten nur ins Fehlerprotokoll
   string result=UpdateCheck.LastResult();
   if(result!=null&&result.StartsWith("OK "))Status("Update auf Version "+result.Substring(3).Trim()+" installiert.",InfoKind.Success);
   else if(result!=null&&result.StartsWith("ZURUECK")) {
    Log.Write("Update",result);
    Status("Vorherige Version wiederhergestellt.",InfoKind.Success);
   }
   else if(result!=null) {
    Log.Write("Update",result);
    Status("Update nicht installiert – die bisherige Version läuft weiter. Einzelheiten stehen im Fehlerprotokoll.",InfoKind.Error);
   }
  }
  // bei jedem Start still prüfen, etwa 15 s nach dem Start, ohne 20-Stunden-Sperre
  autoUpdateRetry.Tick+=(s,a)=>{autoUpdateRetry.Stop();TryAutoUpdate();};
  var timer=new System.Windows.Forms.Timer{Interval=15000};
  timer.Tick+=async (s,a)=>{
   timer.Stop();
   timer.Dispose();
   await CheckForUpdates(false,true);
  };
  timer.Start();
 }

 UpdateHooks MakeUpdateHooks() {
  return new UpdateHooks{
   Status=CurrentUpdateStatus,
   Auto=()=>{UpdateSettings.Load();return UpdateSettings.Auto;},
   SetAuto=on=>{UpdateSettings.Auto=on;UpdateSettings.Save();},
   AutoInstall=()=>{UpdateSettings.Load();return UpdateSettings.AutoInstall;},
   // nach dem Einschalten sofort versuchen, wenn schon eine neue Version bekannt ist
   SetAutoInstall=on=>{UpdateSettings.AutoInstall=on;UpdateSettings.Save();if(on&&update!=null)BeginInvoke(new Action(TryAutoUpdate));},
   CheckNow=()=>CheckForUpdates(true),
   CanInstall=()=>update!=null&&!String.IsNullOrEmpty(update.ZipUrl)&&CanStartInstall(UpdateCheck.InstallBlocker()),
   Install=()=>BeginInvoke(new Action(()=>{var t=InstallUpdate();})), // erst nach dem Schließen des Info-Fensters
   OpenPage=OpenReleasePage
  };
 }

 // Stand für das Info-Fenster: Symbol, Überschrift und zweite Zeile
 UpdateStatus CurrentUpdateStatus() {
  UpdateSettings.Load();
  string last=UpdateSettings.Last==DateTime.MinValue?"":"Zuletzt geprüft "+UpdateSettings.Last.ToLocalTime().ToString("dd.MM.yyyy");
  // Fehler zuerst: auch ein fehlgeschlagenes automatisches Update steht so mit "Details" im Info-Fenster
  if(updateFailure!=null)return updateFailure;
  // automatisches Aktualisieren gescheitert: Kurzmeldung mit "Details" statt der letzten Prüfung
  if(update!=null&&installFailure!=null)return new UpdateStatus(UpdateKind.Available,"Version "+update.Version+" verfügbar","Update fehlgeschlagen.",installFailure);
  // neue Version, aber nicht (gültig) signiert: wird nicht installiert, Grund mit "Details"
  if(update!=null&&update.Problem!=null)return new UpdateStatus(UpdateKind.Available,"Version "+update.Version+" verfügbar",update.Problem,update.ProblemDetail);
  if(update!=null)return new UpdateStatus(UpdateKind.Available,"Version "+update.Version+" verfügbar",last);
  if(UpdateSettings.Last==DateTime.MinValue)return new UpdateStatus(UpdateKind.Unknown,"Noch nicht geprüft","");
  return new UpdateStatus(UpdateKind.Current,"Aktuell",last);
 }

 Task<UpdateStatus> CheckForUpdates(bool manual){return CheckForUpdates(manual,false);}

 // Neueste Version auf GitHub abfragen. Automatisch (manual = false) still: beim Start (atStart) immer,
 // sonst höchstens alle 20 Stunden. Fehler erscheinen nur im Info-Fenster.
 async Task<UpdateStatus> CheckForUpdates(bool manual,bool atStart) {
  if(UpdateCheck.Disabled||Demo.Active||updateRunning)return CurrentUpdateStatus();
  UpdateSettings.Load();
  if(!manual&&(!UpdateSettings.Auto||(!atStart&&(DateTime.UtcNow-UpdateSettings.Last).TotalHours<20)))return CurrentUpdateStatus();
  updateRunning=true;
  try {
   var release=await Task.Run(()=>UpdateCheck.Latest());
   updateFailure=null;
   UpdateSettings.Last=DateTime.UtcNow;
   UpdateSettings.Save();
   if(UpdateCheck.IsNewer(release.Version)) {
    if(update==null||update.Version!=release.Version)installFailure=null;
    update=release;
    ShowUpdateBar(true);
    // "Automatisch aktualisieren": ohne Rückfrage installieren, sobald nichts mehr läuft (nicht bei "Jetzt prüfen")
    if(!manual&&UpdateSettings.AutoInstall&&UpdateCheck.InstallBlocker()==null&&!String.IsNullOrEmpty(release.ZipUrl))
     BeginInvoke(new Action(TryAutoUpdate));
   } else {
    update=null;
    ShowUpdateBar(false);
   }
  } catch(Exception ex) {
   // "Nur Vorabversionen" ist kein Fehler: gilt als geprüft
   if(ex is UpdateCheck.PreReleaseOnlyException){UpdateSettings.Last=DateTime.UtcNow;UpdateSettings.Save();update=null;ShowUpdateBar(false);}
   updateFailure=UpdateStatus.FromError(ex); // erscheint nur im Info-Fenster
  } finally {updateRunning=false;}
  return CurrentUpdateStatus();
 }

 // Beschäftigt: ungespeicherte Monitorauswahl oder ein Dialog (Info-Fenster, Rückfrage, Datei öffnen) ist offen
 bool BusyForUpdate() {
  if(current!=null&&map.Selection.Count>0&&current.MissingMonitors(monitors)==null&&SelectionDiffers(map.Selection,map.MainMonitor))return true;
  foreach(Form f in Application.OpenForms)
   if(f!=this&&f.Visible&&f.Modal)return true;
  return !CanFocus; // modaler Systemdialog (z. B. Datei öffnen) sperrt das Hauptfenster
 }

 // "Automatisch aktualisieren": installieren, wenn nichts läuft – sonst alle 5 Minuten erneut versuchen
 void TryAutoUpdate() {
  if(IsDisposed||update==null||installing||Demo.Active||!UpdateSettings.AutoInstall||!UpdateSettings.Auto)return;
  if(UpdateCheck.InstallBlocker()!=null||String.IsNullOrEmpty(update.ZipUrl)||UpdateCheck.Busy)return;
  if(BusyForUpdate()){autoUpdateRetry.Stop();autoUpdateRetry.Start();return;}
  var t=InstallUpdate(true);
 }

 void ShowUpdateBar(bool show) {
  if(IsDisposed)return;
  show=show&&update!=null;
  if(show) {
   updateBar.Text="Neue Version "+update.Version+" verfügbar – installiert ist "+AppInfo.Version+".";
   updateBar.NewVersion=update.Version;
   string blocker=UpdateCheck.InstallBlocker();
   // nicht abgeschlossenes Update: Der Knopf bietet die Wiederherstellung an
   updateBar.Install.Enabled=CanStartInstall(blocker)&&!String.IsNullOrEmpty(update.ZipUrl);
   if(blocker==UpdateCheck.PendingText)blocker+=" „Jetzt aktualisieren“ stellt zuerst die vorherige Version wieder her.";
   tips.SetToolTip(updateBar.Install,blocker??(update.Problem!=null?"Wird nicht installiert: "+update.Problem+"\n"+update.ProblemDetail:
    String.IsNullOrEmpty(update.ZipUrl)?"Im Release ist keine passende ZIP-Datei.":
    "ZIP herunterladen, Programm beenden, EXE und Liesmich ersetzen und neu starten.\nDie Verbindungen (.rdp-Dateien) bleiben unverändert."));
   tips.SetToolTip(updateBar.Page,"Öffnet die Release-Seite auf GitHub.");
   tips.SetToolTip(updateBar.Later,"Blendet den Hinweis bis zum nächsten Start aus.");
   tips.SetToolTip(updateBar,String.IsNullOrEmpty(update.Notes)?"Die Änderungen stehen auf der Release-Seite.":"Änderungen:\r\n"+update.Notes);
  }
  if(show==updateBar.Visible)return;
  int barHeight=Ui.Px(52)+Ui.Px(12);
  updateBar.Visible=show;
  layout.RowStyles[1].Height=show?barHeight:0;
  // Fenster um die Leiste vergrößern bzw. wieder verkleinern, soweit der Bildschirm reicht
  if(WindowState==FormWindowState.Normal) {
   var wa=Screen.FromControl(this).WorkingArea;
   if(show){int grow=Math.Min(barHeight,wa.Bottom-Bottom);if(grow>0)Height+=grow;}
   else Height=Math.Max(MinimumSize.Height,Height-barHeight);
  }
 }

 // Installieren möglich: kein Hindernis oder nur ein nicht abgeschlossenes Update (dann zuerst Wiederherstellung)
 static bool CanStartInstall(string blocker){return blocker==null||blocker==UpdateCheck.PendingText;}

 void OpenReleasePage() {
  UpdateCheck.OpenPage(update!=null?update.Url:null);
 }

 Task InstallUpdate(){return InstallUpdate(false);}

 async Task InstallUpdate(bool automatic) {
  if(update==null||installing)return;
  var info=update;
  // nur ein Update zur Zeit, auch über mehrere RDP-Pilot-Fenster und Konten hinweg
  if(UpdateCheck.Busy) {
   if(!automatic)MessageBox.Show(this,UpdateCheck.Running,AppInfo.FullName,MessageBoxButtons.OK,MessageBoxIcon.Information);
   return;
  }
  // nicht abgeschlossenes Update: erst die vorherige Version wiederherstellen (nur von Hand, mit Rückfrage)
  if(UpdateCheck.InstallBlocker()==UpdateCheck.PendingText) {
   if(!automatic&&UpdateCheck.OfferRepair(this)){installing=true;Close();}
   return;
  }
  if(!automatic) {
   string text=AppInfo.FullName+" wird beendet, auf Version "+info.Version+" aktualisiert und neu gestartet.\r\n"+
    "Einstellungen und Verbindungen bleiben erhalten.";
   if(MessageBox.Show(this,text+"\r\n\r\nJetzt aktualisieren?",AppInfo.FullName,MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
  }
  installing=true;
  foreach(var b in new[]{updateBar.Install,updateBar.Page,updateBar.Later})b.Enabled=false;
  string barText=updateBar.Text;
  try {
   updateBar.NewVersion=null; // während des Downloads einzeilig
   updateBar.Text="Lade Version "+info.Version+" …";
   await UpdateCheck.Install(info,percent=>BeginInvoke(new Action(()=>updateBar.Text="Lade Version "+info.Version+" … "+percent+" %")));
   Close(); // das Skript wartet auf das Programmende, ersetzt die EXE und startet neu
  } catch(Exception ex) {
   // automatisch: keine Meldung – die Hinweisleiste bleibt, die Kurzmeldung mit "Details" steht im Info-Fenster
   Log.Write(automatic?"Update automatisch installieren":"Update installieren",ex);
   if(automatic)installFailure=(ex.Data["Detail"] as string)??ex.Message;
   else {
    string detail=ex.Data.Contains("Detail")?ex.Data["Detail"] as string:null;
    MessageBox.Show(this,"Das Update ist fehlgeschlagen: "+ex.Message+(String.IsNullOrEmpty(detail)?"":"\n\n"+detail),AppInfo.FullName,MessageBoxButtons.OK,MessageBoxIcon.Warning);
   }
   updateBar.Text=barText;
   updateBar.NewVersion=info.Version;
   updateBar.Page.Enabled=true;
   updateBar.Later.Enabled=true;
   updateBar.Install.Enabled=CanStartInstall(UpdateCheck.InstallBlocker());
   installing=false;
  }
 }

 // Für die Demo-Screenshots: Verbindungsliste aufklappen
 internal ContextMenuStrip OpenConnectionListForDemo(){return select.OpenMenuForDemo();}
 internal void SetConnectionListOpenForDemo(bool value){select.SetOpenForDemo(value);}

 // --- RDP-Liste ---------------------------------------------------------------
 string CurrentPath(){return select.SelectedIndex>=0&&select.SelectedIndex<paths.Count?paths[select.SelectedIndex]:null;}

 static string DescribeFile(string path) {
  try {return Describe(RdpFile.Load(path));} catch {return "";}
 }
 static string Describe(RdpFile f) {
  string server=f.Server,user=f.User;
  if(server.Length>0&&user.Length>0)return server+"  ·  "+user;
  return server+user;
 }

 void AddPath(string path,bool selectIt){AddPath(path,selectIt,false);}
 // lazy: Netzwerkpfad nicht lesen, bevor er ausgewählt wird
 void AddPath(string path,bool selectIt,bool lazy) {
  string full=Path.GetFullPath(path);
  if(!RdpFile.IsRdp(full)){Status("Keine RDP-Datei (.rdp): "+Path.GetFileName(full),InfoKind.Warning);return;}
  int index=paths.FindIndex(p=>String.Equals(p,full,StringComparison.OrdinalIgnoreCase));
  if(index<0) {
   paths.Add(full);
   string title=Path.GetFileNameWithoutExtension(full);
   if(!InFolder(full))title=title+"  ("+Path.GetDirectoryName(full)+")";
   select.Items.Add(new ModernSelect.Item{Title=title,Detail=lazy&&!selectIt?"Netzwerkpfad – wird beim Auswählen gelesen":DescribeFile(full),Tag=full});
   index=paths.Count-1;
   if(!InFolder(full))RememberFile(full);
  }
  UpdateCount();
  if(selectIt){if(select.SelectedIndex==index)LoadCurrent();else select.SelectedIndex=index;}
 }

 // Verbindung aus einem anderen Ordner merken, damit sie beim nächsten Start wieder in der Liste steht
 void RememberFile(string full) {
  if(!live)return;
  UpdateSettings.Load();
  if(UpdateSettings.Files.Contains(full,StringComparer.OrdinalIgnoreCase))return;
  UpdateSettings.Files.Add(full);
  // nicht mehr vorhandene Dateien entfernen, Liste begrenzen
  UpdateSettings.Files.RemoveAll(f=>!RdpFile.IsNetworkPath(f)&&!File.Exists(f)); // Netzwerkpfade nicht prüfen
  while(UpdateSettings.Files.Count>UpdateSettings.MaxFiles)UpdateSettings.Files.RemoveAt(0);
  UpdateSettings.Save();
 }

 // Zuletzt gewählte Verbindung merken
 void RememberLast(string full) {
  if(!live||full==null)return;
  UpdateSettings.Load();
  if(String.Equals(UpdateSettings.LastFile,full,StringComparison.OrdinalIgnoreCase))return;
  UpdateSettings.LastFile=full;
  UpdateSettings.Save();
 }

 void ReloadList(string keep) {
  // über "Andere Datei …" hinzugefügte Verbindungen aus anderen Ordnern bleiben in der Liste
  var external=paths.Where(p=>!InFolder(p)&&(RdpFile.IsNetworkPath(p)||File.Exists(p))).ToList();
  paths.Clear();select.Clear();
  try {
   foreach(string f in Directory.GetFiles(folder,"*.rdp").OrderBy(f=>Path.GetFileName(f),StringComparer.CurrentCultureIgnoreCase))AddPath(f,false);
  } catch(Exception ex){Status("Ordner konnte nicht gelesen werden: "+ex.Message,InfoKind.Error);}
  foreach(string f in external)AddPath(f,false,RdpFile.IsNetworkPath(f));
  if(keep!=null&&File.Exists(keep))AddPath(keep,true);
  else {
   // von selbst keine gemerkte Verbindung im Netz auswählen (die erst, wenn man sie anklickt);
   // der Programmordner selbst ist ohnehin schon eingelesen
   int first=paths.FindIndex(p=>InFolder(p)||!RdpFile.IsNetworkPath(p));
   if(first>=0)select.SelectedIndex=first;
   else LoadCurrent();
  }
  UpdateCount();
 }

 void UpdateCount() {
  int inFolder=paths.Count(p=>InFolder(p));
  connectionInfo.Text=inFolder==1?"1 Verbindung im Ordner":inFolder+" Verbindungen im Ordner";
 }

 void Browse() {
  using(var dialog=new OpenFileDialog()) {
   dialog.Filter="Remotedesktop-Verbindungen (*.rdp)|*.rdp";
   string cur=CurrentPath();
   dialog.InitialDirectory=cur!=null?Path.GetDirectoryName(cur):folder;
   if(dialog.ShowDialog(this)==DialogResult.OK)AddPath(dialog.FileName,true);
  }
 }

 void LoadCurrent() {
  string path=CurrentPath();
  RememberLast(path);
  current=null;
  if(path!=null) {
   try {
    current=RdpFile.Load(path);
    var item=select.SelectedItem;
    if(item!=null){item.Detail=Describe(current);select.Invalidate();}
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
  // Die Verbindung ist auf Bildschirme eingestellt, die es gerade nicht gibt (z. B. unterwegs ohne Dockingstation)
  string missing=current!=null?current.MissingMonitors(monitors):null;
  if(missing!=null)
   Status("„"+Path.GetFileNameWithoutExtension(current.Path)+"“ ist auf "+missing+". Bildschirme neu auswählen und speichern – oder so lassen, bis sie wieder angeschlossen sind.",InfoKind.Warning);
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
   var answer=MessageBox.Show(this,"Die gewählten Monitore liegen nicht direkt aneinander.\nDie Remotedesktopverbindung kann das ablehnen.\n\nTrotzdem speichern?",AppInfo.FullName,MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2);
   if(answer!=DialogResult.Yes)return;
  }
  bool backupCreated=false;
  // Fassung, deren Ziel angezeigt wurde: Daran misst "Speichern & verbinden" später, ob sich das Ziel geändert hat
  RdpFile shown=current.CopyTo(current.Path);
  try {
   current.ApplyMonitors(sel,resolution.Checked,map.MainMonitor);
   backupCreated=current.Save(backupCheck.Checked);
  } catch(RdpFile.ChangedOnDiskException) {
   // Die Datei wurde inzwischen außerhalb geändert (z. B. in der Remotedesktopverbindung): nichts überschreiben,
   // sondern auf Wunsch die aktuelle Fassung neu laden und nur die Monitorauswahl übernehmen
   var answer=MessageBox.Show(this,"„"+Path.GetFileName(current.Path)+"“ wurde seit dem Laden von einem anderen Programm geändert.\n\n"+
    "Die aktuelle Fassung neu laden und nur die Monitorauswahl übernehmen?",AppInfo.FullName,MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2);
   if(answer!=DialogResult.Yes){Status("Nicht gespeichert – die Datei wurde von einem anderen Programm geändert.",InfoKind.Warning);return;}
   try {
    var fresh=RdpFile.Load(current.Path);
    fresh.ApplyMonitors(sel,resolution.Checked,map.MainMonitor);
    backupCreated=fresh.Save(backupCheck.Checked);
    current=fresh;
   } catch(Exception ex) {
    Status("Fehler beim Speichern: "+ex.Message,InfoKind.Error);
    return;
   }
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
   if(LaunchChecked(shown,null))Close();
  }
 }

 void RestoreBackup() {
  if(current==null)return;
  string backup=current.Path+".bak";
  if(!File.Exists(backup))return;
  var answer=MessageBox.Show(this,"Die aktuelle Datei wird durch die Sicherung ersetzt:\n"+Path.GetFileName(backup)+"\n\nUrsprüngliche Datei wiederherstellen?",AppInfo.FullName,MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2);
  if(answer!=DialogResult.Yes)return;
  try {
   // über eine vollständig geschriebene Hilfsdatei; nur, wenn die Datei noch der angezeigten Fassung entspricht
   RdpFile.ReplaceWith(current.Path,backup,current);
   File.Delete(backup);
  } catch(RdpFile.ChangedOnDiskException) {
   Status("Nicht wiederhergestellt – „"+Path.GetFileName(current.Path)+"“ wurde inzwischen von einem anderen Programm geändert. Bitte neu laden (F5).",InfoKind.Warning);
   return;
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

// Sehr dezenter Textlink (kaum sichtbar), z. B. für Support-Funktionen
public class FaintLink:Control {
 bool hover;
 const TextFormatFlags Flags=TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding;
 public FaintLink(string text) {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
  Text=text;Font=Ui.SmallFont;Cursor=Cursors.Hand;TabStop=false;Margin=new Padding(0);
  Size=new Size(TextRenderer.MeasureText(text,Font,Size.Empty,Flags).Width+Ui.Px(20),Ui.Px(24));
 }
 protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
 protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;Invalidate();}
 protected override void OnPaint(PaintEventArgs e) {
  Color back=Parent!=null?Parent.BackColor:Ui.Surface;
  e.Graphics.Clear(back);
  // Ruhezustand fast in Hintergrundfarbe, bei Mouseover etwas deutlicher
  Color fore=Ui.Blend(back,Ui.Muted,hover?0.6f:0.18f);
  TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,fore,Flags|TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
 }
}

}