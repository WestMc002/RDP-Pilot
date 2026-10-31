// reitzner RDP Pilot – RDP-Sitzungen auf den richtigen Monitor lenken
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
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
 public static List<MonitorInfo> Load() {
  var all=Screen.AllScreens;
  var list=new List<MonitorInfo>();
  for(int i=0;i<all.Length;i++)
   list.Add(new MonitorInfo{Id=i,Name=all[i].DeviceName.TrimStart('\\','.'),Bounds=all[i].Bounds,Primary=all[i].Primary});
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

// Liest und schreibt .rdp-Dateien; Kodierung (UTF-16 oder UTF-8) bleibt erhalten
public class RdpFile {
 public string Path;
 public List<string> Lines=new List<string>();
 Encoding encoding;

 public static RdpFile Load(string path) {
  var file=new RdpFile{Path=path};
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

 // Speichert die Datei. Mit createBackup wird vorher das Original als .rdp.bak gesichert,
 // sofern noch keine Sicherung existiert. Rückgabe: true, wenn eine neue Sicherung angelegt wurde.
 public bool Save(bool createBackup) {
  string backup=Path+".bak";
  bool created=false;
  if(createBackup&&!File.Exists(backup)){File.Copy(Path,backup);created=true;}
  File.WriteAllText(Path,String.Join("\r\n",Lines.ToArray())+"\r\n",encoding);
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