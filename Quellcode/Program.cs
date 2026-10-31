// reitzner RDP Pilot – RDP-Sitzungen auf den richtigen Monitor lenken
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle(RDPilot.AppInfo.Product)]
[assembly: AssemblyProduct(RDPilot.AppInfo.Product)]
[assembly: AssemblyDescription("RDP-Sitzungen auf den richtigen Monitor lenken")]
[assembly: AssemblyCopyright("© 2026 Günther Schoder")]

namespace RDPilot {

public static partial class AppInfo {
 public const string Product="RDP Pilot";
 public const string FullName="reitzner RDP Pilot";
 // reitzner-Projekt: in der Oberfläche weder Name noch Website des Entwicklers (Globale Richtlinien)
 public const string Company="reitzner AG";
}

static class Program {
 // Aufruf:
 //   "reitzner RDP Pilot.exe"                          -> Oberfläche
 //   "reitzner RDP Pilot.exe" Test.rdp                 -> Oberfläche mit vorausgewählter Datei
 //   "reitzner RDP Pilot.exe" Test.rdp 2,1             -> Monitore setzen (erster = Hauptmonitor), ohne Oberfläche
 //   "reitzner RDP Pilot.exe" Test.rdp 1,2 /connect    -> setzen und Verbindung starten
 //   zusätzlich /backup                                  -> vorher Sicherung .rdp.bak anlegen
 [STAThread] static int Main(string[] args) {
  Native.EnableDpiAwareness();
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  string file=args.Length>0?args[0]:null;
  string monitorArg=args.Length>1&&!args[1].StartsWith("/")&&!args[1].StartsWith("-")?args[1]:null;
  bool connect=args.Any(a=>a.Equals("/connect",StringComparison.OrdinalIgnoreCase)||a.Equals("-connect",StringComparison.OrdinalIgnoreCase));
  bool backup=args.Any(a=>a.Equals("/backup",StringComparison.OrdinalIgnoreCase)||a.Equals("-backup",StringComparison.OrdinalIgnoreCase));
  if(file!=null&&monitorArg!=null)return CommandLine(file,monitorArg,connect,backup);
  Ui.Init();
  if(file=="--preview")return Preview();
  if(file=="--screenshots")return Screenshots();
  try {Application.Run(new MainForm(file));}
  catch(Exception e){MessageBox.Show(e.Message,AppInfo.Product,MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
  return 0;
 }

 // --preview: Vorschaubilder der Oberfläche (hell und dunkel) nach "Design\Vorschau"
 static int Preview() {
  string dir=Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Design"),"Vorschau");
  return Capture(dir,null,"Vorschau-hell.png","Vorschau-dunkel.png");
 }

 // --screenshots: Bilder für GitHub nach "Screenshots" – nur mit einer Demo-Verbindung,
 // damit keine echten Server, Benutzer oder Pfade zu sehen sind
 static int Screenshots() {
  string demo=Path.Combine(Path.GetTempPath(),"RDPilot-Demo-"+Guid.NewGuid().ToString("N"));
  try {
   Directory.CreateDirectory(demo);
   // Demo-Auswahl wie im typischen Einsatz: linker und rechter Monitor, rechter als Hauptmonitor
   // (bei drei Monitoren zeigt das Bild damit auch Primär-Kennzeichnung und Warnhinweis)
   var mons=Monitors.Load();
   string selection="use multimon:i:0\r\n";
   if(mons.Count>=2)selection="use multimon:i:1\r\nselectedmonitors:s:"+mons[mons.Count-1].Id+","+mons[0].Id+"\r\n";
   File.WriteAllText(Path.Combine(demo,"Demo.rdp"),
    "screen mode id:i:2\r\n"+selection+"full address:s:rdp.example.com\r\nusername:s:demo\r\n",
    new System.Text.UnicodeEncoding(false,true));
   string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Screenshots");
   return Capture(dir,demo,"hell.png","dunkel.png");
  } catch {return 1;}
  finally {try {Directory.Delete(demo,true);} catch {}}
 }

 static int Capture(string dir,string folderOverride,string lightName,string darkName) {
  try {
   Directory.CreateDirectory(dir);
   foreach(bool dark in new[]{false,true}) {
    Ui.ApplyPalette(dark);
    using(var form=new MainForm(null,folderOverride)) {
     form.StartPosition=FormStartPosition.Manual;
     form.Location=new System.Drawing.Point(-30000,-30000);
     form.ShowInTaskbar=false;
     form.Show();
     Application.DoEvents();
     // Ganzes Fenster zeichnen und dann nur den Fensterinhalt ausschneiden:
     // DrawToBitmap zeichnet sonst die alte Titelleiste mit und schneidet unten ab
     using(var full=new System.Drawing.Bitmap(form.Width,form.Height)) {
      form.DrawToBitmap(full,new System.Drawing.Rectangle(0,0,form.Width,form.Height));
      var origin=form.PointToScreen(System.Drawing.Point.Empty);
      var client=new System.Drawing.Rectangle(origin.X-form.Left,origin.Y-form.Top,form.ClientSize.Width,form.ClientSize.Height);
      using(var bitmap=full.Clone(client,full.PixelFormat))
       bitmap.Save(Path.Combine(dir,dark?darkName:lightName),System.Drawing.Imaging.ImageFormat.Png);
     }
     form.Close();
    }
   }
   return 0;
  } catch {return 1;}
 }

 static int CommandLine(string file,string monitorArg,bool connect,bool backup) {
  try {
   if(!File.Exists(file))throw new Exception("RDP-Datei nicht gefunden: "+file);
   var monitors=Monitors.Load();
   var selection=new System.Collections.Generic.List<MonitorInfo>();
   foreach(string part in monitorArg.Split(new[]{',',';','+'},StringSplitOptions.RemoveEmptyEntries)) {
    int nr;
    if(!Int32.TryParse(part.Trim(),out nr)||nr<1||nr>monitors.Count)
     throw new Exception("Monitor "+part.Trim()+" existiert nicht (vorhanden: 1-"+monitors.Count+").");
    var m=monitors[nr-1];
    if(!selection.Contains(m))selection.Add(m);
   }
   var rdp=RdpFile.Load(Path.GetFullPath(file));
   // Der zuerst genannte Monitor wird Hauptmonitor der Sitzung
   rdp.ApplyMonitors(selection,true,selection[0]);
   rdp.Save(backup);
   if(connect)System.Diagnostics.Process.Start("mstsc.exe","\""+rdp.Path+"\"");
   return 0;
  } catch(Exception e) {
   MessageBox.Show(e.Message,AppInfo.Product,MessageBoxButtons.OK,MessageBoxIcon.Error);
   return 1;
  }
 }
}

}