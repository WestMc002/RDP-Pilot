// RDP Pilot – Fehlerprotokoll
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Text;

namespace RDPilot {

// Fehlerprotokoll Fehler.log in %LOCALAPPDATA%\RDP Pilot (wie Dock Pilot). Schreibt nur bei Fehlern, wirft selbst
// nie eine Ausnahme; ab 256 KB wird die Datei zu Fehler.alt.log umbenannt (nichts wird eingelesen). Technisches zu Updates steht hier,
// Meldungen an Anwender nennen keine Pfade.
static class Log {
 static readonly object Gate=new object();
 const long MaxBytes=256*1024;

 public static string Folder {
  get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppInfo.FullName);}
 }
 public static string FilePath {get {return Path.Combine(Folder,"Fehler.log");}}

 public static void Write(string area,string text) {
  if(Demo.Active)return;
  try {
   lock(Gate) {
    Directory.CreateDirectory(Folder);
    string path=FilePath;
    // Rotation ohne Einlesen: ab 256 KB wird Fehler.log zu Fehler.alt.log (ältere Fassung wird ersetzt)
    try {
     var fi=new FileInfo(path);
     if(fi.Exists&&fi.Length>MaxBytes) {
      string old=Path.Combine(Folder,"Fehler.alt.log");
      if(File.Exists(old))File.Replace(path,old,null);
      else File.Move(path,old);
     }
    } catch {}
    string line=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"  "+AppInfo.Version+"  "+area+": "+
     (text??"").Replace("\r\n"," | ").Replace('\n',' ').Replace('\r',' ');
    File.AppendAllText(path,line+Environment.NewLine,new UTF8Encoding(false));
   }
  } catch {}
 }

 public static void Write(string area,Exception ex) {
  if(ex==null)return;
  string detail=ex.Data.Contains("Detail")?ex.Data["Detail"] as string:null;
  Write(area,ex.Message+(String.IsNullOrEmpty(detail)?"":" – "+detail));
 }
}

}
