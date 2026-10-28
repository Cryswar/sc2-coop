// Guest click bridge. The joining SC2 client ignores native mouse clicks in the 3D world (input_probe.py,
// 2026-10-16), but accepts the same clicks as feature-layer actions through its own API. A low-level
// mouse hook watches the guest window and replays world clicks via RequestAction. Native events are
// passed through untouched so the minimap, command card, hotkeys and menus keep working.
//
// Targeted commands: the guest's native targeting UI (A, build menus, abilities) still works, and the map
// adapter (campaign_targeting.galaxy) reports the pending ability in the guest's local "CoopBridge" bank.
// While targeting, a world left click is sent as a queued Move probe (Patrol when shift is held) that the
// map swaps for the pending ability, then Esc ends the native targeting mode (kept while shift is held).
// The bank report comes through the game's network turn (100-600 ms after the key), so the bridge also guesses targeting
// from the key itself: A (attack), and key pairs that started targeting before (learned from the reports). A guessed
// targeting click is sent on the press, without waiting; if no targeting mode was on after all, the map takes the probe
// back (Probe/stray) and the pair is unlearned.
//
// Arrow keys: the bridge scrolls the guest camera itself with camera moves on the guest's own API (shown at once) and
// swallows the keys. It starts from the camera position the map reports in the same bank; while that is stale (minimap
// click, camera hotkeys) a press waits for the next report; in cinematics and with the chat line open the keys go through
// to the map's slower scroll.
//
// Input source: Raw Input (default since 2026-10-22). A hidden window registers as the input sink for mouse and keyboard
// and only acts while the guest's game window is in front. System-wide low-level hooks (SetWindowsHookEx WH_MOUSE_LL /
// WH_KEYBOARD_LL, what keyloggers use) made Windows Defender's cloud check quarantine the unsigned exe again and again.
// Raw Input only listens; it can't swallow events, so the arrow-key scroll above is left to the map, and the swallowed
// ping click reaches the game, which ignores a joiner's world clicks anyway. SC2_COOP_BRIDGE_HOOKS=1 uses the hooks.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml.Linq;

class BridgeCalibration {
    public int[] Window {get;set;}
    public double FeatureWidth {get;set;}
    public int Resolution {get;set;}
    public double[] H {get;set;}
    public static BridgeCalibration Load(string path) {
        var c=new JavaScriptSerializer().Deserialize<BridgeCalibration>(File.ReadAllText(path));
        if(c.H==null||c.H.Length!=9||c.Window==null||c.Window.Length!=2||c.Resolution<16)throw new Exception("Invalid bridge calibration");
        return c;
    }
    // Window client pixel -> feature-layer screen coordinate. Scales from the calibrated client size.
    public bool Map(int x,int y,int width,int height,out int fx,out int fy) {return Map(x,y,width,height,DefaultDistance,DefaultPitch,out fx,out fy);}

    // Zoom. The homography H is exact only at the camera it was measured with: SC2's default zoom (Core CameraData: distance 34,
    // pitch 56, yaw 180). Each mouse-wheel zoom step moves the camera closer and flatter (ZoomTable below), which H doesn't know.
    // A pinhole camera fitted to H (focal scale, principal point, feature-layer offset) gives the ground point for any distance
    // and pitch; the click is H's point plus the model's change from the default camera to the current one.
    public const double DefaultDistance=34,DefaultPitch=56;
    public static readonly double[][] ZoomTable={new[]{34.0,56.0},new[]{30.0,52.0},new[]{26.0,48.0},new[]{22.0,44.0},new[]{18.0,40.0}};
    double[] model;public double ModelResidual;   // k, cx, cy, ox, oy; mean fit error in feature pixels
    public bool Map(int x,int y,int width,int height,double distance,double pitch,out int fx,out int fy) {
        double u,v;bool front=MapExact(x*(double)Window[0]/width,y*(double)Window[1]/height,distance,pitch,out u,out v);
        fx=(int)Math.Round(u);fy=(int)Math.Round(v);
        return front&&fx>=0&&fy>=0&&fx<Resolution&&fy<Resolution;
    }
    // Calibration-window pixel (sx,sy) -> feature-layer point, unrounded.
    public bool MapExact(double sx,double sy,double distance,double pitch,out double u,out double v) {
        double w=H[6]*sx+H[7]*sy+H[8];u=(H[0]*sx+H[1]*sy+H[2])/w;v=(H[3]*sx+H[4]*sy+H[5])/w;
        if(Math.Abs(distance-DefaultDistance)>0.05||Math.Abs(pitch-DefaultPitch)>0.05) {
            if(model==null)FitModel();
            double zx,zy,dx,dy;
            if(Ground(model,sx,sy,distance,pitch,out zx,out zy)&&Ground(model,sx,sy,DefaultDistance,DefaultPitch,out dx,out dy)){u+=zx-dx;v+=zy-dy;}
        }
        return w>0;
    }
    // Feature-layer point under calibration-window pixel (sx,sy) on flat ground, camera looking north at the layer's centre.
    bool Ground(double[] q,double sx,double sy,double distance,double pitch,out double fx,out double fy) {
        double p=pitch*Math.PI/180,cp=Math.Cos(p),sp=Math.Sin(p),half=Window[1]/2.0;
        double x=(sx-q[1])/half*q[0],y=-(sy-q[2])/half*q[0];
        double dy=cp+y*sp,dz=-sp+y*cp;fx=fy=0;if(dz>=-1e-6)return false; // ray above the horizon
        double t=distance*sp/-dz,gx=t*x,gy=-distance*cp+t*dy,ppu=Resolution/FeatureWidth;
        fx=Resolution/2.0+q[3]+gx*ppu;fy=Resolution/2.0+q[4]-gy*ppu;return true;
    }
    public void FitModel() {
        var pts=new List<double[]>();
        for(int i=0;i<17;i++)for(int j=0;j<10;j++){double sx=20+i*(Window[0]-40)/16.0,sy=30+j*(Window[1]*0.76-30)/9;double w=H[6]*sx+H[7]*sy+H[8];pts.Add(new[]{sx,sy,(H[0]*sx+H[1]*sy+H[2])/w,(H[3]*sx+H[4]*sy+H[5])/w});}
        Func<double[],double> cost=q=>{double c=0,fx,fy;foreach(var pt in pts){if(!Ground(q,pt[0],pt[1],DefaultDistance,DefaultPitch,out fx,out fy))return 1e12;c+=(fx-pt[2])*(fx-pt[2])+(fy-pt[3])*(fy-pt[3]);}return c;};
        var best=new[]{0.25,Window[0]/2.0,Window[1]/2.0,0,0};
        for(int r=0;r<4;r++)best=NelderMead(cost,best,new[]{0.02,20,20,5,5},3000);
        double sum=0,gx,gy;foreach(var pt in pts){Ground(best,pt[0],pt[1],DefaultDistance,DefaultPitch,out gx,out gy);sum+=Math.Sqrt((gx-pt[2])*(gx-pt[2])+(gy-pt[3])*(gy-pt[3]));}
        ModelResidual=sum/pts.Count;model=best;
    }
    static double[] NelderMead(Func<double[],double> f,double[] x0,double[] step,int iterations) {
        int n=x0.Length;var s=new double[n+1][];var v=new double[n+1];
        for(int i=0;i<=n;i++){s[i]=(double[])x0.Clone();if(i>0)s[i][i-1]+=step[i-1];v[i]=f(s[i]);}
        Func<double[],double[],double,double[]> along=(a,b,t)=>{var r=new double[n];for(int j=0;j<n;j++)r[j]=a[j]+t*(b[j]-a[j]);return r;};
        for(int it=0;it<iterations;it++) {
            var order=Enumerable.Range(0,n+1).OrderBy(i=>v[i]).ToArray();s=order.Select(i=>s[i]).ToArray();v=order.Select(i=>v[i]).ToArray();
            var c=new double[n];for(int i=0;i<n;i++)for(int j=0;j<n;j++)c[j]+=s[i][j]/n;
            var xr=along(c,s[n],-1);double fr=f(xr);
            if(fr<v[0]){var xe=along(c,s[n],-2);double fe=f(xe);if(fe<fr){s[n]=xe;v[n]=fe;}else{s[n]=xr;v[n]=fr;}}
            else if(fr<v[n-1]){s[n]=xr;v[n]=fr;}
            else{var xc=along(c,s[n],0.5);double fc=f(xc);if(fc<v[n]){s[n]=xc;v[n]=fc;}else for(int i=1;i<=n;i++){s[i]=along(s[0],s[i],0.5);v[i]=f(s[i]);}}
        }
        int best=0;for(int i=1;i<=n;i++)if(v[i]<v[best])best=i;return s[best];
    }
}

class ClickBridge : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct POINT {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct RECT {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT {public POINT pt;public uint mouseData,flags,time;public IntPtr extra;}
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT {public uint vkCode,scanCode,flags,time;public IntPtr extra;}
    [StructLayout(LayoutKind.Sequential)] struct MSG {public IntPtr hwnd;public uint message;public IntPtr wParam,lParam;public uint time;public POINT pt;}
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT {public ushort vk,scan;public uint flags,time;public IntPtr extra;}
    [StructLayout(LayoutKind.Explicit)] struct INPUT {[FieldOffset(0)] public uint type;[FieldOffset(8)] public KEYBDINPUT ki;[FieldOffset(8)] long pad0;[FieldOffset(16)] long pad1;[FieldOffset(24)] long pad2;[FieldOffset(32)] long pad3;}
    delegate IntPtr HookProc(int code,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id,HookProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg,IntPtr hwnd,uint min,uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread,uint msg,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd,ref POINT p);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out RECT r);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,INPUT[] inputs,int size);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    // Drag box overlay (see DragBox below).
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct WNDCLASS {public uint style;public WndProc proc;public int clsExtra,wndExtra;public IntPtr instance,icon,cursor,background;public string menu,className;}
    [StructLayout(LayoutKind.Sequential)] struct PAINTSTRUCT {public IntPtr hdc;public int erase;public RECT paint;public int restore,incUpdate;[MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] reserved;}
    delegate IntPtr WndProc(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern ushort RegisterClass(ref WNDCLASS c);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateWindowEx(uint exStyle,string cls,string name,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd,int cmd);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hwnd,IntPtr rect,bool erase);
    // Raw Input sends a message per mouse movement; WM_PAINT waits until the queue is empty, so during a drag the overlays
    // were never painted (a black box, 2026-10-22). Redraw: invalidate and paint at once.
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr hwnd);
    static void Redraw(IntPtr hwnd){InvalidateRect(hwnd,IntPtr.Zero,false);UpdateWindow(hwnd);}
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd,out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd,ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc,ref RECT r,IntPtr brush);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd,ref POINT p);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr hwnd,IntPtr id,uint ms,IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hwnd,IntPtr id);

    const int WM_MOUSEMOVE=0x200,WM_LBUTTONDOWN=0x201,WM_LBUTTONUP=0x202,WM_RBUTTONDOWN=0x204,WM_RBUTTONUP=0x205,WM_MBUTTONDOWN=0x207,WM_MBUTTONUP=0x208,WM_MOUSEWHEEL=0x20A,
        WM_KEYDOWN=0x100,WM_KEYUP=0x101,WM_SYSKEYDOWN=0x104,WM_SYSKEYUP=0x105;
    // API ability ids (s2client stableid): Smart, Move, Patrol.
    const int SmartAbility=1,MoveAbility=16,PatrolAbility=17,DragPixels=6,DoubleClickMs=350,TargetWaitMs=600;
    // A starts attack targeting with both standard and grid hotkeys.
    const int AttackKey=0x41;

    enum Kind {Spatial,LeftClick,RightClick,TargetClick}
    class Item {public Kind Kind;public byte[] Body;public string What;public int Tick,X,Y;public bool Shift;public int SeqAtInput,InputTick,Pair;public ulong SelectType;}

    readonly GameClient client;readonly BridgeCalibration cal;readonly Action<string> log;readonly string bankRoot;
    readonly BlockingCollection<Item> queue=new BlockingCollection<Item>();
    readonly Thread hookThread,sendThread,bankThread,cameraThread;HookProc mouseProc,keyProc;IntPtr mouseHook,keyHook;uint hookThreadId;
    readonly ManualResetEvent stopping=new ManualResetEvent(false);
    bool leftDown;POINT downAt;POINT lastMove;IntPtr downWindow;int lastClickTick;POINT lastClick;
    // Targeting state from the guest's bank, plus the last guest input that may have started targeting.
    volatile bool targetOn;volatile int targetSeq=-1;volatile string targetAbil="";
    volatile int lastUiInputTick=int.MinValue,seqAtUiInput=-1;
    // After the bridge itself ended targeting (probe + Esc), a late "on" report is stale until the "off" report arrives.
    volatile bool ignoreTargetOn;
    // Local targeting guess. Input "pairs": previous key (within 3 s) << 16 | key, or 0x1000 + command-card cell.
    // learned: pair -> started targeting (from reports) or not (stray probe / probe refused). Default: only A.
    readonly ConcurrentDictionary<int,bool> learned=new ConcurrentDictionary<int,bool>();
    volatile int predictPair,lastInputPair,lastPredictedPair,lastPredictedTick;volatile bool shiftChain;
    int lastKey,lastKeyTick;readonly bool[] keyHeld=new bool[256];volatile bool chatOpen;int strayCount=-1;
    // Guest camera (world units) for the bridge's arrow-key scrolling; camGate guards the camera fields.
    const double ScrollSpeed=25;        // world units per real second (the map's scroll: 18 per game second at Faster)
    const int CamSettleMs=700;          // reports this soon after a camera change may still show the old position
    readonly object camGate=new object();
    double camX,camY,minX=-1e9,minY=-1e9,maxX=1e9,maxY=1e9;bool camKnown,camSeen,camCinematic;volatile bool camEnabled=true;int cameraMoves;
    int lastCamCommand=int.MinValue/2,camDirtyTick=int.MinValue/2;
    readonly bool[] arrowHeld=new bool[4],arrowBridged=new bool[4],arrowPending=new bool[4];int pendingTick;
    const int PendingMaxMs=1500;        // an arrow held for the next camera report goes to the map after this long
    readonly AutoResetEvent cameraWake=new AutoResetEvent(false);
    bool minimapDown;int lastEdgeCheck;
    // Guest camera zoom (distance, pitch) for the click mapping: from the wheel at once, from the map's report once settled.
    double zoomDistance=BridgeCalibration.DefaultDistance,zoomPitch=BridgeCalibration.DefaultPitch;int lastWheelTick=int.MinValue/2;
    double reportedDistance=double.NaN,reportedPitch=double.NaN;bool zoomReportPending;
    // Right-clicks queued but not sent yet: a left click waiting for the targeting report stops waiting when one arrives.
    int pendingRight;
    public int Sent,Failed;

    public ClickBridge(GameClient client,BridgeCalibration cal,Action<string> log,string bankRoot) {
        this.client=client;this.cal=cal;this.log=log;this.bankRoot=bankRoot;
        sendThread=new Thread(SendLoop){IsBackground=true,Name="bridge-send"};sendThread.Start();
        bankThread=new Thread(BankLoop){IsBackground=true,Name="bridge-bank"};bankThread.Start();
        cameraThread=new Thread(CameraLoop){IsBackground=true,Name="bridge-camera"};cameraThread.Start();
        var ready=new ManualResetEvent(false);
        hookThread=new Thread(()=>{
            // Hook coordinates are physical pixels; make window queries on this thread physical too.
            SetThreadDpiAwarenessContext(new IntPtr(-4));
            hookThreadId=GetCurrentThreadId();
            if(!RawMode) {
                mouseProc=OnMouse;keyProc=OnKey;
                mouseHook=SetWindowsHookEx(14,mouseProc,GetModuleHandle(null),0);
                keyHook=SetWindowsHookEx(13,keyProc,GetModuleHandle(null),0);ready.Set();
                if(mouseHook==IntPtr.Zero||keyHook==IntPtr.Zero){log("Click bridge: hook failed "+Marshal.GetLastWin32Error());return;}
            }
            try {CreateDragBox();} catch(Exception e) {log("Click bridge: drag box unavailable: "+e.Message);}
            if(RawMode) {
                // The overlay class is registered by CreateDragBox; the sink is a hidden window of that class.
                try {CreateRawSink();} catch(Exception e) {log("Click bridge: raw input unavailable: "+e.Message);}
                ready.Set();
            }
            try {CreateMarker();} catch(Exception e) {log("Click bridge: move marker unavailable: "+e.Message);}
            try {CreatePingPanel();} catch(Exception e) {log("Click bridge: ping panel unavailable: "+e.Message);}
            MSG msg;while(GetMessage(out msg,IntPtr.Zero,0,0)>0){TranslateMessage(ref msg);DispatchMessage(ref msg);}
            if(mouseHook!=IntPtr.Zero)UnhookWindowsHookEx(mouseHook);if(keyHook!=IntPtr.Zero)UnhookWindowsHookEx(keyHook);
            if(rawSink!=IntPtr.Zero) {
                KillTimer(rawSink,new IntPtr(3));for(int a=0;a<4;a++)UnregisterHotKey(rawSink,a+1);
                var remove=new[]{new RAWINPUTDEVICE{usUsagePage=1,usUsage=2,dwFlags=1},new RAWINPUTDEVICE{usUsagePage=1,usUsage=6,dwFlags=1}};
                RegisterRawInputDevices(remove,2,(uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                ClickBridge gone0;DestroyWindow(rawSink);overlayOwners.TryRemove(rawSink,out gone0);
            }
            // Overlay teardown on the thread that owns the windows; the class stays registered for the next session.
            ClickBridge gone;
            if(marker!=IntPtr.Zero){KillTimer(marker,new IntPtr(1));DestroyWindow(marker);overlayOwners.TryRemove(marker,out gone);}
            if(pingPanel!=IntPtr.Zero){KillTimer(pingPanel,new IntPtr(2));DestroyWindow(pingPanel);overlayOwners.TryRemove(pingPanel,out gone);}
            if(box!=IntPtr.Zero){DestroyWindow(box);overlayOwners.TryRemove(box,out gone);}
            if(boxKeyBrush!=IntPtr.Zero)DeleteObject(boxKeyBrush);
            if(boxBrush!=IntPtr.Zero)DeleteObject(boxBrush);
            if(targetBrush!=IntPtr.Zero)DeleteObject(targetBrush);
        }){IsBackground=true,Name="bridge-hook"};
        hookThread.Start();ready.WaitOne(5000);
        log("Click bridge active for SC2 PID "+client.Process.Id+" (feature layer "+cal.Resolution+"px, "+cal.FeatureWidth+" world units)");
        try {cal.FitModel();log("Click bridge: zoom camera model fits the calibration within "+(cal.ModelResidual/cal.Resolution*cal.FeatureWidth).ToString("0.00")+" world units");}
        catch(Exception e){log("Click bridge: zoom camera model unavailable: "+e.Message);}
    }

    bool GuestForeground() {uint pid;GetWindowThreadProcessId(GetForegroundWindow(),out pid);return pid==(uint)client.Process.Id;}

    void NoteUiInput() {seqAtUiInput=targetSeq;lastUiInputTick=Environment.TickCount;}

    // A key or command-card press that may start targeting. predicted: the bridge treats targeting as on right away.
    void InputStarted(int pair,bool predicted) {
        NoteUiInput();lastInputPair=pair;predictPair=predicted?pair:0;shiftChain=false;ignoreTargetOn=false;
    }
    void ClearPrediction() {predictPair=0;shiftChain=false;}
    bool Predicts(int pair,int vk) {bool known;return learned.TryGetValue(pair,out known)?known:vk==AttackKey;}
    // Targeting is known to be on: guessed from the input, or reported by the map with no newer input pending.
    bool TargetingKnown() {
        if(predictPair!=0)return true;
        return targetOn&&!ignoreTargetOn&&(targetSeq!=seqAtUiInput||Environment.TickCount-lastUiInputTick>=TargetWaitMs);
    }

    IntPtr OnKey(int code,IntPtr wParam,IntPtr lParam) {
        bool swallow=false;
        try {
            if(code>=0) {
                var k=(KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam,typeof(KBDLLHOOKSTRUCT));int m=(int)wParam;
                // Key-ups are tracked whichever window has focus (held arrows, shift); key-downs only for the guest window.
                if(m==WM_KEYUP||m==WM_SYSKEYUP)swallow=GuestKey((int)k.vkCode,false,(k.flags&0x10)!=0);
                else if((m==WM_KEYDOWN||m==WM_SYSKEYDOWN)&&GuestForeground())swallow=GuestKey((int)k.vkCode,true,(k.flags&0x10)!=0);
            }
        } catch(Exception e){log("Click bridge: "+e.Message);}
        return swallow?new IntPtr(1):CallNextHookEx(keyHook,code,wParam,lParam);
    }

    // Returns true when SC2 must not see the key (arrow keys the bridge scrolls with). Runs on the hook thread.
    bool GuestKey(int vk,bool down,bool injected) {
        vk&=255;
        if(vk>=0x25&&vk<=0x28)return ArrowKey(vk-0x25,down,injected);
        if(!down) {
            keyHeld[vk]=false;
            if(vk==0x12||vk==0xA4||vk==0xA5){altArmed=false;HidePingPanel();}
            // Shift-queued targeting stays on while shift is held; releasing it ends that chain.
            if((vk==0x10||vk==0xA0||vk==0xA1)&&shiftChain)ClearPrediction();
            return false;
        }
        bool repeat=keyHeld[vk];keyHeld[vk]=true;
        // The bridge's own Esc (SendInput) is injected; auto-repeat is not a new press.
        if(injected||repeat)return false;
        if(vk==0x0D){chatOpen=!chatOpen;ClearPrediction();return false;} // Enter opens and sends the chat line
        if(vk==0x1B){chatOpen=false;pingMode=false;ClearPrediction();return false;}
        if(chatOpen)return false;
        // Holding Alt over the world opens the ping panel (see PingPanel below); a quick Alt+click stays a plain ping.
        if((vk==0x12||vk==0xA4||vk==0xA5)&&pingPanel!=IntPtr.Zero) {
            IntPtr aw;POINT ap;int aww,awh;
            if(WorldPoint(lastMove,out aw,out ap,out aww,out awh)){altArmed=true;SetTimer(pingPanel,new IntPtr(2),PanelDelayMs,IntPtr.Zero);}
        }
        if((vk>=0x10&&vk<=0x12)||(vk>=0xA0&&vk<=0xA5)||vk==0x5B||vk==0x5C)return false; // modifiers
        int now=Environment.TickCount;
        // Camera keys move the guest camera natively (Backspace: bases, Space: last alert, F5-F8: saved views, a double-tapped
        // control group): the bridge's camera position is stale until the map reports again.
        // Alt+V: the map pans the camera to the ally's view (campaign_guest_ui.galaxy).
        if(vk==0x08||vk==0x20||(vk>=0x74&&vk<=0x77)||(vk>=0x30&&vk<=0x39&&vk==lastKey&&now-lastKeyTick<500)||(vk==0x56&&GetAsyncKeyState(0x12)<0))CameraDirty();
        int pair=(now-lastKeyTick<3000?lastKey<<16:0)|vk;lastKey=vk;lastKeyTick=now;
        // Keys that never start targeting: Esc, Tab, Enter, Space, Backspace, control groups, F-keys.
        bool neutral=vk==0x09||vk==0x20||vk==0x08||(vk>=0x30&&vk<=0x39)||(vk>=0x70&&vk<=0x7B);
        if(neutral){ClearPrediction();return false;}
        InputStarted(pair,Predicts(pair,vk));
        return false;
    }

    // Arrow a (left, up, right, down). The first press decides who scrolls until the key is released: the bridge (key
    // swallowed) when it knows the camera, else the map (key passed on). Key-ups always pass (harmless for the map).
    bool ArrowKey(int a,bool down,bool injected) {
        lock(camGate) {
            // While the map scrolls, the bridge's camera position goes stale until a report after the release.
            if(!down){if(arrowHeld[a]&&!arrowBridged[a]&&!arrowPending[a]){camKnown=false;camDirtyTick=Environment.TickCount;}arrowHeld[a]=false;arrowBridged[a]=false;arrowPending[a]=false;return false;}
            if(arrowHeld[a])return arrowBridged[a]||arrowPending[a];
            arrowHeld[a]=true;
            // Raw Input can't keep the key from the game; the arrow hotkeys do (UpdateArrowHotkeys). Without them the map scrolls
            // (bridging too would scroll twice).
            bool can=(!RawMode||arrowHotkeys)&&!injected&&!chatOpen&&camEnabled&&!camCinematic;
            arrowBridged[a]=can&&camKnown;
            // Camera position stale (minimap click, Space, camera hotkeys): the press waits for the next report (under a second)
            // instead of going to the map, whose scroll would make the position stale again; the keys then kept flipping
            // between the bridge and the map's laggy scroll (2026-10-22 test: 20 switches in 2.5 minutes).
            arrowPending[a]=can&&!camKnown&&RawMode;
            if(arrowPending[a])pendingTick=Environment.TickCount;
            if(!arrowBridged[a]&&!arrowPending[a]&&Environment.GetEnvironmentVariable("SC2_COOP_TEST_ARROWS")=="1")
                log("Test: arrow "+a+" to the map (hotkeys "+arrowHotkeys+", camera known "+camKnown+", cinematic "+camCinematic+", chat "+chatOpen+", injected "+injected+")");
            if(!arrowBridged[a]&&!arrowPending[a]){camKnown=false;camDirtyTick=Environment.TickCount;}
            if(arrowPending[a])return true;
        }
        if(arrowBridged[a])cameraWake.Set();
        return arrowBridged[a];
    }

    // A client pixel at the guest's current zoom -> feature-layer point.
    bool MapZoomed(int x,int y,int w,int h,out int fx,out int fy) {
        double d,p;lock(camGate){d=zoomDistance;p=zoomPitch;}
        return cal.Map(x,y,w,h,d,p,out fx,out fy);
    }
    // One wheel notch is one ZoomTable step (forward = in). Starts from the step nearest the current distance.
    void Wheel(int notches) {
        lock(camGate) {
            var t=BridgeCalibration.ZoomTable;int level=0;for(int i=1;i<t.Length;i++)if(Math.Abs(t[i][0]-zoomDistance)<Math.Abs(t[level][0]-zoomDistance))level=i;
            level=Math.Max(0,Math.Min(t.Length-1,level+notches));zoomDistance=t[level][0];zoomPitch=t[level][1];lastWheelTick=Environment.TickCount;
        }
    }

    void CameraDirty() {lock(camGate){camKnown=false;camDirtyTick=Environment.TickCount;}}

    IntPtr OnMouse(int code,IntPtr wParam,IntPtr lParam) {
        // Mouse input passes through, except a world ping click the bridge replaced with a posted minimap click.
        swallowNow=false;
        try {if(code>=0)Handle((int)wParam,(MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam,typeof(MSLLHOOKSTRUCT)));}
        catch(Exception e){log("Click bridge: "+e.Message);}
        return swallowNow?new IntPtr(1):CallNextHookEx(mouseHook,code,wParam,lParam);
    }

    // Client point of a screen point if the guest window is in the foreground.
    bool ClientPoint(POINT screen,out IntPtr hwnd,out POINT p,out int width,out int height) {
        hwnd=GetForegroundWindow();p=screen;width=height=0;uint pid;GetWindowThreadProcessId(hwnd,out pid);
        if(pid!=(uint)client.Process.Id)return false;
        RECT r;GetClientRect(hwnd,out r);width=r.Right;height=r.Bottom;ScreenToClient(hwnd,ref p);
        return p.X>=0&&p.Y>=0&&p.X<width&&p.Y<height;
    }
    static bool InCommandCard(POINT p,int w,int h) {return p.X/(double)w>0.79&&p.Y/(double)h>0.72;}
    // True if the point lies in the 3D world area (not the console, minimap, command card or resource bar).
    static bool InWorld(POINT p,int w,int h) {
        double fx=p.X/(double)w,fy=p.Y/(double)h;
        return !(fy>0.765||(fx<0.165&&fy>0.70)||(fx>0.79&&fy>0.72)||fy<0.035);
    }
    bool WorldPoint(POINT screen,out IntPtr hwnd,out POINT p,out int width,out int height) {
        return ClientPoint(screen,out hwnd,out p,out width,out height)&&InWorld(p,width,height);
    }

    void Handle(int message,MSLLHOOKSTRUCT info) {
        IntPtr hwnd;POINT p;int w,h;
        bool shift=GetAsyncKeyState(0x10)<0,ctrl=GetAsyncKeyState(0x11)<0;
        if(message==WM_MOUSEMOVE) {
            lastMove=info.pt;if(leftDown)UpdateDragBox(info.pt);
            if(panelShown){int hit=PanelHit(info.pt);if(hit!=panelHover){panelHover=hit;Redraw(pingPanel);}}
            // Edge scrolling moves the guest camera natively (if it works for the guest at all).
            if(Environment.TickCount-lastEdgeCheck>50) {
                lastEdgeCheck=Environment.TickCount;
                if(ClientPoint(info.pt,out hwnd,out p,out w,out h)&&(p.X<=2||p.Y<=2||p.X>=w-3||p.Y>=h-3))CameraDirty();
            }
            return;
        }
        if(message==WM_MBUTTONDOWN||message==WM_MBUTTONUP){if(GuestForeground())CameraDirty();return;} // middle-drag scrolling
        if(message==WM_MOUSEWHEEL) {
            // SC2 zooms the camera with the wheel over the world (not over the console, chat or menus).
            if(WorldPoint(info.pt,out hwnd,out p,out w,out h)){int notches=(short)(info.mouseData>>16)/120;if(notches!=0)Wheel(notches);}
            return;
        }
        if(message==WM_RBUTTONDOWN) {
            pingMode=false; // a right-click cancels SC2's ping mode
            // Sent on press, like the native client (on release, the time the button was held was added to every move).
            // Press position; if that is off the world area, the last move position (odd hook positions were seen before).
            bool world=WorldPoint(info.pt,out hwnd,out p,out w,out h);
            if(!world)world=WorldPoint(lastMove,out hwnd,out p,out w,out h);
            if(!world)return;
            int fx,fy;if(!MapZoomed(p.X,p.Y,w,h,out fx,out fy))return;
            // A right-click ends targeting. It is only a cancel (no move) once the map has reported targeting: a guess may be
            // wrong (A at a barracks trains a marine; the right-click then sets the rally).
            ClearPrediction();
            // Instant local feedback: the guest's native move marker is dropped with the click. Not while targeting.
            if(!(targetOn&&!ignoreTargetOn))ShowMarker(hwnd,p,false);
            Interlocked.Increment(ref pendingRight);
            queue.Add(new Item{Kind=Kind.RightClick,X=fx,Y=fy,Shift=shift,Tick=Environment.TickCount,
                Body=Spatial(Wire.Msg(1,Wire.Num(1,SmartAbility),Wire.Msg(2,Point(fx,fy)),shift?Wire.Num(4,1):new byte[0])),What="smart "+fx+","+fy+" from client "+p.X+","+p.Y});
            return;
        }
        if(message==WM_LBUTTONDOWN) {
            leftDown=false;altArmed=false;
            if(panelShown) {
                // A panel button: that ping type where the panel opened. Anywhere else: the panel closes and the click goes on.
                int hit=PanelHit(info.pt);IntPtr ph=panelHwnd;POINT origin=panelOrigin;HidePingPanel();
                if(hit>=0) {
                    RECT pr;GetClientRect(ph,out pr);int px,py;
                    if(MapZoomed(origin.X,origin.Y,pr.Right,pr.Bottom,out px,out py))Ping(ph,px,py,pr.Right,pr.Bottom,origin,false,"panel "+PanelNames[hit]+" at client "+origin.X+","+origin.Y,hit);
                    swallowNow=true;swallowUp=true;return;
                }
            }
            if(!ClientPoint(info.pt,out hwnd,out p,out w,out h))return;
            if(InCommandCard(p,w,h)) {
                // A button may start targeting; its cell (5 x 3 grid) plus the previous input is the pair to learn.
                int col=Math.Min(4,Math.Max(0,(int)((p.X/(double)w-0.79)/0.21*5))),row=Math.Min(2,Math.Max(0,(int)((p.Y/(double)h-0.72)/0.28*3)));
                int card=0x1000+row*5+col,now=Environment.TickCount,pair=(now-lastKeyTick<3000?lastKey<<16:0)|card;lastKey=card;lastKeyTick=now;
                InputStarted(pair,Predicts(pair,0));return;
            }
            if(InPingButton(p,h)){pingMode=!pingMode;pingModeTick=Environment.TickCount;return;}
            if(p.X/(double)w<0.165&&p.Y/(double)h>0.70) {
                // Minimap: moves the camera natively, or (while targeting) gives the targeted order natively. In ping mode, or
                // with Alt, it pings natively (no camera move).
                minimapDown=!pingMode&&GetAsyncKeyState(0x12)>=0;pingMode=false;if(minimapDown)CameraDirty();if(!shift)ClearPrediction();return;
            }
            // Alt + left click in the world (or any world click in ping mode) is a ping; the guest's own is dropped like its other
            // world clicks. The press and its release are swallowed and a minimap click is posted instead.
            bool modePing=pingMode&&Environment.TickCount-pingModeTick<PingModeMs;
            if((GetAsyncKeyState(0x12)<0||modePing)&&InWorld(p,w,h)) {
                pingMode=false;int px,py;
                if(MapZoomed(p.X,p.Y,w,h,out px,out py)&&Ping(hwnd,px,py,w,h,p,false,"from client "+p.X+","+p.Y+(modePing?" (ping mode)":""),-1)){swallowNow=true;swallowUp=true;}
                return;
            }
            if(InWorld(p,w,h)&&TargetingKnown()) {
                // Targeting: the press is the command (as in the native client), no need to wait for the release.
                int tx,ty;if(!MapZoomed(p.X,p.Y,w,h,out tx,out ty))return;
                ShowMarker(hwnd,p,true);TargetClick(tx,ty,shift,ctrl,"from client "+p.X+","+p.Y);return;
            }
            leftDown=InWorld(p,w,h);downAt=p;downWindow=hwnd;return;
        }
        if(message==WM_LBUTTONUP&&swallowUp){swallowUp=false;swallowNow=true;return;}
        if(message==WM_LBUTTONUP&&minimapDown){minimapDown=false;CameraDirty();}
        if(message==WM_LBUTTONUP)HideDragBox();
        if(message==WM_LBUTTONUP&&leftDown) {
            leftDown=false;
            // The release may fall outside the world area (drag into the console); clamp instead of dropping.
            hwnd=downWindow;p=info.pt;ScreenToClient(hwnd,ref p);RECT r;GetClientRect(hwnd,out r);w=r.Right;h=r.Bottom;
            p.X=Math.Max(0,Math.Min(w-1,p.X));p.Y=Math.Max(0,Math.Min((int)(h*0.765),p.Y));
            int ax,ay,bx,by;
            if(Math.Abs(p.X-downAt.X)<=DragPixels&&Math.Abs(p.Y-downAt.Y)<=DragPixels) {
                if(!MapZoomed(downAt.X,downAt.Y,w,h,out ax,out ay))return;
                bool dbl=Environment.TickCount-lastClickTick<DoubleClickMs&&Math.Abs(downAt.X-lastClick.X)<=DragPixels&&Math.Abs(downAt.Y-lastClick.Y)<=DragPixels;
                lastClickTick=Environment.TickCount;lastClick=downAt;
                ulong type=(ulong)(dbl||ctrl?(shift?4:3):(shift?2:1));
                // Selection or targeted command is decided on the send thread, once the targeting state is known.
                queue.Add(new Item{Kind=Kind.LeftClick,X=ax,Y=ay,Shift=shift,SelectType=type,Tick=Environment.TickCount,SeqAtInput=seqAtUiInput,InputTick=lastUiInputTick,
                    Body=Spatial(Wire.Msg(3,Wire.Msg(1,Point(ax,ay)),Wire.Num(2,type))),What="select "+type+" "+ax+","+ay+" from client "+downAt.X+","+downAt.Y});
            } else {
                if(!MapZoomed(downAt.X,downAt.Y,w,h,out ax,out ay)|!MapZoomed(p.X,p.Y,w,h,out bx,out by)) {
                    ax=Clamp(ax);ay=Clamp(ay);bx=Clamp(bx);by=Clamp(by);
                }
                Enqueue(Wire.Msg(4,Wire.Msg(1,Wire.Msg(1,Point(Math.Min(ax,bx),Math.Min(ay,by))),Wire.Msg(2,Point(Math.Max(ax,bx),Math.Max(ay,by)))),shift?Wire.Num(2,1):new byte[0]),"rect "+Math.Min(ax,bx)+","+Math.Min(ay,by)+"-"+Math.Max(ax,bx)+","+Math.Max(ay,by));
            }
            return;
        }
    }

    // Guest ping. The guest's world clicks are dropped, Alt+clicks included, but its minimap clicks are not: a minimap
    // Alt+click, or a minimap click in SC2's ping mode, gives a real ping event (observed 2026-10-20). So a world Alt+click (or
    // a world click after the minimap ping button) becomes a click posted to the guest window on the minimap pixel of the same
    // world point. The map (campaign_guest_ui.galaxy) shows the ping to both players. No chat line, no API action.
    const int PingModeMs=15000;
    volatile bool pingMode;int pingModeTick;bool swallowUp,swallowNow;
    // option: -1 plain, else the ping panel's type (0 attack, 1 on my way, 2 retreat, 3 defend), sent as option+1 Scroll Lock
    // presses right before the minimap click; the map (CoopPingTypeKey) applies them to the guest's next ping.
    bool Ping(IntPtr hwnd,int fx,int fy,int w,int h,POINT back,bool postAlt,string what,int option) {
        double cx,cy,x0,y0,x1,y1,ccx,ccy;bool seen;
        lock(camGate){cx=camX;cy=camY;seen=camSeen;x0=minX;y0=minY;x1=maxX;y1=maxY;}
        if(!seen||x0<-1e8){log("Click bridge: ping "+what+" skipped (the map hasn't reported the guest camera yet)");return false;}
        // The feature layer is a top-down square of FeatureWidth world units centred on the camera target.
        double wx=cx+(fx/(double)cal.Resolution-0.5)*cal.FeatureWidth,wy=cy-(fy/(double)cal.Resolution-0.5)*cal.FeatureWidth;
        string key=w+"x"+h+" "+x0+","+y0+","+x1+","+y1;
        lock(camGate){if(key!=pingCorrKey){pingCorrKey=key;pingCorrX=pingCorrY=0;}ccx=pingCorrX;ccy=pingCorrY;pingSentX=wx;pingSentY=wy;pingSentTick=Environment.TickCount;}
        POINT m=MinimapPixel(wx-ccx,wy-ccy,w,h,x0,y0,x1,y1);
        IntPtr at=new IntPtr((m.Y<<16)|(m.X&0xFFFF)),home=new IntPtr((back.Y<<16)|(back.X&0xFFFF));
        // Posted from a worker with short gaps (a press and release in one frame may be missed); the hover goes back after.
        ThreadPool.QueueUserWorkItem(_=>{
            for(int k=0;k<=option&&option<4;k++){PostMessage(hwnd,WM_KEYDOWN,new IntPtr(0x91),new IntPtr(0x00460001));Thread.Sleep(15);PostMessage(hwnd,WM_KEYUP,new IntPtr(0x91),new IntPtr(unchecked((int)0xC0460001)));Thread.Sleep(15);}
            if(postAlt){PostMessage(hwnd,WM_SYSKEYDOWN,new IntPtr(0x12),new IntPtr(0x20380001));Thread.Sleep(20);}
            PostMessage(hwnd,WM_MOUSEMOVE,IntPtr.Zero,at);Thread.Sleep(20);
            PostMessage(hwnd,WM_LBUTTONDOWN,new IntPtr(1),at);Thread.Sleep(40);
            PostMessage(hwnd,WM_LBUTTONUP,IntPtr.Zero,at);Thread.Sleep(20);
            if(postAlt){PostMessage(hwnd,WM_SYSKEYUP,new IntPtr(0x12),new IntPtr(unchecked((int)0xE0380001)));Thread.Sleep(20);}
            PostMessage(hwnd,WM_MOUSEMOVE,IntPtr.Zero,home);
        });
        var inv=System.Globalization.CultureInfo.InvariantCulture;
        log("Click bridge: ping "+what+" -> world "+wx.ToString("0.0",inv)+","+wy.ToString("0.0",inv)+" via minimap pixel "+m.X+","+m.Y+(ccx!=0||ccy!=0?" (offset correction "+ccx.ToString("0.0",inv)+","+ccy.ToString("0.0",inv)+")":""));
        return true;
    }

    // Minimap offset learned from where the map says the guest's pings landed (Ping section of the CoopBridge bank). The
    // minimap geometry below is a fit at one window size; other sizes and aspect ratios can shift it. Kept per window size and
    // map; each report moves the correction 70 % of the way to the observed error.
    double pingCorrX,pingCorrY,pingSentX,pingSentY;int pingSentTick=int.MinValue/2,pingReportSeq=-1;string pingCorrKey="";
    void ReadPingReport(Func<string,string,XElement> value) {
        var seq=value("Ping","seq");if(seq==null){if(pingReportSeq<0)pingReportSeq=0;return;}
        int n=int.Parse((string)seq.Attribute("int"));
        if(pingReportSeq<0||n==pingReportSeq){pingReportSeq=n;return;} // the first read is a baseline (the bank outlives a game)
        pingReportSeq=n;
        Func<string,double> num=k=>{var v=value("Ping",k);return v==null?double.NaN:double.Parse(v.Attributes().First().Value,System.Globalization.CultureInfo.InvariantCulture);};
        double x=num("x"),y=num("y"),ex,ey,cx,cy;
        lock(camGate) {
            if(double.IsNaN(x)||double.IsNaN(y)||Environment.TickCount-pingSentTick>5000)return;
            ex=x-pingSentX;ey=y-pingSentY;
            if(Math.Abs(ex)>8||Math.Abs(ey)>8||(Math.Abs(ex)<0.4&&Math.Abs(ey)<0.4))return; // not this ping, or within a minimap pixel
            pingCorrX+=0.7*ex;pingCorrY+=0.7*ey;cx=pingCorrX;cy=pingCorrY;
        }
        var inv=System.Globalization.CultureInfo.InvariantCulture;
        log("Click bridge: guest ping landed "+ex.ToString("0.0",inv)+","+ey.ToString("0.0",inv)+" off; minimap offset correction now "+cx.ToString("0.0",inv)+","+cy.ToString("0.0",inv));
    }

    // Ping panel. Holding Alt opens SC2's ping panel (attack / on my way / retreat / defend), but not for the guest (its world
    // input layer is gated like its clicks). So the bridge draws one: after PanelDelayMs of Alt over the world, four buttons
    // above the cursor in a click-through overlay; a click on one pings that type where the panel opened, releasing Alt closes it.
    const int PanelDelayMs=180;const uint WM_APP_TESTPANEL=0x8003;
    static readonly string[] PanelNames={"Attack","On my way","Retreat","Defend"};
    static readonly System.Drawing.Color[] PanelColors={System.Drawing.Color.FromArgb(196,58,48),System.Drawing.Color.FromArgb(44,128,200),
        System.Drawing.Color.FromArgb(214,158,40),System.Drawing.Color.FromArgb(60,156,82)};
    IntPtr pingPanel,panelHwnd;volatile bool panelShown;bool altArmed;POINT panelOrigin,testPanelAt;int panelHover=-1;
    RECT panelScreen;int panelButtonW,panelButtonH,panelGap;

    void CreatePingPanel() {
        if(box==IntPtr.Zero)throw new Exception("needs the overlay window class");
        pingPanel=CreateWindowEx(0x80000|0x20|0x8000000|0x80|0x8,"SC2CoopDragBox","",0x80000000,0,0,1,1,IntPtr.Zero,IntPtr.Zero,GetModuleHandle(null),IntPtr.Zero);
        if(pingPanel==IntPtr.Zero)throw new Exception("CreateWindowEx "+Marshal.GetLastWin32Error());
        overlayOwners[pingPanel]=this;
        SetLayeredWindowAttributes(pingPanel,BoxKey,255,1);
    }

    // hwnd/p: guest window and client point (the ping's spot). Runs on the hook thread.
    void ShowPingPanel(IntPtr hwnd,POINT p,int w,int h) {
        if(pingPanel==IntPtr.Zero||ExclusiveFullscreen.Check())return;
        double k=h/720.0;panelButtonW=(int)(86*k);panelButtonH=(int)(28*k);panelGap=(int)(5*k);
        int total=4*panelButtonW+3*panelGap;
        var tl=new POINT{X=Math.Max(0,Math.Min(w-total,p.X-total/2)),Y=Math.Max(0,p.Y-panelButtonH-(int)(18*k))};
        ClientToScreen(hwnd,ref tl);
        panelScreen=new RECT{Left=tl.X,Top=tl.Y,Right=tl.X+total,Bottom=tl.Y+panelButtonH};
        panelHwnd=hwnd;panelOrigin=p;panelHover=-1;panelShown=true;
        SetWindowPos(pingPanel,new IntPtr(-1),tl.X,tl.Y,total,panelButtonH,0x10|0x40);
        Redraw(pingPanel);
    }
    void HidePingPanel(){if(pingPanel!=IntPtr.Zero){KillTimer(pingPanel,new IntPtr(2));if(panelShown){ShowWindow(pingPanel,0);panelShown=false;}}}
    int PanelHit(POINT screen) {
        if(!panelShown||screen.Y<panelScreen.Top||screen.Y>=panelScreen.Bottom||screen.X<panelScreen.Left||screen.X>=panelScreen.Right)return -1;
        int x=screen.X-panelScreen.Left,i=x/(panelButtonW+panelGap);
        return i<4&&x-i*(panelButtonW+panelGap)<panelButtonW?i:-1;
    }
    void PaintPingPanel(IntPtr dc,RECT r) {
        FillRect(dc,ref r,boxKeyBrush);
        using(var g=System.Drawing.Graphics.FromHdc(dc))
        using(var font=new System.Drawing.Font("Segoe UI",Math.Max(9f,panelButtonH*0.46f),System.Drawing.FontStyle.Bold,System.Drawing.GraphicsUnit.Pixel)) {
            for(int i=0;i<4;i++) {
                var rect=new System.Drawing.Rectangle(i*(panelButtonW+panelGap),0,panelButtonW,panelButtonH);
                var c=PanelColors[i];if(i==panelHover)c=System.Drawing.Color.FromArgb(Math.Min(255,c.R+40),Math.Min(255,c.G+40),Math.Min(255,c.B+40));
                using(var b=new System.Drawing.SolidBrush(c))g.FillRectangle(b,rect);
                using(var pen=new System.Drawing.Pen(i==panelHover?System.Drawing.Color.White:System.Drawing.Color.FromArgb(20,24,30),2))g.DrawRectangle(pen,rect.X+1,rect.Y+1,rect.Width-2,rect.Height-2);
                System.Windows.Forms.TextRenderer.DrawText(g,PanelNames[i],font,rect,System.Drawing.Color.White,System.Windows.Forms.TextFormatFlags.HorizontalCenter|System.Windows.Forms.TextFormatFlags.VerticalCenter);
            }
        }
    }
    // Minimap geometry, fitted on The Outlaws at 1280x720 (2026-10-20, three minimap clicks against the map's world positions,
    // within 0.1 world units): a square of 0.2376 h, its left edge 0.0293 h from the client's left and its bottom 0.0139 h
    // above the client's bottom, showing the playable area scaled to fit and centred. Assumes the console scales with height.
    static POINT MinimapPixel(double x,double y,int w,int h,double x0,double y0,double x1,double y1) {
        double s=0.2376*h,left=0.0293*h,bottom=h-0.0139*h,k=Math.Max(x1-x0,y1-y0)/s;
        double ox=left+(s-(x1-x0)/k)/2,oy=bottom-(s-(y1-y0)/k)/2;
        x=Math.Max(x0,Math.Min(x1,x));y=Math.Max(y0,Math.Min(y1,y));
        return new POINT{X=(int)Math.Round(ox+(x-x0)/k),Y=(int)Math.Round(oy-(y-y0)/k)};
    }
    // The minimap's ping button (top of the three buttons right of the minimap) puts SC2 into ping mode.
    static bool InPingButton(POINT p,int h) {return p.X>=0.283*h&&p.X<=0.322*h&&p.Y>=h-0.230*h&&p.Y<=h-0.191*h;}

    // Drag box: the guest's native drag is dropped, so SC2 draws no selection rectangle. A click-through, non-activating,
    // topmost layered window owned by the hook thread draws one over the guest window while the left button is held.
    // The window's background is the colour key (fully transparent); only the 2 px frame is opaque.
    const uint BoxKey=0x00FF00FF,BoxColor=0x0040E040,TargetColor=0x004040F0; // COLORREF 0x00BBGGRR: magenta key, green frame, red target marker
    const uint WM_APP_TESTBOX=0x8001;
    IntPtr box,boxKeyBrush,boxBrush,targetBrush,markerBrush;bool boxShown;RECT testBox;

    // The window class can be registered only once per process, but a launcher runs one ClickBridge per game session.
    // So the class has one static window procedure that hands each message to the bridge owning that window (a second
    // session used to get windows wired to the first, disposed bridge: white, never-hidden boxes; owner report 2026-10-19).
    static readonly object classGate=new object();static bool classRegistered;
    static readonly WndProc sharedProc=SharedProc; // static field: the delegate must outlive every window
    static readonly ConcurrentDictionary<IntPtr,ClickBridge> overlayOwners=new ConcurrentDictionary<IntPtr,ClickBridge>();
    static IntPtr SharedProc(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam) {
        ClickBridge owner;
        return overlayOwners.TryGetValue(hwnd,out owner)?owner.DragBoxProc(hwnd,msg,wParam,lParam):DefWindowProc(hwnd,msg,wParam,lParam);
    }

    void CreateDragBox() {
        lock(classGate) {
            if(!classRegistered) {
                var wc=new WNDCLASS{proc=sharedProc,instance=GetModuleHandle(null),className="SC2CoopDragBox"};
                if(RegisterClass(ref wc)==0)throw new Exception("RegisterClass "+Marshal.GetLastWin32Error());
                classRegistered=true;
            }
        }
        var wcInstance=GetModuleHandle(null);
        // WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|WS_EX_TOPMOST, WS_POPUP
        box=CreateWindowEx(0x80000|0x20|0x8000000|0x80|0x8,"SC2CoopDragBox","",0x80000000,0,0,1,1,IntPtr.Zero,IntPtr.Zero,wcInstance,IntPtr.Zero);
        if(box==IntPtr.Zero)throw new Exception("CreateWindowEx "+Marshal.GetLastWin32Error());
        overlayOwners[box]=this;
        SetLayeredWindowAttributes(box,BoxKey,255,1); // LWA_COLORKEY
        boxKeyBrush=CreateSolidBrush(BoxKey);boxBrush=CreateSolidBrush(BoxColor);targetBrush=CreateSolidBrush(TargetColor);markerBrush=boxBrush;
    }

    // --- Raw Input (default input source; see the file header) ----------------------------------------------------
    static readonly bool RawMode=Environment.GetEnvironmentVariable("SC2_COOP_BRIDGE_HOOKS")!="1";
    IntPtr rawSink;
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE {public ushort usUsagePage,usUsage;public uint dwFlags;public IntPtr hwndTarget;}
    [DllImport("user32.dll",SetLastError=true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices,uint count,uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input,uint command,byte[] data,ref uint size,uint headerSize);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    void CreateRawSink() {
        // Never shown; WS_POPUP. Mouse (page 1, usage 2) and keyboard (usage 6), RIDEV_INPUTSINK: also while another window
        // (the game) is in front.
        rawSink=CreateWindowEx(0x80|0x8000000,"SC2CoopDragBox","",0x80000000,0,0,0,0,IntPtr.Zero,IntPtr.Zero,GetModuleHandle(null),IntPtr.Zero);
        if(rawSink==IntPtr.Zero)throw new Exception("CreateWindowEx "+Marshal.GetLastWin32Error());
        overlayOwners[rawSink]=this;
        var devices=new[]{new RAWINPUTDEVICE{usUsagePage=1,usUsage=2,dwFlags=0x100,hwndTarget=rawSink},new RAWINPUTDEVICE{usUsagePage=1,usUsage=6,dwFlags=0x100,hwndTarget=rawSink}};
        if(!RegisterRawInputDevices(devices,2,(uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))throw new Exception("RegisterRawInputDevices "+Marshal.GetLastWin32Error());
        SetTimer(rawSink,new IntPtr(3),ArrowCheckMs,IntPtr.Zero);
        log("Click bridge: input via Raw Input (no system-wide hooks)");
    }

    // Arrow keys with Raw Input: the bridge's fast scroll needs the keys kept from the game (the map would scroll too, a
    // network turn later). While the guest window is in front and the bridge knows the camera, the four arrows are
    // registered as hotkeys of the sink window (RegisterHotKey: the key goes to us, not the game); otherwise (camera
    // stale, chat line open, cinematic, another window in front) they are released and the map scrolls as before.
    const int ArrowCheckMs=100;bool arrowHotkeys,arrowLogged;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    void PassArrow(int a) {
        for(int i=0;i<4;i++)UnregisterHotKey(rawSink,i+1);
        arrowHotkeys=false;
        // The arrow's key-down as an extended key (as the keyboard sends it), now that no hotkey takes it.
        var inputs=new INPUT[1];inputs[0].type=1;inputs[0].ki.vk=(ushort)(0x25+a);inputs[0].ki.scan=(ushort)new[]{0x4B,0x48,0x4D,0x50}[a];inputs[0].ki.flags=1;
        SendInput(1,inputs,Marshal.SizeOf(typeof(INPUT)));
    }
    void UpdateArrowHotkeys() {
        bool want;int late=-1;
        lock(camGate) {
            // Reserved also while the camera position is stale: a press then waits for the next report (ArrowKey).
            want=GuestForeground()&&!chatOpen&&camEnabled&&!camCinematic;
            // No report came in time (the map stopped reporting?): that arrow goes to the map after all.
            if(Environment.TickCount-pendingTick>PendingMaxMs)for(int a=0;a<4;a++)if(arrowPending[a]){arrowPending[a]=false;camKnown=false;camDirtyTick=Environment.TickCount;late=a;}
        }
        if(late>=0){log("Click bridge: no camera report for a held arrow; the map scrolls it");PassArrow(late);return;}
        // Mid-scroll the keys stay with the bridge until released.
        if(!want&&arrowHotkeys)for(int a=0;a<4;a++)if(arrowHeld[a]&&(arrowBridged[a]||arrowPending[a]))return;
        if(want==arrowHotkeys)return;
        int failed=0;
        for(int a=0;a<4;a++){if(want){if(!RegisterHotKey(rawSink,a+1,0x4000,(uint)(0x25+a)))failed++;}else UnregisterHotKey(rawSink,a+1);} // MOD_NOREPEAT
        arrowHotkeys=want&&failed==0;
        if(failed>0){for(int a=0;a<4;a++)UnregisterHotKey(rawSink,a+1);log("Click bridge: arrow keys can't be reserved ("+failed+" taken by another program); the map scrolls");}
        else if(want!=arrowLogged){arrowLogged=want;log("Click bridge: arrow keys "+(want?"scroll through the bridge":"go to the map's scroll"));}
    }
    // WM_INPUT: the same handlers the hooks call. RAWINPUTHEADER is 24 bytes on x64 (type, size, device, wParam); the device is
    // null for injected input (the bridge's own Esc). Mouse: button flags at +4, wheel delta at +6, motion at +12/+16.
    // Keyboard: flags at +2 (1 = key up), virtual key at +6.
    byte[] rawBuffer=new byte[64];
    void OnRawInput(IntPtr input) {
        const int header=24;uint size=0;
        GetRawInputData(input,0x10000003,null,ref size,header);
        if(size>rawBuffer.Length)rawBuffer=new byte[size];
        if(GetRawInputData(input,0x10000003,rawBuffer,ref size,header)!=size)return;
        uint type=BitConverter.ToUInt32(rawBuffer,0);bool injected=BitConverter.ToInt64(rawBuffer,8)==0;
        if(type==1) {
            int flags=BitConverter.ToUInt16(rawBuffer,header+2),vk=BitConverter.ToUInt16(rawBuffer,header+6);
            if(vk==0||vk>=255)return;
            // The bridge only injects Esc; an injected arrow (remote-control or macro tools) is still the player's.
            if(vk>=0x25&&vk<=0x28)injected=false;
            if((flags&1)!=0)GuestKey(vk,false,injected);else if(GuestForeground())GuestKey(vk,true,injected);
            return;
        }
        if(type!=0)return;
        int buttons=BitConverter.ToUInt16(rawBuffer,header+4);short wheel=BitConverter.ToInt16(rawBuffer,header+6);
        int dx=BitConverter.ToInt32(rawBuffer,header+12),dy=BitConverter.ToInt32(rawBuffer,header+16);
        POINT pt;GetCursorPos(out pt);var info=new MSLLHOOKSTRUCT{pt=pt};
        if(dx!=0||dy!=0||(BitConverter.ToUInt16(rawBuffer,header)&1)!=0)Handle(WM_MOUSEMOVE,info);
        if((buttons&0x1)!=0)Handle(WM_LBUTTONDOWN,info);
        if((buttons&0x2)!=0)Handle(WM_LBUTTONUP,info);
        if((buttons&0x4)!=0)Handle(WM_RBUTTONDOWN,info);
        if((buttons&0x8)!=0)Handle(WM_RBUTTONUP,info);
        if((buttons&0x10)!=0)Handle(WM_MBUTTONDOWN,info);
        if((buttons&0x20)!=0)Handle(WM_MBUTTONUP,info);
        if((buttons&0x400)!=0){info.mouseData=(uint)((int)wheel<<16);Handle(WM_MOUSEWHEEL,info);}
    }

    IntPtr DragBoxProc(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam) {
        if(msg==0xFF&&hwnd==rawSink){try{OnRawInput(lParam);}catch(Exception e){log("Click bridge: "+e.Message);}return DefWindowProc(hwnd,msg,wParam,lParam);}
        if(msg==0x113&&hwnd==rawSink){try{UpdateArrowHotkeys();}catch(Exception e){log("Click bridge: "+e.Message);}return IntPtr.Zero;} // WM_TIMER
        // WM_HOTKEY: an arrow press kept from the game (its key-down doesn't come as raw input; the key-up does). If the bridge
        // doesn't scroll for it after all, the hotkeys are released and the press is passed on, so the map scrolls: an arrow key
        // is never lost.
        if(msg==0x312&&hwnd==rawSink){int a=(int)wParam-1;if(a>=0&&a<4)try{if(!ArrowKey(a,true,false))PassArrow(a);}catch(Exception e){log("Click bridge: "+e.Message);}return IntPtr.Zero;}
        if(msg==0x14)return new IntPtr(1);           // WM_ERASEBKGND: painted below
        if(msg==0x84)return new IntPtr(-1);          // WM_NCHITTEST: HTTRANSPARENT
        if(msg==0x21)return new IntPtr(3);           // WM_MOUSEACTIVATE: MA_NOACTIVATE
        if(msg==0x113&&hwnd==pingPanel) {          // WM_TIMER: Alt held long enough
            KillTimer(hwnd,new IntPtr(2));IntPtr aw;POINT ap;int aww,awh;
            if(altArmed&&GetAsyncKeyState(0x12)<0&&WorldPoint(lastMove,out aw,out ap,out aww,out awh))ShowPingPanel(aw,ap,aww,awh);
            return IntPtr.Zero;
        }
        if(msg==0x0F&&hwnd==pingPanel){PAINTSTRUCT ps;IntPtr dc=BeginPaint(hwnd,out ps);RECT r;GetClientRect(hwnd,out r);PaintPingPanel(dc,r);EndPaint(hwnd,ref ps);return IntPtr.Zero;}
        if(msg==WM_APP_TESTPANEL) {
            if(wParam==IntPtr.Zero){HidePingPanel();return IntPtr.Zero;}
            IntPtr gw=client.Process.MainWindowHandle;RECT gr;GetClientRect(gw,out gr);
            ShowPingPanel(gw,testPanelAt,gr.Right,gr.Bottom);panelHover=(int)wParam-1;InvalidateRect(pingPanel,IntPtr.Zero,false);return IntPtr.Zero;
        }
        if(msg==0x113&&hwnd==marker){KillTimer(hwnd,new IntPtr(1));ShowWindow(hwnd,0);return IntPtr.Zero;} // WM_TIMER
        if(msg==WM_APP_TESTMARKER){ShowMarker(client.Process.MainWindowHandle,testMarker,false);return IntPtr.Zero;}
        if(msg==0x0F&&hwnd==marker) {                // WM_PAINT (marker)
            PAINTSTRUCT ps;IntPtr dc=BeginPaint(hwnd,out ps);RECT r;GetClientRect(hwnd,out r);PaintMarker(dc,r);EndPaint(hwnd,ref ps);return IntPtr.Zero;
        }
        if(msg==0x0F) {                              // WM_PAINT
            PAINTSTRUCT ps;IntPtr dc=BeginPaint(hwnd,out ps);RECT r;GetClientRect(hwnd,out r);
            FillRect(dc,ref r,boxKeyBrush);
            foreach(var edge in new[]{new RECT{Left=0,Top=0,Right=r.Right,Bottom=2},new RECT{Left=0,Top=r.Bottom-2,Right=r.Right,Bottom=r.Bottom},
                new RECT{Left=0,Top=0,Right=2,Bottom=r.Bottom},new RECT{Left=r.Right-2,Top=0,Right=r.Right,Bottom=r.Bottom}}){var e=edge;FillRect(dc,ref e,boxBrush);}
            EndPaint(hwnd,ref ps);return IntPtr.Zero;
        }
        if(msg==WM_APP_TESTBOX) {
            if(wParam==IntPtr.Zero){HideDragBox();return IntPtr.Zero;}
            IntPtr guest=client.Process.MainWindowHandle;
            ShowDragBox(guest,new POINT{X=testBox.Left,Y=testBox.Top},new POINT{X=testBox.Right,Y=testBox.Bottom});return IntPtr.Zero;
        }
        return DefWindowProc(hwnd,msg,wParam,lParam);
    }

    // Move marker: a small green plus flashed at the right-click point for MarkerMs, in a second click-through overlay
    // (same window class and styles as the drag box; a timer on the hook thread hides it).
    const int MarkerSize=22,MarkerMs=400;const uint WM_APP_TESTMARKER=0x8002;
    IntPtr marker;POINT testMarker;

    void CreateMarker() {
        if(box==IntPtr.Zero)throw new Exception("needs the overlay window class");
        marker=CreateWindowEx(0x80000|0x20|0x8000000|0x80|0x8,"SC2CoopDragBox","",0x80000000,0,0,MarkerSize,MarkerSize,IntPtr.Zero,IntPtr.Zero,GetModuleHandle(null),IntPtr.Zero);
        if(marker==IntPtr.Zero)throw new Exception("CreateWindowEx "+Marshal.GetLastWin32Error());
        overlayOwners[marker]=this;
        SetLayeredWindowAttributes(marker,BoxKey,255,1);
    }

    // hwnd/p: guest window and client point; target: red (a targeted command) instead of green (move). Runs on the hook thread.
    void ShowMarker(IntPtr hwnd,POINT p,bool target) {
        if(marker==IntPtr.Zero||ExclusiveFullscreen.Check())return;
        markerBrush=target?targetBrush:boxBrush;
        ClientToScreen(hwnd,ref p);
        SetWindowPos(marker,new IntPtr(-1),p.X-MarkerSize/2,p.Y-MarkerSize/2,MarkerSize,MarkerSize,0x10|0x40);
        Redraw(marker);
        SetTimer(marker,new IntPtr(1),MarkerMs,IntPtr.Zero);
    }

    void PaintMarker(IntPtr dc,RECT r) {
        FillRect(dc,ref r,boxKeyBrush);
        int c=r.Right/2,t=3;
        // A plus with a gap in the middle: four 3 px bars.
        foreach(var bar in new[]{new RECT{Left=c-1,Top=0,Right=c-1+t,Bottom=c-3},new RECT{Left=c-1,Top=c+3,Right=c-1+t,Bottom=r.Bottom},
            new RECT{Left=0,Top=c-1,Right=c-3,Bottom=c-1+t},new RECT{Left=c+3,Top=c-1,Right=r.Right,Bottom=c-1+t}}){var b=bar;FillRect(dc,ref b,markerBrush);}
    }

    // Called from the hook on mouse moves while the left button is held after a world press.
    void UpdateDragBox(POINT screen) {
        if(box==IntPtr.Zero)return;
        POINT p=screen;ScreenToClient(downWindow,ref p);
        if(!boxShown&&Math.Abs(p.X-downAt.X)<=DragPixels&&Math.Abs(p.Y-downAt.Y)<=DragPixels)return;
        // While targeting, a drag isn't a selection (and the native client draws none either).
        if(TargetingKnown()){HideDragBox();return;}
        ShowDragBox(downWindow,downAt,p);
    }

    // a and b are client points of hwnd; the box is clamped to the world area, like the selection rectangle sent on release.
    void ShowDragBox(IntPtr hwnd,POINT a,POINT b) {
        if(ExclusiveFullscreen.Check())return;
        RECT r;GetClientRect(hwnd,out r);int w=r.Right,h=r.Bottom;if(w<=0||h<=0)return;
        int bottom=(int)(h*0.765);
        Func<int,int,int,int> clamp=(v,lo,hi)=>Math.Max(lo,Math.Min(hi,v));
        var tl=new POINT{X=clamp(Math.Min(a.X,b.X),0,w-1),Y=clamp(Math.Min(a.Y,b.Y),0,bottom)};
        int bw=clamp(Math.Max(a.X,b.X),0,w-1)-tl.X+1,bh=clamp(Math.Max(a.Y,b.Y),0,bottom)-tl.Y+1;
        ClientToScreen(hwnd,ref tl);
        // HWND_TOPMOST, SWP_NOACTIVATE|SWP_SHOWWINDOW
        SetWindowPos(box,new IntPtr(-1),tl.X,tl.Y,Math.Max(bw,4),Math.Max(bh,4),0x10|0x40);
        Redraw(box);boxShown=true;
    }

    void HideDragBox() {if(box!=IntPtr.Zero&&boxShown){ShowWindow(box,0);boxShown=false;}}

    // Test hook (SC2_COOP_TEST_GUEST_UI): show the drag box for a guest-client rectangle (hook thread draws it), no mouse.
    public void TestDragBox(int ax,int ay,int bx,int by,bool show) {
        testBox=new RECT{Left=ax,Top=ay,Right=bx,Bottom=by};
        if(box!=IntPtr.Zero)PostMessage(box,WM_APP_TESTBOX,new IntPtr(show?1:0),IntPtr.Zero);
    }
    // Test hook (SC2_COOP_TEST_GUEST_UI): the ping an Alt+click at this feature-layer point produces.
    // Alt isn't physically held here, so it is posted around the click. Needs no focus.
    public void TestPing(int fx,int fy) {
        IntPtr hwnd=client.Process.MainWindowHandle;RECT r;GetClientRect(hwnd,out r);
        Ping(hwnd,fx,fy,r.Right,r.Bottom,new POINT{X=r.Right/2,Y=r.Bottom/3},true,"(test) feature "+fx+","+fy,-1);
    }
    // Test hook (SC2_COOP_TEST_PINGAT): an Alt+click at guest client point x,y (option -1), or the ping panel opened there with
    // button `option` highlighted for 2 s and then clicked. Same mapping and posting as the real input.
    public void TestPingAt(int x,int y,int option) {
        IntPtr hwnd=client.Process.MainWindowHandle;RECT r;GetClientRect(hwnd,out r);int fx,fy;
        if(option>=0&&pingPanel!=IntPtr.Zero){testPanelAt=new POINT{X=x,Y=y};PostMessage(pingPanel,WM_APP_TESTPANEL,new IntPtr(option+1),IntPtr.Zero);Thread.Sleep(2000);PostMessage(pingPanel,WM_APP_TESTPANEL,IntPtr.Zero,IntPtr.Zero);}
        if(MapZoomed(x,y,r.Right,r.Bottom,out fx,out fy))Ping(hwnd,fx,fy,r.Right,r.Bottom,new POINT{X=x,Y=y},true,"(test) "+(option>=0?"panel "+PanelNames[option]+" ":"")+"at client "+x+","+y,option);
    }

    // Test hook (SC2_COOP_TEST_BRIDGE): the items a real drag and right-click produce after the hook, without moving the mouse.
    // Test hook (SC2_COOP_TEST_SELECT): a plain left click at a feature-layer point, as the hook would queue it.
    public void TestSelect(int x,int y) {
        queue.Add(new Item{Kind=Kind.LeftClick,X=x,Y=y,SelectType=1,Tick=Environment.TickCount,SeqAtInput=-2,InputTick=Environment.TickCount-TargetWaitMs,
            Body=Spatial(Wire.Msg(3,Wire.Msg(1,Point(x,y)),Wire.Num(2,1))),What="test select "+x+","+y});
    }
    public void TestDragAndSmart(int ax,int ay,int bx,int by,int fx,int fy) {
        Enqueue(Wire.Msg(4,Wire.Msg(1,Wire.Msg(1,Point(ax,ay)),Wire.Msg(2,Point(bx,by)))),"test rect "+ax+","+ay+"-"+bx+","+by);
        Interlocked.Increment(ref pendingRight);
        queue.Add(new Item{Kind=Kind.RightClick,X=fx,Y=fy,Tick=Environment.TickCount,Body=Spatial(Wire.Msg(1,Wire.Num(1,SmartAbility),Wire.Msg(2,Point(fx,fy)))),What="test smart "+fx+","+fy});
    }

    // Test hook: flash the move marker at a guest-client point (posted to the hook thread, which owns the window).
    public void TestMarker(int x,int y) {testMarker=new POINT{X=x,Y=y};if(box!=IntPtr.Zero)PostMessage(box,WM_APP_TESTMARKER,IntPtr.Zero,IntPtr.Zero);}
    // Test hooks (SC2_COOP_TEST_INPUTLAT in the test copy): a key as the keyboard hook sees it in the guest window, and a
    // world left click (press and release in place) at a feature-layer point, without the keyboard or mouse.
    public bool TestKey(int vk,bool down) {return GuestKey(vk,down,false);}
    // A key posted to the guest window (its native UI, e.g. A starts targeting), and a box select at feature-layer points.
    public void TestNativeKey(int vk) {
        IntPtr hwnd=client.Process.MainWindowHandle;
        PostMessage(hwnd,WM_KEYDOWN,new IntPtr(vk),new IntPtr(1));Thread.Sleep(60);PostMessage(hwnd,WM_KEYUP,new IntPtr(vk),new IntPtr(unchecked((int)0xC0000001)));
    }
    // Test hook (SC2_COOP_TEST_BIGGROUP): a targeted click at this feature-layer point once the map's Test/ready flag is set.
    public volatile int[] TestProbeAt;
    public void TestRect(int ax,int ay,int bx,int by) {Enqueue(Wire.Msg(4,Wire.Msg(1,Wire.Msg(1,Point(ax,ay)),Wire.Msg(2,Point(bx,by)))),"test rect "+ax+","+ay+"-"+bx+","+by);}
    public void TestWheel(int notches) {Wheel(notches);}
    public string TestZoom() {lock(camGate)return zoomDistance.ToString("0.0")+"/"+zoomPitch.ToString("0.0");}
    // Feature-layer point the bridge sends for a guest-client pixel at the current zoom (and at the default zoom).
    public string TestMap(int x,int y) {
        var r=client.Process.MainWindowHandle;int fx,fy,gx,gy;bool a=MapZoomed(x,y,cal.Window[0],cal.Window[1],out fx,out fy),b=cal.Map(x,y,cal.Window[0],cal.Window[1],out gx,out gy);
        return fx+","+fy+(a?"":"!")+" default "+gx+","+gy+(b?"":"!");
    }
    public void TestWorldLeftClick(int x,int y) {
        if(TargetingKnown()){TargetClick(x,y,false,false,"(test)");return;}
        queue.Add(new Item{Kind=Kind.LeftClick,X=x,Y=y,SelectType=1,Tick=Environment.TickCount,SeqAtInput=seqAtUiInput,InputTick=lastUiInputTick,
            Body=Spatial(Wire.Msg(3,Wire.Msg(1,Point(x,y)),Wire.Num(2,1))),What="test world click "+x+","+y});
    }

    // A world click while targeting is known (guessed or reported): sent as a probe right away. Body is the plain select,
    // used if the probe is refused (e.g. only buildings selected: no targeting after all).
    void TargetClick(int x,int y,bool shift,bool ctrl,string from) {
        int pair=predictPair;
        if(pair!=0){lastPredictedPair=pair;lastPredictedTick=Environment.TickCount;}
        if(shift){if(pair!=0)shiftChain=true;} else ClearPrediction();
        ulong type=(ulong)(ctrl?(shift?4:3):(shift?2:1));
        queue.Add(new Item{Kind=Kind.TargetClick,X=x,Y=y,Shift=shift,Pair=pair,SelectType=type,Tick=Environment.TickCount,
            Body=Spatial(Wire.Msg(3,Wire.Msg(1,Point(x,y)),Wire.Num(2,type))),What=(pair!=0?"target (guessed from "+PairName(pair)+") ":"target ")+x+","+y+" "+from});
    }
    static string KeyName(int k) {return k>=0x1000?"card "+(k-0x1000):(k>=0x30&&k<=0x5A)?((char)k).ToString():"0x"+k.ToString("X2");}
    static string PairName(int pair) {int prev=pair>>16;return (prev!=0?KeyName(prev)+",":"")+KeyName(pair&0xFFFF);}
    static byte[] Probe(int x,int y,bool shift) {return Spatial(Wire.Msg(1,Wire.Num(1,(ulong)(shift?PatrolAbility:MoveAbility)),Wire.Msg(2,Point(x,y)),Wire.Num(4,1)));}

    // Test hook: a key that may start targeting, then a left click with a right-click right behind it.
    public void TestClickAfterKey(int x,int y,int fx,int fy) {
        NoteUiInput();
        queue.Add(new Item{Kind=Kind.LeftClick,X=x,Y=y,SelectType=1,Tick=Environment.TickCount,SeqAtInput=seqAtUiInput,InputTick=lastUiInputTick,
            Body=Spatial(Wire.Msg(3,Wire.Msg(1,Point(x,y)),Wire.Num(2,1))),What="test select after key "+x+","+y});
        Interlocked.Increment(ref pendingRight);
        queue.Add(new Item{Kind=Kind.RightClick,X=fx,Y=fy,Tick=Environment.TickCount,Body=Spatial(Wire.Msg(1,Wire.Num(1,SmartAbility),Wire.Msg(2,Point(fx,fy)))),What="test smart after key "+fx+","+fy});
    }

    int Clamp(int v) {return Math.Max(0,Math.Min(cal.Resolution-1,v));}
    static byte[] Point(int x,int y) {return Wire.Join(Wire.Num(1,(ulong)x),Wire.Num(2,(ulong)y));}
    // spatial: ActionSpatial body -> RequestAction{actions:[Action{action_feature_layer:...}]}
    static byte[] Spatial(byte[] spatial) {return Wire.Msg(1,Wire.Msg(2,spatial));}
    void Enqueue(byte[] spatial,string what) {queue.Add(new Item{Kind=Kind.Spatial,Body=Spatial(spatial),What=what,Tick=Environment.TickCount});}

    // A recent key or command-card press may be starting targeting; give the map's report time to arrive. The report goes
    // through the game's network turn, so the wait can last hundreds of ms. A right-click queued behind this click ends
    // the wait (it would otherwise wait too); the click is then decided with what is known so far.
    bool TargetingFor(Item item,out bool cutShort) {
        cutShort=false;
        if(Environment.TickCount-item.InputTick<TargetWaitMs) {
            int deadline=item.InputTick+TargetWaitMs;
            while(targetSeq==item.SeqAtInput&&Environment.TickCount-deadline<0) {
                if(Volatile.Read(ref pendingRight)>0){cutShort=true;break;}
                Thread.Sleep(5);
            }
        }
        return targetOn&&!ignoreTargetOn;
    }

    void SendLoop() {
        foreach(var item in queue.GetConsumingEnumerable()) {
            try {
                byte[] body=item.Body;string what=item.What;bool esc=false,cutShort=false;ulong result;
                if(item.Kind==Kind.RightClick)Interlocked.Decrement(ref pendingRight);
                if(item.Kind==Kind.TargetClick) {
                    // Queued so units that cannot use the ability keep their orders; the map restores them.
                    result=Send(Probe(item.X,item.Y,item.Shift));
                    if(result==1){what+=" via probe"+(item.Shift?" (shift)":"");esc=!item.Shift;}
                    else {
                        // The selection can't take a Move (e.g. only buildings): not a targeted command after all.
                        if(item.Pair!=0)learned[item.Pair]=false;
                        what+=": probe refused (result "+result+"), sent as select "+item.SelectType;result=Send(body);
                    }
                } else {
                    if(item.Kind==Kind.LeftClick&&TargetingFor(item,out cutShort)) {
                        body=Probe(item.X,item.Y,item.Shift);
                        what="target "+targetAbil+" via probe "+item.X+","+item.Y+(item.Shift?" (shift)":"");esc=!item.Shift;
                    } else if(item.Kind==Kind.RightClick&&targetOn&&!ignoreTargetOn) {
                        PressEscape();ignoreTargetOn=true;log("Click bridge: right-click cancels targeting "+targetAbil);continue;
                    }
                    result=Send(body);
                    // A point select that hits nothing (result 3) is retried a few feature pixels around the click: the
                    // calibration is a flat-ground model, so small units on slopes or at other resolutions are missed.
                    if(cutShort)what+=" (targeting wait cut short by a right-click)";
                    // Not after a cut-short wait: the click may have been meant for targeting, so don't grab a nearby unit.
                    if(result==3&&item.Kind==Kind.LeftClick&&!esc&&!cutShort&&item.SelectType!=0)
                        foreach(var o in SelectOffsets) {
                            int x=Clamp(item.X+o[0]),y=Clamp(item.Y+o[1]);
                            if(Send(Spatial(Wire.Msg(3,Wire.Msg(1,Point(x,y)),Wire.Num(2,item.SelectType))))==1){result=1;what+=" (hit at "+x+","+y+")";break;}
                        }
                }
                if(result==1)Sent++;else Failed++;
                // A late "on" report for the targeting just ended is stale.
                if(esc){PressEscape();ignoreTargetOn=true;}
                log("Click bridge: "+what+" -> "+(result==1?"ok":"result "+result)+" in "+(Environment.TickCount-item.Tick)+" ms");
            } catch(Exception e) {Failed++;log("Click bridge send: "+e.Message);}
        }
    }

    static readonly int[][] SelectOffsets={new[]{2,0},new[]{-2,0},new[]{0,2},new[]{0,-2},new[]{2,2},new[]{-2,2},new[]{2,-2},new[]{-2,-2},new[]{4,0},new[]{-4,0},new[]{0,4},new[]{0,-4}};
    ulong Send(byte[] body) {
        var response=client.Request(11,body,10).M(11);
        object raw=response.Fields.ContainsKey(1)?response.Fields[1][0]:null;
        return raw is ulong?(ulong)raw:raw is byte[]&&((byte[])raw).Length>0?((byte[])raw)[0]:0UL;
    }

    void PressEscape() {
        if(!GuestForeground())return;
        var inputs=new INPUT[2];inputs[0].type=inputs[1].type=1;inputs[0].ki.vk=inputs[1].ki.vk=0x1B;inputs[1].ki.flags=2;
        SendInput(2,inputs,Marshal.SizeOf(typeof(INPUT)));
    }

    // The map saves the guest's "CoopBridge" bank whenever target mode changes, after probes, and when the guest camera moves
    // (at least once a game second).
    void BankLoop() {
        string path=null;DateTime stamp=DateTime.MinValue;int nextSearch=0;
        while(!stopping.WaitOne(15)) {
            try {
                if(path==null) {
                    if(Environment.TickCount-nextSearch<0)continue;nextSearch=Environment.TickCount+1000;
                    path=Directory.Exists(bankRoot)?Directory.EnumerateFiles(bankRoot,"CoopBridge.SC2Bank",SearchOption.AllDirectories).FirstOrDefault():null;
                    if(path!=null)log("Click bridge: reading targeting state from "+path);
                    continue;
                }
                var t=File.GetLastWriteTimeUtc(path);if(t==stamp)continue;stamp=t;
                var doc=XDocument.Parse(File.ReadAllText(path));
                Func<string,string,XElement> value=(sec,k)=>doc.Root.Elements("Section").Where(s=>(string)s.Attribute("name")==sec)
                    .SelectMany(s=>s.Elements("Key")).Where(e=>(string)e.Attribute("name")==k).Select(e=>e.Element("Value")).FirstOrDefault();
                ReadCamera(value);ReadPingReport(value);
                var seq=value("Target","seq");var on=value("Target","on");var abil=value("Target","abil");if(seq==null)continue;
                targetAbil=abil!=null?(string)abil.Attribute("string")??"":"";
                bool nowOn=on!=null&&(string)on.Attribute("flag")=="1";int newSeq=int.Parse((string)seq.Attribute("int"));
                if(!nowOn)ignoreTargetOn=false;
                if(nowOn&&newSeq!=targetSeq&&!ignoreTargetOn) {
                    // Learn the input that started this targeting, so it is guessed next time.
                    int pair=lastInputPair;
                    if(pair!=0&&Environment.TickCount-lastUiInputTick<1500&&!Predicts(pair,pair&0xFFFF)){learned[pair]=true;log("Click bridge: learned targeting input "+PairName(pair)+" ("+targetAbil+")");}
                }
                targetOn=nowOn;targetSeq=newSeq;
                var testReady=value("Test","ready");var testAt=TestProbeAt;
                if(testAt!=null&&testReady!=null&&(string)testReady.Attribute("flag")=="1") {
                    TestProbeAt=null;
                    queue.Add(new Item{Kind=Kind.TargetClick,X=testAt[0],Y=testAt[1],Tick=Environment.TickCount,Body=Probe(testAt[0],testAt[1],false),What="test big-group probe "+testAt[0]+","+testAt[1]});
                }
                var stray=value("Probe","stray");
                if(stray!=null) {
                    int n=int.Parse((string)stray.Attribute("int"));
                    if(strayCount>=0&&n>strayCount) {
                        int pair=lastPredictedPair;bool recent=pair!=0&&Environment.TickCount-lastPredictedTick<3000;
                        if(recent)learned[pair]=false;
                        log("Click bridge: no targeting mode was on"+(recent?" after "+PairName(pair):"")+"; the map took the probe back (stray "+n+")");
                    }
                    strayCount=n;
                }
            } catch(IOException) {stamp=DateTime.MinValue;} catch(Exception e) {log("Click bridge bank: "+e.Message);stamp=DateTime.MinValue;}
        }
    }

    // Camera section of the bank. A report is taken as the camera position only when nothing has moved the camera for a while:
    // it is the synced camera target, a network turn (plus the bank write) behind the guest's screen.
    void ReadCamera(Func<string,string,XElement> value) {
        Func<string,double> num=k=>{var v=value("Camera",k);return v==null?double.NaN:double.Parse((string)v.Attributes().First().Value,System.Globalization.CultureInfo.InvariantCulture);};
        double x=num("x"),y=num("y");if(double.IsNaN(x)||double.IsNaN(y))return;string zoom=null;
        var cine=value("Camera","cine");bool first=false,wake=false;double dist=num("dist"),pitch=num("pitch");
        lock(camGate) {
            minX=num("min_x");minY=num("min_y");maxX=num("max_x");maxY=num("max_y");
            if(double.IsNaN(minX)||double.IsNaN(minY)||double.IsNaN(maxX)||double.IsNaN(maxY)){minX=minY=-1e9;maxX=maxY=1e9;}
            camCinematic=cine!=null&&(string)cine.Attribute("flag")=="1";
            int now=Environment.TickCount;
            bool busy=now-lastCamCommand<CamSettleMs||now-camDirtyTick<CamSettleMs;
            for(int a=0;a<4;a++)if(arrowHeld[a]&&!arrowPending[a])busy=true;
            if(!busy) {
                first=!camSeen;camSeen=true;camX=x;camY=y;camKnown=true;
                // Arrows held for this report start the bridge's scroll now.
                for(int a=0;a<4;a++)if(arrowPending[a]){arrowPending[a]=false;arrowBridged[a]=true;wake=true;}
            }
            // The reported zoom is the synced one, a network turn behind the guest's screen: a wheel step counts at once and a report
            // only once it changes (a camera the mission sets, or the synced wheel zoom), and not within 1.5 s of a wheel step.
            if(!double.IsNaN(dist)&&!double.IsNaN(pitch)) {
                if(double.IsNaN(reportedDistance)||Math.Abs(dist-reportedDistance)>0.05||Math.Abs(pitch-reportedPitch)>0.05){reportedDistance=dist;reportedPitch=pitch;zoomReportPending=true;}
                if(zoomReportPending&&now-lastWheelTick>1500) {
                    zoomReportPending=false;
                    if(Math.Abs(dist-zoomDistance)>0.05||Math.Abs(pitch-zoomPitch)>0.05){zoomDistance=dist;zoomPitch=pitch;zoom="guest camera zoom: distance "+dist.ToString("0.0")+", pitch "+pitch.ToString("0.0");}
                }
            }
        }
        if(wake)cameraWake.Set();
        if(first)log("Click bridge: guest camera at "+x.ToString("0.0")+","+y.ToString("0.0")+"; arrow keys scroll it from here");
        if(zoom!=null)log("Click bridge: "+zoom);
    }

    // While arrows the bridge took are held: move the guest camera on its own API (shown at once), ~60 times a second.
    void CameraLoop() {
        var clock=System.Diagnostics.Stopwatch.StartNew();long last=-1;int failures=0;
        while(!stopping.WaitOne(0)) {
            int dx,dy;double x=0,y=0;bool move=false;
            lock(camGate) {
                dx=(arrowBridged[2]?1:0)-(arrowBridged[0]?1:0);dy=(arrowBridged[1]?1:0)-(arrowBridged[3]?1:0);
                bool any=arrowBridged[0]||arrowBridged[1]||arrowBridged[2]||arrowBridged[3];
                if(any) {
                    long now=clock.ElapsedMilliseconds;double dt=last<0?0.016:Math.Min(0.1,(now-last)/1000.0);last=now;
                    if((dx!=0||dy!=0)&&!camCinematic&&camEnabled) {
                        camX=Math.Max(minX,Math.Min(maxX,camX+dx*ScrollSpeed*dt));camY=Math.Max(minY,Math.Min(maxY,camY+dy*ScrollSpeed*dt));
                        x=camX;y=camY;move=true;lastCamCommand=Environment.TickCount;
                    }
                } else last=-1;
            }
            if(last<0){cameraWake.WaitOne(100);continue;}
            if(move) {
                try {
                    // RequestAction{actions:[Action{action_raw(1): ActionRaw{camera_move(2): {center_world_space(1): Point{x,y}}}}]}
                    ulong r=Send(Wire.Msg(1,Wire.Msg(1,Wire.Msg(2,Wire.Msg(1,Wire.Float(1,(float)x),Wire.Float(2,(float)y))))));
                    if(r==1){cameraMoves++;failures=0;}
                    else if(++failures==3){camEnabled=false;log("Click bridge: camera moves refused (result "+r+"); arrow keys go to the map's scroll");}
                } catch(Exception e){if(++failures==3){camEnabled=false;log("Click bridge camera: "+e.Message+"; arrow keys go to the map's scroll");}}
            }
            stopping.WaitOne(15);
        }
    }

    public void Dispose() {
        stopping.Set();
        if(hookThreadId!=0)PostThreadMessage(hookThreadId,0x12,IntPtr.Zero,IntPtr.Zero);
        hookThread.Join(3000);cameraThread.Join(2000);
        queue.CompleteAdding();sendThread.Join(2000);
        log("Click bridge stopped: "+Sent+" actions accepted, "+Failed+" rejected, "+cameraMoves+" camera moves");
    }
}
