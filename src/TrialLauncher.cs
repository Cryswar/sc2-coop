// SC2 shared-control research launcher. Uses Windows/.NET only; no game assets included.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class Wire {
    public Dictionary<int,List<object>> Fields = new Dictionary<int,List<object>>();
    public static byte[] Join(params byte[][] parts) { return parts.SelectMany(x=>x).ToArray(); }
    public static byte[] Var(ulong value) { var b=new List<byte>(); do { b.Add((byte)((value&127)|(value>127?128UL:0)));value>>=7; } while(value!=0); return b.ToArray(); }
    public static byte[] Num(int field,ulong value) {return Join(Var((ulong)(field<<3)),Var(value));}
    public static byte[] Float(int field,float value) {return Join(Var((ulong)((field<<3)|5)),BitConverter.GetBytes(value));}
    public static byte[] Msg(int field,params byte[][] parts) {var b=Join(parts);return Join(Var((ulong)((field<<3)|2)),Var((ulong)b.Length),b);}
    public static byte[] Str(int field,string value) {return Msg(field,Encoding.UTF8.GetBytes(value));}
    static ulong ReadVar(byte[] data,ref int at) {
        ulong value=0; for(int shift=0;shift<70;shift+=7) {if(at>=data.Length)throw new InvalidDataException("Truncated protobuf");byte b=data[at++];value|=(ulong)(b&127)<<shift;if((b&128)==0)return value;}throw new InvalidDataException("Invalid varint");
    }
    public static Wire Parse(byte[] data) {
        var r=new Wire(); int at=0;
        while(at<data.Length) {
            ulong key=ReadVar(data,ref at);int f=(int)(key>>3);object v;
            switch(key&7) {
                case 0:v=ReadVar(data,ref at);break;
                case 2:int n=checked((int)ReadVar(data,ref at));if(n>data.Length-at)throw new InvalidDataException("Invalid length");v=data.Skip(at).Take(n).ToArray();at+=n;break;
                case 1:if(data.Length-at<8)throw new InvalidDataException();v=data.Skip(at).Take(8).ToArray();at+=8;break;
                case 5:if(data.Length-at<4)throw new InvalidDataException();v=data.Skip(at).Take(4).ToArray();at+=4;break;
                default:throw new InvalidDataException("Unsupported protobuf wire type");
            }
            if(!r.Fields.ContainsKey(f))r.Fields[f]=new List<object>();r.Fields[f].Add(v);
        }return r;
    }
    public ulong N(int f) {return Fields.ContainsKey(f)?(ulong)Fields[f][0]:0;}
    public byte[] B(int f) {return Fields.ContainsKey(f)?(byte[])Fields[f][0]:new byte[0];}
    public string S(int f) {return Encoding.UTF8.GetString(B(f));}
    public Wire M(int f) {return Parse(B(f));}
}

class Settings {
    public Settings() {MapName="OutlawsCoopProbe.SC2Map";Raw=true;Bridge=true;BridgeCalibration="bridge-calibration.json";TunnelPort=6600;}
    public string Install {get;set;}
    public string Workspace {get;set;}
    public string MapSha256 {get;set;}
    public string MapName {get;set;}
    // The game creator only gets native world mouse input when the raw interface is enabled (input_probe.py, 2026-10-16).
    public bool Raw {get;set;}
    // Guest world clicks are replayed through the guest API (ClickBridge.cs).
    public bool Bridge {get;set;}
    // Launcher-to-launcher tunnel port (TCP). Only the host needs it reachable (port forward / UPnP).
    public int TunnelPort {get;set;}
    public string BridgeCalibration {get;set;}
    public bool Observe {get;set;}
    public bool FeatureLayer {get;set;}
    public bool SeparateProgramData {get;set;}
    // Product mode: run the player's own SC2 install directly (no copy, no executable change); the map lives in MapPath.
    public bool Product {get;set;}
    public string MapPath {get;set;}
    // Which map recipe built the prepared map (TrialPreparation.Recipe); a launcher update that changes the map asks for Prepare again.
    public string Recipe {get;set;}
    [ScriptIgnore] public string ConfigDirectory {get;set;}
}

class GameClient : IDisposable {
    // Local mode (both players' windows on this PC): small windows whatever the player's display settings.
    public static bool ForceWindowed;
    public Process Process;
    public ClientWebSocket Socket;
    public string Root;
    public int ApiPort;
    public bool Joined;
    ulong requestId;
    readonly object gate=new object();
    static void CleanTemp(string dir,Action<string> log) {
        if(!Directory.Exists(dir))return;int n=0;
        foreach(var f in Directory.GetFiles(dir,"*.SC2Map"))try{File.Delete(f);n++;}catch{}
        if(n>0)log("Removed "+n+" old mission map cop"+(n==1?"y":"ies")+" from the game's temp folder");
    }
    public GameClient(Settings settings,string root,bool visible,Action<string> log) {
        Root=root;
        foreach(string sub in new[]{"Documents","AppData/Local/Temp","AppData/Roaming","ProgramData","Public/Documents","Other","temp"})Directory.CreateDirectory(Path.Combine(root,sub));
        // The game's own temp folder (-tempDir below) keeps a copy of every mission map it was given (about 10 MB each);
        // a game that was closed or crashed leaves its copies behind. Nothing runs in this profile yet, so clear them.
        CleanTemp(Path.Combine(root,"temp","StarCraft II","CoopTrial"),log);
        // Tiny offline settings file lives only inside this disposable profile.
        Directory.CreateDirectory(Path.Combine(root,"Documents/StarCraft II"));
        if(!settings.Product)File.WriteAllText(Path.Combine(root,"Documents/StarCraft II/Variables.txt"),"displayMode=0\nwidth=1280\nheight=720\nGraphicsOptionOverallQuality=0\nselectuncontrolable=true\n");
        var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();ApiPort=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var info=new ProcessStartInfo(Path.Combine(settings.Install,"Versions/Base98310/SC2_x64.exe"));
        info.WorkingDirectory=Path.Combine(settings.Install,"Support64");info.UseShellExecute=false;
        info.CreateNoWindow=true;info.WindowStyle=visible?ProcessWindowStyle.Normal:ProcessWindowStyle.Hidden;
        info.Arguments="-listen 127.0.0.1 -port "+ApiPort+" -dataDir "+Program.Q(settings.Install)+" -tempDir "+Program.Q(Path.Combine(root,"temp"));
        // The player's own install: their display mode and window size (Documents\StarCraft II\Variables.txt). Forcing a
        // 1280x720 window here lost the player's Windowed (Fullscreen) setting (2026-10-23 report). Test profiles and local
        // mode (two windows on one screen) keep the small window.
        if(!settings.Product||ForceWindowed)info.Arguments+=" -displayMode 0 -windowwidth 1280 -windowheight 720";
        if(!settings.Product) {
            info.EnvironmentVariables["SC2_TEST_ROOT"]=root;
            info.EnvironmentVariables["SC2_TEST_SHARED_ROOT"]=settings.SeparateProgramData?root:Path.GetDirectoryName(root)+"/shared";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(root)+"/shared/ProgramData");
        info.EnvironmentVariables["TEMP"]=Path.Combine(root,"temp");info.EnvironmentVariables["TMP"]=Path.Combine(root,"temp");
        Process=Process.Start(info);log("Started "+Path.GetFileName(root)+", PID "+Process.Id+", local API "+ApiPort);
        // Test hook: SC2_COOP_TEST_STARTFAIL=1 kills the first start at once, to exercise the retry below.
        if(Environment.GetEnvironmentVariable("SC2_COOP_TEST_STARTFAIL")=="1"){Environment.SetEnvironmentVariable("SC2_COOP_TEST_STARTFAIL",null);try{Process.Kill();Process.WaitForExit();}catch{}}
        var deadline=DateTime.UtcNow.AddSeconds(60);int retries=0;
        while(DateTime.UtcNow<deadline) {
            // SC2 sometimes quits right after starting (seen once: exit -2001, 6 s after the previous game closed; the next
            // start worked). Try again twice before giving up.
            if(Process.HasExited&&retries<2) {
                retries++;log("StarCraft II closed right after starting (exit code "+Process.ExitCode+"); starting it again ("+retries+"/2)");
                Thread.Sleep(3000);Process=Process.Start(info);log("Started "+Path.GetFileName(root)+", PID "+Process.Id+", local API "+ApiPort);
                deadline=DateTime.UtcNow.AddSeconds(60);continue;
            }
            if(Process.HasExited)throw new Exception("SC2 exited before API startup: "+Process.ExitCode);
            Socket=new ClientWebSocket();Socket.Options.KeepAliveInterval=TimeSpan.Zero;
            try {using(var cancel=new CancellationTokenSource(1500))Socket.ConnectAsync(new Uri("ws://127.0.0.1:"+ApiPort+"/sc2api"),cancel.Token).GetAwaiter().GetResult();break;}
            catch {Socket.Dispose();Socket=null;Thread.Sleep(300);}
        }
        if(Socket==null) {Process.Kill();throw new Exception("SC2 API startup timed out");}
        var ping=Request(19,new byte[0],10).M(19);
        if(ping.N(4)!=98310||ping.S(2)!="9C1F3D3D30CA252E33FC67CA05CBE1EA") {Dispose();throw new Exception("SC2 build/data version does not match this trial");}
        log("SC2 "+ping.S(1)+" verified");
    }
    public Wire Request(int field,byte[] body,int timeout) {
        lock(gate) {
            using(var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(timeout))) {
                ulong id=++requestId;
                var message=Wire.Join(Wire.Msg(field,body),Wire.Num(97,id));Socket.SendAsync(new ArraySegment<byte>(message),WebSocketMessageType.Binary,true,cancel.Token).GetAwaiter().GetResult();
                using(var stream=new MemoryStream()) {
                    var buffer=new byte[65536];WebSocketReceiveResult r;
                    do {r=Socket.ReceiveAsync(new ArraySegment<byte>(buffer),cancel.Token).GetAwaiter().GetResult();if(r.MessageType==WebSocketMessageType.Close)throw new IOException("SC2 closed its API connection");stream.Write(buffer,0,r.Count);if(stream.Length>64*1024*1024)throw new IOException("Oversized API response");}while(!r.EndOfMessage);
                    var response=Wire.Parse(stream.ToArray());
                    if(response.N(97)!=id)throw new IOException("SC2 returned an unexpected response ID");
                    if(response.Fields.ContainsKey(98))throw new Exception("SC2 API: "+response.S(98));
                    return response;
                }
            }
        }
    }
    // Types a chat message as this player (RequestAction.action_chat, broadcast); map chat triggers see it.
    public ulong Chat(string text) {return Request(11,Wire.Msg(1,Wire.Msg(6,Wire.Num(1,1),Wire.Str(2,text))),10).M(11).N(1);}
    public void Leave(Action<string> log) {if(Joined&&!Process.HasExited)try {Request(5,new byte[0],10);Joined=false;}catch(Exception e){log("Leave: "+e.Message);}}
    public void Dispose(){Dispose(10000);}
    // waitMs: how long SC2 gets to quit by itself before it is killed (a hub that launched a mission never quits by itself).
    public void Dispose(int waitMs) {
        try {if(!Process.HasExited&&Socket!=null)Request(8,new byte[0],5);}catch { }
        if(Process!=null) {if(!Process.WaitForExit(waitMs)){Process.Kill();Process.WaitForExit();}Program.Log("SC2 PID "+Process.Id+" exit "+Process.ExitCode);}
        if(Socket!=null)Socket.Dispose();
    }
}

class Session {
    public readonly List<GameClient> Clients=new List<GameClient>();
    public readonly ManualResetEvent Stop=new ManualResetEvent(false);
    // Set when a StarCraft II process of this session exited on its own (crash, or closed from the task bar).
    public bool Crashed;
    public string Run;
    ClickBridge bridge;
    Tunnel tunnel;
    // Campaign hub flow: reuse one tunnel for every mission and keep each player's profile folder between games.
    public Tunnel PresetTunnel;
    public string[] PresetRoots;
    // Campaign process reuse: games already running (from the hub) instead of new ones, and keep them open afterwards.
    public GameClient[] PresetClients;public bool KeepClients;
    // RequestJoinGame race (1 Terran, 2 Zerg, 3 Protoss, 4 Random): campaign missions join as player 1's race (Zeratul's missions are Protoss).
    public ulong Race=1;
    // The guest's race when it differs (separate armies in the map's own guest slot); 0 = Race.
    public ulong GuestRace=0;
    // Campaign: "joining" (games created, loading), "ready" (everyone in), "ended" (the game is over, closing).
    public Action<string> Progress;
    void Report(string stage){var p=Progress;if(p!=null)try{p(stage);}catch{}}
    Upnp upnp;

    public static string JoinInfo(Upnp u,int port) {return JoinInstructions(u,port);}
    static string JoinInstructions(Upnp u,int port) {
        string lan=u.LanIp??"this PC's address";
        if(u.Mapped&&u.ExternalIp!=null) {
            var ip=IPAddress.Parse(u.ExternalIp).GetAddressBytes();
            bool shared=ip[0]==10||(ip[0]==100&&ip[1]>=64&&ip[1]<128)||(ip[0]==172&&ip[1]>=16&&ip[1]<32)||(ip[0]==192&&ip[1]==168);
            if(shared)return "Your router's internet address ("+u.ExternalIp+") is shared by your provider (carrier-grade NAT), so your buddy cannot reach you directly. Use Tailscale (see README) and give your buddy your Tailscale address.";
            return "Tell your buddy to choose Join and enter:  "+u.ExternalIp+(port==6600?"":":"+port)+"   (same Wi-Fi/LAN: "+lan+")";
        }
        return "Your buddy needs to reach TCP port "+port+" on this PC. Either forward TCP "+port+" to "+lan+" in your router and give your buddy your public IP, or use Tailscale and give your buddy your Tailscale address (see README). Same Wi-Fi/LAN: "+lan;
    }
    // Player-1 marines (unit type 48): count, how many have orders, mean position. Raw Unit fields: unit_type 4, owner 5, pos 6, orders 22.
    static string Marines(GameClient c) {
        var raw=c.Request(10,new byte[0],20).M(10).M(3).M(5);int n=0,moving=0;double x=0,y=0;
        if(raw.Fields.ContainsKey(2))foreach(byte[] b in raw.Fields[2]) {
            var u=Wire.Parse(b);if(u.N(4)!=48||u.N(5)!=1)continue;
            var pos=u.M(6);n++;x+=BitConverter.ToSingle(pos.B(1),0);y+=BitConverter.ToSingle(pos.B(2),0);if(u.Fields.ContainsKey(22))moving++;
        }
        return "loop "+Wire.Parse(c.Request(10,new byte[0],20).M(10).B(3)).N(9)+", P1 marines "+n+", with orders "+moving+(n>0?", mean pos "+(x/n).ToString("0.0")+","+(y/n).ToString("0.0"):"");
    }
    // True when a game has left the match (status other than in_game 3) and should be closed.
    bool GameLeft(string mode,bool hosting,ref ulong frozenLoop,ref int frozenSince,ref bool warned) {
        for(int i=0;i<Clients.Count;i++) {
            ulong status;
            try{status=Clients[i].Request(19,new byte[0],10).N(99);}catch(Exception e){Program.Log("Status check: "+e.Message);continue;}
            if(status==3)continue;
            bool observable=i==0&&(hosting||mode=="local");
            if(status==5&&observable) {
                ulong loop=0;try{loop=Wire.Parse(Clients[0].Request(10,new byte[0],10).M(10).B(3)).N(9);}catch{}
                if(loop!=frozenLoop){frozenLoop=loop;frozenSince=Environment.TickCount;
                    if(!warned){warned=true;Program.Log("StarCraft II's API reports the game as over, but the game is still running; keeping it open. Your buddy's mouse control may stop (please send launcher.log).");}
                    continue;}
                if(Environment.TickCount-frozenSince<4000)continue;
            }
            Program.Log("Game "+(Clients.Count>1?"window "+(i+1)+" ":"")+"has left the match (status "+status+"); closing StarCraft II.");
            return true;
        }
        return false;
    }

    static List<Wire> RawUnits(GameClient c) {
        var raw=c.Request(10,new byte[0],20).M(10).M(3).M(5);var l=new List<Wire>();
        if(raw.Fields.ContainsKey(2))foreach(byte[] b in raw.Fields[2])l.Add(Wire.Parse(b));return l;
    }
    static float F(Wire w,int f){return BitConverter.ToSingle(w.B(f),0);}
    static bool InCircles(Wire u,double[][] circles){var p=u.M(6);double x=F(p,1),y=F(p,2);return circles.Any(c=>(x-c[0])*(x-c[0])+(y-c[1])*(y-c[1])<=c[2]*c[2]);}
    static readonly double[][] RebelBase={new[]{45.2631,26.874,16},new[]{48.211,42.6577,10.154},new[]{30.7148,38.3874,7.2941},new[]{36.7187,41.8776,4.223}};
    static string Rescue(GameClient c) {
        // Dominion (2) and its patrol ally (5) inside region 25, which the rescue trigger waits on. DebugGameState show_map
        // first: the observation lists only units the host can see.
        c.Request(20,Wire.Msg(1,Wire.Num(2,1)),20);Thread.Sleep(1000);
        var kill=RawUnits(c).Where(u=>(u.N(5)==2||u.N(5)==5)&&InCircles(u,RebelBase)).Select(u=>u.N(3)).ToList();
        var debug=new List<byte[]>();
        if(kill.Count>0)debug.Add(Wire.Msg(1,Wire.Msg(4,kill.Select(t=>Wire.Num(1,t)).SelectMany(x=>x).ToArray())));
        // SC2_COOP_TEST_RESCUE_OWNER=<player>: whose marines (default 1; the guest's slot to test separate armies).
        ulong owner=ulong.Parse(Environment.GetEnvironmentVariable("SC2_COOP_TEST_RESCUE_OWNER")??"1");
        // SC2_COOP_TEST_RESCUE_AT=x,y: where the marines appear (default the rescue area 48.4,23.6).
        var at=(Environment.GetEnvironmentVariable("SC2_COOP_TEST_RESCUE_AT")??"48.4,23.6").Split(',').Select(v=>float.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        debug.Add(Wire.Msg(1,Wire.Msg(3,Wire.Num(1,48),Wire.Num(2,owner),Wire.Msg(3,Wire.Float(1,at[0]),Wire.Float(2,at[1])),Wire.Num(4,4))));
        c.Request(20,Wire.Join(debug.ToArray()),20);
        return "rescue jump: killed "+kill.Count+" Dominion units in the rebel base, spawned 4 P"+owner+" marines at "+at[0]+","+at[1];
    }
    static string KillAll(GameClient c,ulong owner) {
        c.Request(20,Wire.Msg(1,Wire.Num(2,1)),20);Thread.Sleep(1000); // DebugGameState show_map, so hidden units are listed
        var kill=RawUnits(c).Where(u=>u.N(5)==owner).Select(u=>u.N(3)).ToList();
        if(kill.Count>0)c.Request(20,Wire.Msg(1,Wire.Msg(4,kill.Select(t=>Wire.Num(1,t)).SelectMany(x=>x).ToArray())),20);
        return "killed all "+kill.Count+" units of P"+owner;
    }
    static string Owners(GameClient c) {
        var units=RawUnits(c);var response=c.Request(10,new byte[0],20).M(10);var obs=Wire.Parse(response.B(3));
        Func<ulong,int> n=o=>units.Count(u=>u.N(5)==o);
        // ResponseObservation.player_result (4): PlayerResult{player_id 1, result 2: 1 victory, 2 defeat, 3 tie, 4 undecided}.
        string results=response.Fields.ContainsKey(4)?string.Join(" ",response.Fields[4].Select(b=>{var r=Wire.Parse((byte[])b);return "P"+r.N(1)+"="+r.N(2);})):"none";
        string owners=string.Join(", ",units.GroupBy(u=>u.N(5)).OrderBy(g=>g.Key).Select(g=>"P"+g.Key+" "+g.Count()));
        return "loop "+obs.N(9)+", units by owner: "+owners+"; player results: "+results;
    }
    // Connects the two launchers (direct TCP, or Steam) and checks the hello; the caller opens the game ports.
    public Tunnel EstablishLink(Settings settings,string mode,string host,ref int basePort,ref ulong lobby,out string sc2Host) {
        bool steam=mode=="steam-host"||mode=="steam-join",hosting=mode=="host"||mode=="steam-host",guest=mode=="join"||mode=="steam-join";
        sc2Host=host;
        // "dgram": this side can take game UDP as unreliable messages (Steam only; SC2_COOP_NO_DGRAM=1 turns it off).
        bool offerDgram=steam&&Environment.GetEnvironmentVariable("SC2_COOP_NO_DGRAM")!="1";
        string hello=new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"v",2},{"map",settings.MapSha256},{"base",basePort},{"dgram",offerDgram?1:0}});
        var local=IPAddress.Parse(Environment.GetEnvironmentVariable("SC2_COOP_LOCAL_IP")??"127.0.0.1");
        if(steam) {
            var s=SteamSession.Get(Program.Log);
            // CLI: "friend" joins the first friend whose Steam status shows a lobby.
            if(guest&&host.Trim()=="friend"){var found=s.HostingFriends();if(found.Count==0)throw new Exception("No Steam friend is hosting");lobby=found[0].Lobby;Program.Log("Joining Steam friend "+found[0]);}
            var link=hosting?s.Host(settings.MapSha256,TimeSpan.FromMinutes(15),Stop,Program.UiLog!=null):s.Join(lobby,TimeSpan.FromMinutes(15),Stop);
            tunnel=Tunnel.Over(hosting,link,"Steam user "+link.Peer,basePort,local,hello,Program.Log);sc2Host=hosting?"127.0.0.1":local.ToString();
        } else if(mode=="host") {
            upnp=Environment.GetEnvironmentVariable("SC2_COOP_NO_UPNP")=="1"?new Upnp{LanIp=Upnp.DetectLanIp()}:Upnp.TryMap(settings.TunnelPort,Program.Log);
            Program.Log(JoinInstructions(upnp,settings.TunnelPort));
            tunnel=Tunnel.Accept(settings.TunnelPort,basePort,hello,TimeSpan.FromMinutes(15),Stop,Program.Log);sc2Host="127.0.0.1";
        } else {
            string target=host.Trim();int port=settings.TunnelPort;int colon=target.LastIndexOf(':');
            if(colon>0&&target.IndexOf(':')==colon){port=int.Parse(target.Substring(colon+1));target=target.Substring(0,colon);}
            tunnel=Tunnel.Connect(target,port,basePort,local,hello,Program.Log);sc2Host=local.ToString();
        }
        var remote=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(tunnel.RemoteHello);
        if(!remote.ContainsKey("map")||(string)remote["map"]!=settings.MapSha256)throw new Exception("Your buddy's prepared map differs from yours (different game version or launcher version)");
        // The tunnel maps ports, so each side may use its own base (single-PC tests need distinct ones).
        if(guest){string own=Environment.GetEnvironmentVariable("SC2_COOP_LOCAL_BASE");basePort=own!=null?int.Parse(own):Convert.ToInt32(remote["base"]);tunnel.UseBase(basePort);}
        if(offerDgram&&remote.ContainsKey("dgram")&&Convert.ToInt32(remote["dgram"])==1)tunnel.EnableDatagrams();
        else if(steam)Program.Log("Game UDP packets go reliably over Steam"+(offerDgram?" (your buddy's launcher is older)":""));
        return tunnel;
    }

    public void Start(Settings settings,string mode,string host,int basePort,bool visible,int seconds) {
        Program.Validate(settings);GameClient.ForceWindowed=mode=="local";
        IPAddress address;
        // steam-host/steam-join carry the same tunnel over Steam (Steam.cs); host is then a lobby id or "-" (wait for an invite).
        bool steam=mode=="steam-host"||mode=="steam-join",hosting=mode=="host"||mode=="steam-host",guest=mode=="join"||mode=="steam-join";
        ulong lobby=0;
        if(mode=="join"){if(string.IsNullOrWhiteSpace(host))throw new Exception("Enter the host's address");}
        else if(mode=="steam-join"){string id=(host??"").Trim();if(id!=""&&id!="-"&&id!="friend"&&!ulong.TryParse(id,out lobby)&&!SteamSession.ParseConnect(id,out lobby))throw new Exception("Pick a Steam friend from the list, paste a lobby id, or leave it empty to wait for an invite");}
        else if(!IPAddress.TryParse(host,out address)||address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)throw new Exception("Enter an IPv4 address, such as 127.0.0.1 for the local trial");
        if(basePort<1024||basePort>65531)throw new Exception("Base port must be 1024..65531");
        Run=Path.Combine(settings.Workspace,"runs",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+mode);Directory.CreateDirectory(Run);
        Program.LogFile=Path.Combine(Run,"launcher.log");
        var before=Program.ProfileSnapshot();
        // Host and join go through the launcher tunnel; both games then use a loopback host_ip.
        string sc2Host=host;
        try {
            if(hosting||guest) {
                // Campaign: one tunnel serves the hub stage and every mission (CampaignRun in Campaign.cs).
                if(PresetTunnel!=null){tunnel=PresetTunnel;basePort=tunnel.BasePort;sc2Host=hosting?"127.0.0.1":(Environment.GetEnvironmentVariable("SC2_COOP_LOCAL_IP")??"127.0.0.1");}
                else tunnel=EstablishLink(settings,mode,host,ref basePort,ref lobby,out sc2Host);
                tunnel.Open();
            }
            if(PresetClients!=null)Clients.AddRange(PresetClients);
            else for(int i=0;i<(mode=="local"?2:1);i++)Clients.Add(new GameClient(settings,PresetRoots!=null?PresetRoots[i]:Path.Combine(Run,"client-"+(i+1)),visible,Program.Log));
            // SaveMap provides the same relative map name on every PC. Never use the host's absolute map path remotely.
            // The name includes the map's hash: a joining game keeps an earlier copy under the old name when it was killed
            // (no cleanup), saves the new one as "...~00" and then loads the stale copy, so every later join failed with
            // ChecksumError (11) (2026-10-21). One name per map content can't collide.
            byte[] map=File.ReadAllBytes(Program.MapFile(settings));
            string savedName="CoopTrial/Coop-"+Program.Sha(map).Substring(0,12)+".SC2Map";
            foreach(var c in Clients) {
                var saved=c.Request(18,Wire.Join(Wire.Str(1,savedName),Wire.Msg(2,map)),30).M(18);
                if(saved.N(1)!=0)throw new Exception("SaveMap failed: "+saved.N(1));
            }
            if(!guest) {
                byte[] opponent=mode=="solo"?Wire.Msg(3,Wire.Num(1,2),Wire.Num(2,1),Wire.Num(3,1)):Wire.Msg(3,Wire.Num(1,1));
                string mapPath=mode=="solo"?Program.MapFile(settings):savedName;
                var created=Clients[0].Request(1,Wire.Join(Wire.Msg(1,Wire.Str(1,mapPath)),Wire.Msg(3,Wire.Num(1,1)),opponent,Wire.Num(5,12345),Wire.Num(6,1)),60).M(1);
                if(created.N(1)!=0)throw new Exception("CreateGame failed: "+created.N(1)+" "+created.S(2));
            }
            Report("joining");
            Program.Log("Joining via "+sc2Host+" using game ports "+basePort+".."+(basePort+3)+". Both PCs must start within 3 minutes.");
            var tasks=Clients.Select((c,index)=>Task.Run(()=>{
                byte[] options=Wire.Join(Wire.Num(1,settings.Raw?1UL:0),Wire.Num(2,0));
                bool bridged=settings.Bridge&&(guest||(mode=="local"&&index==1));
                if(bridged) {
                    // The bridged guest gains nothing from raw (its native world input is gated anyway).
                    if(Environment.GetEnvironmentVariable("SC2_COOP_GUEST_RAW")=="0")options=Wire.Join(Wire.Num(1,0),Wire.Num(2,0));
                    var cal=Program.Calibration(settings);
                    options=Wire.Join(options,Wire.Msg(3,Wire.Float(1,(float)cal.FeatureWidth),Wire.Msg(2,Wire.Num(1,(ulong)cal.Resolution),Wire.Num(2,(ulong)cal.Resolution)),Wire.Msg(3,Wire.Num(1,64),Wire.Num(2,64))));
                }
                else if(settings.FeatureLayer)options=Wire.Join(options,Wire.Msg(3,Wire.Float(1,24),Wire.Msg(2,Wire.Num(1,84),Wire.Num(2,84)),Wire.Msg(3,Wire.Num(1,64),Wire.Num(2,64))));
                byte[] ports=mode=="solo"?new byte[0]:Wire.Join(
                    Wire.Msg(4,Wire.Num(1,(ulong)basePort),Wire.Num(2,(ulong)basePort+1)),
                    Wire.Msg(5,Wire.Num(1,(ulong)basePort+2),Wire.Num(2,(ulong)basePort+3)),
                    Wire.Str(8,sc2Host));
                ulong race=GuestRace!=0&&(guest||(mode=="local"&&index==1))?GuestRace:Race;
                var joined=c.Request(2,Wire.Join(Wire.Num(1,race),Wire.Msg(3,options),ports,
                    Wire.Str(7,mode=="local"&&index==1?"Buddy":Program.MyName())),180).M(2);
                if(joined.N(2)!=0)throw new Exception((Clients.Count>1?"Game window "+(index+1)+": ":"")+"JoinGame failed: "+joined.N(2)+" "+joined.S(3));
                c.Joined=true;Program.Log("Joined player "+joined.N(1));
            })).ToArray();
            Task.WaitAll(tasks);
            if(settings.Bridge&&!hosting&&mode!="solo")bridge=new ClickBridge(Clients[guest?0:1],Program.Calibration(settings),Program.Log,
                settings.Product?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"StarCraft II","Banks"):Clients[guest?0:1].Root);
            Report("ready");
            Program.Log("READY — use the game windows; assigned player IDs are logged above. Stop closes this trial only.");
            // Test hooks for transport tests without mouse input: the host logs player-1 marines from its own observation,
            // the guest feeds "ax,ay,bx,by;fx,fy" (feature-layer coordinates) into the click bridge after 35 s.
            if(hosting&&Environment.GetEnvironmentVariable("SC2_COOP_TEST_MARINES")=="1")Task.Run(()=>{foreach(int wait in new[]{25,25,25}){if(Stop.WaitOne(wait*1000))return;try{Program.Log("Test: "+Marines(Clients[0]));}catch(Exception e){Program.Log("Test observation: "+e.Message);}}});
            // SC2_COOP_TEST_STATUS=1: every client logs its API status (3 in game, 5 ended) every 10 s.
            if(Environment.GetEnvironmentVariable("SC2_COOP_TEST_STATUS")=="1")foreach(var sc in Clients.ToArray()){var c=sc;int idx=Clients.IndexOf(c)+1;Task.Run(()=>{
                ulong last=0;while(!Stop.WaitOne(10000)){try{ulong st=c.Request(19,new byte[0],10).N(99);if(st!=last)Program.Log("Test: client-"+idx+" API status "+st+(st==5?" (game ended for this client)":""));last=st;}catch(Exception e){Program.Log("Test status client-"+idx+": "+e.Message);return;}}});}
            // SC2_COOP_TEST_RESCUE=<s>: the host jumps to The Outlaws' rebel rescue (debug: kill Dominion units in the rebel
            // base, region 25; spawn player-1 marines in the rescue area, region 24), then logs rebels/P1 counts.
            string rescue=Environment.GetEnvironmentVariable("SC2_COOP_TEST_RESCUE");
            if(!string.IsNullOrEmpty(rescue)&&(hosting||mode=="local"))Task.Run(()=>{
                if(Stop.WaitOne(int.Parse(rescue)*1000))return;
                // SC2_COOP_TEST_KILL_PLAYER=n[,m...]: kill all of player n's units instead (elimination without the rescue
                // script); each further player 65 s later, after the unit counts below.
                string killPlayer=Environment.GetEnvironmentVariable("SC2_COOP_TEST_KILL_PLAYER");
                var victims=killPlayer==null?new string[]{null}:killPlayer.Split(',');
                foreach(var victim in victims) {
                    try {Program.Log("Test: "+(victim!=null?KillAll(Clients[0],ulong.Parse(victim)):Rescue(Clients[0])));} catch(Exception e){Program.Log("Test rescue: "+e.Message);}
                    foreach(int wait in new[]{5,10,20,30}){if(Stop.WaitOne(wait*1000))return;try{Program.Log("Test: "+Owners(Clients[0]));}catch(Exception e){Program.Log("Test owners: "+e.Message);}}
                }
            });
            // SC2_COOP_TEST_GUEST_MOVE=<s>,x,y (local mode): s seconds in, the guest's own client attack-moves all of its own
            // units to x,y through the API (raw unit command, ability 23 = attack), like a player's order.
            string guestMove=Environment.GetEnvironmentVariable("SC2_COOP_TEST_GUEST_MOVE");
            if(!string.IsNullOrEmpty(guestMove)&&mode=="local"&&Clients.Count>1)Task.Run(()=>{
                var a=guestMove.Split(',');if(Stop.WaitOne(int.Parse(a[0])*1000))return;
                try {
                    var g=Clients[1];float x=float.Parse(a[1],System.Globalization.CultureInfo.InvariantCulture),y=float.Parse(a[2],System.Globalization.CultureInfo.InvariantCulture);
                    // The guest's own observation can crash its client (player id > 3); the host's lists the guest's units.
                    ulong guestId=ulong.Parse(Environment.GetEnvironmentVariable("SC2_COOP_TEST_RESCUE_OWNER")??"6");
                    var mine=RawUnits(Clients[0]).Where(u=>u.N(5)==guestId).Select(u=>u.N(3)).ToList();
                    var cmd=Wire.Join(Wire.Num(1,23),Wire.Msg(2,Wire.Float(1,x),Wire.Float(2,y)),Wire.Join(mine.Select(t=>Wire.Num(4,t)).ToArray()));
                    var r=g.Request(11,Wire.Msg(1,Wire.Msg(1,Wire.Msg(1,cmd))),20).M(11);
                    Program.Log("Test: guest attack-move of "+mine.Count+" own units to "+x+","+y+", result "+r.N(1));
                } catch(Exception e){Program.Log("Test guest move: "+e.Message);}
            });
            string testSelect=Environment.GetEnvironmentVariable("SC2_COOP_TEST_SELECT");
            if(bridge!=null&&!string.IsNullOrEmpty(testSelect))Task.Run(()=>{
                if(Stop.WaitOne(30000))return;
                foreach(var pt in testSelect.Split(';')){var n=pt.Split(',').Select(int.Parse).ToArray();bridge.TestSelect(n[0],n[1]);if(Stop.WaitOne(1500))return;}
            });
            // SC2_COOP_TEST_LEAVE=<client>,<s>: that client leaves the game through the API (stand-in for End Game).
            string testLeave=Environment.GetEnvironmentVariable("SC2_COOP_TEST_LEAVE");
            if(!string.IsNullOrEmpty(testLeave))Task.Run(()=>{
                var n=testLeave.Split(',').Select(int.Parse).ToArray();if(Stop.WaitOne(n[1]*1000))return;
                try{Program.Log("Test: client-"+n[0]+" leaves the game");Clients[n[0]-1].Request(5,new byte[0],10);}catch(Exception e){Program.Log("Test leave: "+e.Message);}
            });
            // SC2_COOP_TEST_DELAY=1 (local mode): command-to-order delay for host (raw move) and guest (feature-layer
            // right-click, as the bridge sends it), measured on the host's observation.
            if((mode=="local"||mode=="solo")&&Environment.GetEnvironmentVariable("SC2_COOP_TEST_DELAY")=="1")Task.Run(()=>{
                if(Stop.WaitOne(25000))return;
                try {
                    if(Clients.Count>1)Clients[1].Request(11,Wire.Msg(1,Wire.Msg(2,Wire.Msg(4,Wire.Msg(1,Wire.Msg(1,Wire.Join(Wire.Num(1,205),Wire.Num(2,111))),Wire.Msg(2,Wire.Join(Wire.Num(1,241),Wire.Num(2,146))))))),10);
                    Thread.Sleep(1000);
                    for(int i=0;i<12&&!Stop.WaitOne(0);i++) {
                        bool guestTurn=Clients.Count>1&&i%2==1;var marines=RawUnits(Clients[0]).Where(u=>u.N(4)==48&&u.N(5)==1).ToList();
                        float ty=i%4<2?30f:36f;var sw=Stopwatch.StartNew();
                        if(guestTurn)Clients[1].Request(11,Wire.Msg(1,Wire.Msg(2,Wire.Msg(1,Wire.Num(1,1),Wire.Msg(2,Wire.Join(Wire.Num(1,220),Wire.Num(2,(ulong)(i%4<2?183:150))))))),10);
                        else Clients[0].Request(11,Wire.Msg(1,Wire.Msg(1,Wire.Msg(1,Wire.Join(Wire.Num(1,16),Wire.Msg(2,Wire.Float(1,86f),Wire.Float(2,ty)),Wire.Join(marines.Select(u=>Wire.Num(4,u.N(3))).ToArray()))))),10);
                        long sent=sw.ElapsedMilliseconds;ulong loop0=0,loop1=0;
                        while(sw.ElapsedMilliseconds<3000) {
                            var resp=Clients[0].Request(10,new byte[0],10).M(10);var obs=Wire.Parse(resp.B(3));if(loop0==0)loop0=obs.N(9);
                            var raw=obs.M(5);bool moving=raw.Fields.ContainsKey(2)&&raw.Fields[2].Any(b=>{var u=Wire.Parse((byte[])b);return u.N(4)==48&&u.N(5)==1&&u.Fields.ContainsKey(22);});
                            if(moving){loop1=obs.N(9);break;}
                        }
                        Program.Log("Test delay: "+(guestTurn?"guest feature-layer move":"host raw move")+" -> orders seen after "+sw.ElapsedMilliseconds+" ms (request "+sent+" ms, game loops "+loop0+"->"+loop1+")");
                        Clients[0].Request(11,Wire.Msg(1,Wire.Msg(1,Wire.Msg(1,Wire.Join(Wire.Num(1,4),Wire.Join(marines.Select(u=>Wire.Num(4,u.N(3))).ToArray()))))),10);
                        Thread.Sleep(1500);
                    }
                } catch(Exception e){Program.Log("Test delay: "+e.Message);}
            });
            string testBridge=Environment.GetEnvironmentVariable("SC2_COOP_TEST_BRIDGE");
            if(bridge!=null&&!string.IsNullOrEmpty(testBridge))Task.Run(()=>{
                // SC2_COOP_TEST_BRIDGE_REPEAT=n repeats every 20 s (stall tests).
                int repeat=int.Parse(Environment.GetEnvironmentVariable("SC2_COOP_TEST_BRIDGE_REPEAT")??"1");
                var n=testBridge.Split(',',';').Select(int.Parse).ToArray();
                for(int i=0;i<repeat;i++){if(Stop.WaitOne(i==0?35000:20000))return;Program.Log("Test: bridge drag + right-click #"+(i+1));bridge.TestDragAndSmart(n[0],n[1],n[2],n[3],n[4],n[5]);}
            });
            // SC2_COOP_TEST_GUEST_UI="ax,ay,bx,by;fx,fy[;s]": after s seconds (default 40) the guest bridge draws its drag box over the
            // guest-client rectangle for 15 s and sends (3 times, 5 s apart) the ping an Alt+click at feature point fx,fy would send, without the mouse.
            string testUi=Environment.GetEnvironmentVariable("SC2_COOP_TEST_GUEST_UI");
            if(bridge!=null&&!string.IsNullOrEmpty(testUi))Task.Run(()=>{
                var parts=testUi.Split(';');var r=parts[0].Split(',').Select(int.Parse).ToArray();var f=parts[1].Split(',').Select(int.Parse).ToArray();
                if(Stop.WaitOne((parts.Length>2?int.Parse(parts[2]):40)*1000))return;
                Program.Log("Test: guest drag box "+parts[0]+" and ping "+parts[1]);bridge.TestDragBox(r[0],r[1],r[2],r[3],true);
                for(int i=0;i<3;i++){bridge.TestPing(f[0],f[1]);if(Stop.WaitOne(5000))return;}bridge.TestDragBox(0,0,0,0,false);Program.Log("Test: guest drag box hidden");
            });
            // SC2_COOP_TEST_SCANMOVE=1 (local mode, The Outlaws): 30 s in, the host spawns 2 medics (debug) by the starting marines; the
            // guest box-selects them with the marines, presses A (posted to its window and fed to the bridge) and clicks a point;
            // 3 s later the host logs each marine's and medic's first order (ability id: 23 attack, 19 scan move, 16 move).
            if(mode=="local"&&bridge!=null&&Environment.GetEnvironmentVariable("SC2_COOP_TEST_SCANMOVE")=="1")Task.Run(()=>{
                if(Stop.WaitOne(30000))return;
                try {
                    Clients[0].Request(20,Wire.Msg(1,Wire.Msg(3,Wire.Num(1,1731),Wire.Num(2,1),Wire.Msg(3,Wire.Float(1,86.5f),Wire.Float(2,34.5f)),Wire.Num(4,2))),20);
                    if(Stop.WaitOne(2000))return;
                    bridge.TestRect(190,95,255,165);if(Stop.WaitOne(1500))return;
                    bridge.TestNativeKey(0x41);bridge.TestKey(0x41,true);bridge.TestKey(0x41,false);if(Stop.WaitOne(500))return;
                    bridge.TestWorldLeftClick(150,260);if(Stop.WaitOne(3000))return;
                    var units=RawUnits(Clients[0]).Where(u=>u.N(5)==1&&(u.N(4)==1731||u.N(4)==48)).ToList();
                    Program.Log("Test scan move: "+string.Join("; ",units.Select(u=>(u.N(4)==1731?"medic":"marine")+" "+(u.Fields.ContainsKey(22)?"order "+u.M(22).N(1):"no order"))));
                } catch(Exception e){Program.Log("Test scan move: "+e.Message);}
            });
            // SC2_COOP_TEST_PINGAT="x,y,option;...[;@s]": from s seconds (default 45), every 6 s, the guest pings at client point x,y as an
            // Alt+click would (option -1) or through its ping panel (option 0-3, panel shown for 2 s first), without the mouse.
            string testPingAt=Environment.GetEnvironmentVariable("SC2_COOP_TEST_PINGAT");
            if(bridge!=null&&!string.IsNullOrEmpty(testPingAt))Task.Run(()=>{
                var items=testPingAt.Split(';');var at=items.FirstOrDefault(x=>x.StartsWith("@"));
                if(Stop.WaitOne((at!=null?int.Parse(at.Substring(1)):45)*1000))return;
                foreach(var it in items.Where(x=>!x.StartsWith("@"))){var n=it.Split(',').Select(int.Parse).ToArray();bridge.TestPingAt(n[0],n[1],n[2]);if(Stop.WaitOne(6000))return;}
            });
            DateTime deadline=seconds>0?DateTime.UtcNow.AddSeconds(seconds):DateTime.MaxValue;
            string probe=Environment.GetEnvironmentVariable("SC2_COOP_PROBE_GUEST_OBS");
            if(!string.IsNullOrEmpty(probe)&&mode=="local") {
                Thread.Sleep(30000);
                foreach(int i in new[]{0,1}) {
                    try {
                        var o=Clients[i].Request(10,new byte[0],20).M(10);
                        var obs=o.M(3);var raw=obs.M(5);
                        int units=raw.Fields.ContainsKey(2)?raw.Fields[2].Count:0;
                        Program.Log("Probe observation client-"+(i+1)+": loop "+obs.N(9)+", raw units "+units+", fields "+string.Join(",",obs.Fields.Keys));
                    } catch(Exception e){Program.Log("Probe observation client-"+(i+1)+" failed: "+e.Message);}
                }
            }
            int tick=0;ulong frozenLoop=0;int frozenSince=0;bool warnedApiEnd=false;
            while(!Stop.WaitOne(settings.Observe?100:1000)&&DateTime.UtcNow<deadline) {
                var gone=Clients.FirstOrDefault(c=>c.Process.HasExited);
                if(gone!=null){Crashed=true;Program.Log("StarCraft II"+(Clients.Count>1?" window "+(Clients.IndexOf(gone)+1):"")+" closed unexpectedly (exit code "+gone.Process.ExitCode+"); ending the mission.");break;}
                if(tunnel!=null&&tunnel.Closed){Program.Log("Connection to your buddy was lost");break;}
                // End Game leaves the match and then waits for this launcher; close the game when it is no longer in
                // the match. Exception: the host's simulation still advancing means only the API flagged the game as
                // over (a mission event); keep it running then.
                if(++tick%2==0&&GameLeft(mode,hosting,ref frozenLoop,ref frozenSince,ref warnedApiEnd))break;
                if(settings.Observe)foreach(var c in Clients)c.Request(10,new byte[0],10);
            }
        }
        finally {
            Report("ended");
            if(bridge!=null)bridge.Dispose();
            Task.WaitAll(Clients.Select(c=>Task.Run(()=>c.Leave(Program.Log))).ToArray());
            foreach(var c in Clients)if(!KeepClients||c.Process.HasExited)c.Dispose();
            if(tunnel!=null&&tunnel!=PresetTunnel)tunnel.Dispose();
            if(upnp!=null)upnp.Dispose();
            bool unchanged=settings.Product||before.SequenceEqual(Program.ProfileSnapshot());
            if(!settings.Product)Program.Log("Normal SC2 Documents profile unchanged: "+unchanged);
            if(!unchanged)throw new Exception("Normal SC2 profile changed during trial; inspect launcher.log before further runs");
        }
    }
}

static class UdpTest {
    public static void Run(bool host,int basePort,int seconds) {
        var s=SteamSession.Get(Program.Log);bool offer=Environment.GetEnvironmentVariable("SC2_COOP_NO_DGRAM")!="1";
        string hello=new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"v",2},{"map","udp-test"},{"base",basePort},{"dgram",offer?1:0}});
        var never=new ManualResetEvent(false);ulong lobby=0;
        if(!host){var f=s.HostingFriends();if(f.Count==0)throw new Exception("No hosting friend");lobby=f[0].Lobby;}
        var link=host?s.Host("udp-test-0000000000",TimeSpan.FromMinutes(5),never,false):s.Join(lobby,TimeSpan.FromMinutes(5),never);
        using(var t=Tunnel.Over(host,link,"Steam user "+link.Peer,basePort,System.Net.IPAddress.Loopback,hello,Program.Log)) {
            var remote=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(t.RemoteHello);
            if(offer&&remote.ContainsKey("dgram")&&Convert.ToInt32(remote["dgram"])==1)t.EnableDatagrams();
            t.Open();
            if(host) {
                // Stands in for the host's game port: echo every datagram back to its sender (the tunnel).
                using(var game=new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,basePort))) {
                    game.Client.ReceiveTimeout=1000;var end=DateTime.UtcNow.AddSeconds(seconds+10);int n=0;
                    while(DateTime.UtcNow<end&&!t.Closed){try{var from=new System.Net.IPEndPoint(System.Net.IPAddress.Any,0);var d=game.Receive(ref from);game.Send(d,d.Length,from);n++;}catch(System.Net.Sockets.SocketException){}}
                    Program.Log("UDP test host: echoed "+n+" packets");
                }
            } else {
                // Stands in for the guest's game port (base+2), talking to the impersonated host game port (base).
                using(var game=new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,basePort+2))) {
                    var target=new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,basePort);var sent=new Dictionary<int,long>();var rtt=new List<double>();
                    var sw=System.Diagnostics.Stopwatch.StartNew();int seq=0;var payload=new byte[200];
                    var rx=new Thread(()=>{var from=new System.Net.IPEndPoint(System.Net.IPAddress.Any,0);while(true){try{var d=game.Receive(ref from);int k=BitConverter.ToInt32(d,0);long t0;lock(sent){if(sent.TryGetValue(k,out t0)){rtt.Add(sw.ElapsedTicks-t0);sent.Remove(k);}}}catch{return;}}}){IsBackground=true};rx.Start();
                    while(sw.Elapsed.TotalSeconds<seconds){Buffer.BlockCopy(BitConverter.GetBytes(seq),0,payload,0,4);lock(sent)sent[seq]=sw.ElapsedTicks;game.Send(payload,payload.Length,target);seq++;Thread.Sleep(20);}
                    Thread.Sleep(2000);
                    lock(sent) {
                        var ms=rtt.Select(x=>x*1000.0/System.Diagnostics.Stopwatch.Frequency).OrderBy(x=>x).ToList();
                        Program.Log("UDP test: "+seq+" sent, "+ms.Count+" echoed, "+sent.Count+" lost"+(ms.Count>0?"; round trip median "+ms[ms.Count/2].ToString("0")+" ms, 95th "+ms[(int)(ms.Count*0.95)].ToString("0")+", 99th "+ms[(int)(ms.Count*0.99)].ToString("0")+", max "+ms[ms.Count-1].ToString("0"):""));
                    }
                }
            }
        }
    }
}

class Program {
    public static string Base=AppDomain.CurrentDomain.BaseDirectory;
    public static string LogFile;
    public static Action<string> UiLog;
    static object logLock=new object();
    public static string Q(string p) {if(p.Contains("\""))throw new Exception("Invalid path");return "\""+p+"\"";}
    public static string Sha(byte[] b) {using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
    public static void Log(string s) {lock(logLock){string text=DateTime.Now.ToString("HH:mm:ss")+" "+s;Console.WriteLine(text);if(LogFile!=null)File.AppendAllText(LogFile,text+Environment.NewLine);if(UiLog!=null)UiLog(text);}}
    public static string[] ProfileSnapshot() {
        string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"StarCraft II");
        return Directory.Exists(path)?Directory.GetFiles(path,"*",SearchOption.AllDirectories).OrderBy(x=>x).Select(x=>{var f=new FileInfo(x);return x+"|"+f.Length+"|"+f.LastWriteTimeUtc.Ticks;}).ToArray():new string[0];
    }
    // The SC2 install of the running campaign (for dependency checks); set by CampaignRun.
    public static string CurrentInstall;
    // In-game name and color (launcher row "Your name / Color"), kept per Windows user. Color: SC2 color index, -1 = default,
    // or an RGB (TeamColors.IsRgb; "#RRGGBB" in the profile file).
    public static string PlayerName;public static int PlayerColor=-1;
    // Separate-army choices (SeparateOptions; the host's apply): mission-given units and mission money.
    public static string SeparateUnits=SeparateOptions.UnitChoices[0],SeparateMoney=SeparateOptions.MoneyChoices[0];
    // Mission game speed (the host's applies; -coopspeed in a mission changes it). Faster: what API games ran at before.
    public static readonly string[] GameSpeeds={"slower","slow","normal","fast","faster"};
    public static string GameSpeed="faster";
    public static string CleanSpeed(string v){v=(v??"").Trim().ToLowerInvariant();return GameSpeeds.Contains(v)?v:"faster";}
    static string ProfileFile{get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SC2Coop","profile.txt");}}
    public static void LoadProfile() {
        try{if(File.Exists(ProfileFile)){var l=File.ReadAllLines(ProfileFile);PlayerName=l.Length>0?l[0].Trim():"";PlayerColor=l.Length>1?TeamColors.Parse(l[1]):-1;
            if(l.Length>2)SeparateUnits=SeparateOptions.Clean(l[2].Trim(),SeparateOptions.UnitChoices);if(l.Length>3)SeparateMoney=SeparateOptions.Clean(l[3].Trim(),SeparateOptions.MoneyChoices);
            if(l.Length>4)GameSpeed=CleanSpeed(l[4]);}}catch{}
        if(Environment.GetEnvironmentVariable("SC2_COOP_UNITS")!=null)SeparateUnits=SeparateOptions.Clean(Environment.GetEnvironmentVariable("SC2_COOP_UNITS"),SeparateOptions.UnitChoices);
        if(Environment.GetEnvironmentVariable("SC2_COOP_MONEY")!=null)SeparateMoney=SeparateOptions.Clean(Environment.GetEnvironmentVariable("SC2_COOP_MONEY"),SeparateOptions.MoneyChoices);
        if(Environment.GetEnvironmentVariable("SC2_COOP_SPEED")!=null)GameSpeed=CleanSpeed(Environment.GetEnvironmentVariable("SC2_COOP_SPEED"));
        // Test/CLI overrides.
        if(Environment.GetEnvironmentVariable("SC2_COOP_NAME")!=null)PlayerName=Environment.GetEnvironmentVariable("SC2_COOP_NAME");
        if(Environment.GetEnvironmentVariable("SC2_COOP_COLOR")!=null)PlayerColor=TeamColors.Parse(Environment.GetEnvironmentVariable("SC2_COOP_COLOR"));
    }
    public static void SaveProfile(){try{Directory.CreateDirectory(Path.GetDirectoryName(ProfileFile));File.WriteAllLines(ProfileFile,new[]{PlayerName??"",TeamColors.Format(PlayerColor),SeparateUnits,SeparateMoney,GameSpeed});}catch{}}
    // The name StarCraft II shows for this player (chat, pings): the chosen one, else the Windows user name.
    public static string MyName() {
        string n=(PlayerName??"").Trim();if(n=="")n=Environment.UserName;
        n=new string(n.Where(c=>!char.IsControl(c)).ToArray());return n.Length>20?n.Substring(0,20):n;
    }
    public static string MapFile(Settings s) {
        return s.MapPath!=null?Path.GetFullPath(Path.Combine(s.ConfigDirectory,s.MapPath)):Path.Combine(s.Install,"Maps/"+s.MapName);
    }
    public static BridgeCalibration Calibration(Settings s) {
        string path=Path.IsPathRooted(s.BridgeCalibration)?s.BridgeCalibration:Path.Combine(s.ConfigDirectory,s.BridgeCalibration);
        return BridgeCalibration.Load(path);
    }
    public static Settings Load(string path) {
        var s=new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path));
        s.ConfigDirectory=Path.GetDirectoryName(path);
        s.Install=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path),s.Install));
        s.Workspace=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path),s.Workspace));return s;
    }
    public static void Validate(Settings s) {
        string exe=Path.Combine(s.Install,"Versions/Base98310/SC2_x64.exe");
        if(s.Product) {
            if(!File.Exists(exe)||Sha(File.ReadAllBytes(exe))!="541a027834eb1c9d0004baa24568e3b07670df479f17256b475609a9158ac9f4")throw new Exception("This build supports StarCraft II 5.0.17.98310 only; your game is missing or a different version. Prepare again after updating, or ask for a matching launcher.");
        } else {
            if(!File.Exists(exe+".original")||Sha(File.ReadAllBytes(exe+".original"))!="541a027834eb1c9d0004baa24568e3b07670df479f17256b475609a9158ac9f4")throw new Exception("Expected a prepared, independent copy of SC2 build 98310");
            byte[] original=File.ReadAllBytes(exe+".original");int at=TrialPreparation.FindUnique(original,Encoding.ASCII.GetBytes("USER32.dll\0"));Array.Copy(Encoding.ASCII.GetBytes("sc2win.dll\0"),0,original,at,11);
            if(Sha(File.ReadAllBytes(exe))!=Sha(original))throw new Exception("Prepared executable checksum mismatch");
            if(Sha(File.ReadAllBytes(Path.Combine(s.Install,"Versions/Base98310/sc2win.dll")))!=Sha(File.ReadAllBytes(Path.Combine(Base,"sc2win.dll"))))throw new Exception("Folder isolation DLL checksum mismatch");
        }
        if(Path.GetFileName(s.MapName)!=s.MapName)throw new Exception("MapName must be a filename");
        if(s.Recipe!=TrialPreparation.Recipe())throw new Exception("This launcher version changes the mission map; click Prepare again");
        if(!File.Exists(MapFile(s))||Sha(File.ReadAllBytes(MapFile(s)))!=s.MapSha256)throw new Exception("Prepared map is missing or changed; run Prepare again");
        Directory.CreateDirectory(s.Workspace);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [STAThread] static int Main(string[] args) {
        // Physical pixels everywhere: the click bridge maps hook coordinates into the SC2 client area.
        try {SetProcessDpiAwarenessContext(new IntPtr(-4));} catch { }
        try {
            if(args.Length>0&&args[0]=="prepare") {TrialPreparation.Prepare(args[1],args[2]);return 0;}
            if(args.Length>0&&args[0]=="build-map") {TrialPreparation.BuildMap(args[1]);return 0;}
            if(args.Length>0&&args[0]=="upnp-test") {
                int port=int.Parse(args[1]);bool before=Upnp.IsMapped(port);Log("Before: mapping for TCP "+port+" present = "+before);
                if(before){Log("Aborting: the router already has a TCP "+port+" mapping.");return 2;}
                using(var u=Upnp.TryMap(port,Log)) {
                    Log("LAN "+u.LanIp+", mapped "+u.Mapped+", router external address "+(u.ExternalIp??"unknown"));
                    Log(Session.JoinInfo(u,port));Log("While open: present = "+Upnp.IsMapped(port));
                    System.Threading.Thread.Sleep(5000);
                }
                Log("After removal: present = "+Upnp.IsMapped(port));return 0;
            }
            // steam-udp-test host|join <base> <seconds>: game-UDP echo through the Steam tunnel, no SC2 (the join side
            // sends 50 numbered packets a second to the host's impersonated game port and times the echoes).
            if(args.Length>0&&args[0]=="steam-udp-test") {UdpTest.Run(args[1]=="host",int.Parse(args[2]),int.Parse(args[3]));return 0;}
            // campaign <config> <local|host|join|steam-host|steam-join> <host address or -> <campaign folder> [base port]
            // campaign-build <config> <campaign folder> <mission> [difficulty]: offline check of the hub and mission builders.
            if(args.Length>0&&args[0]=="campaign-build") {
                var st=Load(Path.GetFullPath(args[1]));string outDir=Path.Combine(st.Workspace,"campaign","maps");CurrentInstall=st.Install;
                CampaignMaps.BuildKitHub(args[2],outDir);int guest;CampaignMaps.BuildMission(args[2],args[3],args.Length>4?int.Parse(args[4]):2,outDir,out guest);
                Log("Campaign banks: "+string.Join(", ",CampaignMaps.CampaignBanks(args[2])));return 0;
            }
            // campaign-check <campaign folder> [report.md]: what each map aims at player 1 (Campaign.cs CampaignCheck).
            if(args.Length>0&&args[0]=="campaign-check") {
                int open;string report=CampaignCheck.Run(args[1],out open);
                string outFile=args.Length>2?args[2]:Path.Combine(Base,"local","campaign-check.md");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));File.WriteAllText(outFile,report);
                Console.WriteLine("Report: "+Path.GetFullPath(outFile)+" ("+open+" items beyond what the launcher handles)");return 0;
            }
            // campaign-colors <config> <campaign folder>: the campaign's team colors (index, name, RGB).
            if(args.Length>0&&args[0]=="campaign-colors") {
                var st=Load(Path.GetFullPath(args[1]));
                foreach(var c in TeamColors.For(st.Install,args[2]))Console.WriteLine(c.Index+"	"+c.Name+"	#"+(c.Swatch.ToArgb()&0xFFFFFF).ToString("X6"));
                return 0;
            }
            // map-files <map or mod> [filter] [entry]: the archive's file names (containing filter), or one file's text.
            if(args.Length>0&&args[0]=="map-files") {
                if(args.Length>3){var b=MapArchive.ReadFile(args[1],args[3]);Console.WriteLine(b==null?"(absent)":Encoding.UTF8.GetString(b));return 0;}
                foreach(var kv in MapArchive.Read(args[1]))if(args.Length<3||kv.Key.IndexOf(args[2],StringComparison.OrdinalIgnoreCase)>=0)Console.WriteLine(kv.Key+"	"+kv.Value.Length);
                return 0;
            }
            // campaign-banks <config> save|load <name> [host|guest]  /  campaign-banks <config> list
            if(args.Length>0&&args[0]=="campaign-banks") {
                var st=Load(Path.GetFullPath(args[1]));string who=args.Length>4?args[4]:"host";
                if(args[2]=="save")CampaignSnapshots.Save(st,args[3],who);
                else if(args[2]=="load")CampaignSnapshots.Load(st,args[3],who);
                else foreach(var n in CampaignSnapshots.List(st))Console.WriteLine(n);
                return 0;
            }
            // campaign-reset <config> <campaign folder>: the launcher's "Reset…" (the campaign's co-op save starts over).
            if(args.Length>2&&args[0]=="campaign-reset") {
                string backup=CampaignSaves.Reset(Load(Path.GetFullPath(args[1])),args[2]);
                Console.WriteLine(backup!=null?"Reset; old progress in "+backup:"Reset (there was no progress)");return 0;
            }
            if(args.Length>0&&args[0]=="campaign") {
                LoadProfile();
                var stop=new ManualResetEvent(false);Console.CancelKeyPress+=(s2,e2)=>{e2.Cancel=true;stop.Set();};
                new CampaignRun(Load(Path.GetFullPath(args[1])),args[2],args[3],args[4],args.Length>5?int.Parse(args[5]):6500,stop).Run();return 0;
            }
            if(args.Length>0&&args[0]=="prepare-product") {TrialPreparation.PrepareProduct(args[1]);return 0;}
            if(args.Length>0&&args[0]=="run") {
                var session=new Session();Console.CancelKeyPress+=(s,e)=>{e.Cancel=true;session.Stop.Set();};
                session.Start(Load(Path.GetFullPath(args[1])),args[2],args[3],int.Parse(args[4]),!args.Contains("--hidden"),args.Length>5&&!args[5].StartsWith("--")?int.Parse(args[5]):0);return 0;
            }
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new LauncherWindow());return 0;
        }catch(Exception e){Log("ERROR: "+e.GetBaseException().Message);return 1;}
    }
}

// The window is LauncherWindow.cs; what it does is LauncherController (LauncherCore.cs).
