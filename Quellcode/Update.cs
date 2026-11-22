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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RDPilot {

// Neueste Veröffentlichung auf GitHub
sealed class UpdateInfo {
 public string Version; // z. B. 1.16.2026.1110 (ohne "v")
 public string Url;     // Release-Seite auf GitHub
 public string ZipUrl;  // angehängtes ZIP (EXE und Liesmich)
 public string Notes;   // Abschnitt "Änderungen" als Klartext (Tooltip der Hinweisleiste)
 public string Sha256;    // SHA-256 des ZIP aus dem signierten Manifest (Großbuchstaben); ohne gültige Signatur leer
 public string Problem;   // Kurzmeldung, warum nicht installiert wird ("Nicht signiert." usw.), sonst null
 public string ProblemDetail; // ausführlicher Text dazu (für "Details")
 public byte[] Manifest;  // geprüftes Manifest (Bytes wie auf GitHub); das Update-Skript prüft es selbst noch einmal
 public long ZipSize;      // Größe des ZIP laut GitHub (Bytes, 0 = unbekannt); der Download wird darauf begrenzt
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
// Selbst-Update nach den Globalen Richtlinien (Updates › Prüfung, Selbst-Update, Signierte Updates),
// Referenz Dock Pilot (UpdateCheck.cs): Installiert wird nur ein Release, dessen Manifest mit dem Schlüssel des
// Herausgebers signiert ist. Das Skript liegt als Datei im Arbeitsordner und wird über einen Lader mit SHA-256-Prüfung
// gestartet (-EncodedCommand); es prüft Signatur und Prüfsumme selbst noch einmal (erhöht in einem geschützten
// Arbeitsordner unter ProgramData), ersetzt nur EXE und Liesmich atomar mit Journal und Rücksicherung.
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
   using(var stream=resp.GetResponseStream())text=Encoding.UTF8.GetString(ReadLimited(stream,MaxResponse));
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
  // ZIP und signiertes Manifest nur mit den erwarteten Namen und von der Download-Adresse dieses Repositorys
  string wanted=AssetName(version),wantedManifest=ManifestName(version);
  string prefix="https://github.com/"+Repo+"/releases/download/";
  string zipUrl=null,manifestUrl=null;
  long zipSize=0;
  var assets=obj.ContainsKey("assets")?obj["assets"] as List<object>:null;
  if(assets!=null)
   foreach(object o in assets) {
    var a=o as Dictionary<string,object>;
    if(a==null||!a.ContainsKey("name")||!a.ContainsKey("browser_download_url"))continue;
    string name=a["name"] as string,dl=a["browser_download_url"] as string;
    if(dl==null||!dl.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))continue;
    if(String.Equals(name,wanted,StringComparison.Ordinal)) {
     zipUrl=dl;
     object size;
     if(a.TryGetValue("size",out size)&&size is double)zipSize=(long)(double)size;
    }
    else if(String.Equals(name,wantedManifest,StringComparison.Ordinal))manifestUrl=dl;
   }
  info.Notes=ExtractNotes(obj.ContainsKey("body")?obj["body"] as string:null);
  if(zipUrl==null)return info; // kein ZIP: nur die Release-Seite
  // Die Prüfsumme kommt nur aus dem signierten Manifest, nicht von GitHub: Wer das Release austauscht,
  // kann ohne den privaten Schlüssel keine passende Signatur erzeugen.
  if(manifestUrl==null) {
   info.Problem="Nicht signiert.";
   info.ProblemDetail="Im Release fehlt die Signatur ("+wantedManifest+"). RDP Pilot installiert nur signierte Updates.";
   return info;
  }
  byte[] manifest;
  try {manifest=DownloadSmall(manifestUrl,8192);}
  catch(Exception ex) {
   info.Problem="Signatur nicht lesbar.";
   info.ProblemDetail="Die Signatur konnte nicht geladen werden: "+ex.Message;
   return info;
  }
  string sha,detail;
  string problem=CheckManifest(manifest,version,out sha,out detail);
  if(problem!=null){info.Problem=problem;info.ProblemDetail=detail;return info;}
  info.ZipUrl=zipUrl;
  info.ZipSize=zipSize;
  info.Sha256=sha;
  info.Manifest=manifest;
  return info;
 }

 public static string AssetName(string version){return "RDP-Pilot-"+version+".zip";}
 public static string ManifestName(string version){return "RDP-Pilot-"+version+".manifest";}

 // Öffentliche Schlüssel (RSA 3072) des Herausgebers: Haupt- und Reserveschlüssel. Signiert wird mit dem
 // Hauptschlüssel; der Reserveschlüssel ersetzt ihn, falls er verloren geht.
 const string MainKey="<RSAKeyValue><Modulus>tLtylaHt5SgGCrTrEaM0H3Z/UqIQWSL2c3rCpuhZEYnCyJVDIYFCNuLbZ2OeapsJgCttOnwv5djvmpHTcGV6DeFM059O699xepTE0QWKYYM69sCFG4gunH2xh39bM20q9qWbui1Fyy+nN4cqzy26eR7tzXyClAJSS1zCKBuoF1uAtt2c1f1fcCHXdHsjWXZA6Z8OZeiIOizYSfiVHNIJFr8kSFQfnGRaygwp2ta27Hv0z0oKM6Zrvld/vXkgL2um7X1+GV24IzIVT7J8nxmar//yPoc6ugYhgcLjUMBgjwe/5m4REJQFWwntMF1uj5Jf+ehBFZS87kGtSvTykh7mea71JMdLpvAuhicMaP4T8Fr1uTYCPKR4cuiRI+6uogKVOZN23BuZS5iBj1zZkcmopNOXbQupsOkYp2QwejMHoEV7cEc71ZlRqqJed0vBqO8B5H21mrBGJm3kKHxa2QFzY5BZY7ZcC4EG2TZIkC8eZRNUXXmZOdAWGR+aPKeJg7DZ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
 const string ReserveKey="<RSAKeyValue><Modulus>zY/NkGbkd4IZHMMuGty2WLmQ0hm6lEZBql2v84BPf3NT+rpenfLK0kk7aKfRXgFS1G8tz+tHHd58GhioFFhnXN2syFTUus7u5csblitORGoNjTf9Rlicxw6Ut3hX7tkn2u+2PRKE5xOOL+epZV1YvBgr/W67WTt5RZEyfA0Vgn8llhrXaSD32iVNEzkeY5yy1bvQAmoxAk3KjNcD8Uuia9XP9qs/orDO0giIj/xsfY2D7qp/rkhSaIOnt/+gRWHbjhC/PGPga3dmcIMC1C3X0Z/meg6XgI+zWeXOOxFe8jCVRlaeGDoRJlJJGvg6uzIJlLCQeNxnSEufZCD10e+ex2xx75Gp3bqCvBvr49VXEC+pLxQteQMj2V5tuhq23MtIeSV0huaKZ1SPm1kxFjG5vj/11bo2VCkcx9dhfEBDdsucJqdUecbLJp58Y6smM2Ah4Qh7nILSNHHcpWey100qMN3WdwXsfzPVuRtD1c16KgC3TQIov2/2fGQGUxn1Awox</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

 static readonly string[] ManifestKeys={"Programm","Version","Datei","SHA256","Schluessel","Erstellt"};

 // Manifest prüfen (UTF-8, LF, feste Reihenfolge, letzte Zeile "Signatur=" mit RSA-SHA256/PKCS#1 v1.5 über alle
 // Bytes davor). Rückgabe: null = gültig (dann sha gesetzt), sonst Kurzmeldung; detail erklärt sie.
 public static string CheckManifest(byte[] data,string version,out string sha,out string detail) {
  sha=null;detail=null;
  const string Invalid="Signatur ungültig.";
  byte[] marker=Encoding.ASCII.GetBytes("\nSignatur=");
  int at=IndexOf(data,marker);
  if(at<0){detail="Im Manifest fehlt die Zeile „Signatur=“.";return Invalid;}
  byte[] signed=new byte[at+1];
  Array.Copy(data,signed,signed.Length);
  byte[] signature;
  try {signature=Convert.FromBase64String(Encoding.ASCII.GetString(data,at+marker.Length,data.Length-at-marker.Length).Trim());}
  catch {detail="Die Signatur im Manifest ist nicht lesbar.";return Invalid;}
  string[] lines;
  try {lines=new UTF8Encoding(false,true).GetString(signed).Split('\n');}
  catch {detail="Das Manifest ist kein gültiger UTF-8-Text.";return Invalid;}
  if(lines.Length!=ManifestKeys.Length+1||lines[ManifestKeys.Length].Length!=0){detail="Das Manifest hat nicht den erwarteten Aufbau.";return Invalid;}
  var values=new Dictionary<string,string>(StringComparer.Ordinal);
  for(int i=0;i<ManifestKeys.Length;i++) {
   if(!lines[i].StartsWith(ManifestKeys[i]+"=",StringComparison.Ordinal)){detail="Im Manifest fehlt oder steht falsch: "+ManifestKeys[i]+".";return Invalid;}
   values[ManifestKeys[i]]=lines[i].Substring(ManifestKeys[i].Length+1);
  }
  string keyName=values["Schluessel"];
  string keyXml=keyName=="Haupt"?MainKey:keyName=="Reserve"?ReserveKey:null;
  if(keyXml==null){detail="Unbekannter Schlüssel im Manifest: "+keyName;return Invalid;}
  bool ok;
  try {
   // PROV_RSA_AES (24): RSA mit SHA-256 auch unter .NET 4
   using(var rsa=new RSACryptoServiceProvider(new CspParameters(24))) {
    rsa.PersistKeyInCsp=false;
    rsa.FromXmlString(keyXml);
    ok=rsa.VerifyData(signed,"SHA256",signature);
   }
  } catch(Exception ex){detail="Die Signatur konnte nicht geprüft werden: "+ex.Message;return Invalid;}
  if(!ok){detail="Die Signatur passt nicht zum "+keyName+"schlüssel von RDP Pilot. Das Release wurde nicht vom Herausgeber signiert oder nachträglich verändert.";return Invalid;}
  // gültig signiert: Inhalt muss genau zu diesem Programm und dieser Version passen
  if(values["Programm"]!="RDP Pilot"){detail="Das Manifest gehört zu „"+values["Programm"]+"“, nicht zu RDP Pilot.";return Invalid;}
  if(values["Version"]!=version){detail="Das Manifest nennt Version "+values["Version"]+", das Release "+version+".";return Invalid;}
  if(values["Datei"]!=AssetName(version)){detail="Das Manifest nennt die Datei „"+values["Datei"]+"“.";return Invalid;}
  if(!Regex.IsMatch(values["SHA256"],"^[0-9a-f]{64}$")){detail="Die Prüfsumme im Manifest ist ungültig.";return Invalid;}
  sha=values["SHA256"].ToUpperInvariant();
  return null;
 }

 static int IndexOf(byte[] data,byte[] pattern) {
  for(int i=0;i+pattern.Length<=data.Length;i++) {
   int j=0;
   while(j<pattern.Length&&data[i+j]==pattern[j])j++;
   if(j==pattern.Length)return i;
  }
  return -1;
 }

 // Kleine Datei laden (höchstens max Bytes)
 static byte[] DownloadSmall(string url,int max) {
  var req=(HttpWebRequest)WebRequest.Create(url);
  req.UserAgent=UserAgent;
  req.Timeout=15000;
  req.ReadWriteTimeout=15000;
  if(req.Proxy!=null)req.Proxy.Credentials=CredentialCache.DefaultCredentials;
  using(var resp=(HttpWebResponse)req.GetResponse())
  using(var stream=resp.GetResponseStream())return ReadLimited(stream,max);
 }

 // Antwort der Schnittstelle höchstens 1 MB (eine Release-Antwort hat wenige Kilobyte)
 const int MaxResponse=1024*1024;
 // Obergrenze für das Update-ZIP (RDP Pilot selbst ist unter 1 MB)
 const long MaxZip=64L*1024*1024;

 // Stream lesen, höchstens max Bytes – sonst Abbruch
 static byte[] ReadLimited(Stream stream,int max) {
  using(var ms=new MemoryStream()) {
   var buf=new byte[8192];
   int n;
   while((n=stream.Read(buf,0,buf.Length))>0) {
    ms.Write(buf,0,n);
    if(ms.Length>max)throw new InvalidDataException("Antwort größer als "+(max/1024)+" KB.");
   }
   return ms.ToArray();
  }
 }

 // Datei laden und beim Lesen auf max Bytes begrenzen (danach Abbruch, Datei weg)
 static void DownloadLimited(string url,string file,long max,Action<int> progress) {
  var req=(HttpWebRequest)WebRequest.Create(url);
  req.UserAgent=UserAgent;
  req.Timeout=30000;
  req.ReadWriteTimeout=30000;
  if(req.Proxy!=null)req.Proxy.Credentials=CredentialCache.DefaultCredentials;
  bool ok=false;
  try {
   using(var resp=(HttpWebResponse)req.GetResponse()) {
    if(resp.ContentLength>max)throw new Exception("Das Update ist größer als erwartet.");
    long expected=resp.ContentLength>0?resp.ContentLength:max;
    using(var stream=resp.GetResponseStream())
    using(var fs=new FileStream(file,FileMode.CreateNew,FileAccess.Write)) {
     var buf=new byte[81920];
     long total=0;
     int n,last=-1;
     while((n=stream.Read(buf,0,buf.Length))>0) {
      total+=n;
      if(total>max)throw new Exception("Das Update ist größer als erwartet.");
      fs.Write(buf,0,n);
      int percent=(int)Math.Min(100,total*100/Math.Max(1,expected));
      if(progress!=null&&percent!=last){last=percent;progress(percent);}
     }
    }
   }
   ok=true;
  } finally {
   if(!ok)try {File.Delete(file);} catch {}
  }
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

 // Warum nicht installiert wird (null = kann installiert werden):
 // - im Projektordner (mit Erstellen.bat): dort entsteht die EXE beim Bauen
 // - RDP Pilot läuft selbst mit Administratorrechten: Skript und Neustart liefen dann erhöht mit Arbeitsordner
 //   im Temp-Ordner, sobald die Schreibprobe gelingt (normal gestartet nimmt das Update den geschützten Weg)
 // - ein früheres Update wurde nicht abgeschlossen (PendingText): erst wiederherstellen (OfferRepair)
 public static string InstallBlocker() {
  if(InProjectFolder)
   return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
  if(IsElevated)
   return AppInfo.FullName+" läuft gerade mit Administratorrechten. Bitte "+AppInfo.FullName+" normal starten und dann aktualisieren.";
  if(PendingRestore()!=null)return PendingText;
  return null;
 }

 // Läuft die EXE im Projektordner (Erstellen.bat daneben)?
 static bool InProjectFolder {get {return File.Exists(Path.Combine(Application.StartupPath,"Erstellen.bat"));}}

 // Läuft RDP Pilot mit Administratorrechten (erhöht gestartet)?
 static bool IsElevated {
  get {
   try {
    using(var id=System.Security.Principal.WindowsIdentity.GetCurrent())
     return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
   } catch {return false;}
  }
 }

 static bool CanWrite(string dir) {
  try {
   string probe=Path.Combine(dir,"~update-test.tmp");
   File.WriteAllText(probe,"");
   File.Delete(probe);
   return true;
  } catch {return false;}
 }

 // Nur ein Update zur Zeit: von Hand und automatisch teilen sich diese Sperre; sie bleibt nach dem Start des
 // Skripts gesetzt, weil sich das Programm danach beendet. Das Skript hält zusätzlich eine Mutex je Zielordner.
 static int busy;
 public const string Running="Ein Update läuft bereits.";
 public static bool Busy {get {return busy!=0||ScriptRunning();}}

 // Sperre des Update-Skripts: sitzungsübergreifend (Global\) und je Installationsziel, gehalten bis nach dem
 // Dateiaustausch bzw. der Rücksicherung
 static string LockName {get {return @"Global\RDP-Pilot-Update-"+TargetId;}}

 // Kennung des Installationsziels (24 Zeichen der SHA-256 des Programmordners) für Sperre und Arbeitsordner
 static string TargetId {
  get {
   string dir=Path.GetFullPath(Application.StartupPath).TrimEnd('\\').ToUpperInvariant();
   using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(dir))).Replace("-","").Substring(0,24);
  }
 }

 static bool ScriptRunning() {
  try {
   Mutex m;
   if(!Mutex.TryOpenExisting(LockName,out m))return false;
   m.Dispose();
   return true;
  } catch(UnauthorizedAccessException){return true;} // vom Skript mit Administratorrechten oder aus einem anderen Konto
  catch {return false;}
 }

 // Diese Dateien übernimmt das Update aus dem ZIP – sonst nichts
 const string ExeName="RDP Pilot.exe",ManualName="Liesmich.txt";

 // ZIP herunterladen, gegen das signierte Manifest prüfen und das Update-Skript starten. Das Skript wartet auf das
 // Programmende, prüft Signatur und Prüfsumme erneut, ersetzt EXE und Liesmich atomar (mit Journal und Rücksicherung)
 // und startet neu. Danach muss der Aufrufer das Programm beenden. Ohne Schreibrechte im Programmordner läuft das
 // Skript mit Administratorrechten in einem geschützten Arbeitsordner; den Neustart übernimmt dann ein zweiter
 // Prozess mit normalen Rechten. Referenz: Dock Pilot ab 1.70 (Update-Logik aus WindowGrid 1.65).
 public static async Task Install(UpdateInfo release,Action<int> progress) {
  if(Interlocked.CompareExchange(ref busy,1,0)!=0)throw new Exception(Running);
  bool started=false;
  try {
   await Task.Run(()=>InstallLocked(release,progress));
   started=true;
  } finally {
   if(!started)Interlocked.Exchange(ref busy,0);
  }
 }

 static void InstallLocked(UpdateInfo release,Action<int> progress) {
  if(release.Problem!=null)throw Fail(release.Problem,release.ProblemDetail);
  if(String.IsNullOrEmpty(release.ZipUrl)||String.IsNullOrEmpty(release.Sha256)||release.Manifest==null)
   throw new Exception(NoZip);
  string blocker=InstallBlocker();
  if(blocker!=null)throw new Exception(blocker);
  if(ScriptRunning())throw new Exception(Running);
  PrepareTls();
  Directory.CreateDirectory(Folder);
  // Aufräumen nur unter der Sperre des Installationsziels; ist sie belegt, läuft gerade ein Update
  if(!WithTargetLock(CleanupLocked))throw new Exception(Running);
  if(PendingRestore()!=null)throw new Exception(PendingText);
  // eigener Arbeitsordner je Versuch; der Name beginnt mit der Zielkennung, damit die Sperrprüfung nur Ordner
  // dieses Installationsziels betrachtet
  string attempt=Path.Combine(Folder,TargetId+"-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(attempt);
  string zip=Path.Combine(attempt,AssetName(release.Version));
  // Download begrenzen: höchstens die von GitHub genannte Größe, in jedem Fall höchstens 64 MB
  long limit=MaxZip;
  if(release.ZipSize>0) {
   if(release.ZipSize>MaxZip)throw new Exception("Das Update ist ungewöhnlich groß und wird nicht geladen.");
   limit=release.ZipSize;
  }
  DownloadLimited(release.ZipUrl,zip,limit,progress);
  if(release.ZipSize>0&&new FileInfo(zip).Length!=release.ZipSize) {
   try {File.Delete(zip);} catch {}
   throw new Exception("Download unvollständig.");
  }
  string hash=Sha256(zip);
  if(!String.Equals(hash,release.Sha256,StringComparison.OrdinalIgnoreCase)) {
   try {File.Delete(zip);} catch {}
   throw Fail("Download beschädigt.","Die Prüfsumme (SHA-256) der heruntergeladenen Datei stimmt nicht mit dem signierten Manifest überein.\r\n"+
    "Erwartet: "+release.Sha256+"\r\nErhalten: "+hash);
  }
  try {File.Delete(LogFile);} catch {}
  string exe=Application.ExecutablePath;
  bool elevated=!CanWrite(Path.GetDirectoryName(exe));
  // Erhöht: eigener, nicht vorhersagbarer Arbeitsordner direkt unter ProgramData, den das Skript nur für
  // Administratoren beschreibbar anlegt. Sonst reicht der Arbeitsordner im Temp-Ordner des Benutzers.
  string work=elevated
   ?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RDP-Pilot-Update-"+TargetId+"-"+Guid.NewGuid().ToString("N"))
   :attempt;
  int pid=Process.GetCurrentProcess().Id;
  // Manifest neben das ZIP legen: Das Skript prüft die Signatur selbst und nimmt die Prüfsumme nur daraus
  string manifestFile=Path.Combine(attempt,ManifestName(release.Version));
  File.WriteAllBytes(manifestFile,release.Manifest);
  var vars=new StringBuilder();
  vars.Append("$ProcessId = ").Append(pid).Append("\n");
  Var(vars,"Zip",zip);
  vars.Append("$ZipSize = ").Append(new FileInfo(zip).Length).Append("\n");
  Var(vars,"Manifest",manifestFile);
  Var(vars,"Exe",exe);
  Var(vars,"Version",release.Version);
  Var(vars,"Sha256",release.Sha256.ToUpperInvariant());
  Var(vars,"AssetName",AssetName(release.Version));
  Var(vars,"MainKey",MainKey);
  Var(vars,"ReserveKey",ReserveKey);
  Var(vars,"LockName",LockName);
  Var(vars,"TargetId",TargetId);
  Var(vars,"TempRoot",Folder);
  Var(vars,"Log",LogFile);
  Var(vars,"Work",work);
  Var(vars,"ExeName",ExeName);
  Var(vars,"ManualName",ManualName);
  vars.Append("$Elevated = $").Append(elevated?"true":"false").Append("\n");
  vars.Append("$Tray = $false\n"); // RDP Pilot hat keinen Infobereich: immer mit Fenster neu starten
  // Skript als Datei im Arbeitsordner, auf der Befehlszeile nur Lader, Werte und SHA-256 des Skripts: Der Lader
  // liest die Datei einmal in den Speicher, prüft die Prüfsumme und führt genau diese Bytes aus. So bleibt die
  // Befehlszeile weit unter 32767 Zeichen, auch bei langen Pfaden.
  byte[] body=new UTF8Encoding(false).GetBytes(Compact(InstallScript));
  string scriptFile=Path.Combine(attempt,"Update-Skript.txt");
  File.WriteAllBytes(scriptFile,body);
  string scriptHash;
  using(var h=SHA256.Create())scriptHash=BitConverter.ToString(h.ComputeHash(body)).Replace("-","");
  Var(vars,"ScriptFile",scriptFile);
  Var(vars,"ScriptHash",scriptHash);
  var start=PowerShell(vars+InstallLoader);
  // Programmordner ohne Schreibrechte (z. B. unter Programme): Skript mit Administratorrechten starten
  if(elevated)start.Verb="runas";
  Process installer;
  try {installer=Process.Start(start);}
  catch(System.ComponentModel.Win32Exception ex) {
   if(ex.NativeErrorCode==1223)throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
   throw;
  }
  if(!elevated)return;
  // Neustart mit normalen Rechten: Ein nicht erhöhter Prozess wartet auf das Ende des Updates, übernimmt das
  // Ergebnis nach Update.log und startet RDP Pilot – sonst liefe es danach mit Administratorrechten.
  StartRestartHelper(pid,installer,attempt,exe,work);
 }

 static void StartRestartHelper(int pid,Process installer,string attempt,string exe,string work) {
  int installerId=0;
  try {if(installer!=null)installerId=installer.Id;} catch {}
  var r=new StringBuilder();
  r.Append("$ProcessId = ").Append(pid).Append("\n");
  r.Append("$Installer = ").Append(installerId).Append("\n");
  Var(r,"Attempt",attempt);
  Var(r,"Exe",exe);
  Var(r,"Log",LogFile);
  Var(r,"Work",work);
  r.Append("$Tray = $false\n");
  Process.Start(PowerShell(r+RestartScript));
 }

 // --- Nicht abgeschlossenes Update (Globale Richtlinien, Selbst-Update › Offene Wiederherstellung) ---------------

 // Merker im Arbeitsordner: Rücksicherung unvollständig, Inhalt = Ordner mit den alten Dateien
 const string PendingMarker="Wiederherstellung-offen";

 // Kurzmeldung für Anwender: Ein Update wurde nicht abgeschlossen (Technisches steht im Fehlerprotokoll)
 public const string PendingText="Das letzte Update wurde nicht abgeschlossen.";

 // Arbeitsordner eines nicht abgeschlossenen Updates für dieses Installationsziel (null = keiner). Nur lesend:
 // Aufgeräumt wird ausschließlich in CleanupLocked unter der Sperre des Installationsziels.
 static string PendingRestore() {
  try {
   foreach(string dir in PendingDirs())return dir;
  } catch {}
  return null;
 }

 // Arbeitsordner mit Merker "Wiederherstellung-offen" für dieses Ziel (Temp-Ordner und ProgramData)
 static List<string> PendingDirs() {
  var dirs=new List<string>();
  if(Directory.Exists(Folder))dirs.AddRange(Directory.GetDirectories(Folder,TargetId+"-*"));
  string data=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
  dirs.AddRange(Directory.GetDirectories(data,"RDP-Pilot-Update-"+TargetId+"-*"));
  return dirs.Where(d=>File.Exists(Path.Combine(d,PendingMarker))).ToList();
 }

 // Sperre des Installationsziels kurz selbst halten (dieselbe Mutex wie Update- und Reparatur-Skript), damit
 // Aufräumen und Prüfen nie gleichzeitig mit einem laufenden Update oder einer Wiederherstellung geschehen.
 // false = gerade gesperrt (ein Skript läuft) – dann nichts tun.
 static bool WithTargetLock(Action action) {
  Mutex m;
  try {m=new Mutex(false,LockName);}
  catch(UnauthorizedAccessException){return false;} // von einem Skript mit Administratorrechten gehalten
  using(m) {
   bool have;
   try {have=m.WaitOne(0);}
   catch(AbandonedMutexException){have=true;}
   if(!have)return false;
   try {action();}
   finally {m.ReleaseMutex();}
   return true;
  }
 }

 // Unter der Sperre: Temp-Arbeitsordner, bei denen laut Journal nichts zurückzusetzen ist, still entfernen, und
 // abgebrochene Versuche (älter als ein Tag) aufräumen. Unter ProgramData wird nie still aufgeräumt: RDP Pilot
 // aktualisiert nie mit Adminrechten (InstallBlocker greift vorher), offene Ordner dort werden per Rückfrage
 // wiederhergestellt.
 public static void Cleanup() {
  try {WithTargetLock(CleanupLocked);} catch {}
 }

 static void CleanupLocked() {
  if(Directory.Exists(Folder)) {
   foreach(string dir in PendingDirs()) {
    if(!dir.StartsWith(Folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
    if(!NothingToRestore(dir))continue;
    try {
     Directory.Delete(dir,true);
     Log.Write("Update","Nicht abgeschlossenes Update aufgeräumt, es war nichts zurückzusetzen: "+dir);
    } catch {}
   }
   CleanOldAttempts();
  }
 }

 // Wiederherstellungsjournal prüfen: true, wenn jede eingetragene Datei noch der Sicherung entspricht bzw. bei
 // "Neu" nicht vorhanden ist (das Update hat dann nichts verändert). Übrig gebliebene Hilfsdateien werden entfernt.
 static bool NothingToRestore(string dir) {
  try {
   string exe=Application.ExecutablePath;
   string target=Path.GetDirectoryName(exe);
   string journal=Path.Combine(dir,"Wiederherstellung.txt");
   // Das Journal entsteht vor dem Merker und endet vor dem ersten Austausch mit "Bereit". Fehlt es oder ist es
   // unvollständig, ist der Stand unbekannt: nicht still aufräumen, sondern per Rückfrage wiederherstellen.
   if(!File.Exists(journal)||new FileInfo(journal).Length>64*1024)return false;
   string[] lines=File.ReadAllLines(journal,Encoding.UTF8);
   if(!lines.Any(l=>l=="Bereit"))return false;
   foreach(string line in lines) {
    if(!line.StartsWith("Vorher ",StringComparison.Ordinal))continue;
    var m=Regex.Match(line,@"^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+? \| SHA256: [0-9A-F]{64})? \| Hilfsdatei: (.+)$");
    if(!m.Success)return false;
    string dst=m.Groups[3].Value,tmp=m.Groups[4].Value;
    if(!String.Equals(dst,exe,StringComparison.OrdinalIgnoreCase)&&
       !String.Equals(dst,Path.Combine(target,ManualName),StringComparison.OrdinalIgnoreCase))return false;
    if(m.Groups[2].Value=="Sicherung") {
     string bak=Path.Combine(Path.Combine(dir,"Alt"),m.Groups[1].Value);
     if(!File.Exists(bak)||!File.Exists(dst)||Sha256(bak)!=Sha256(dst))return false;
    }
    else if(File.Exists(dst))return false;
    // Hilfsdatei nur im Programmordner und mit dem erwarteten Namen entfernen
    if(String.Equals(Path.GetDirectoryName(tmp),target,StringComparison.OrdinalIgnoreCase)&&
       Path.GetFileName(tmp).StartsWith("."+Path.GetFileName(dst)+".update-",StringComparison.OrdinalIgnoreCase))
     try {if(File.Exists(tmp))File.Delete(tmp);} catch {}
   }
   return true;
  } catch {return false;}
 }

 // Nicht abgeschlossenes Update: nachfragen und die vorherige Version wiederherstellen (RDP Pilot wird dazu beendet
 // und danach neu gestartet). true = Wiederherstellung läuft, RDP Pilot soll sich beenden.
 public static bool OfferRepair(IWin32Window owner) {
  // im Projektordner (Erstellen.bat neben der EXE) wird nie etwas zurückgesetzt, wie beim Update
  if(InProjectFolder)return false;
  // erst unter der Sperre aufräumen, was nichts verändert hat; ist sie belegt, läuft gerade ein Update
  if(!WithTargetLock(CleanupLocked)) {
   MessageBox.Show(owner,Running,AppInfo.FullName,MessageBoxButtons.OK,MessageBoxIcon.Information);
   return false;
  }
  string dir=PendingRestore();
  if(dir==null)return false;
  Log.Write("Update","Nicht abgeschlossenes Update gefunden: "+dir);
  if(MessageBox.Show(owner,PendingText+"\n\n"+AppInfo.FullName+" stellt jetzt die vorherige Version wieder her und startet danach neu.\n\nJetzt wiederherstellen?",
   AppInfo.FullName,MessageBoxButtons.YesNo,MessageBoxIcon.Information)!=DialogResult.Yes)return false;
  try {Repair(dir);return true;}
  catch(Exception ex) {
   Log.Write("Update","Wiederherstellung nicht gestartet: "+ex.Message);
   MessageBox.Show(owner,ex.Message,AppInfo.FullName,MessageBoxButtons.OK,MessageBoxIcon.Warning);
   return false;
  }
 }

 static void Repair(string dir) {
  if(InProjectFolder)throw new Exception("Im Projektordner wird nichts zurückgesetzt.");
  string data=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
  bool protectedDir=String.Equals(Path.GetDirectoryName(dir),data,StringComparison.OrdinalIgnoreCase);
  string exe=Application.ExecutablePath;
  bool writable=CanWrite(Path.GetDirectoryName(exe));
  // Sicherungen aus dem Temp-Ordner nie mit Administratorrechten zurückkopieren (jedes Programm des Benutzers könnte
  // sie verändert haben); dort hat das Update ohne Adminrechte gearbeitet, also reichen sie auch jetzt.
  if(!protectedDir&&!writable)
   throw new Exception("Die vorherige Version lässt sich nicht automatisch wiederherstellen. Bitte "+AppInfo.FullName+" neu herunterladen.");
  bool elevated=protectedDir;
  try {File.Delete(LogFile);} catch {}
  int pid=Process.GetCurrentProcess().Id;
  var vars=new StringBuilder();
  vars.Append("$ProcessId = ").Append(pid).Append("\n");
  Var(vars,"Work",dir);
  Var(vars,"Exe",exe);
  Var(vars,"ManualName",ManualName);
  Var(vars,"LockName",LockName);
  Var(vars,"TargetId",TargetId);
  Var(vars,"Log",LogFile);
  vars.Append("$Elevated = $").Append(elevated?"true":"false").Append("\n");
  vars.Append("$Tray = $false\n");
  var start=PowerShell(vars+RepairScript);
  if(elevated)start.Verb="runas";
  Process repairer;
  try {repairer=Process.Start(start);}
  catch(System.ComponentModel.Win32Exception ex) {
   if(ex.NativeErrorCode==1223)throw new Exception("Wiederherstellung abgebrochen (Administratorrechte nicht erteilt).");
   throw;
  }
  if(!elevated)return;
  // Neustart mit normalen Rechten über den Neustarthelfer (liest das Ergebnis aus dem geschützten Ordner)
  StartRestartHelper(pid,repairer,"",exe,dir);
 }

 // Arbeitsordner abgebrochener Versuche (älter als ein Tag) im Temp-Ordner entfernen; Ordner mit offener
 // Wiederherstellung nie
 static void CleanOldAttempts() {
  try {
   foreach(string dir in Directory.GetDirectories(Folder)) {
    var di=new DirectoryInfo(dir);
    if((di.Attributes&FileAttributes.ReparsePoint)!=0)continue;
    if(File.Exists(Path.Combine(dir,PendingMarker)))continue;
    if(di.LastWriteTimeUtc<DateTime.UtcNow.AddDays(-1))try {di.Delete(true);} catch {}
   }
   try {File.Delete(Path.Combine(Folder,"Update.ps1"));} catch {} // Hilfsskript bis 1.52
   foreach(string f in Directory.GetFiles(Folder,"RDP-Pilot-*.zip"))try {File.Delete(f);} catch {} // Downloads bis 1.53
  } catch {}
 }

 // PowerShell mit dem Skript als Kommando (-EncodedCommand), nur über den festen Systempfad
 static ProcessStartInfo PowerShell(string script) {
  string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
  string ps=Path.Combine(system,@"WindowsPowerShell\v1.0\powershell.exe");
  if(!File.Exists(ps))throw new Exception("Windows PowerShell wurde nicht gefunden.");
  string arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand "+
   Convert.ToBase64String(Encoding.Unicode.GetBytes(Compact(script)));
  if(arguments.Length>32000)throw new Exception("Das Update-Skript ist zu lang.");
  return new ProcessStartInfo(ps,arguments){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=system};
 }

 // Kommentarzeilen und Einrückung weglassen
 static string Compact(string script) {
  var compact=new StringBuilder();
  foreach(string raw in script.Replace("\r","").Split('\n')) {
   string line=raw.Trim();
   if(line.Length==0||line.StartsWith("#",StringComparison.Ordinal))continue;
   compact.Append(line).Append('\n');
  }
  return compact.ToString();
 }

 // Variable für das Skript als PowerShell-Text in einfachen Anführungszeichen
 static void Var(StringBuilder sb,string name,string value) {
  sb.Append('$').Append(name).Append(" = '");
  foreach(char c in value??"") {
   if(c=='\n'||c=='\r')throw new Exception("Ungültiger Pfad für das Update.");
   sb.Append(c);
   // PowerShell nimmt auch typografische einfache Anführungszeichen als Begrenzer: verdoppeln
   if(c=='\''||c=='‘'||c=='’'||c=='‚'||c=='‛')sb.Append(c);
  }
  sb.Append("'\n");
 }

 const string NoZip="Im Release ist keine passende ZIP-Datei.";

 // Ergebnis des letzten Updates ("OK x", "FEHLER …", "ZURUECK …", "OFFEN …") nach dem Neustart mit --nach-update
 // Das Ergebnis ist eine kurze Zeile: höchstens 16 KB lesen (ein größerer Inhalt gilt als unbekanntes Ergebnis)
 public static string LastResult() {
  try {
   if(!File.Exists(LogFile))return null;
   using(var fs=new FileStream(LogFile,FileMode.Open,FileAccess.Read,FileShare.Read)) {
    if(fs.Length>16*1024)return "FEHLER Ergebnis des Updates unlesbar (Update.log zu groß).";
    return Encoding.UTF8.GetString(ReadLimited(fs,16*1024)).Trim().TrimStart('\uFEFF');
   }
  } catch {return null;}
 }

 public static void OpenPage(string url) {
  try {Process.Start(new ProcessStartInfo(String.IsNullOrEmpty(url)?ReleasesPage:url){UseShellExecute=true});} catch {}
 }

 // Skripte wie Dock Pilot ab 1.70 (Update-Logik aus WindowGrid 1.65), nur Namen angepasst. Nur ASCII-Anführungszeichen.
 // InstallLoader: steht mit den Werten auf der Befehlszeile; liest $ScriptFile einmal in den Speicher, prüft die SHA-256
 //   gegen $ScriptHash und führt genau diese Bytes aus. Weicht sie ab oder fehlt die Datei, wird nichts verändert.
 // InstallScript: Davor setzt das Programm $ProcessId, $Zip, $ZipSize, $Manifest, $Exe, $Version, $Sha256, $AssetName,
 //   $MainKey, $ReserveKey, $LockName, $TargetId, $TempRoot, $Log, $Work, $ExeName, $ManualName, $Elevated, $Tray.
 // RepairScript: Wiederherstellung nach einem nicht abgeschlossenen Update ($ProcessId, $Work, $Exe, $ManualName,
 //   $TargetId, $Log, $Elevated, $Tray). Ziele nur EXE oder Liesmich, Sicherungen nur aus <Work>\Alt\<Nummer>;
 //   im Projektordner nie. Ergebnis: ZURUECK, FEHLER (nichts verändert) oder OFFEN (kein Neustart).
 // RestartScript: Neustart nach einem Update mit Administratorrechten, selbst mit normalen Rechten
 //   ($ProcessId, $Installer, $Attempt, $Exe, $Log, $Work, $Tray); startet nur bei OK/FEHLER/ZURUECK ohne Merker.
 const string InstallLoader=@"
$ErrorActionPreference = 'Stop'
# geschuetzten Arbeitsordner anlegen: Besitzer Administratoren, schreiben nur SYSTEM und Administratoren, lesen angemeldete Benutzer
function New-WorkFolder {
  $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
  $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
  $root = [Environment]::GetFolderPath('CommonApplicationData')
  if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('RDP-Pilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
  $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
  $none = [System.Security.AccessControl.PropagationFlags]::None
  $sec = New-Object System.Security.AccessControl.DirectorySecurity
  $sec.SetAccessRuleProtection($true, $false)
  $sec.SetOwner($admins)
  foreach ($sid in @($admins, $system)) {
    $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $inherit, $none, 'Allow')))
  }
  $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $inherit, $none, 'Allow')))
  if (Test-Path -LiteralPath $Work) { throw 'Der Arbeitsordner fuer das Update existiert bereits.' }
  [void][System.IO.Directory]::CreateDirectory($Work, $sec)
  $o = (Get-Acl -LiteralPath $Work).GetOwner([System.Security.Principal.SecurityIdentifier])
  if (-not ($o -eq $admins -or $o -eq $system)) { throw 'Der Arbeitsordner fuer das Update ist nicht geschuetzt.' }
}
$code = $null
try {
  # hoechstens 256 KB lesen; waehrend des Lesens darf niemand schreiben (FileShare.Read)
  $fs = [System.IO.File]::Open($ScriptFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt 262144) { throw 'Das Update-Skript ist zu gross.' }
    $b = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $b.Length) { $k = $fs.Read($b, $n, $b.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $b.Length) { throw 'Das Update-Skript ist unvollstaendig.' }
  } finally { $fs.Dispose() }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $h = [System.BitConverter]::ToString($sha.ComputeHash($b)).Replace('-', '')
  $sha.Dispose()
  if ($h -cne $ScriptHash) { throw 'Das Update-Skript wurde veraendert.' }
  $code = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($b)
} catch {
  # eindeutiger Status: Skript nicht geladen, nichts veraendert (FEHLER = bisherige Version darf wieder starten)
  $msg = 'FEHLER Update-Skript nicht geladen, nichts veraendert: ' + $_.Exception.Message
  if ($Elevated) {
    # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner; den Neustart macht der nicht erhoehte Helfer
    try { New-WorkFolder; $msg | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {}
  } else {
    try { $msg | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    # nach der Frist erneut pruefen: laeuft die alte Instanz noch, nicht starten (der Start wuerde ohnehin abgefangen)
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
      try { ($msg + ' RDP Pilot wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
    }
    elseif ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
    else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
  }
  exit 1
}
. ([scriptblock]::Create($code))
";

 const string InstallScript=@"
$ErrorActionPreference = 'Stop'
# Dateien begrenzt lesen bzw. kopieren; waehrenddessen darf niemand schreiben (FileShare.Read). Bei Ueberschreitung Abbruch, Teilkopie weg.
function Read-Limited([string]$path, [long]$max) {
  $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  try {
    if ($fs.Length -gt $max) { throw ('Die Datei ist zu gross: ' + $path) }
    $buf = New-Object byte[] ([int]$fs.Length)
    $n = 0
    while ($n -lt $buf.Length) { $k = $fs.Read($buf, $n, $buf.Length - $n); if ($k -le 0) { break }; $n += $k }
    if ($n -ne $buf.Length) { throw ('Die Datei ist unvollstaendig: ' + $path) }
    return ,$buf
  } finally { $fs.Dispose() }
}
function Copy-Limited([string]$src, [string]$dst, [long]$max) {
  $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  $out = $null
  try {
    if ($in.Length -gt $max) { throw ('Die Datei ist zu gross: ' + $src) }
    $out = [System.IO.File]::Open($dst, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $buf = New-Object byte[] 65536
    $total = 0
    while (($k = $in.Read($buf, 0, $buf.Length)) -gt 0) {
      $total += $k
      if ($total -gt $max) { throw ('Die Datei ist zu gross: ' + $src) }
      $out.Write($buf, 0, $k)
    }
    $out.Dispose()
    $out = $null
  } catch {
    if ($out) { $out.Dispose(); $out = $null; Remove-Item -LiteralPath $dst -Force -ErrorAction SilentlyContinue }
    throw
  } finally {
    if ($out) { $out.Dispose() }
    $in.Dispose()
  }
}
# ZIP hoechstens so gross wie heruntergeladen und nie ueber 64 MB
$zipMax = [Math]::Min([long]$ZipSize, [long]67108864)
$target = Split-Path $Exe -Parent
$workReady = -not $Elevated
$result = $null
$lock = $null
$haveLock = $false
$noRestart = $false
$archive = $null
$zipStream = $null
try {
  # nur ein Update zur Zeit (Sperre bis zum Ende dieses Skripts)
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  # erst nach bestaetigtem Ende der alten Instanz Dateien aendern
  $deadline = (Get-Date).AddSeconds(60)
  while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { throw 'RDP Pilot wurde nicht beendet.' }
    Start-Sleep -Milliseconds 250
  }
  Start-Sleep -Milliseconds 500
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('RDP-Pilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
    # alte Arbeitsordner frueherer Updates entfernen: nur eigene (Besitzer Administratoren), keine Verknuepfungen,
    # und nur nachweislich inaktive: gleiches Ziel (diese Sperre halten wir) oder anderes Ziel ohne gehaltene Sperre
    # und mindestens eine Stunde unveraendert; Ordner ohne Zielkennung (aeltere Versionen) nur nach einer Stunde
    Get-ChildItem -LiteralPath $root -Directory -Filter 'RDP-Pilot-Update-*' -Force -ErrorAction SilentlyContinue | ForEach-Object {
      try {
        if (-not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
          $o = (Get-Acl -LiteralPath $_.FullName).GetOwner([System.Security.Principal.SecurityIdentifier])
          if ($o -eq $admins -or $o -eq $system) {
            $inactive = $false
            if ($_.Name -match '^RDP-Pilot-Update-([0-9A-F]{24})-[0-9a-f]{32}$') {
              if ($Matches[1] -ceq $TargetId) { $inactive = $true }
              else {
                $held = $true
                try { $other = $null; $held = [System.Threading.Mutex]::TryOpenExisting('Global\RDP-Pilot-Update-' + $Matches[1], [ref]$other); if ($other) { $other.Dispose() } } catch { $held = $true }
                $inactive = (-not $held) -and ($_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1))
              }
            } else {
              $inactive = $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1)
            }
            if (Test-Path -LiteralPath (Join-Path $_.FullName 'Wiederherstellung-offen')) { $inactive = $false }
            if ($inactive) { [System.IO.Directory]::Delete($_.FullName, $true) }
          }
        }
      } catch {}
    }
    # Arbeitsordner gleich mit Rechten anlegen (New-WorkFolder steht im Lader)
    New-WorkFolder
    $workReady = $true
    $zipWork = Join-Path $Work 'Update.zip'
    $manifestWork = Join-Path $Work 'Update.manifest'
    Copy-Limited $Zip $zipWork $zipMax
    Copy-Limited $Manifest $manifestWork 8192
  } else {
    $zipWork = $Zip
    $manifestWork = $Manifest
  }
  # Signatur des Manifests selbst pruefen (eigene Herausgeberpruefung, an der Kopie, die verwendet wird)
  $bytes = Read-Limited $manifestWork 8192
  $marker = [System.Text.Encoding]::ASCII.GetBytes([string][char]10 + 'Signatur=')
  $at = -1
  for ($i = 0; $i -le $bytes.Length - $marker.Length -and $at -lt 0; $i++) {
    $j = 0
    while ($j -lt $marker.Length -and $bytes[$i + $j] -eq $marker[$j]) { $j++ }
    if ($j -eq $marker.Length) { $at = $i }
  }
  if ($at -lt 0) { throw 'Im Manifest fehlt die Signatur.' }
  $signed = New-Object byte[] ($at + 1)
  [System.Array]::Copy($bytes, $signed, $at + 1)
  $sig = [System.Convert]::FromBase64String([System.Text.Encoding]::ASCII.GetString($bytes, $at + $marker.Length, $bytes.Length - $at - $marker.Length).Trim())
  $lines = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($signed).Split([char]10)
  $names = @('Programm', 'Version', 'Datei', 'SHA256', 'Schluessel', 'Erstellt')
  if ($lines.Count -ne 7 -or $lines[6] -ne '') { throw 'Das Manifest hat nicht den erwarteten Aufbau.' }
  $mv = @{}
  for ($i = 0; $i -lt 6; $i++) {
    $p = $names[$i] + '='
    if (-not $lines[$i].StartsWith($p, [System.StringComparison]::Ordinal)) { throw ('Im Manifest fehlt ' + $names[$i] + '.') }
    $mv[$names[$i]] = $lines[$i].Substring($p.Length)
  }
  if ($mv['Schluessel'] -ceq 'Haupt') { $keyXml = $MainKey }
  elseif ($mv['Schluessel'] -ceq 'Reserve') { $keyXml = $ReserveKey }
  else { throw 'Unbekannter Schluessel im Manifest.' }
  $csp = New-Object System.Security.Cryptography.CspParameters -ArgumentList 24
  $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList $csp
  $rsa.PersistKeyInCsp = $false
  $rsa.FromXmlString($keyXml)
  $valid = $rsa.VerifyData($signed, 'SHA256', $sig)
  $rsa.Clear()
  if (-not $valid) { throw 'Die Signatur des Updates ist ungueltig.' }
  if ($mv['Programm'] -cne 'RDP Pilot' -or $mv['Version'] -cne $Version -or $mv['Datei'] -cne $AssetName) { throw 'Das Manifest passt nicht zu diesem Update.' }
  if ($mv['SHA256'] -cnotmatch '^[0-9a-f]{64}$') { throw 'Die Pruefsumme im Manifest ist ungueltig.' }
  $expected = $mv['SHA256'].ToUpperInvariant()
  if ($expected -ne $Sha256) { throw 'Die Pruefsumme im Manifest weicht von der Pruefung im Programm ab.' }
  # ZIP einmal oeffnen (waehrenddessen darf niemand schreiben oder loeschen), Pruefsumme ueber genau diesen Stream
  # berechnen und denselben Stream entpacken; er bleibt bis nach dem Austausch offen
  $zipStream = [System.IO.File]::Open($zipWork, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
  if ($zipStream.Length -gt $zipMax) { throw 'Das Update ist zu gross.' }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $hash = [System.BitConverter]::ToString($sha.ComputeHash($zipStream)).Replace('-', '')
  $sha.Dispose()
  if ($hash -ne $expected) { throw ('Die Pruefsumme des Updates stimmt nicht (erwartet ' + $expected + ', erhalten ' + $hash + ').') }
  $zipStream.Position = 0
  Add-Type -AssemblyName System.IO.Compression
  $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
  # nur bekannte Eintraege (feste Namen im Stammordner), je Datei begrenzt; geschrieben wird direkt aus dem Archiv ins Ziel
  function Find-Entry([string]$name, [long]$max) {
    $hits = @($archive.Entries | Where-Object { $_.FullName -eq $name })
    if ($hits.Count -gt 1) { throw ('Im ZIP steht ' + $name + ' mehrfach.') }
    if ($hits.Count -eq 0) { return $null }
    if ($hits[0].Length -gt $max) { throw ('Im ZIP ist ' + $name + ' zu gross.') }
    return $hits[0]
  }
  # erst vollstaendig in eine Hilfsdatei im Zielordner schreiben, auf den Datentraeger bringen und pruefen,
  # dann das Ziel in einem Schritt ersetzen (File.Replace bzw. File.Move): Das Ziel ist nie halb geschrieben
  function Write-Entry($entry, [string]$dst, [string]$tmp, [long]$max) {
    try {
      $in = $entry.Open()
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
          $buf = New-Object byte[] 65536
          $total = 0
          while (($k = $in.Read($buf, 0, $buf.Length)) -gt 0) {
            $total += $k
            if ($total -gt $max) { throw ('Im ZIP ist ' + $entry.FullName + ' zu gross.') }
            $out.Write($buf, 0, $k)
          }
          $out.Flush($true)
        } finally { $out.Dispose() }
      } finally { $in.Dispose() }
      if ($total -ne $entry.Length) { throw ('Im ZIP ist ' + $entry.FullName + ' unvollstaendig.') }
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  # Ruecksicherung ebenso: Sicherung erst vollstaendig in eine Hilfsdatei im Zielordner, dann das Ziel in einem Schritt ersetzen
  function Restore-File([string]$src, [string]$dst) {
    $tmp = Join-Path (Split-Path $dst -Parent) ('.' + (Split-Path $dst -Leaf) + '.zurueck-' + [Guid]::NewGuid().ToString('N'))
    try {
      $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
        if ((Get-Item -LiteralPath $tmp).Length -ne $in.Length) { throw ('Ruecksicherung von ' + $dst + ' unvollstaendig.') }
      } finally { $in.Dispose() }
      # [NullString]::Value statt $null: PowerShell macht aus $null sonst einen leeren Text (ungueltiger Pfad)
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  $exeEntry = Find-Entry $ExeName 33554432
  if (-not $exeEntry) { throw ('Im ZIP fehlt ' + $ExeName) }
  $pairs = @(,@($exeEntry, $Exe, 33554432))
  $manualEntry = Find-Entry $ManualName 1048576
  if ($manualEntry) { $pairs += ,@($manualEntry, (Join-Path $target $ManualName), 1048576) }
  # unter der Sperre erneut nach offenen Wiederherstellungen fuer dieses Ziel suchen (ein anderer Versuch kann seit
  # der Vorpruefung im Programm abgebrochen sein); Treffer oder fehlgeschlagene Pruefung = nichts austauschen
  $open = $null
  try {
    $roots = @(@($TempRoot, ($TargetId + '-*')), @([Environment]::GetFolderPath('CommonApplicationData'), ('RDP-Pilot-Update-' + $TargetId + '-*')))
    foreach ($r in $roots) {
      if (Test-Path -LiteralPath $r[0]) {
        foreach ($d in @(Get-ChildItem -LiteralPath $r[0] -Directory -Filter $r[1] -Force -ErrorAction Stop)) {
          if ($d.FullName -ne $Work -and (Test-Path -LiteralPath (Join-Path $d.FullName 'Wiederherstellung-offen'))) { $open = $d.FullName }
        }
      }
    }
  } catch { throw ('Offene Wiederherstellungen konnten nicht geprueft werden: ' + $_.Exception.Message) }
  if ($open) { throw ('Eine fruehere Wiederherstellung ist noch offen (' + $open + ').') }
  # Rueckfallkopien im eigenen Arbeitsordner, nicht neben den Programmdateien
  $alt = Join-Path $Work 'Alt'
  New-Item -ItemType Directory -Path $alt -Force | Out-Null
  # Journal zuerst (endet mit ""Bereit""), dann der Status: Ein Status ohne vollstaendiges Journal gilt als beschaedigt.
  # Wiederherstellungsjournal: vor jedem Austausch Zielpfad, Sicherungsdatei und ob das Ziel vorher existierte
  $journal = Join-Path $Work 'Wiederherstellung.txt'
  @(('RDP Pilot ' + $Version + ' - Wiederherstellungsjournal'),
    'Jede Zeile ""Vorher"" gilt als moeglicherweise ueberschrieben, auch ohne folgende Zeile ""Ersetzt"" (Abbruch waehrend des Kopierens).',
    'Wiederherstellen fuer jede Zeile ""Vorher"": bei ""Sicherung"" die Sicherungsdatei auf das Ziel zurueckkopieren (sie ist vollstaendig), bei ""Neu"" die Zieldatei loeschen, falls vorhanden.',
    '""Ersetzt n"" bestaetigt nur, dass der Austausch dieser Datei abgeschlossen wurde. Eine uebrig gebliebene ""Hilfsdatei"" kann geloescht werden.',
    '', 'Bereit') | Set-Content -LiteralPath $journal -Encoding utf8 -ErrorAction Stop
  # Status vor dem ersten Ueberschreiben dauerhaft anlegen: bricht der Austausch ab (auch hart beendet), bleibt der
  # Arbeitsordner geschuetzt und neue Updates gesperrt. Laesst er sich nicht schreiben, wird nichts ausgetauscht.
  $pending = Join-Path $Work 'Wiederherstellung-offen'
  $alt | Out-File -LiteralPath $pending -Encoding utf8 -ErrorAction Stop
  if (-not (Test-Path -LiteralPath $pending)) { throw 'Statusdatei konnte nicht angelegt werden.' }
  $done = @()
  $i = 0
  try {
    foreach ($p in $pairs) {
      $i++
      $bak = Join-Path $alt ([string]$i)
      $tmp = Join-Path (Split-Path $p[1] -Parent) ('.' + (Split-Path $p[1] -Leaf) + '.update-' + [Guid]::NewGuid().ToString('N'))
      $had = Test-Path -LiteralPath $p[1]
      if ($had) {
        # erst vollstaendig unter Hilfsnamen kopieren, dann umbenennen: vorhandene Sicherungsdatei = vollstaendig
        Copy-Item -LiteralPath $p[1] -Destination ($bak + '.teil') -Force
        Move-Item -LiteralPath ($bak + '.teil') -Destination $bak -Force
        $bakHash = (Get-FileHash -LiteralPath $bak -Algorithm SHA256).Hash
        $line = 'Vorher ' + $i + ': Sicherung | Ziel: ' + $p[1] + ' | Sicherungsdatei: ' + $bak + ' | SHA256: ' + $bakHash + ' | Hilfsdatei: ' + $tmp
      } else {
        $line = 'Vorher ' + $i + ': Neu | Ziel: ' + $p[1] + ' | Hilfsdatei: ' + $tmp
      }
      Add-Content -LiteralPath $journal -Value $line -Encoding utf8 -ErrorAction Stop
      $done += ,@($p[1], $bak, $had)
      Write-Entry $p[0] $p[1] $tmp $p[2]
      Add-Content -LiteralPath $journal -Value ('Ersetzt ' + $i) -Encoding utf8 -ErrorAction SilentlyContinue
    }
  } catch {
    # jede Datei einzeln zuruecksetzen, Fehler sammeln; bei unvollstaendiger Ruecksicherung Sicherung behalten und nicht neu starten
    $err = $_.Exception.Message
    $failed = @()
    foreach ($d in $done) {
      try {
        if ($d[2]) { Restore-File $d[1] $d[0] }
        else { Remove-Item -LiteralPath $d[0] -Force -ErrorAction Stop }
      } catch { $failed += $d[0] }
    }
    if ($failed.Count -gt 0) {
      # Status bleibt stehen: Arbeitsordner wird nie automatisch bereinigt, neue Updates sind gesperrt
      $noRestart = $true
      throw ('Ruecksicherung unvollstaendig (' + ($failed -join ', ') + '). Die alten Dateien liegen in ' + $alt + ', die Zuordnung steht in ' + $journal + '. Fehler: ' + $err)
    }
    # Ruecksicherung vollstaendig bestaetigt: Status entfernen; klappt das nicht, sicherheitshalber nicht neu starten
    try { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force -ErrorAction Stop } } catch { $noRestart = $true }
    throw
  }
  # Installation vollstaendig: erst Status entfernen, dann die Rueckfallkopien
  try { Remove-Item -LiteralPath $pending -Force -ErrorAction Stop }
  catch { $noRestart = $true; throw ('Update installiert, aber der Status in ' + $Work + ' konnte nicht entfernt werden.') }
  Remove-Item -LiteralPath $alt -Recurse -Force -ErrorAction SilentlyContinue
  $archive.Dispose()
  $zipStream.Dispose()
  Remove-Item -LiteralPath $zipWork -Force -ErrorAction SilentlyContinue
  if ($Elevated) { Remove-Item -LiteralPath $manifestWork -Force -ErrorAction SilentlyContinue }
  $result = 'OK ' + $Version
} catch {
  if ($archive) { try { $archive.Dispose() } catch {} }
  if ($zipStream) { try { $zipStream.Dispose() } catch {} }
  # Abschlussstatus: FEHLER = nichts veraendert bzw. vollstaendig zurueckgesetzt (Neustart erlaubt),
  # OFFEN = Ruecksicherung unvollstaendig (kein Neustart, Sicherung bleibt)
  if ($noRestart) { $result = 'OFFEN ' + $_.Exception.Message } else { $result = 'FEHLER ' + $_.Exception.Message }
}
if ($Elevated) {
  # Ergebnis nur in den eigenen, geschuetzten Arbeitsordner schreiben; Neustart macht der nicht erhoehte Prozess
  if ($workReady) { try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {} }
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if (-not $noRestart) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if (-not $Elevated -and -not $noRestart) {
  # nur starten, wenn die alte Instanz beendet ist (z. B. nicht nach Ablauf der Wartefrist)
  if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
    try { ($result + ' RDP Pilot wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
  }
  elseif ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
  else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
}
";

 const string RepairScript=@"
$ErrorActionPreference = 'Stop'
$result = $null
$noRestart = $false
$haveLock = $false
$lock = $null
$target = Split-Path $Exe -Parent
try {
  $deadline = (Get-Date).AddSeconds(60)
  while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
  if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { throw 'RDP Pilot wurde nicht beendet.' }
  # im Projektordner (Erstellen.bat neben der EXE) nie etwas zuruecksetzen
  if (Test-Path -LiteralPath (Join-Path $target 'Erstellen.bat')) { throw 'Im Projektordner wird nichts zurueckgesetzt.' }
  # dieselbe Sperre wie das Update: nie zwei Wiederherstellungen oder Update und Wiederherstellung gleichzeitig
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  # unter der Sperre erneut pruefen: Eine andere Wiederherstellung kann inzwischen fertig sein
  if (-not (Test-Path -LiteralPath (Join-Path $Work 'Wiederherstellung-offen'))) { throw 'Es ist keine Wiederherstellung mehr offen.' }
  $item = Get-Item -LiteralPath $Work -Force
  if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'Unerwarteter Arbeitsordner.' }
  if ($Elevated) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $root = [Environment]::GetFolderPath('CommonApplicationData')
    if ((Split-Path $Work -Parent) -ne $root -or (Split-Path $Work -Leaf) -notlike ('RDP-Pilot-Update-' + $TargetId + '-*')) { throw 'Unerwarteter Arbeitsordner.' }
    $o = (Get-Acl -LiteralPath $Work).GetOwner([System.Security.Principal.SecurityIdentifier])
    if (-not ($o -eq $admins -or $o -eq $system)) { throw 'Der Arbeitsordner ist nicht geschuetzt.' }
  }
  # Erhoeht: Arbeitsordner, Journal, Ordner Alt und jede Sicherung muessen den Administratoren oder SYSTEM gehoeren,
  # duerfen keine Umleitung sein und duerfen fuer niemand anderen beschreibbar sein (wirksame ACL, ohne reine
  # Vererbungseintraege). Sonst koennte ein Prozess ohne Adminrechte untergeschobene Dateien zuruecksetzen lassen.
  $trusted = @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
  $writeMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
  function Assert-Protected([string]$p) {
    if (-not $Elevated) { return }
    $it = Get-Item -LiteralPath $p -Force
    if ($it.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw ('Unerwartete Umleitung: ' + $p) }
    $acl = Get-Acl -LiteralPath $p
    if ($trusted -notcontains $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value) { throw ('Nicht geschuetzt (Besitzer): ' + $p) }
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
      if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
      if ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
      if ($trusted -contains $rule.IdentityReference.Value) { continue }
      if (([int64]$rule.FileSystemRights -band $writeMask) -ne 0) { throw ('Nicht geschuetzt (Schreibrecht fuer ' + $rule.IdentityReference.Value + '): ' + $p) }
    }
  }
  Assert-Protected $Work
  function Restore-File([string]$src, [string]$dst) {
    $tmp = Join-Path (Split-Path $dst -Parent) ('.' + (Split-Path $dst -Leaf) + '.zurueck-' + [Guid]::NewGuid().ToString('N'))
    try {
      $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
      try {
        $out = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $in.CopyTo($out); $out.Flush($true) } finally { $out.Dispose() }
        if ((Get-Item -LiteralPath $tmp).Length -ne $in.Length) { throw ('Ruecksicherung von ' + $dst + ' unvollstaendig.') }
      } finally { $in.Dispose() }
      if (Test-Path -LiteralPath $dst) { [System.IO.File]::Replace($tmp, $dst, [NullString]::Value) }
      else { [System.IO.File]::Move($tmp, $dst) }
    } catch {
      Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      throw
    }
  }
  $allowed = @($Exe, (Join-Path $target $ManualName))
  $journal = Join-Path $Work 'Wiederherstellung.txt'
  # Journal vollstaendig pruefen, bevor irgendetwas zurueckgesetzt wird. Fehlt es, ist es zu gross, fehlt ""Bereit""
  # oder passt ein Eintrag nicht: Stand unbekannt, Merker bleibt und es wird nicht neu gestartet (OFFEN).
  $noRestart = $true
  if (-not (Test-Path -LiteralPath $journal -PathType Leaf)) { throw 'Das Wiederherstellungsjournal fehlt.' }
  Assert-Protected $journal
  if ((Get-Item -LiteralPath $journal).Length -gt 65536) { throw 'Das Wiederherstellungsjournal ist zu gross.' }
  $lines = [System.IO.File]::ReadAllLines($journal)
  $ready = $false
  $seen = @{}
  $entries = @()
  foreach ($line in $lines) {
    if ($line -eq 'Bereit') { $ready = $true; continue }
    if ($line.StartsWith('Ersetzt ')) { if ($line -notmatch '^Ersetzt \d+$') { throw 'Das Journal ist nicht lesbar.' }; continue }
    if (-not $line.StartsWith('Vorher ')) { if ($ready) { throw 'Das Journal ist nicht lesbar.' }; continue }
    if (-not $ready) { throw 'Das Journal ist unvollstaendig.' }
    if ($line -notmatch '^Vorher (\d+): (Sicherung|Neu) \| Ziel: (.+?)(?: \| Sicherungsdatei: .+? \| SHA256: ([0-9A-F]{64}))? \| Hilfsdatei: (.+)$') { throw 'Das Journal ist nicht lesbar.' }
    $e = @{ N = $Matches[1]; Kind = $Matches[2]; Dst = $Matches[3]; Hash = $Matches[4]; Tmp = $Matches[5] }
    if ($seen.ContainsKey($e.N)) { throw 'Das Journal ist nicht lesbar.' }
    $seen[$e.N] = $true
    if ($allowed -notcontains $e.Dst) { throw 'Unerwarteter Eintrag im Journal.' }
    if ($e.Kind -eq 'Sicherung') {
      if (-not $e.Hash) { throw 'Das Journal ist nicht lesbar.' }
      $alt = Join-Path $Work 'Alt'
      $e.Bak = Join-Path $alt $e.N
      if (-not (Test-Path -LiteralPath $e.Bak -PathType Leaf)) { throw ('Sicherung fehlt: ' + $e.Bak) }
      Assert-Protected $alt
      Assert-Protected $e.Bak
      if ((Get-FileHash -LiteralPath $e.Bak -Algorithm SHA256).Hash -ne $e.Hash) { throw ('Sicherung veraendert: ' + $e.Bak) }
    }
    $entries += ,$e
  }
  if (-not $ready) { throw 'Das Journal ist unvollstaendig.' }
  $noRestart = $false
  $failed = @()
  foreach ($e in $entries) {
    $kind = $e.Kind; $dst = $e.Dst; $tmp = $e.Tmp
    try {
      if ($kind -eq 'Sicherung') {
        $bak = $e.Bak
        $same = (Test-Path -LiteralPath $dst) -and ((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash -eq $e.Hash)
        if (-not $same) { Restore-File $bak $dst }
      } elseif (Test-Path -LiteralPath $dst) {
        Remove-Item -LiteralPath $dst -Force
      }
      if ((Split-Path $tmp -Parent) -eq $target -and (Split-Path $tmp -Leaf) -like ('.' + (Split-Path $dst -Leaf) + '.update-*')) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
      }
    } catch { $failed += ($dst + ': ' + $_.Exception.Message) }
  }
  if ($failed.Count -gt 0) { $noRestart = $true; throw ('Wiederherstellung unvollstaendig (' + ($failed -join '; ') + ').') }
  Remove-Item -LiteralPath (Join-Path $Work 'Wiederherstellung-offen') -Force
  $result = 'ZURUECK Die vorherige Version wurde wiederhergestellt.'
} catch {
  if ($noRestart) { $result = 'OFFEN ' + $_.Exception.Message } else { $result = 'FEHLER ' + $_.Exception.Message }
}
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if ($Elevated) {
  # Ergebnis in den geschuetzten Arbeitsordner; den Neustart macht der nicht erhoehte Neustarthelfer
  try { $result | Out-File -LiteralPath (Join-Path $Work 'Update.log') -Encoding utf8 } catch {}
} else {
  try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch { $noRestart = $true }
  if ($result.StartsWith('ZURUECK')) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
  if (-not $noRestart -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
    if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
    else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
  }
}
";

 const string RestartScript=@"
$ErrorActionPreference = 'SilentlyContinue'
# Neustart erst nach bestaetigtem Ende des Installers; bei Fristablauf Fehler melden und nicht starten
$end = (Get-Date).AddMinutes(15)
$workLog = Join-Path $Work 'Update.log'
$finished = $false
while ((Get-Date) -lt $end) {
  if ($Installer -gt 0) { if (-not (Get-Process -Id $Installer -ErrorAction SilentlyContinue)) { $finished = $true; break } }
  elseif (Test-Path -LiteralPath $workLog) { $finished = $true; break }
  Start-Sleep -Milliseconds 500
}
if (-not $finished) {
  'FEHLER Das Update ist nach 15 Minuten nicht fertig geworden. RDP Pilot wurde nicht neu gestartet.' | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
Start-Sleep -Milliseconds 300
$text = $null
if (Test-Path -LiteralPath $workLog) { $text = Get-Content -LiteralPath $workLog -Raw -Encoding UTF8 }
if ($text) { $text = $text.Trim() }
if ($Attempt) { Remove-Item -LiteralPath $Attempt -Recurse -Force }
# Neustart nur bei eindeutigem Abschluss: installiert (OK) oder nichts veraendert bzw. vollstaendig zurueckgesetzt (FEHLER)
$clear = $text -and ($text -match '^(OK|FEHLER|ZURUECK) ') -and -not (Test-Path -LiteralPath (Join-Path $Work 'Wiederherstellung-offen'))
if (-not $clear) {
  if (-not $text) { $text = 'OFFEN Ergebnis des Updates unbekannt.' }
  ($text + ' RDP Pilot wurde nicht neu gestartet.') | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
$text | Out-File -LiteralPath $Log -Encoding utf8
# alte Instanz muss beendet sein (Ladefehler kommen sehr frueh)
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
  ($text + ' RDP Pilot wurde nicht neu gestartet, weil die alte Instanz noch laeuft.') | Out-File -LiteralPath $Log -Encoding utf8
  exit
}
if ($Tray) { Start-Process -FilePath $Exe -ArgumentList '--nach-update','/tray' }
else { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
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
 // Bei wenig Platz zweizeilig: oben fett "Neue Version x", darunter grau "Installiert ist y" (null = immer einzeilig)
 string newVersion;
 public string NewVersion {get {return newVersion;} set {newVersion=value;Invalidate();}}
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
  const TextFormatFlags flags=TextFormatFlags.Left|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix;
  int room=Math.Max(10,right-x);
  if(newVersion!=null&&TextRenderer.MeasureText(g,Text,Font,new Size(int.MaxValue,Height),flags|TextFormatFlags.NoPadding).Width>room) {
   // zweizeilig: Zeilen mit NoPadding messen und als Block senkrecht mittig setzen
   string top="Neue Version "+newVersion,bottom="Installiert ist "+AppInfo.Version;
   Font small=Ui.UiFont;
   int h1=TextRenderer.MeasureText(g,top,Font,new Size(int.MaxValue,Height),flags|TextFormatFlags.NoPadding).Height;
   int h2=TextRenderer.MeasureText(g,bottom,small,new Size(int.MaxValue,Height),flags|TextFormatFlags.NoPadding).Height;
   int gap=Ui.Px(2),y=(Height-h1-gap-h2)/2;
   TextRenderer.DrawText(g,top,Font,new Rectangle(x,y,room,h1),Ui.TextColor,flags|TextFormatFlags.NoPadding);
   TextRenderer.DrawText(g,bottom,small,new Rectangle(x,y+h1+gap,room,h2),Ui.Muted,flags|TextFormatFlags.NoPadding);
  } else
   TextRenderer.DrawText(g,Text,Font,new Rectangle(x,0,room,Height),Ui.TextColor,flags|TextFormatFlags.VerticalCenter);
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