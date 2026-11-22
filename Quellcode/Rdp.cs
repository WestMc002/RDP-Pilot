// RDP Pilot – RDP-Sitzungen auf den richtigen Monitor lenken
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RDPilot {

// Start einer Verbindung, gleich für Oberfläche und Kommandozeile: Die Datei wird einmal frisch eingelesen und
// geprüft, mstsc bekommt eine eigene Kopie genau dieser Fassung (nicht die Originaldatei, die sich bis zum Start
// noch ändern könnte). Kopien liegen in %TEMP%\RDP Pilot\<zufällig>\ und werden nach 10 Minuten gelöscht.
public static class RdpLaunch {
 // Systemprogramme nur über den festen Pfad starten, nie über den Suchpfad
 public static string Mstsc {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"mstsc.exe");}}
 public static string TempRoot {get {return Path.Combine(Path.GetTempPath(),AppInfo.FullName);}}

 public static void CleanTemp(TimeSpan maxAge) {
  try {
   if(!Directory.Exists(TempRoot))return;
   DateTime limit=DateTime.UtcNow-maxAge;
   foreach(string f in Directory.GetFiles(TempRoot,"*.rdp")) // Kopien bis 1.52 direkt im Ordner
    try {if(File.GetLastWriteTimeUtc(f)<limit)File.Delete(f);} catch {}
   foreach(string d in Directory.GetDirectories(TempRoot))
    try {if(Directory.GetLastWriteTimeUtc(d)<limit)Directory.Delete(d,true);} catch {}
  } catch {}
 }

 // Liest die Datei frisch ein. problem: Text, wenn ein Zieleintrag mehrfach vorkommt (dann nicht starten).
 public static RdpFile Read(string path,out string problem) {
  var fresh=RdpFile.Load(path);
  string key=fresh.AmbiguousKey();
  problem=key==null?null:"„"+Path.GetFileNameWithoutExtension(path)+"“ enthält den Eintrag „"+key+"“ mehrfach – unklar, wohin verbunden würde. Bitte die Datei bereinigen.";
  return fresh;
 }

 // Kopie der geprüften Fassung anlegen (modify ändert nur die Kopie) und mstsc damit starten
 public static void Start(RdpFile checkedFile,Action<RdpFile> modify) {
  CleanTemp(TimeSpan.FromMinutes(10));
  // Pro Start ein eigener Unterordner, damit sich gleichnamige Verbindungen nicht überschreiben;
  // der Dateiname bleibt (mstsc zeigt ihn im Fenstertitel)
  string dir=Path.Combine(TempRoot,Guid.NewGuid().ToString("N").Substring(0,12));
  Directory.CreateDirectory(dir);
  var copy=checkedFile.CopyTo(Path.Combine(dir,Path.GetFileName(checkedFile.Path)));
  if(modify!=null)modify(copy);
  copy.Save(false);
  Process.Start(new ProcessStartInfo(Mstsc,"\""+copy.Path+"\""){UseShellExecute=false});
 }
}

// Ein lokaler Monitor. Nr = Nummer von links nach rechts (Anzeige), Id = Monitor-ID für mstsc
public class MonitorInfo {
 public int Nr;
 public int Id;
 public string Name;
 public Rectangle Bounds;
 public bool Primary;
 public override string ToString() {
  return "Monitor "+Nr+": "+Bounds.Width+" x "+Bounds.Height+(Primary?" (primär)":"");
 }
}

public static class Monitors {
 // Feste Beispielmonitore für die Demo-Screenshots (sonst null = echte Monitore)
 public static List<MonitorInfo> Override;

 public static List<MonitorInfo> Load() {
  if(Override!=null)return new List<MonitorInfo>(Override);
  var all=Screen.AllScreens;
  var list=new List<MonitorInfo>();
  for(int i=0;i<all.Length;i++)
   list.Add(new MonitorInfo{Id=i,Name=all[i].DeviceName.TrimStart('\\','.'),Bounds=all[i].Bounds,Primary=all[i].Primary});
  // Die Monitor-IDs von mstsc entsprechen nicht der Reihenfolge, in der Windows die Monitore meldet,
  // sondern der Nummer des Anzeigegeräts (siehe MstscIds).
  var ids=MstscIds.Get(all);
  if(ids!=null)
   foreach(var m in list) {
    int id;
    if(ids.TryGetValue(m.Bounds.X+","+m.Bounds.Y,out id))m.Id=id;
   }
  list=list.OrderBy(m=>m.Bounds.X).ThenBy(m=>m.Bounds.Y).ToList();
  for(int i=0;i<list.Count;i++)list[i].Nr=i+1;
  return list;
 }

 // Hauptmonitor der Sitzung (Taskleiste): der gewählte Hauptmonitor, sonst der lokale
 // Primärmonitor, falls ausgewählt, sonst der linke
 public static List<MonitorInfo> SessionOrder(IList<MonitorInfo> selection,MonitorInfo preferred=null) {
  var main=preferred!=null&&selection.Contains(preferred)?preferred:(selection.FirstOrDefault(m=>m.Primary)??selection.OrderBy(m=>m.Nr).First());
  var order=new List<MonitorInfo>{main};
  order.AddRange(selection.Where(m=>m!=main).OrderBy(m=>m.Nr));
  return order;
 }

 static bool Touch(Rectangle a,Rectangle b) {
  int v=Math.Min(a.Bottom,b.Bottom)-Math.Max(a.Top,b.Top);
  int h=Math.Min(a.Right,b.Right)-Math.Max(a.Left,b.Left);
  bool side=(a.Right==b.Left||b.Right==a.Left)&&v>0;
  bool stack=(a.Bottom==b.Top||b.Bottom==a.Top)&&h>0;
  return side||stack;
 }

 // Liegen alle gewählten Monitore zusammenhängend aneinander?
 public static bool Adjacent(IList<MonitorInfo> selection) {
  if(selection.Count<=1)return true;
  var connected=new List<MonitorInfo>{selection[0]};
  bool changed=true;
  while(changed) {
   changed=false;
   foreach(var m in selection) {
    if(connected.Contains(m))continue;
    if(connected.Any(c=>Touch(m.Bounds,c.Bounds))){connected.Add(m);changed=true;}
   }
  }
  return connected.Count==selection.Count;
 }

 public static string Describe(IList<MonitorInfo> selection) {
  return String.Join(" + ",selection.OrderBy(m=>m.Nr).Select(m=>m.Nr.ToString()).ToArray());
 }
}

// Monitor-IDs der Remotedesktopverbindung: mstsc zählt die Monitore in der Reihenfolge der Anzeigegeräte
// (\\.\DISPLAY1, DISPLAY2 …), nicht in der Reihenfolge, in der Windows sie meldet. Kleinste DISPLAY-Nummer = ID 0 usw.
// Ermittelt an einem Rechner mit vier Monitoren durch Vergleich mit "mstsc /l". Das Programm ruft mstsc dafür
// nicht auf (unter Windows 11 lässt sich die Liste von mstsc nicht auslesen und das Fenster würde aufblitzen).
static class MstscIds {
 // Schlüssel "X,Y" (linke obere Ecke) -> mstsc-ID; null, wenn ein Gerätename keine DISPLAY-Nummer hat
 public static Dictionary<string,int> Get(Screen[] screens) {
  var numbered=new List<KeyValuePair<int,Screen>>();
  foreach(var s in screens) {
   var m=Regex.Match(s.DeviceName??"",@"DISPLAY(\d+)",RegexOptions.IgnoreCase);
   if(!m.Success)return null;
   numbered.Add(new KeyValuePair<int,Screen>(Int32.Parse(m.Groups[1].Value),s));
  }
  var map=new Dictionary<string,int>();
  int id=0;
  foreach(var entry in numbered.OrderBy(x=>x.Key))map[entry.Value.Bounds.X+","+entry.Value.Bounds.Y]=id++;
  return map;
 }
}

// Liest und schreibt .rdp-Dateien; Kodierung (UTF-16 oder UTF-8) bleibt erhalten
public class RdpFile {
 public string Path;
 public List<string> Lines=new List<string>();
 Encoding encoding;
 // Inhalt der Datei beim Laden (SHA-256 der Bytes): Weicht die Datei beim Speichern davon ab, hat ein anderes
 // Programm sie geändert, und sie wird nicht überschrieben. null = neue Datei (z. B. Kopie für "Nur verbinden").
 string loadedHash;

 // Eine .rdp-Datei hat wenige Kilobyte; größere Dateien werden nicht gelesen
 public const long MaxSize=1024*1024;

 // Die Datei wurde seit dem Laden von außen geändert
 public sealed class ChangedOnDiskException:IOException {
  public ChangedOnDiskException():base("Die Datei wurde seit dem Laden von einem anderen Programm geändert."){}
 }

 // Netzwerkpfad (\\server\freigabe\… oder verbundenes Netzlaufwerk)? Ermittelt ohne Zugriff auf das Netz,
 // damit beim Programmstart keine Verbindung zum Server (und keine Anmeldung) ausgelöst wird.
 public static bool IsNetworkPath(string path) {
  if(String.IsNullOrEmpty(path))return false;
  if(path.StartsWith(@"\\")||path.StartsWith("//"))return !path.StartsWith(@"\\?\")||path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase);
  try {
   string root=System.IO.Path.GetPathRoot(path);
   return !String.IsNullOrEmpty(root)&&root.Length>=2&&root[1]==':'&&new DriveInfo(root.Substring(0,1)).DriveType==DriveType.Network;
  } catch {return false;}
 }

 public static bool IsRdp(string path) {
  return path!=null&&String.Equals(System.IO.Path.GetExtension(path),".rdp",StringComparison.OrdinalIgnoreCase);
 }

 public static RdpFile Load(string path) {
  if(!IsRdp(path))throw new InvalidDataException("Keine RDP-Datei (.rdp): "+System.IO.Path.GetFileName(path));
  var file=new RdpFile{Path=path};
  byte[] bytes=ReadLimited(path);
  file.loadedHash=Hash(bytes);
  if(bytes.Length>=2&&bytes[0]==0xFF&&bytes[1]==0xFE)file.encoding=new UnicodeEncoding(false,true);
  else if(bytes.Length>=3&&bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF)file.encoding=new UTF8Encoding(true);
  else file.encoding=new UTF8Encoding(false);
  string text=file.encoding.GetString(bytes);
  if(text.Length>0&&text[0]==(char)0xFEFF)text=text.Substring(1);
  foreach(string line in text.Split('\n')) {
   string l=line.TrimEnd('\r');
   if(l.Trim().Length>0)file.Lines.Add(l);
  }
  return file;
 }

 public string Get(string key) {
  string prefix=key+":";
  foreach(string l in Lines) {
   if(l.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) {
    string[] parts=l.Split(new[]{':'},3);
    return parts.Length==3?parts[2]:"";
   }
  }
  return null;
 }

 public void Set(string key,string type,string value) {
  string line=key+":"+type+":"+value,prefix=key+":";
  for(int i=0;i<Lines.Count;i++) {
   if(Lines[i].StartsWith(prefix,StringComparison.OrdinalIgnoreCase)){Lines[i]=line;return;}
  }
  Lines.Add(line);
 }

 public void Remove(string key) {
  string prefix=key+":";
  Lines.RemoveAll(l=>l.StartsWith(prefix,StringComparison.OrdinalIgnoreCase));
 }

 // Kopie der Datei unter einem anderen Pfad (gleiche Zeilen und Kodierung), z. B. für "Nur verbinden"
 public RdpFile CopyTo(string path) {
  return new RdpFile{Path=path,Lines=new List<string>(Lines),encoding=encoding};
 }

 // Liest eine Datei in einem Zug aus demselben Stream und bricht ab, sobald mehr als MaxSize Bytes kommen
 // (auch wenn die Datei erst während des Lesens wächst)
 public static byte[] ReadLimited(string path) {
  using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
   var ms=new MemoryStream();
   var buf=new byte[16384];
   int n;
   while((n=s.Read(buf,0,buf.Length))>0) {
    ms.Write(buf,0,n);
    if(ms.Length>MaxSize)throw new InvalidDataException("Die Datei ist zu groß für eine RDP-Datei (über "+(MaxSize/1024)+" KB).");
   }
   return ms.ToArray();
  }
 }

 static string Hash(byte[] bytes) {
  using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-","");
 }

 // true, wenn der Inhalt der Datei nicht mehr dem beim Laden entspricht
 public bool ChangedOnDisk() {
  if(loadedHash==null)return false; // neue Datei (z. B. Kopie für "Nur verbinden")
  try {return !File.Exists(Path)||Hash(ReadLimited(Path))!=loadedHash;}
  catch {return true;}
 }

 // Sperre je Datei, damit zwei RDP-Pilot-Fenster nicht gleichzeitig dieselbe Datei schreiben
 static System.Threading.Mutex SaveLock(string path) {
  string id=Hash(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())).Substring(0,24);
  var m=new System.Threading.Mutex(false,@"Local\RDP-Pilot-Datei-"+id);
  try {
   if(!m.WaitOne(5000)){m.Dispose();throw new IOException("Die Datei wird gerade von einem anderen RDP-Pilot-Fenster gespeichert.");}
  } catch(System.Threading.AbandonedMutexException){}
  return m;
 }

 // Schreibt Bytes vollständig in eine Hilfsdatei im Zielordner und ersetzt das Ziel dann in einem Schritt.
 // check (optional) wird unmittelbar vor dem Austausch aufgerufen und kann abbrechen. Kann das Laufwerk
 // nicht in einem Schritt ersetzen, wird nichts geändert.
 static void WriteReplace(string target,byte[] bytes,Action check) {
  string temp=target+"."+Guid.NewGuid().ToString("N").Substring(0,8)+".tmp";
  try {
   using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
    stream.Write(bytes,0,bytes.Length);
    stream.Flush(true);
   }
   if(check!=null)check();
   if(File.Exists(target)) {
    try {File.Replace(temp,target,null,true);}
    catch(PlatformNotSupportedException) {
     throw new IOException("Dieses Laufwerk kann die Datei nicht sicher in einem Schritt ersetzen. Es wurde nichts geändert.");
    }
   } else File.Move(temp,target);
  } finally {
   try {if(File.Exists(temp))File.Delete(temp);} catch {}
  }
 }

 // Ersetzt eine Datei durch den Inhalt einer anderen (z. B. Wiederherstellen aus .rdp.bak), ebenfalls über eine
 // vollständig geschriebene Hilfsdatei. expected: Fassung, die beim Bestätigen angezeigt war; weicht die Datei
 // davon ab (vor dem Schreiben und unmittelbar vor dem Austausch geprüft), wird nichts ersetzt.
 // Grenze des Konfliktschutzes: Gegen Programme, die die Datei ohne Sperre schreiben, bleibt zwischen der letzten
 // Prüfung und dem Austausch ein sehr kurzes Zeitfenster; ihre Änderung kann dann verloren gehen.
 public static void ReplaceWith(string target,string source,RdpFile expected) {
  if(!IsRdp(target))throw new InvalidDataException("Keine RDP-Datei (.rdp): "+System.IO.Path.GetFileName(target));
  byte[] bytes=ReadLimited(source);
  using(var m=SaveLock(target)) {
   try {
    Action check=()=>{if(expected!=null&&expected.ChangedOnDisk())throw new ChangedOnDiskException();};
    check();
    WriteReplace(target,bytes,check);
   }
   finally {m.ReleaseMutex();}
  }
 }

 // Speichert die Datei. Mit createBackup wird vorher das Original als .rdp.bak gesichert,
 // sofern noch keine Sicherung existiert. Rückgabe: true, wenn eine neue Sicherung angelegt wurde.
 // Wurde die Datei seit dem Laden von außen geändert, wird nichts geschrieben (ChangedOnDiskException).
 // Die neue Fassung entsteht erst vollständig als Hilfsdatei im selben Ordner und ersetzt dann das Original
 // in einem Schritt – ein Abbruch beim Schreiben hinterlässt keine halbe Datei.
 public bool Save(bool createBackup) {
  if(!IsRdp(Path))throw new InvalidDataException("Keine RDP-Datei (.rdp): "+System.IO.Path.GetFileName(Path));
  byte[] bytes=ToBytes();
  bool created=false;
  using(var m=SaveLock(Path)) {
   try {
    if(ChangedOnDisk())throw new ChangedOnDiskException();
    // Sicherung ebenfalls über eine Hilfsdatei, damit nie eine halbe .bak entsteht
    string backup=Path+".bak";
    if(createBackup&&File.Exists(Path)&&!File.Exists(backup)){WriteReplace(backup,ReadLimited(Path),null);created=true;}
    // unmittelbar vor dem Austausch noch einmal prüfen, ob ein anderes Programm geschrieben hat
    WriteReplace(Path,bytes,()=>{if(ChangedOnDisk())throw new ChangedOnDiskException();});
   } finally {m.ReleaseMutex();}
  }
  loadedHash=Hash(bytes);
  return created;
 }

 // Inhalt so, wie er geschrieben wird (Kodierung wie beim Laden, mit Vorspann/BOM wie gehabt)
 byte[] ToBytes() {
  var ms=new MemoryStream();
  using(var writer=new StreamWriter(ms,encoding)) writer.Write(String.Join("\r\n",Lines.ToArray())+"\r\n");
  return ms.ToArray();
 }

 // Schreibt die Monitorauswahl in die Datei
 public void ApplyMonitors(IList<MonitorInfo> selection,bool setResolution,MonitorInfo main=null) {
  var order=Monitors.SessionOrder(selection,main);
  Rectangle b=order[0].Bounds;
  Set("screen mode id","i","2");
  Set("winposstr","s","0,1,"+b.Left+","+b.Top+","+b.Right+","+b.Bottom);
  if(order.Count==1) {
   // Ein Monitor: Vollbild dort, wo das Fenster laut winposstr liegt
   Set("use multimon","i","0");
   Remove("selectedmonitors");
   if(setResolution) {
    Set("desktopwidth","i",b.Width.ToString());
    Set("desktopheight","i",b.Height.ToString());
   }
  } else {
   // Mehrere Monitore: erste ID = Hauptmonitor der Sitzung
   Set("use multimon","i","1");
   Set("selectedmonitors","s",String.Join(",",order.Select(m=>m.Id.ToString()).ToArray()));
  }
 }

 // Hauptmonitor laut Datei: erste ID in selectedmonitors (nur bei mehreren Monitoren)
 public MonitorInfo CurrentMain(IList<MonitorInfo> monitors) {
  string smon=Get("selectedmonitors");
  if(Get("use multimon")!="1"||String.IsNullOrEmpty(smon))return null;
  int id;
  if(!Int32.TryParse(smon.Split(',')[0].Trim(),out id))return null;
  return monitors.FirstOrDefault(m=>m.Id==id);
 }

 // Ermittelt die Monitore, die aktuell in der Datei eingestellt sind
 public List<MonitorInfo> CurrentMonitors(IList<MonitorInfo> monitors) {
  var result=new List<MonitorInfo>();
  string multi=Get("use multimon"),smon=Get("selectedmonitors");
  if(multi=="1"&&!String.IsNullOrEmpty(smon)) {
   var ids=new List<int>();
   foreach(string part in smon.Split(',')) {int id;if(Int32.TryParse(part.Trim(),out id))ids.Add(id);}
   result.AddRange(monitors.Where(m=>ids.Contains(m.Id)));
   if(result.Count>0)return result;
  }
  if(multi=="1"&&String.IsNullOrEmpty(smon)) {
   result.AddRange(monitors);
   return result;
  }
  string wp=Get("winposstr");
  if(!String.IsNullOrEmpty(wp)) {
   string[] p=wp.Split(',');
   int x,y;
   if(p.Length>=6&&Int32.TryParse(p[2].Trim(),out x)&&Int32.TryParse(p[3].Trim(),out y)) {
    var hit=monitors.FirstOrDefault(m=>m.Bounds.Contains(x+10,y+10));
    if(hit!=null){result.Add(hit);return result;}
   }
  }
  var primary=monitors.FirstOrDefault(m=>m.Primary);
  if(primary!=null)result.Add(primary);
  return result;
 }

 // Ist die Datei auf Bildschirme eingestellt, die es gerade nicht gibt (z. B. Notebook ohne Dockingstation)?
 // Rückgabe: Satzteil für die Meldung ("… ist auf <Rückgabe>"), sonst null
 public string MissingMonitors(IList<MonitorInfo> monitors) {
  if(monitors==null||monitors.Count==0)return null;
  if(Get("use multimon")=="1") {
   string smon=Get("selectedmonitors");
   if(String.IsNullOrEmpty(smon))return null; // alle Bildschirme
   int missing=0;
   foreach(string part in smon.Split(',')) {
    int id;
    if(Int32.TryParse(part.Trim(),out id)&&!monitors.Any(m=>m.Id==id))missing++;
   }
   if(missing==0)return null;
   return missing==1?"einen Bildschirm eingestellt, der gerade nicht angeschlossen ist":missing+" Bildschirme eingestellt, die gerade nicht angeschlossen sind";
  }
  // Ein Monitor im Vollbild: Position laut winposstr
  if(Get("screen mode id")!="2")return null;
  string wp=Get("winposstr");
  if(String.IsNullOrEmpty(wp))return null;
  string[] p=wp.Split(',');
  int x,y;
  if(p.Length>=6&&Int32.TryParse(p[2].Trim(),out x)&&Int32.TryParse(p[3].Trim(),out y)&&!monitors.Any(m=>m.Bounds.Contains(x+10,y+10)))
   return "einen Bildschirm eingestellt, der gerade nicht angeschlossen ist";
  return null;
 }

 // Einträge, die bestimmen, wohin und über welches Gateway verbunden wird
 static readonly string[] TargetKeys={"full address","alternate full address","server port","username",
  "gatewayhostname","gatewayusagemethod","gatewayprofileusagemethod","gatewaycredentialssource"};

 // Sicherheitsrelevante Einträge (Anmeldung, Weiterleitungen, Startprogramm, Signatur): kommen sie mehrfach vor,
 // hängt es vom RDP-Client ab, welcher Wert gilt – solche Dateien werden wie doppelte Zieleinträge abgelehnt.
 static readonly string[] SecurityKeys={"authentication level","enablecredsspsupport","prompt for credentials",
  "prompt for credentials on client","negotiate security layer","disableconnectionsharing","kdcproxyname",
  "redirectclipboard","redirectdrives","drivestoredirect","redirectprinters","redirectcomports","redirectsmartcards",
  "redirectposdevices","redirectwebauthn","devicestoredirect","usbdevicestoredirect","camerastoredirect",
  "audiocapturemode","alternate shell","shell working directory","remoteapplicationmode","remoteapplicationprogram",
  "remoteapplicationname","remoteapplicationcmdline","remoteapplicationexpandcmdline","remoteapplicationexpandworkingdir",
  "remoteapplicationfile","loadbalanceinfo","signature","signscope"};

 // Ein Ziel- oder sicherheitsrelevanter Eintrag kommt mehrfach vor: Welcher Wert gilt, ist dann unklar
 // (RDP Pilot zeigt den ersten). Verglichen wird der Schlüssel ohne Groß-/Kleinschreibung und Leerzeichen am Rand.
 public string AmbiguousKey() {
  foreach(string key in TargetKeys.Concat(SecurityKeys))
   if(Lines.Count(l=>l.IndexOf(':')>0&&KeyOf(l)==key)>1)return key;
  return null;
 }

 // Einträge, die RDP Pilot selbst setzt (Monitorauswahl). Alle anderen Einstellungen müssen beim Verbinden
 // dieselben sein wie in der Fassung, die angezeigt bzw. gespeichert wurde.
 public static readonly string[] MonitorKeys={"screen mode id","use multimon","selectedmonitors","winposstr","desktopwidth","desktopheight"};
 static bool IsMonitorKey(string key){return MonitorKeys.Contains(key,StringComparer.OrdinalIgnoreCase);}
 static string KeyOf(string line){int i=line.IndexOf(':');return (i<0?line:line.Substring(0,i)).Trim().ToLowerInvariant();}

 // Alle Einstellungen außer der Monitorauswahl mit Schlüssel, Typ und Wert. Verschiedene Schlüssel dürfen ihre
 // Reihenfolge tauschen; mehrfach vorhandene Schlüssel behalten ihre Reihenfolge (OrderBy ist stabil), damit auch
 // ein Umordnen von Duplikaten als Änderung zählt.
 public string SettingsSignature {
  get {
   return String.Join("\n",Lines.Where(l=>!IsMonitorKey(KeyOf(l)))
    .OrderBy(KeyOf,StringComparer.Ordinal).Select(l=>KeyOf(l)+":"+TypeOf(l)+":"+Value(l)).ToArray());
  }
 }

 // Abweichende Einstellungen (außer Monitorauswahl) in Worten, z. B. "Server: alt.firma.de → neu.firma.de"
 public string DescribeChanges(RdpFile other) {
  var keys=Lines.Concat(other.Lines).Select(KeyOf).Where(k=>k.Length>0&&!IsMonitorKey(k)).Distinct().ToList();
  var changes=new List<string>();
  foreach(string k in keys) {
   // Typ und Wert gemeinsam vergleichen, Reihenfolge mehrfacher Einträge zählt
   var mine=Lines.Where(l=>KeyOf(l)==k).ToList();
   var theirs=other.Lines.Where(l=>KeyOf(l)==k).ToList();
   if(mine.Select(l=>TypeOf(l)+":"+Value(l)).SequenceEqual(theirs.Select(l=>TypeOf(l)+":"+Value(l))))continue;
   // Gleiche Werte mit anderem Typ (z. B. s → i): Typ mit anzeigen, sonst sähe die Meldung unverändert aus
   bool showType=mine.Select(Value).SequenceEqual(theirs.Select(Value));
   string a=Describe(mine,showType),c=Describe(theirs,showType);
   changes.Add(TargetLabel(k)+": "+a+" → "+c);
  }
  const int max=12;
  string text=String.Join("\n",changes.Take(max).ToArray());
  if(changes.Count>max)text+="\n… und "+(changes.Count-max)+" weitere";
  return text;
 }
 static string Value(string line){string[] p=line.Split(new[]{':'},3);return p.Length==3?p[2]:"";}
 static string TypeOf(string line){string[] p=line.Split(new[]{':'},3);return p.Length>=2?p[1].Trim().ToLowerInvariant():"";}
 static string Describe(List<string> lines,bool showType) {
  if(lines.Count==0)return "(fehlt)";
  return String.Join(" | ",lines.Select(l=>(showType?"Typ "+(TypeOf(l).Length>0?TypeOf(l):"(leer)")+" ":"")+Shorten(Value(l))).ToArray());
 }
 static string Shorten(string s){return s.Length>60?s.Substring(0,57)+"…":(s.Length>0?s:"(leer)");}
 static string TargetLabel(string key) {
  switch(key) {
   case "full address":return "Server";
   case "alternate full address":return "Alternative Adresse";
   case "server port":return "Port";
   case "username":return "Benutzer";
   case "gatewayhostname":return "Gateway";
   default:return key;
  }
 }

 // Ziel in Worten, z. B. "server.firma.de · max (über Gateway rdgw.firma.de)"
 public string DescribeTarget() {
  string text=Server.Length>0?Server:"(kein Server)";
  if(User.Length>0)text+=" · "+User;
  string gw=Get("gatewayhostname");
  if(!String.IsNullOrEmpty(gw))text+=" (über Gateway "+gw+")";
  return text;
 }

 public string Server {
  get {
   string address=Get("full address");
   return String.IsNullOrEmpty(address)?"":address;
  }
 }
 public string User {
  get {
   string user=Get("username");
   return String.IsNullOrEmpty(user)?"":user;
  }
 }
}

}