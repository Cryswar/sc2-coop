// Game-port tunnel between the two launchers. SC2 only plays API multiplayer games on loopback addresses
// (pysc2 lan_sc2_env.py documents the same limit), so both games use a loopback host_ip and this tunnel
// carries their four game ports over ONE TCP connection that the guest's launcher opens to the host's
// launcher. Only the host needs an inbound port; the guest's NAT never matters.
//
// Port directions, per pysc2: the guest's SC2 connects TCP to the server base port and the host's SC2
// connects TCP to the client base port; UDP flows between the two game ports with fixed addresses.
// Each side therefore impersonates the other side's ports on its own loopback address.
//
// Frames: [type:1][channel:2][length:4][payload]. Types: 0 hello (JSON), 1 UDP datagram, 2 TCP open,
// 3 TCP data, 4 TCP close, 5 control message (campaign orchestration, UTF-8 JSON).
//
// The frames ride on any ordered, reliable byte stream: a TCP connection (default, Accept/Connect) or a Steam
// P2P link (SteamLink in Steam.cs, via Over).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Optional message transport for game UDP (Steam): datagrams travel unreliably and unordered, outside the reliable
// frame stream, so one lost packet doesn't hold up the others (SC2's game protocol has its own reliability).
interface IDatagramLink {
    void SendDatagram(byte[] data,int count);
    Action<byte[]> OnDatagram {set;}
}

class Tunnel : IDisposable {
    const byte Hello=0,Udp=1,OpenFrame=2,Data=3,Close=4,Control=5;
    public readonly bool IsHost;readonly IPAddress local;int basePort;readonly Action<string> log;
    Stream stream;string remoteName;readonly object writeGate=new object();
    UdpClient udp;TcpListener listener;
    readonly ConcurrentDictionary<int,TcpClient> conns=new ConcurrentDictionary<int,TcpClient>();
    int nextChannel;volatile bool closed;
    public long BytesUp,BytesDown,DatagramsUp,DatagramsDown;
    IDatagramLink datagrams;
    public string RemoteHello;
    // Control messages from the other launcher (campaign hub flow); readable once StartReading has run.
    public readonly BlockingCollection<string> Controls=new BlockingCollection<string>();
    public int BasePort{get{return basePort;}}
    bool reading;

    // Ports: server game (UDP) = base, server base (TCP) = base+1, client game (UDP) = base+2, client base (TCP) = base+3.
    int ServerGame{get{return basePort;}} int ServerBase{get{return basePort+1;}} int ClientGame{get{return basePort+2;}} int ClientBase{get{return basePort+3;}}

    Tunnel(bool host,IPAddress local,int basePort,Action<string> log){IsHost=host;this.local=local;this.basePort=basePort;this.log=log;nextChannel=host?1:0x8000;}

    // Host: wait for the guest's launcher on tunnelPort (all interfaces), then exchange hello lines.
    public static Tunnel Accept(int tunnelPort,int basePort,string hello,TimeSpan timeout,WaitHandle cancel,Action<string> log) {
        var t=new Tunnel(true,IPAddress.Loopback,basePort,log);
        var server=new TcpListener(IPAddress.Any,tunnelPort);server.Start();
        try {
            log("Waiting for your buddy to connect on TCP port "+tunnelPort+"…");
            var pending=server.AcceptTcpClientAsync();
            int which=WaitHandle.WaitAny(new[]{((IAsyncResult)pending).AsyncWaitHandle,cancel},timeout);
            if(which!=0)throw new Exception(which==1?"Cancelled":"Nobody connected within "+(int)timeout.TotalMinutes+" minutes");
            var link=pending.Result;link.NoDelay=true;t.stream=link.GetStream();t.remoteName=link.Client.RemoteEndPoint.ToString();
        } finally {server.Stop();}
        t.Start(hello);return t;
    }

    // Guest: connect to the host's launcher. local is the loopback address this side's SC2 uses.
    public static Tunnel Connect(string host,int tunnelPort,int basePort,IPAddress local,string hello,Action<string> log) {
        var t=new Tunnel(false,local,basePort,log);
        var link=new TcpClient();link.NoDelay=true;
        var connect=link.ConnectAsync(host,tunnelPort);
        if(!connect.Wait(TimeSpan.FromSeconds(20)))throw new Exception("Could not reach "+host+":"+tunnelPort+" (timed out). Check the address and that the host's port is open.");
        t.stream=link.GetStream();t.remoteName=link.Client.RemoteEndPoint.ToString();
        t.Start(hello);return t;
    }

    // Any other transport: an already connected, ordered, reliable stream (the tunnel owns and closes it).
    public static Tunnel Over(bool host,Stream link,string remoteName,int basePort,IPAddress local,string hello,Action<string> log) {
        var t=new Tunnel(host,host?IPAddress.Loopback:local,basePort,log);t.stream=link;t.remoteName=remoteName;
        try{t.Start(hello);}catch{link.Dispose();throw;}
        return t;
    }

    void Start(string hello) {
        Send(Hello,0,Encoding.UTF8.GetBytes(hello));
        var first=ReadFrame();if(first==null||first.Item1!=Hello)throw new Exception("The other launcher did not answer (different program or version?)");
        RemoteHello=Encoding.UTF8.GetString(first.Item3);
        log("Tunnel connected to "+remoteName);
    }

    // Game UDP over the link's datagram path; call after both hellos said they support it, before Open.
    public bool EnableDatagrams() {
        var d=stream as IDatagramLink;if(d==null)return false;
        datagrams=d;
        d.OnDatagram=payload=>{
            Interlocked.Increment(ref DatagramsDown);Interlocked.Add(ref BytesDown,payload.Length);
            var u=udp;if(u!=null&&!closed)try{u.Send(payload,payload.Length,new IPEndPoint(local,IsHost?ServerGame:ClientGame));}catch{}
        };
        log("Game UDP packets go unreliably over Steam (no head-of-line blocking)");
        return true;
    }

    // The guest adopts the host's game ports from the hello exchange.
    public void UseBase(int port) {basePort=port;}

    // Opens the local impersonation sockets. Call after the hello exchange, before the games join.
    public void Open() {
        StartReading();
        if(udp!=null)return; // already open (campaign: one tunnel serves every mission)
        // Host impersonates the guest's client ports; guest impersonates the host's server ports.
        udp=new UdpClient(new IPEndPoint(local,IsHost?ClientGame:ServerGame));
        // Windows reports ICMP port-unreachable as an error on the next Receive; the game may not be up yet.
        try{udp.Client.IOControl(unchecked((int)0x9800000C),new byte[]{0,0,0,0},null);}catch{}
        listener=new TcpListener(local,IsHost?ClientBase:ServerBase);listener.Start();
        new Thread(UdpLoop){IsBackground=true,Name="tunnel-udp"}.Start();
        new Thread(AcceptLoop){IsBackground=true,Name="tunnel-accept"}.Start();
        log("Tunnel ports open on "+local+" ("+(IsHost?"UDP "+ClientGame+", TCP "+ClientBase:"UDP "+ServerGame+", TCP "+ServerBase)+")");
    }

    // Starts the frame reader before the game ports are open (control messages only until Open).
    public void StartReading() {
        lock(writeGate){if(reading)return;reading=true;}
        new Thread(ReadLoop){IsBackground=true,Name="tunnel-read"}.Start();
        new Thread(Heartbeat){IsBackground=true,Name="tunnel-heartbeat"}.Start();
    }
    // Heartbeat: an empty Hello frame every 5 s (the reader ignores Hello frames after the handshake). If nothing arrives
    // for 30 s the buddy's launcher is gone even when the TCP connection looks open (e.g. their StarCraft II, started by
    // the killed launcher, inherited and kept its socket; seen 2026-10-21).
    long lastHeard=DateTime.UtcNow.Ticks;
    void Heartbeat() {
        while(!closed) {
            Thread.Sleep(5000);if(closed)return;
            try{Send(Hello,0,new byte[0]);}catch{}
            if(DateTime.UtcNow.Ticks-Interlocked.Read(ref lastHeard)>TimeSpan.FromSeconds(30).Ticks) {
                log("Tunnel: nothing heard from your buddy for 30 seconds; closing the connection");closed=true;
                try{if(stream!=null)stream.Dispose();}catch{}return;
            }
        }
    }

    public void SendControl(string message) {Send(Control,0,Encoding.UTF8.GetBytes(message));}

    void Send(byte type,int channel,byte[] payload,int count=-1) {
        if(count<0)count=payload.Length;
        // One write per frame, so message transports (Steam) send one message per frame.
        var frame=new byte[7+count];frame[0]=type;frame[1]=(byte)(channel>>8);frame[2]=(byte)channel;
        frame[3]=(byte)(count>>24);frame[4]=(byte)(count>>16);frame[5]=(byte)(count>>8);frame[6]=(byte)count;
        Buffer.BlockCopy(payload,0,frame,7,count);
        lock(writeGate)stream.Write(frame,0,frame.Length);
        Interlocked.Add(ref BytesUp,count+7);
    }

    Tuple<byte,int,byte[]> ReadFrame() {
        var header=ReadExact(7);if(header==null)return null;Interlocked.Exchange(ref lastHeard,DateTime.UtcNow.Ticks);
        int channel=(header[1]<<8)|header[2],length=(header[3]<<24)|(header[4]<<16)|(header[5]<<8)|header[6];
        if(length<0||length>1<<20)throw new IOException("Bad tunnel frame");
        var payload=length==0?new byte[0]:ReadExact(length);if(payload==null)return null;
        Interlocked.Add(ref BytesDown,length+7);
        return Tuple.Create(header[0],channel,payload);
    }
    byte[] ReadExact(int n) {var b=new byte[n];int at=0;while(at<n){int r=stream.Read(b,at,n-at);if(r<=0)return null;at+=r;}return b;}

    void ReadLoop() {
        try {
            Tuple<byte,int,byte[]> f;
            while(!closed&&(f=ReadFrame())!=null) {
                switch(f.Item1) {
                    case Control:Controls.Add(Encoding.UTF8.GetString(f.Item3));break;
                    case Udp:
                        // Deliver as if sent from the impersonated port to the local game's own port.
                        if(udp==null)break;
                        udp.Send(f.Item3,f.Item3.Length,new IPEndPoint(local,IsHost?ServerGame:ClientGame));break;
                    case OpenFrame:
                        int ch=f.Item2;var target=new IPEndPoint(local,IsHost?ServerBase:ClientBase);
                        try {var c=new TcpClient();c.NoDelay=true;c.Connect(target);conns[ch]=c;new Thread(()=>Pump(ch,c)){IsBackground=true}.Start();}
                        catch(Exception e){log("Tunnel: local connect to "+target+" failed: "+e.Message);Send(Close,ch,new byte[0]);}
                        break;
                    case Data:
                        TcpClient conn;if(conns.TryGetValue(f.Item2,out conn))try{conn.GetStream().Write(f.Item3,0,f.Item3.Length);}catch{Drop(f.Item2,false);}
                        break;
                    case Close:Drop(f.Item2,false);break;
                }
            }
        } catch(Exception e){if(!closed)log("Tunnel read: "+e.Message);}
        if(!closed)log("Tunnel closed by the other side");
        closed=true;
    }

    void UdpLoop() {
        var from=new IPEndPoint(IPAddress.Any,0);
        while(!closed) {
            try {
                var d=udp.Receive(ref from);var link=datagrams;
                if(link!=null){link.SendDatagram(d,d.Length);Interlocked.Increment(ref DatagramsUp);Interlocked.Add(ref BytesUp,d.Length);}
                else Send(Udp,0,d);
            }
            catch(SocketException e) {if(e.ErrorCode==10054)continue;if(!closed)log("Tunnel UDP: "+e.Message);return;}
            catch(Exception e) {if(!closed)log("Tunnel UDP: "+e.Message);return;}
        }
    }

    void AcceptLoop() {
        try {
            while(!closed) {
                var c=listener.AcceptTcpClient();c.NoDelay=true;int ch=Interlocked.Increment(ref nextChannel)&0xFFFF;
                conns[ch]=c;Send(OpenFrame,ch,new byte[0]);new Thread(()=>Pump(ch,c)){IsBackground=true}.Start();
            }
        } catch(Exception e){if(!closed)log("Tunnel accept: "+e.Message);}
    }

    void Pump(int ch,TcpClient c) {
        var buffer=new byte[65536];
        try {var s=c.GetStream();int n;while((n=s.Read(buffer,0,buffer.Length))>0)Send(Data,ch,buffer,n);}
        catch { }
        Drop(ch,true);
    }

    void Drop(int ch,bool tell) {
        TcpClient c;if(!conns.TryRemove(ch,out c))return;
        try{c.Close();}catch{}
        if(tell&&!closed)try{Send(Close,ch,new byte[0]);}catch{}
    }

    public bool Closed{get{return closed;}}

    public void Dispose() {
        closed=true;
        try{if(listener!=null)listener.Stop();}catch{}
        try{if(udp!=null)udp.Close();}catch{}
        foreach(var ch in new List<int>(conns.Keys))Drop(ch,false);
        try{if(stream!=null)stream.Dispose();}catch{}
        log("Tunnel closed ("+BytesUp/1024+" KB sent, "+BytesDown/1024+" KB received"+(datagrams!=null?"; "+DatagramsUp+"/"+DatagramsDown+" game UDP packets sent/received unreliably":"")+")");
    }
}
