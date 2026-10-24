// Loading curtain: a black launcher window over the StarCraft II window while the launcher swaps games (hub -> mission ->
// hub). Without it the player sees the desktop, then an empty black SC2 window, for 10-30 s. It never takes focus or input.
// SC2_COOP_NO_CURTAIN=1 turns it off. Automated runs (SC2_COOP_TEST_LAUNCH) leave it off too: nobody watches those games,
// and the curtain would cover whatever the person at the PC is doing meanwhile. SC2_COOP_TEST_CURTAIN_SHOT (curtain
// tests) keeps it on.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// StarCraft II in exclusive Fullscreen (Options > Graphics > Display Mode): a window shown on top of it (the curtain, the
// click bridge's drag box, markers and ping panel) makes the game lose its Direct3D device, and a failed reset crashes the
// game (2026-10-23: the buddy's game crashed seconds into a mission, its graphics log full of "Lost D3D9 device"). Windows
// reports a Direct3D game running fullscreen in front (SHQueryUserNotificationState: QUNS_RUNNING_D3D_FULL_SCREEN); once
// seen, the launcher warns and draws no overlays for the rest of the session.
static class ExclusiveFullscreen {
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    static volatile bool seen;static int lastCheck=int.MinValue/2;
    public static bool Seen{get{return seen;}}
    // Asks Windows at most once a second; true from the first time it was seen.
    public static bool Check() {
        if(seen)return true;
        int now=Environment.TickCount;if(now-lastCheck<1000)return false;lastCheck=now;
        int state;
        try{if(SHQueryUserNotificationState(out state)!=0||state!=3)return false;}catch{return false;}
        seen=true;
        Program.Log("WARNING: StarCraft II is running in exclusive Fullscreen, which can make it crash when anything is drawn over it "+
            "(\"Lost D3D9 device\"). Set Options > Graphics > Display Mode to Windowed (Fullscreen): it looks the same. "+
            "Until then the launcher draws no loading screen, drag box, markers or ping panel.");
        return true;
    }
}

static class Curtain {
    static Thread thread;static CurtainForm form;static readonly ManualResetEvent ready=new ManualResetEvent(false);
    static Rectangle? lastBounds;static System.Threading.Timer safety;
    static bool Off{get{return Environment.GetEnvironmentVariable("SC2_COOP_NO_CURTAIN")=="1"||
        (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SC2_COOP_TEST_LAUNCH"))&&string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SC2_COOP_TEST_CURTAIN_SHOT")));}}

    [StructLayout(LayoutKind.Sequential)] struct RECT{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f,IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,System.Text.StringBuilder s,int n);
    delegate bool EnumProc(IntPtr h,IntPtr l);

    // Remember where this game's window is, so the curtain covers the same spot when that window closes. The process's
    // "main window" can be the (minimized) trigger debugger, so take its largest visible "StarCraft II" window.
    public static void Track(Process p) {
        try {
            int pid=p.Id;Rectangle? best=null;
            EnumWindows((h,l)=>{uint w;GetWindowThreadProcessId(h,out w);if(w!=pid||!IsWindowVisible(h)||IsIconic(h))return true;
                var t=new System.Text.StringBuilder(64);GetWindowText(h,t,64);RECT r;
                if(t.ToString()=="StarCraft II"&&GetWindowRect(h,out r)&&r.Right-r.Left>200&&r.Bottom-r.Top>150){var b=Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
                    if(best==null||b.Width*b.Height>best.Value.Width*best.Value.Height)best=b;}
                return true;},IntPtr.Zero);
            if(best!=null)lastBounds=best;
        } catch {}
        if(ExclusiveFullscreen.Check())Hide();
    }

    public static void Show(string title,string status) {
        if(Off||ExclusiveFullscreen.Check())return;
        var bounds=lastBounds??Screen.PrimaryScreen.Bounds;
        Run(f=>{f.Bounds=bounds;f.Title.Text=title??"";f.Status.Text=status??"";f.Layout2();if(!f.Visible)f.Show();f.BringToFront();
            Program.Log("Loading curtain: "+title+" / "+status+" ("+f.Bounds.Width+"x"+f.Bounds.Height+" at "+f.Bounds.X+","+f.Bounds.Y+(lastBounds==null?", whole screen":"")+")");Snapshot(f);});
        // Never left up by mistake (e.g. an error path that doesn't hide it).
        if(safety!=null)safety.Dispose();safety=new System.Threading.Timer(_=>Hide(),null,240000,Timeout.Infinite);
    }
    public static void Status(string status){if(Off||form==null)return;Run(f=>{f.Status.Text=status??"";f.Layout2();if(f.Visible){Program.Log("Loading curtain: "+status);Snapshot(f);}});}
    public static void Hide(){if(form==null)return;Run(f=>{if(f.Visible)Program.Log("Loading curtain hidden");f.Hide();});}
    // Test hook: SC2_COOP_TEST_CURTAIN_SHOT=<folder> saves what the curtain draws (the form only, not the screen).
    static int shots;
    static void Snapshot(CurtainForm f) {
        string dir=Environment.GetEnvironmentVariable("SC2_COOP_TEST_CURTAIN_SHOT");if(string.IsNullOrEmpty(dir))return;
        try{System.IO.Directory.CreateDirectory(dir);using(var b=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(b,new Rectangle(0,0,f.Width,f.Height));b.Save(System.IO.Path.Combine(dir,"curtain-"+(++shots)+".png"));}}catch(Exception e){Program.Log("Curtain shot: "+e.Message);}
    }

    static void Run(Action<CurtainForm> a) {
        if(thread==null) {
            thread=new Thread(()=>{form=new CurtainForm();var h=form.Handle;ready.Set();Application.Run();}){IsBackground=true,Name="Curtain"};
            thread.SetApartmentState(ApartmentState.STA);thread.Start();
        }
        ready.WaitOne();
        try{form.BeginInvoke(a,form);}catch{}
    }

    class CurtainForm:Form {
        public readonly Label Title=new Label(),Status=new Label();
        public CurtainForm() {
            FormBorderStyle=FormBorderStyle.None;BackColor=Color.Black;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;
            Title.ForeColor=Color.FromArgb(190,220,200);Title.Font=new Font("Segoe UI Semibold",28f);Title.AutoSize=true;Title.BackColor=Color.Transparent;
            Status.ForeColor=Color.FromArgb(140,150,145);Status.Font=new Font("Segoe UI",13f);Status.AutoSize=true;Status.BackColor=Color.Transparent;
            Controls.Add(Title);Controls.Add(Status);
        }
        public void Layout2() {
            int cy=ClientSize.Height/2;
            Title.Location=new Point(Math.Max(0,(ClientSize.Width-Title.PreferredWidth)/2),cy-Title.PreferredHeight);
            Status.Location=new Point(Math.Max(0,(ClientSize.Width-Status.PreferredWidth)/2),cy+12);
        }
        protected override void OnResize(EventArgs e){base.OnResize(e);Layout2();}
        protected override bool ShowWithoutActivation{get{return true;}}
        protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80|0x20;return p;}} // no-activate, tool window, click-through
    }
}
