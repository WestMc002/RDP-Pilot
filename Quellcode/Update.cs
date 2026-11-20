// RDP Pilot – Update-Prüfung über die GitHub-Releases und Selbst-Update
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
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
 public string ZipSha256; // Prüfsumme des ZIP laut GitHub ("digest"); ohne sie wird nicht installiert
}

// Einstellungen: %LOCALAPPDATA%\RDP Pilot\Einstellungen.ini
// - Update-Prüfung: automatisch prüfen (Standard an), automatisch aktualisieren (Standard aus),
//   Zeitpunkt der letzten Prüfung in UTC nach ISO 8601
// - Verbindungen außerhalb des Programmordners ("Andere Datei …", Drag & Drop) und die zuletzt gewählte Verbindung
static class UpdateSettings {
 public static bool Auto=true;
 public static bool AutoInstall=false; // neue Version ohne Rückfrage installieren
 public static DateTime Last=DateTime.MinValue; // UTC
 public static readonly List<string> Files=new List<string>(); // gemerkte .rdp-Dateien aus anderen Ordnern
 public static string LastFile;                                // zuletzt gewählte Verbindung
 public const int MaxFiles=30;
 static bool loaded;

 static string FilePath {
  get {return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppInfo.FullName),"Einstellungen.ini");}
 }

 // Bis 1.44 stand vor dem Programmnamen noch ein Zusatz ("<Zusatz> RDP Pilot"):
 // Einstellungen einmalig aus dem alten Ordner übernehmen
 static void MoveOldSettings() {
  try {
   if(File.Exists(FilePath))return;
   string root=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
   foreach(string dir in Directory.GetDirectories(root,"* "+AppInfo.Product)) {
    string old=Path.Combine(dir,"Einstellungen.ini");
    if(!File.Exists(old))continue;
    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
    File.Copy(old,FilePath);
    return;
   }
  } catch {}
 }

 public static void Load() {
  if(loaded)return;
  loaded=true;
  try {
   MoveOldSettings();
   if(!File.Exists(FilePath))return;
   foreach(string line in File.ReadAllLines(FilePath,Encoding.UTF8)) {
    int i=line.IndexOf('=');
    if(i<1)continue;
    string key=line.Substring(0,i).Trim(),value=line.Substring(i+1).Trim();
    if(key.Equals("UpdateAuto",StringComparison.OrdinalIgnoreCase))Auto=value!="0";
    else if(key.Equals("Datei",StringComparison.OrdinalIgnoreCase)) {
     if(value.Length>0&&!Files.Contains(value,StringComparer.OrdinalIgnoreCase))Files.Add(value);
    }
    else if(key.Equals("Zuletzt",StringComparison.OrdinalIgnoreCase))LastFile=value.Length>0?value:null;
    else if(key.Equals("UpdateAutoInstall",StringComparison.OrdinalIgnoreCase))AutoInstall=value=="1";
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
   sb.Append("UpdateAutoInstall=").Append(AutoInstall?"1":"0").Append("\r\n");
   foreach(string f in Files)sb.Append("Datei=").Append(f).Append("\r\n");
   if(!String.IsNullOrEmpty(LastFile))sb.Append("Zuletzt=").Append(LastFile).Append("\r\n");
   File.WriteAllText(FilePath,sb.ToString(),new UTF8Encoding(false));
  } catch {}
 }
}

// Fragt nur die öffentliche GitHub-Schnittstelle ab (ohne Anmeldung, es werden keine Daten übertragen).
// Selbst-Update nach den Globalen Richtlinien (Updates › Selbst-Update, Absicherung vom 20.11.2026):
// ZIP laden und gegen die Prüfsumme von GitHub prüfen, Hilfsskript per -EncodedCommand starten, Programm beenden;
// das Skript prüft das ZIP erneut und ersetzt nur EXE und Liesmich; ein eigener, nicht erhöhter Prozess startet neu.
static class UpdateCheck {
 public const string Repo="WestMc002/RDP-Pilot";
 public const string ReleasesPage="https://github.com/"+Repo+"/releases";
 const string Api="https://api.github.com/repos/"+Repo+"/releases/latest";

 // Keine Prüfung bei Vorschaubildern und Screenshots
 public static bool Disabled;

 // Arbeitsordner für Download und Hilfsskript
 public static string Folder {get {return Path.Combine(Path.GetTempPath(),"RDP-Pilot-Update");}}
 public static string LogFile {get {return Path.Combine(Folder,"Update.log");}}

 static string UserAgent {get {return "RDP-Pilot/"+AppInfo.Version;}} // GitHub verlangt einen User-Agent

 static void PrepareTls() {
  try {ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;} catch {} // TLS 1.2
 }

 // Fehler mit fester Kurzmeldung (Message) und technischem Originaltext für "Details" (Data["Detail"])
 internal static Exception Fail(string shortText,string detail) {
  var ex=new Exception(shortText??"");
  if(!String.IsNullOrEmpty(detail))ex.Data["Detail"]=detail;
  return ex;
 }

 // Auf GitHub gibt es (noch) keine fertige Version – kein Fehler
 internal sealed class PreReleaseOnlyException:Exception {
  public PreReleaseOnlyException():base("Nur Vorabversionen"){}
 }

 // Neueste Veröffentlichung holen. Wirft bei Netz- oder Formatfehlern mit fester Kurzmeldung
 // ("Nicht erreichbar.", "Keine Verbindung.", "Keine Antwort.", "Zu viele Anfragen.", "Antwort unlesbar.").
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
   using(var r=new StreamReader(resp.GetResponseStream(),Encoding.UTF8))text=ReadLimited(r,MaxResponseChars);
  } catch(WebException ex) {
   var http=ex.Response as HttpWebResponse;
   // 404 bei /releases/latest: es gibt nur Vorabversionen oder noch nichts – kein Fehler
   if(http!=null&&http.StatusCode==HttpStatusCode.NotFound)throw new PreReleaseOnlyException();
   if(http!=null&&(int)http.StatusCode==403)throw Fail("Zu viele Anfragen.",ex.Message);
   switch(ex.Status) {
    case WebExceptionStatus.Timeout:
     throw Fail("Keine Antwort.",ex.Message);
    case WebExceptionStatus.NameResolutionFailure:
    case WebExceptionStatus.ProxyNameResolutionFailure:
    case WebExceptionStatus.ConnectFailure:
     throw Fail("Keine Verbindung.",ex.Message);
   }
   throw Fail("Nicht erreichbar.",ex.Message);
  } catch(InvalidDataException ex) {
   throw Fail("Antwort unlesbar.",ex.Message);
  } catch(IOException ex) {
   throw Fail("Nicht erreichbar.",ex.Message);
  }

  Dictionary<string,object> obj;
  try {obj=MiniJson.Parse(text) as Dictionary<string,object>;}
  catch {obj=null;}
  string tag=obj!=null&&obj.ContainsKey("tag_name")?obj["tag_name"] as string:null;
  string version=(tag??"").TrimStart('v','V').Trim();
  if(Parse(version)==null)throw Fail("Antwort unlesbar.",null);

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
    // nur der Anhang "RDP-Pilot-<Version>.zip" (Globale Richtlinien, Updates › Prüfung)
    if(name!=null&&dl!=null&&String.Equals(name,"RDP-Pilot-"+version+".zip",StringComparison.OrdinalIgnoreCase)
     &&dl.StartsWith("https://github.com/"+Repo+"/releases/download/",StringComparison.OrdinalIgnoreCase)) {
     info.ZipUrl=dl;
     // Prüfsumme, die GitHub beim Hochladen ermittelt hat
     string digest=a.ContainsKey("digest")?a["digest"] as string:null;
     if(digest!=null&&digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)&&digest.Length==7+64)
      info.ZipSha256=digest.Substring(7).ToUpperInvariant();
     break;
    }
   }
  info.Notes=ExtractNotes(obj.ContainsKey("body")?obj["body"] as string:null);
  return info;
 }

 // Antwort der Schnittstelle höchstens so groß lesen (eine Release-Antwort hat wenige Kilobyte)
 const int MaxResponseChars=2*1024*1024;
 static string ReadLimited(TextReader r,int max) {
  var sb=new StringBuilder();
  var buf=new char[8192];
  int n;
  while((n=r.Read(buf,0,buf.Length))>0) {
   sb.Append(buf,0,n);
   if(sb.Length>max)throw new InvalidDataException("Antwort größer als "+(max/1024/1024)+" MB.");
  }
  return sb.ToString();
 }

 static string Sha256(string file) {
  using(var s=File.OpenRead(file))
  using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");
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

 // ZIP herunterladen, prüfen und das Hilfsskript starten. Das Skript wartet, bis dieses Programm beendet ist,
 // prüft das ZIP erneut, ersetzt EXE und Liesmich und startet das Programm neu. Danach muss der Aufrufer das
 // Programm beenden.
 // Sicherheit:
 // - Das Skript wird nicht als Datei abgelegt, sondern direkt übergeben (-EncodedCommand), damit es niemand
 //   zwischen Schreiben und Start austauschen kann.
 // - Prüfsumme: Das ZIP muss zur Prüfsumme passen, die GitHub zum Release-Anhang liefert ("digest"). Das Skript
 //   kopiert es in einen eigenen Arbeitsordner (erhöht: %ProgramData%\RDP Pilot\Update\<zufällig>, nur SYSTEM und
 //   Administratoren) und prüft dort noch einmal, ob es noch dieselbe Datei ist.
 // - Das Skript startet das Programm nicht selbst neu: Ein zweiter, nicht erhöhter Prozess wartet auf sein Ende
 //   und startet das Programm mit --nach-update – so läuft es nach einem erhöhten Update nicht mit
 //   Administratorrechten weiter.
 public static async Task Install(UpdateInfo release,Action<int> progress) {
  if(String.IsNullOrEmpty(release.ZipUrl))throw new Exception("Im Release ist keine passende ZIP-Datei.");
  string blocker=InstallBlocker();
  if(blocker!=null)throw new Exception(blocker);
  PrepareTls();
  Directory.CreateDirectory(Folder);
  try {File.Delete(Path.Combine(Folder,"Update.ps1"));} catch {} // Hilfsskript früherer Versionen
  string zip=Path.Combine(Folder,"RDP-Pilot-"+release.Version+".zip");
  using(var client=new WebClient()) {
   client.Headers[HttpRequestHeader.UserAgent]=UserAgent;
   if(client.Proxy!=null)client.Proxy.Credentials=CredentialCache.DefaultCredentials;
   if(progress!=null)client.DownloadProgressChanged+=(s,e)=>progress(e.ProgressPercentage);
   await client.DownloadFileTaskAsync(new Uri(release.ZipUrl),zip);
  }
  // Ohne Prüfsumme von GitHub oder bei Abweichung nicht installieren (Kurzmeldung, in "Details" beide Werte)
  string hash=Sha256(zip);
  if(String.IsNullOrEmpty(release.ZipSha256)||!String.Equals(hash,release.ZipSha256,StringComparison.OrdinalIgnoreCase)) {
   try {File.Delete(zip);} catch {}
   throw Fail("Download beschädigt.","SHA-256 laut GitHub: "+(String.IsNullOrEmpty(release.ZipSha256)?"(keine angegeben)":release.ZipSha256)+
    "\r\nSHA-256 der geladenen Datei: "+hash);
  }
  try {File.Delete(LogFile);} catch {}
  string exe=Application.ExecutablePath;
  string target=Path.GetDirectoryName(exe);
  int pid=Process.GetCurrentProcess().Id;
  bool elevate=!CanWrite(target);
  var start=PowerShell(Script,
   "-ProcessId "+pid+" -Zip "+PsQuote(zip)+" -Hash "+PsQuote(hash)+" -Exe "+PsQuote(exe)+
   " -Version "+PsQuote(release.Version)+" -Log "+PsQuote(LogFile));
  // Programmordner ohne Schreibrechte (z. B. unter Programme): Hilfsskript mit Administratorrechten starten
  if(elevate)start.Verb="runas";
  Process updater;
  try {updater=Process.Start(start);}
  catch(System.ComponentModel.Win32Exception ex) {
   if(ex.NativeErrorCode==1223)throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
   throw;
  }
  int updaterId=0;
  try {if(updater!=null)updaterId=updater.Id;} catch {}
  // Neustart mit normalen Rechten, sobald das Hilfsskript fertig ist
  Process.Start(PowerShell(RestartScript,"-ProcessId "+pid+" -UpdaterId "+updaterId+" -Exe "+PsQuote(exe)+" -Log "+PsQuote(LogFile)));
 }

 // PowerShell unsichtbar mit dem Skript als Befehl starten (kein Skript auf der Platte)
 static ProcessStartInfo PowerShell(string script,string args) {
  string command="& {"+script+"} "+args;
  string encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
  return new ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "+encoded)
   {UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden};
 }

 // Text als PowerShell-Zeichenkette in einfachen Anführungszeichen
 static string PsQuote(string s){return "'"+(s??"").Replace("'","''")+"'";}

 // Ergebnis des letzten Updates ("OK x" bzw. "FEHLER …") nach dem Neustart mit --nach-update
 public static string LastResult() {
  try {return File.Exists(LogFile)?File.ReadAllText(LogFile,Encoding.UTF8).Trim():null;}
  catch {return null;}
 }

 public static void OpenPage(string url) {
  try {Process.Start(new ProcessStartInfo(String.IsNullOrEmpty(url)?ReleasesPage:url){UseShellExecute=true});} catch {}
 }

 // Hilfsskript (als -EncodedCommand übergeben). Ersetzt nur EXE und Liesmich; schlägt etwas fehl, wird der
 // vorherige Stand aller ersetzten Dateien wiederhergestellt. Ergebnis "OK x" bzw. "FEHLER …" in Update.log.
 const string Script=@"
param([int]$ProcessId,[string]$Zip,[string]$Hash,[string]$Exe,[string]$Version,[string]$Log)
$ErrorActionPreference = 'Stop'
$name = Split-Path $Exe -Leaf
$target = Split-Path $Exe -Parent
$allowed = @($name, 'Liesmich.txt')
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$work = $null
$saved = @()
try {
  try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
  Start-Sleep -Milliseconds 500
  if ($admin) {
    $base = Join-Path $env:ProgramData 'RDP Pilot\Update'
    [void][IO.Directory]::CreateDirectory($base)
    $sec = New-Object System.Security.AccessControl.DirectorySecurity
    $sec.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-32-544', 'S-1-5-18')) {
      $id = New-Object System.Security.Principal.SecurityIdentifier($sid)
      $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    $work = Join-Path $base ([guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $work) { throw 'Arbeitsordner existiert bereits.' }
    [void][IO.Directory]::CreateDirectory($work, $sec)
  } else {
    $work = Join-Path ([IO.Path]::GetTempPath()) ('RDP-Pilot-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($work)
  }
  $copy = Join-Path $work 'Update.zip'
  Copy-Item -LiteralPath $Zip -Destination $copy
  if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $Hash) { throw 'Die Update-Datei wurde nach dem Herunterladen verändert.' }
  $dir = Join-Path $work 'Neu'
  Expand-Archive -LiteralPath $copy -DestinationPath $dir
  $new = Get-ChildItem -LiteralPath $dir -Recurse -File | Where-Object { $_.Name -eq $name } | Select-Object -First 1
  if (-not $new) { throw ('Im ZIP fehlt ' + $name) }
  $files = @(Get-ChildItem -LiteralPath $new.DirectoryName -File | Where-Object { $allowed -contains $_.Name })
  try {
    foreach ($f in $files) {
      $dest = Join-Path $target $f.Name
      if (Test-Path -LiteralPath $dest) { Copy-Item -LiteralPath $dest -Destination ($dest + '.alt') -Force; $saved += ,@($dest, ($dest + '.alt')) }
      else { $saved += ,@($dest, '') }
    }
    foreach ($f in $files) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $target $f.Name) -Force }
  } catch {
    foreach ($s in $saved) {
      if ($s[1]) { Copy-Item -LiteralPath $s[1] -Destination $s[0] -Force -ErrorAction SilentlyContinue }
      else { Remove-Item -LiteralPath $s[0] -Force -ErrorAction SilentlyContinue }
    }
    throw
  }
  foreach ($s in $saved) { if ($s[1]) { Remove-Item -LiteralPath $s[1] -Force -ErrorAction SilentlyContinue } }
  Remove-Item -LiteralPath $Zip -Force -ErrorAction SilentlyContinue
  ('OK ' + $Version) | Out-File -LiteralPath $Log -Encoding utf8
} catch {
  ('FEHLER ' + $_.Exception.Message) | Out-File -LiteralPath $Log -Encoding utf8
} finally {
  if ($work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
";

 // Neustart mit normalen Rechten (läuft nicht erhöht): wartet auf das Ende von Programm und Hilfsskript
 // (höchstens 15 Minuten), liest Update.log und startet das Programm mit --nach-update – auch nach einem Fehler,
 // damit das Programm die Meldung zeigen kann. Get-Process statt Wait-Process: Auf ein erhöhtes Hilfsskript
 // darf ein nicht erhöhter Prozess nicht unbedingt warten, sehen kann er es.
 const string RestartScript=@"
param([int]$ProcessId,[int]$UpdaterId,[string]$Exe,[string]$Log)
try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
$until = (Get-Date).AddMinutes(15)
while ((Get-Date) -lt $until) {
  if ($UpdaterId -gt 0) { if (-not (Get-Process -Id $UpdaterId -ErrorAction SilentlyContinue)) { break } }
  elseif (Test-Path -LiteralPath $Log) { break }
  Start-Sleep -Milliseconds 500
}
Start-Sleep -Milliseconds 500
$result = ''
try { $result = (Get-Content -LiteralPath $Log -Raw -ErrorAction Stop).Trim() } catch {}
if (-not $result) { ('FEHLER Das Update wurde nicht abgeschlossen.') | Out-File -LiteralPath $Log -Encoding utf8 }
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

enum UpdateKind {Unknown,Current,Available,Failed,Checking,PreRelease}

// Stand der Update-Prüfung für das Info-Fenster: Überschrift, zweite Zeile und
// technischer Originaltext für "Details" (More, nur bei Fehlern)
sealed class UpdateStatus {
 public readonly UpdateKind Kind;
 public readonly string Title,Detail,More;
 public UpdateStatus(UpdateKind kind,string title,string detail):this(kind,title,detail,null){}
 public UpdateStatus(UpdateKind kind,string title,string detail,string more){Kind=kind;Title=title;Detail=detail??"";More=more??"";}

 // Stand aus einer Ausnahme der Prüfung: feste Kurzmeldung, Originaltext in den Details
 public static UpdateStatus FromError(Exception ex) {
  if(ex is UpdateCheck.PreReleaseOnlyException)
   return new UpdateStatus(UpdateKind.PreRelease,"Nur Vorabversionen","Noch keine fertige Version.");
  string shortText=ex.Message??"";
  string more=ex.Data.Contains("Detail")?ex.Data["Detail"] as string:null;
  // Unbekannter Fehler: kein eigener Text, nur "Details" mit der Originalmeldung
  if(more==null&&!IsShort(shortText)){more=shortText;shortText="";}
  return new UpdateStatus(UpdateKind.Failed,"Prüfung nicht möglich",shortText,more);
 }
 static bool IsShort(string t) {
  foreach(string k in new[]{"Nicht erreichbar.","Keine Verbindung.","Keine Antwort.","Zu viele Anfragen.","Antwort unlesbar."})
   if(t==k)return true;
  return false;
 }
}

// Verbindung des Info-Fensters zur Update-Prüfung im Hauptfenster
sealed class UpdateHooks {
 public Func<UpdateStatus> Status;
 public Func<bool> Auto;
 public Action<bool> SetAuto;
 public Func<bool> AutoInstall;          // "Automatisch aktualisieren"; ohne diese Hooks fehlt die Zeile
 public Action<bool> SetAutoInstall;
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
 protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);hover=false;Cursor=Enabled?Cursors.Hand:Cursors.Default;Invalidate();}
 protected override void OnMouseClick(MouseEventArgs e){base.OnMouseClick(e);if(e.Button==MouseButtons.Left&&Enabled)Checked=!Checked;}
 protected override void OnPaint(PaintEventArgs e) {
  var g=e.Graphics;
  g.Clear(Parent!=null?Parent.BackColor:Ui.Surface);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  int sw=Ui.Px(36),sh=Ui.Px(18);
  var r=new RectangleF(Width-sw-1.5f,(Height-sh)/2f,sw,sh);
  // gesperrt: blasser, ohne Hover
  Color back=Parent!=null?Parent.BackColor:Ui.Surface;
  Func<Color,Color> dim=c=>Enabled?c:Ui.Blend(back,c,0.4f);
  using(var p=Ui.Round(r,r.Height/2)) {
   if(on) using(var b=new SolidBrush(dim(hover?Ui.PrimaryHover:Ui.Primary)))g.FillPath(b,p);
   else {
    using(var b=new SolidBrush(hover?Ui.SurfaceHover:Ui.Surface))g.FillPath(b,p);
    using(var pen=new Pen(dim(Ui.Muted),Math.Max(1f,Ui.S)))g.DrawPath(pen,p);
   }
  }
  float kd=(on?12:10)*Ui.S*(hover?1.1f:1f);
  float kx=on?r.Right-3.5f*Ui.S-kd:r.X+4*Ui.S;
  using(var b=new SolidBrush(on?Ui.OnPrimary:dim(Ui.Muted)))g.FillEllipse(b,kx,r.Y+(r.Height-kd)/2,kd,kd);
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
 // Eigene Hintergrundfarbe = Füllfarbe der Leiste: Die Knöpfe malen ihre runden Ecken mit der
 // Hintergrundfarbe des Elternelements aus, sonst stünden dort helle Zwickel in Fensterfarbe
 Color OuterBack {get {return Parent!=null?Parent.BackColor:Ui.Bg;}}
 Color Fill {get {return Ui.Blend(OuterBack,Ui.Accent,0.14f);}}
 protected override void OnParentChanged(EventArgs e){base.OnParentChanged(e);BackColor=Fill;}
 protected override void OnParentBackColorChanged(EventArgs e){base.OnParentBackColorChanged(e);BackColor=Fill;}
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
  Color back=OuterBack;
  if(BackColor!=Fill){BackColor=Fill;foreach(Control c in Controls)c.Invalidate();}
  g.Clear(back);
  g.SmoothingMode=SmoothingMode.AntiAlias;
  using(var p=Ui.Round(new RectangleF(0.5f,0.5f,Width-1.5f,Height-1.5f),Ui.Px(8)))
  using(var b=new SolidBrush(Fill))
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
  object v=Value(s,ref i,0);
  Skip(s,ref i);
  if(i!=s.Length)throw new InvalidDataException("JSON: unerwartete Zeichen am Ende.");
  return v;
 }
 static void Skip(string s,ref int i){while(i<s.Length&&Char.IsWhiteSpace(s[i]))i++;}
 const int MaxDepth=64; // tiefer verschachtelte Antworten gelten als unlesbar (kein Stapelüberlauf)
 static object Value(string s,ref int i,int depth) {
  if(depth>MaxDepth)throw new InvalidDataException("JSON: zu tief verschachtelt.");
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
    d[key]=Value(s,ref i,depth+1);
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
    l.Add(Value(s,ref i,depth+1));
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