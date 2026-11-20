// RDP Pilot – RDP-Sitzungen auf den richtigen Monitor lenken
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RDPilot {

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
 // Stand der Datei beim Laden: Wurde sie danach von einem anderen Programm geändert, wird nicht überschrieben
 DateTime loadedTime;
 long loadedLength=-1;

 // Eine .rdp-Datei hat wenige Kilobyte; größere Dateien werden nicht gelesen
 public const long MaxSize=1024*1024;

 // Die Datei wurde seit dem Laden von außen geändert
 public sealed class ChangedOnDiskException:IOException {
  public ChangedOnDiskException():base("Die Datei wurde seit dem Laden von einem anderen Programm geändert."){}
 }

 public static bool IsRdp(string path) {
  return path!=null&&String.Equals(System.IO.Path.GetExtension(path),".rdp",StringComparison.OrdinalIgnoreCase);
 }

 public static RdpFile Load(string path) {
  if(!IsRdp(path))throw new InvalidDataException("Keine RDP-Datei (.rdp): "+System.IO.Path.GetFileName(path));
  var file=new RdpFile{Path=path};
  var fi=new FileInfo(path);
  if(fi.Length>MaxSize)throw new InvalidDataException("Die Datei ist zu groß für eine RDP-Datei ("+(fi.Length/1024)+" KB).");
  file.loadedTime=fi.LastWriteTimeUtc;
  file.loadedLength=fi.Length;
  byte[] bytes=File.ReadAllBytes(path);
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

 // true, wenn die Datei seit dem Laden von außen geändert wurde
 public bool ChangedOnDisk() {
  if(loadedLength<0)return false; // neue Datei (z. B. Kopie für "Nur verbinden")
  try {
   var fi=new FileInfo(Path);
   return !fi.Exists||fi.LastWriteTimeUtc!=loadedTime||fi.Length!=loadedLength;
  } catch {return true;}
 }

 // Speichert die Datei. Mit createBackup wird vorher das Original als .rdp.bak gesichert,
 // sofern noch keine Sicherung existiert. Rückgabe: true, wenn eine neue Sicherung angelegt wurde.
 // Wurde die Datei seit dem Laden von außen geändert, wird nichts geschrieben (ChangedOnDiskException).
 // Die neue Fassung entsteht erst vollständig als Hilfsdatei im selben Ordner und ersetzt dann das Original
 // in einem Schritt – ein Abbruch beim Schreiben hinterlässt keine halbe Datei.
 public bool Save(bool createBackup) {
  if(!IsRdp(Path))throw new InvalidDataException("Keine RDP-Datei (.rdp): "+System.IO.Path.GetFileName(Path));
  if(ChangedOnDisk())throw new ChangedOnDiskException();
  string backup=Path+".bak";
  bool created=false;
  if(createBackup&&File.Exists(Path)&&!File.Exists(backup)){File.Copy(Path,backup);created=true;}
  string temp=Path+"."+Guid.NewGuid().ToString("N").Substring(0,8)+".tmp";
  try {
   using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
   using(var writer=new StreamWriter(stream,encoding)) {
    writer.Write(String.Join("\r\n",Lines.ToArray())+"\r\n");
    writer.Flush();
    stream.Flush(true);
   }
   if(File.Exists(Path)) {
    try {File.Replace(temp,Path,null,true);}
    catch(PlatformNotSupportedException){File.Copy(temp,Path,true);} // Laufwerke ohne Ersetzen in einem Schritt
   } else File.Move(temp,Path);
  } finally {
   try {if(File.Exists(temp))File.Delete(temp);} catch {}
  }
  var fi=new FileInfo(Path);
  loadedTime=fi.LastWriteTimeUtc;
  loadedLength=fi.Length;
  return created;
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