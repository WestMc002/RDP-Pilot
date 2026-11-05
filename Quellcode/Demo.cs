// reitzner RDP Pilot – Screenshots mit Beispieldaten (hell und dunkel) für GitHub
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RDPilot {

// Erzeugt die Screenshots für die README aus Beispieldaten.
// Aufruf: "reitzner RDP Pilot.exe" --screenshots [Zielordner] (Standard: Ordner "Screenshots" neben der EXE).
// Echte Verbindungen und die echte Monitoranordnung werden nicht verwendet und nicht verändert:
// Die Verbindungen liegen in einem Temp-Ordner, die Monitore sind fest vorgegeben (drei Monitore, Mitte primär).
// Ablage: hell.png / dunkel.png (Hauptfenster), liste-hell.png / liste-dunkel.png (aufgeklappte Verbindungsliste),
// info-hell.png / info-dunkel.png (Info-Fenster).
// Jedes Bild wird einmal vor schwarzem und einmal vor weißem Hintergrund aufgenommen; daraus ergeben sich
// durchsichtige runde Ecken und der Windows-Schatten.
static class Demo {
 // true, solange die Screenshots erstellt werden (z. B. Programmpfad im Info-Fenster neutral anzeigen)
 public static bool Active;
 public const string DemoProgramFolder=@"C:\Tools\reitzner RDP Pilot";

 [StructLayout(LayoutKind.Sequential)] struct RECT {public int Left,Top,Right,Bottom;}
 [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd,int attr,out RECT value,int size);
 const int DWMWA_EXTENDED_FRAME_BOUNDS=9;

 public static bool RunScreenshots(string outDir) {
  string temp=Path.Combine(Path.GetTempPath(),"RDPilot-Screenshots-"+Guid.NewGuid().ToString("N"));
  try {
   Active=true;
   Directory.CreateDirectory(outDir);
   try {File.Delete(Path.Combine(outDir,"Fehler.txt"));} catch {}
   Directory.CreateDirectory(temp);
   WriteDemoConnections(temp);
   Monitors.Override=DemoMonitors();

   // Maus aus dem Bild (rechts unten auf dem Hauptbildschirm); Fenster öffnen dort, wo die Maus ist
   var area=Screen.PrimaryScreen.WorkingArea;
   Cursor.Position=new Point(area.Right-2,area.Bottom-2);

   using(var backdrop=new Backdrop()) {
    // Hintergrundfläche einmal vorab anzeigen, damit das erste Anzeigen nichts anderes schließt
    backdrop.Cover(new Rectangle(area.Right-2,area.Bottom-2,1,1),Color.Black);
    Pump(300);
    backdrop.Park();

    foreach(bool dark in new[]{false,true}) {
     Ui.ApplyPalette(dark);
     string suffix=dark?"dunkel":"hell";

     // Hauptfenster (Titelbild) und aufgeklappte Verbindungsliste
     using(var form=new MainForm(null,temp)) {
      form.ShowInTaskbar=false;
      form.Show();
      form.TopMost=true;
      form.Activate();
      Pump(1500); // Aufbau, Monitorkarte und Verbindungen laden
      Rectangle bounds=FrameBounds(form);
      Save(Capture(backdrop,bounds,Margin(form),form.Handle),Path.Combine(outDir,suffix+".png"));

      // Aufgeklappte Verbindungsliste: Solange das Menü offen ist, lässt Windows das Hauptfenster nicht vor die
      // Hintergrundfläche. Deshalb zuerst das Fenster mit aufgeklapptem Auswahlfeld, dann das Menü allein aufnehmen
      // und beide Bilder übereinanderlegen (gleicher Bildausschnitt).
      form.SetConnectionListOpenForDemo(true);
      Pump(300);
      var menu=form.OpenConnectionListForDemo();
      if(menu==null)throw new InvalidOperationException("Die Verbindungsliste konnte nicht geöffnet werden.");
      Pump(800);
      if(!menu.Visible){menu=form.OpenConnectionListForDemo();Pump(800);} // falls Windows sie wieder geschlossen hat
      if(menu==null||!menu.Visible)throw new InvalidOperationException("Die Verbindungsliste wurde sofort wieder geschlossen.");
      Rectangle area2=Rectangle.Union(bounds,menu.Bounds);
      menu.Close();
      Pump(300);
      form.SetConnectionListOpenForDemo(true);
      Pump(300);
      using(var window=Capture(backdrop,area2,Margin(form),form.Handle)) {
       menu=form.OpenConnectionListForDemo();
       Pump(800);
       if(menu==null||!menu.Visible)throw new InvalidOperationException("Die Verbindungsliste wurde sofort wieder geschlossen.");
       using(var list=Capture(backdrop,area2,Margin(form),menu.Handle)) {
        var combined=new Bitmap(window);
        using(var g=Graphics.FromImage(combined)) {
         g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceOver;
         g.DrawImageUnscaled(list,0,0);
        }
        Save(combined,Path.Combine(outDir,"liste-"+suffix+".png"));
       }
       menu.Close();
      }
      form.SetConnectionListOpenForDemo(false);
      Pump(200);
      form.Close();
     }
     Pump(300);

     // Info-Fenster
     using(var about=new AboutDialog(temp)) {
      about.StartPosition=FormStartPosition.CenterScreen;
      about.ShowInTaskbar=false;
      about.Show();
      about.TopMost=true;
      about.Activate();
      Pump(1200);
      Save(Capture(backdrop,FrameBounds(about),Margin(about),about.Handle),Path.Combine(outDir,"info-"+suffix+".png"));
      about.Close();
     }
     Pump(300);
    }
   }
   return true;
  } catch(Exception ex) {
   try {File.WriteAllText(Path.Combine(outDir,"Fehler.txt"),ex.ToString());} catch {}
   return false;
  } finally {
   Active=false;
   Monitors.Override=null;
   try {Directory.Delete(temp,true);} catch {}
  }
 }

 // Zwei erfundene Verbindungen (example.com): "Arbeitsplatz" auf Monitor 1 + 2 mit Hauptmonitor 1, "Server" auf einem Monitor
 static void WriteDemoConnections(string dir) {
  var utf16=new UnicodeEncoding(false,true);
  File.WriteAllText(Path.Combine(dir,"Arbeitsplatz.rdp"),
   "screen mode id:i:2\r\nuse multimon:i:1\r\nselectedmonitors:s:1,0\r\nfull address:s:arbeitsplatz.example.com\r\nusername:s:demo\r\n",utf16);
  File.WriteAllText(Path.Combine(dir,"Server.rdp"),
   "screen mode id:i:2\r\nuse multimon:i:0\r\nwinposstr:s:0,1,0,0,2560,1440\r\nfull address:s:server.example.com\r\nusername:s:admin\r\n",utf16);
 }

 // Beispielmonitore: drei Monitore 2560 x 1440 nebeneinander, der mittlere ist primär (mstsc-ID 0)
 static List<MonitorInfo> DemoMonitors() {
  return new List<MonitorInfo> {
   new MonitorInfo{Nr=1,Id=1,Name="DISPLAY2",Bounds=new Rectangle(-2560,0,2560,1440),Primary=false},
   new MonitorInfo{Nr=2,Id=0,Name="DISPLAY1",Bounds=new Rectangle(0,0,2560,1440),Primary=true},
   new MonitorInfo{Nr=3,Id=2,Name="DISPLAY3",Bounds=new Rectangle(2560,0,2560,1440),Primary=false}
  };
 }

 // Sichtbarer Fensterrahmen ohne den unsichtbaren Größenänderungsrand
 static Rectangle FrameBounds(Form f) {
  RECT r;
  if(DwmGetWindowAttribute(f.Handle,DWMWA_EXTENDED_FRAME_BOUNDS,out r,Marshal.SizeOf(typeof(RECT)))==0)
   return Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
  return f.Bounds;
 }

 // Rand um das Fenster für den Schatten (24 Einheiten)
 static int Margin(Control c) {
  try {using(var g=c.CreateGraphics())return (int)Math.Round(24*g.DpiX/96f);}
  catch {return 24;}
 }

 // Nimmt den Bereich vor schwarzem und vor weißem Hintergrund auf und berechnet daraus die Durchsichtigkeit
 static Bitmap Capture(Backdrop backdrop,Rectangle bounds,int margin,params IntPtr[] targets) {
  var r=Rectangle.Inflate(bounds,margin,margin);
  backdrop.Cover(r,Color.Black,targets);
  Pump(300);
  using(var onBlack=Grab(r)) {
   backdrop.Cover(r,Color.White,targets);
   Pump(300);
   using(var onWhite=Grab(r)) {
    backdrop.Park();
    return Combine(onBlack,onWhite);
   }
  }
 }

 static Bitmap Grab(Rectangle r) {
  var bmp=new Bitmap(r.Width,r.Height,PixelFormat.Format32bppArgb);
  using(var g=Graphics.FromImage(bmp))g.CopyFromScreen(r.Location,Point.Empty,r.Size);
  return bmp;
 }

 static Bitmap Combine(Bitmap onBlack,Bitmap onWhite) {
  int w=onBlack.Width,h=onBlack.Height;
  var rect=new Rectangle(0,0,w,h);
  var result=new Bitmap(w,h,PixelFormat.Format32bppArgb);
  var bd=onBlack.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
  var wd=onWhite.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
  var od=result.LockBits(rect,ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
  try {
   int n=bd.Stride*h;
   var b=new byte[n];
   var wh=new byte[n];
   var o=new byte[n];
   Marshal.Copy(bd.Scan0,b,0,n);
   Marshal.Copy(wd.Scan0,wh,0,n);
   for(int i=0;i+3<n;i+=4) {
    int diff=((wh[i]-b[i])+(wh[i+1]-b[i+1])+(wh[i+2]-b[i+2]))/3;
    int a=Math.Max(0,Math.Min(255,255-diff));
    if(a==0)continue; // ganz durchsichtig
    for(int c=0;c<3;c++)o[i+c]=(byte)Math.Min(255,b[i+c]*255/a);
    o[i+3]=(byte)a;
   }
   Marshal.Copy(o,0,od.Scan0,n);
  } finally {
   onBlack.UnlockBits(bd);
   onWhite.UnlockBits(wd);
   result.UnlockBits(od);
  }
  return result;
 }

 static void Save(Bitmap bmp,string file) {
  using(bmp)bmp.Save(file,ImageFormat.Png);
 }

 // Wartet und verarbeitet dabei die Fensternachrichten (Zeichnen, Laden)
 static void Pump(int ms) {
  var sw=Stopwatch.StartNew();
  while(sw.ElapsedMilliseconds<ms) {
   Application.DoEvents();
   Thread.Sleep(10);
  }
 }

 // Einfarbige Fläche hinter dem aufgenommenen Bereich
 sealed class Backdrop:Form {
  public Backdrop() {
   FormBorderStyle=FormBorderStyle.None;
   ShowInTaskbar=false;
   StartPosition=FormStartPosition.Manual;
   TopMost=true;
  }

  protected override bool ShowWithoutActivation {get {return true;}}

  protected override CreateParams CreateParams {
   get {
    var cp=base.CreateParams;
    cp.ExStyle|=0x08000000|0x00000080; // WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW
    return cp;
   }
  }

  public void Cover(Rectangle r,Color color,params IntPtr[] targets) {
   BackColor=color;
   Bounds=r;
   if(!Visible)Show();
   // vor alle anderen Fenster legen, danach die aufgenommenen Fenster (zuletzt das Menü) wieder davor
   SetWindowPos(Handle,HwndTopmost,0,0,0,0,NoMoveSizeActivate);
   foreach(var t in targets)SetWindowPos(t,HwndTopmost,0,0,0,0,NoMoveSizeActivate);
   Refresh();
  }

  // Zwischen zwei Aufnahmen ausblenden. Nur außerhalb des Bildschirms parken reicht nicht: Dann blieb die Fläche
  // im zweiten Durchgang (dunkel) vor dem Hauptfenster bzw. rutschte hinter das Konsolenfenster der Erstellen.bat.
  // Vor Schwarz und Weiß derselben Aufnahme wird sie nicht neu eingeblendet, nur umgefärbt (Globale Richtlinien),
  // und nach dem Einblenden wartet Capture 300 ms, bis Windows fertig eingeblendet hat.
  public void Park() {
   Hide();
  }

  static readonly IntPtr HwndTopmost=new IntPtr(-1);
  const uint NoMoveSizeActivate=0x0001|0x0002|0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE

  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd,IntPtr after,int x,int y,int cx,int cy,uint flags);
 }
}

}