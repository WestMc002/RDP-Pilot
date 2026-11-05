// reitzner RDP Pilot – Update-Prüfung über die GitHub-Releases und Selbst-Update
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RDPilot {

// Neueste Veröffentlichung auf GitHub
sealed class UpdateInfo {
 public string Version; // z. B. 1.16.2026.1110 (ohne "v")
 public string Url;     // Release-Seite auf GitHub
 public string ZipUrl;  // angehängtes ZIP (EXE und Liesmich)
 public string Notes;   // Abschnitt "Änderungen" als Klartext (Tooltip der Hinweisleiste)
}

// Einstellungen der Update-Prüfung: %LOCALAPPDATA%\reitzner RDP Pilot\Einstellungen.ini
// (automatisch prüfen ein/aus, Zeitpunkt der letzten Prüfung in UTC nach ISO 8601)
static class UpdateSettings {
 public static bool Auto=true;
 public static DateTime Last=DateTime.MinValue; // UTC
 static bool loaded;

 static string FilePath {
  get {return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppInfo.FullName),"Einstellungen.ini");}
 }

 public static void Load() {
  if(loaded)return;
  loaded=true;
  try {
   if(!File.Exists(FilePath))return;
   foreach(string line in File.ReadAllLines(FilePath,Encoding.UTF8)) {
    int i=line.IndexOf('=');
    if(i<1)continue;
    string key=line.Substring(0,i).Trim(),value=line.Substring(i+1).Trim();
    if(key.Equals("UpdateAuto",StringComparison.OrdinalIgnoreCase))Auto=value!="0";
    else if(key.Equals("UpdateLast",StringComparison.OrdinalIgnoreCase)) {
     DateTime d;
     if(DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal|DateTimeStyles.AssumeUniversal,out d))Last=d;
    }
   }
  } catch {}
 }

 public static void Save() {
  try {
   Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
   var sb=new StringBuilder();
   sb.Append("UpdateAuto=").Append(Auto?"1":"0").Append("\r\n");
   if(Last!=DateTime.MinValue)sb.Append("UpdateLast=").Append(Last.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture)).Append("\r\n");
   File.WriteAllText(FilePath,sb.ToString(),new UTF8Encoding(false));
  } catch {}
 }
}

// Fragt nur die öffentliche GitHub-Schnittstelle ab (ohne Anmeldung, es werden keine Daten übertragen).
// Selbst-Update: ZIP laden, Hilfsskript starten, Programm beenden; das Skript ersetzt EXE und Liesmich und startet neu.
static class UpdateCheck {
 public const string Repo="WestMc002/reitzner-RDP-Pilot";
 public const string ReleasesPage="https://github.com/"+Repo+"/releases";
 const string Api="https://api.github.com/repos/"+Repo+"/releases/latest";

 // Keine Prüfung bei Vorschaubildern und Screenshots
 public static bool Disabled;

 // Arbeitsordner für Download und Hilfsskript
 public static string Folder {get {return Path.Combine(Path.GetTempPath(),"reitzner-RDP-Pilot-Update");}}
 public static string LogFile {get {return Path.Combine(Folder,"Update.log");}}

 static string UserAgent {get {return "reitzner-RDP-Pilot/"+AppInfo.Version;}} // GitHub verlangt einen User-Agent

 static void PrepareTls() {
  try {ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;} catch {} // TLS 1.2
 }

 // Neueste Veröffentlichung holen. Wirft bei Netz- oder Formatfehlern (mit verständlicher Meldung).
 public static UpdateInfo Latest() {
  PrepareTls();
  var req=(HttpWebRequest)WebRequest.Create(Api);
  req.Method="GET";
  req.Timeout=15000;
  req.ReadWriteTimeout=15000;
  req.Accept="application/vnd.github+json";
  req.UserAgent=UserAgent;
  req.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
  if(req.Proxy!=null)req.Proxy.Credentials=CredentialCache.DefaultCredentials;
  string text;
  try {
   using(var resp=(HttpWebResponse)req.GetResponse())
   using(var r=new StreamReader(resp.GetResponseStream(),Encoding.UTF8))text=r.ReadToEnd();
  } catch(WebException ex) {
   var http=ex.Response as HttpWebResponse;
   if(http!=null&&http.StatusCode==HttpStatusCode.NotFound)throw new Exception("Auf GitHub ist noch keine Version veröffentlicht.");
   if(http!=null&&(int)http.StatusCode==403)throw new Exception("GitHub hat die Anfrage abgelehnt (zu viele Anfragen). Später erneut versuchen.");
   throw new Exception("GitHub nicht erreichbar: "+ex.Message);
  }

  var obj=MiniJson.Parse(text) as Dictionary<string,object>;
  if(obj==null)throw new Exception("Unerwartete Antwort von GitHub.");
  string tag=obj.ContainsKey("tag_name")?obj["tag_name"] as string:null;
  if(String.IsNullOrEmpty(tag))throw new Exception("Antwort von GitHub ohne Versionsnummer.");
  string version=tag.TrimStart('v','V').Trim();
  if(Parse(version)==null)throw new Exception("Unbekannte Versionsnummer: "+tag);

  var info=new UpdateInfo{Version=version,Url=ReleasesPage};
  string url=obj.ContainsKey("html_url")?obj["html_url"] as string:null;
  // nur Seiten und Downloads des eigenen Repositorys verwenden
  if(url!=null&&url.StartsWith("https://github.com/"+Repo+"/",StringComparison.OrdinalIgnoreCase))info.Url=url;
  var assets=obj.ContainsKey("assets")?obj["assets"] as List<object>:null;
  if(assets!=null)
   foreach(object o in assets) {
    var a=o as Dictionary<string,object>;
    if(a==null||!a.ContainsKey("name")||!a.ContainsKey("browser_download_url"))continue;
    string name=a["name"] as string,dl=a["browser_download_url"] as string;
    if(name!=null&&dl!=null&&name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)
     &&dl.StartsWith("https://github.com/"+Repo+"/releases/download/",StringComparison.OrdinalIgnoreCase)) {
     info.ZipUrl=dl;
     break;
    }
   }
  info.Notes=ExtractNotes(obj.ContainsKey("body")?obj["body"] as string:null);
  return info;
 }

 // true, wenn die Version neuer als die laufende ist
 public static bool IsNewer(string version) {
  Version a=Parse(version),b=Parse(AppInfo.Version);
  return a!=null&&b!=null&&a>b;
 }

 static Version Parse(string v) {
  Version r;
  return System.Version.TryParse(v??"",out r)?r:null;
 }

 // Im Projektordner (mit Erstellen.bat) wird nicht automatisch ersetzt: dort entsteht die EXE beim Bauen
 public static string InstallBlocker() {
  if(File.Exists(Path.Combine(Application.StartupPath,"Erstellen.bat")))
   return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
  return null;
 }

 static bool CanWrite(string dir) {
  try {
   string probe=Path.Combine(dir,"~update-test.tmp");
   File.WriteAllText(probe,"");
   File.Delete(probe);
   return true;
  } catch {return false;}
 }

 // ZIP herunterladen, Hilfsskript schreiben und starten. Das Skript wartet, bis dieses Programm beendet ist,
 // ersetzt EXE und Liesmich und startet das Programm neu. Danach muss der Aufrufer das Programm beenden.
 public static async Task Install(UpdateInfo release,Action<int> progress) {
  if(String.IsNullOrEmpty(release.ZipUrl))throw new Exception("Im Release ist keine ZIP-Datei angehängt.");
  string blocker=InstallBlocker();
  if(blocker!=null)throw new Exception(blocker);
  PrepareTls();
  Directory.CreateDirectory(Folder);
  string zip=Path.Combine(Folder,"reitzner-RDP-Pilot-"+release.Version+".zip");
  using(var client=new WebClient()) {
   client.Headers[HttpRequestHeader.UserAgent]=UserAgent;
   if(client.Proxy!=null)client.Proxy.Credentials=CredentialCache.DefaultCredentials;
   if(progress!=null)client.DownloadProgressChanged+=(s,e)=>progress(e.ProgressPercentage);
   await client.DownloadFileTaskAsync(new Uri(release.ZipUrl),zip);
  }
  if(new FileInfo(zip).Length<1024)throw new Exception("Die heruntergeladene Datei ist unvollständig.");
  string script=Path.Combine(Folder,"Update.ps1");
  File.WriteAllText(script,Script,new UTF8Encoding(true));
  try {File.Delete(LogFile);} catch {}
  string exe=Application.ExecutablePath;
  string target=Path.GetDirectoryName(exe);
  var start=new ProcessStartInfo("powershell.exe",
   "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""+script+"\" -ProcessId "+Process.GetCurrentProcess().Id+
   " -Zip \""+zip+"\" -Exe \""+exe+"\" -Version \""+release.Version+"\""){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden};
  // Programmordner ohne Schreibrechte (z. B. unter Programme): Hilfsskript mit Administratorrechten starten
  if(!CanWrite(target))start.Verb="runas";
  try {Process.Start(start);}
  catch(System.ComponentModel.Win32Exception ex) {
   if(ex.NativeErrorCode==1223)throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
   throw;
  }
 }

 // Ergebnis des letzten Updates ("OK x" bzw. "FEHLER …") nach dem Neustart mit --nach-update
 public static string LastResult() {
  try {return File.Exists(LogFile)?File.ReadAllText(LogFile,Encoding.UTF8).Trim():null;}
  catch {return null;}
 }

 public static void OpenPage(string url) {
  try {Process.Start(new ProcessStartInfo(String.IsNullOrEmpty(url)?ReleasesPage:url){UseShellExecute=true});} catch {}
 }

 const string Script=@"# reitzner RDP Pilot – Update (wird vom Programm erzeugt)
param([int]$ProcessId,[string]$Zip,[string]$Exe,[string]$Version)
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'Update.log'
$name = Split-Path $Exe -Leaf
$target = Split-Path $Exe -Parent
try {
  try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
  Start-Sleep -Milliseconds 500
  $dir = Join-Path $PSScriptRoot 'Neu'
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  Expand-Archive -Path $Zip -DestinationPath $dir -Force
  $new = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -eq $name } | Select-Object -First 1
  if (-not $new) { throw ('Im ZIP fehlt ' + $name) }
  $old = $Exe + '.alt'
  Copy-Item $Exe $old -Force
  try {
    Copy-Item $new.FullName $Exe -Force
    Get-ChildItem $new.DirectoryName -File | Where-Object { $_.Name -ne $name } | Copy-Item -Destination $target -Force
  } catch {
    Copy-Item $old $Exe -Force
    throw
  }
  Remove-Item $old -Force
  Remove-Item $dir -Recurse -Force
  Remove-Item $Zip -Force
  ('OK ' + $Version) | Out-File $log -Encoding utf8
} catch {
  ('FEHLER ' + $_.Exception.Message) | Out-File $log -Encoding utf8
}
Start-Process -FilePath $Exe -ArgumentList '--nach-update'
";

 // Aus dem Release-Text nur den Abschnitt "## Änderungen", Punkte als "•", ohne Markdown
 internal static string ExtractNotes(string body) {
  if(String.IsNullOrEmpty(body))return "";
  var lines=body.Replace("\r\n","\n").Split('\n');
  var sb=new StringBuilder();
  bool inside=false,any=false;
  foreach(string raw in lines) {
   string l=raw.Trim();
   if(l.StartsWith("#")) {
    if(inside)break;
    inside=l.TrimStart('#').Trim().StartsWith("Änderungen",StringComparison.OrdinalIgnoreCase);
    continue;
   }
   if(!inside||l.Length==0)continue;
   if(l.StartsWith("- ")||l.StartsWith("* "))l="•  "+l.Substring(2).Trim();
   l=l.Replace("**","").Replace("`","");
   if(any)sb.Append("\r\n");
   sb.Append(l);
   any=true;
  }
  return sb.ToString();
 }
}

enum UpdateKind {Unknown,Current,Available,Failed,Checking}

// Stand der Update-Prüfung für das Info-Fenster
sealed class UpdateStatus {
 public readonly UpdateKind Kind;
 public readonly string Title,Detail;
 public UpdateStatus(UpdateKind kind,string title,string detail){Kind=kind;Title=title;Detail=detail??"";}
}

// Verbindung des Info-Fensters zur Update-Prüfung im Hauptfenster
sealed class UpdateHooks {
 public Func<UpdateStatus> Status;
 public Func<bool> Auto;
 public Action<bool> SetAuto;
 public Func<Task<UpdateStatus>> CheckNow;
 public Func<bool> CanInstall;
 public Action Install;
 public Action OpenPage;
}

// Symbole aus Segoe Fluent Icons, nach den tatsächlich gezeichneten Pixeln zentriert
static class Glyphs {
 public const string Completed="",Download="",Warning="",Info="",Sync="",Refresh="",Link="";

 public static Bitmap Render(string glyph,Color color,int size) {
  if(String.IsNullOrEmpty(glyph))return null;
  try {
   using(var font=new Font(Ui.IconFont!=null?Ui.IconFont.FontFamily.Name:"Segoe MDL2 Assets",size,GraphicsUnit.Pixel)) {
    int big=size*2;
    using(var work=new Bitmap(big,big,PixelFormat.Format32bppArgb)) {
     using(var g=Graphics.FromImage(work)) {
      g.Clear(Color.Transparent);
      g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      using(var b=new SolidBrush(color))
      using(var sf=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
       g.DrawString(glyph,font,b,new RectangleF(0,0,big,big),sf);
     }
     // sichtbaren Bereich ermitteln und mittig in das Ergebnis setzen
     int minX=big,minY=big,maxX=-1,maxY=-1;
     var data=work.LockBits(new Rectangle(0,0,big,big),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
     var px=new byte[data.Stride*big];
     Marshal.Copy(data.Scan0,px,0,px.Length);
     work.UnlockBits(data);
     for(int y=0;y<big;y++)for(int x=0;x<big;x++) {
      if(px[y*data.Stride+x*4+3]<8)continue;
      if(x<minX)minX=x;if(x>maxX)maxX=x;if(y<minY)minY=y;if(y>maxY)maxY=y;
     }
     var result=new Bitmap(size,size,PixelFormat.Format32bppArgb);
     if(maxX<0)return result;
     int w=maxX-minX+1,h=maxY-minY+1;
     using(var g=Graphics.FromImage(result)) {
      g.Clear(Color.Transparent);
      g.DrawImage(work,new Rectangle((size-w)/2,(size-h)/2,w,h),new Rectangle(minX,minY,w,h),GraphicsUnit.Pixel);
     }
     return result;
    }
   }
  } catch {return null;}
 }
}

// Symbol des Update-Stands (11 pt)
class StatusGlyph:Control {
 string glyph;
 Color color;
 public StatusGlyph() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
 }
 public void Set(string g,Color c){glyph=g;color=c;Invalidate();}
 protected override void OnPaint(PaintEventArgs e) {
  e.Graphics.Clear(Parent!=null?Parent.BackColor:Ui.Surface);
  int gs=(int)Math.Round(11f*96f/72f*Ui.S);
  using(var img=Glyphs.Render(glyph,color,gs))
   if(img!=null)e.Graphics.DrawImage(img,(Width-gs)/2,(Height-gs)/2,gs,gs);
 }
}

// Schalter (Windows-11-Stil)
class ToggleSwitch:Control {
 bool on,hover;
 public event EventHandler CheckedChanged;
 public ToggleSwitch() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
  Cursor=Cursors.Hand;
  Size=new Size(Ui.Px(40),Ui.Px(28));
 }
 public bool Checked {
  get{return on;}
  set{if(on==value)return;on=value;Invalidate();if(CheckedChanged!=null)CheckedChanged(this,EventArgs.Empty);}
 }
 protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
 protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;Invalidate();}
 protected override void OnMouseClick(MouseEventArgs e){base.OnMouseClick(e);if(e.Button==MouseButtons.Left)Checked=!Checked;}
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(Parent!=null?Parent.BackColor:Ui.Surface);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  int sw=Ui.Px(36),sh=Ui.Px(18);
  var r=new RectangleF(Width-sw-1.5f,(Height-sh)/2f,sw,sh);
  using(var p=Ui.Round(r,r.Height/2)) {
   if(on) using(var b=new SolidBrush(hover?Ui.PrimaryHover:Ui.Primary))g.FillPath(b,p);
   else {
    using(var b=new SolidBrush(hover?Ui.SurfaceHover:Ui.Surface))g.FillPath(b,p);
    using(var pen=new Pen(Ui.Muted,Math.Max(1f,Ui.S)))g.DrawPath(pen,p);
   }
  }
  float kd=(on?12:10)*Ui.S*(hover?1.1f:1f);
  float kx=on?r.Right-3.5f*Ui.S-kd:r.X+4*Ui.S;
  using(var b=new SolidBrush(on?Ui.OnPrimary:Ui.Muted))g.FillEllipse(b,kx,r.Y+(r.Height-kd)/2,kd,kd);
 }
}

// Hinweisleiste oben im Hauptfenster: neue Version mit "Jetzt aktualisieren", "Release-Seite" und "Später"
class UpdateBar:Control {
 public readonly ModernButton Install,Page,Later;
 public UpdateBar() {
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
  BackColor=Ui.Bg;
  Font=Ui.UiSemibold;
  Install=new ModernButton("Jetzt aktualisieren",ButtonKind.Primary,Glyphs.Download);
  Page=new ModernButton("Release-Seite",ButtonKind.Secondary,Glyphs.Link);
  Later=new ModernButton("Später",ButtonKind.Secondary,null);
  foreach(var b in new[]{Install,Page,Later}) {
   b.Margin=Padding.Empty;
   b.Height=Ui.Px(32);
   Controls.Add(b);
  }
 }
 protected override void OnTextChanged(EventArgs e){base.OnTextChanged(e);Invalidate();}
 protected override void OnLayout(LayoutEventArgs e) {
  base.OnLayout(e);
  if(Install==null||Page==null||Later==null)return;
  int x=Width-Ui.Px(12);
  foreach(var b in new[]{Later,Page,Install}) {
   x-=b.Width;
   b.Location=new Point(x,(Height-b.Height)/2);
   x-=Ui.Px(8);
  }
 }
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  Color back=Parent!=null?Parent.BackColor:Ui.Bg;
  g.Clear(back);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  using(var p=Ui.Round(new RectangleF(0.5f,0.5f,Width-1.5f,Height-1.5f),Ui.Px(8)))
  using(var b=new SolidBrush(Ui.Blend(back,Ui.Accent,0.14f)))
  using(var pen=new Pen(Ui.Blend(back,Ui.Accent,0.45f))) {g.FillPath(b,p);g.DrawPath(pen,p);}
  int x=Ui.Px(16),gs=(int)Math.Round(11f*96f/72f*Ui.S);
  using(var img=Glyphs.Render(Glyphs.Download,Ui.Accent,gs))
   if(img!=null)g.DrawImage(img,x,(Height-gs)/2,gs,gs);
  x+=Ui.Px(34);
  int right=(Install!=null?Install.Left:Width)-Ui.Px(12);
  TextRenderer.DrawText(g,Text,Font,new Rectangle(x,0,Math.Max(10,right-x),Height),Ui.TextColor,
   TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
 }
}

// Kleiner JSON-Leser (Objekte, Listen, Texte, Zahlen, true/false/null) für die GitHub-Antwort
static class MiniJson {
 public static object Parse(string s) {
  int i=0;
  object v=Value(s,ref i);
  Skip(s,ref i);
  if(i!=s.Length)throw new InvalidDataException("JSON: unerwartete Zeichen am Ende.");
  return v;
 }
 static void Skip(string s,ref int i){while(i<s.Length&&Char.IsWhiteSpace(s[i]))i++;}
 static object Value(string s,ref int i) {
  Skip(s,ref i);
  if(i>=s.Length)throw new InvalidDataException("JSON: unerwartetes Ende.");
  char c=s[i];
  if(c=='{') {
   var d=new Dictionary<string,object>();
   i++;Skip(s,ref i);
   if(i<s.Length&&s[i]=='}'){i++;return d;}
   while(true) {
    Skip(s,ref i);
    string key=Str(s,ref i);
    Skip(s,ref i);
    Expect(s,ref i,':');
    d[key]=Value(s,ref i);
    Skip(s,ref i);
    if(i<s.Length&&s[i]==','){i++;continue;}
    Expect(s,ref i,'}');
    return d;
   }
  }
  if(c=='[') {
   var l=new List<object>();
   i++;Skip(s,ref i);
   if(i<s.Length&&s[i]==']'){i++;return l;}
   while(true) {
    l.Add(Value(s,ref i));
    Skip(s,ref i);
    if(i<s.Length&&s[i]==','){i++;continue;}
    Expect(s,ref i,']');
    return l;
   }
  }
  if(c=='"')return Str(s,ref i);
  if(Word(s,ref i,"true"))return true;
  if(Word(s,ref i,"false"))return false;
  if(Word(s,ref i,"null"))return null;
  int start=i;
  while(i<s.Length&&"+-0123456789.eE".IndexOf(s[i])>=0)i++;
  if(i==start)throw new InvalidDataException("JSON: unerwartetes Zeichen '"+c+"'.");
  double num;
  Double.TryParse(s.Substring(start,i-start),NumberStyles.Float,CultureInfo.InvariantCulture,out num);
  return num;
 }
 static bool Word(string s,ref int i,string w) {
  if(String.CompareOrdinal(s,i,w,0,w.Length)!=0)return false;
  i+=w.Length;
  return true;
 }
 static void Expect(string s,ref int i,char c) {
  if(i>=s.Length||s[i]!=c)throw new InvalidDataException("JSON: '"+c+"' erwartet.");
  i++;
 }
 static string Str(string s,ref int i) {
  Expect(s,ref i,'"');
  var sb=new StringBuilder();
  while(true) {
   if(i>=s.Length)throw new InvalidDataException("JSON: Text ohne Ende.");
   char c=s[i++];
   if(c=='"')return sb.ToString();
   if(c!='\\'){sb.Append(c);continue;}
   if(i>=s.Length)throw new InvalidDataException("JSON: Text ohne Ende.");
   char e=s[i++];
   switch(e) {
    case 'n':sb.Append('\n');break;
    case 'r':sb.Append('\r');break;
    case 't':sb.Append('\t');break;
    case 'b':sb.Append('\b');break;
    case 'f':sb.Append('\f');break;
    case 'u':
     if(i+4>s.Length)throw new InvalidDataException("JSON: \\u unvollständig.");
     sb.Append((char)Int32.Parse(s.Substring(i,4),NumberStyles.HexNumber,CultureInfo.InvariantCulture));
     i+=4;
     break;
    default:sb.Append(e);break; // \" \\ \/
   }
  }
 }
}

}