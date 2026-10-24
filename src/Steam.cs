// Steam transport for the launcher tunnel: no IP addresses, no port forwarding. Follows coop-anything
// playbook/networking-steam-spacewar.md: Spacewar (AppID 480), steam_api64.dll loaded at runtime through the flat
// C API (no SDK headers), manual callback dispatch, ISteamNetworkingMessages through Valve's relay.
//
// Host: creates a friends-only lobby tagged sc2coop=1 and waits for a lobby member's first message.
// Guest: picks a friend whose Steam status shows them in a Spacewar lobby (or a pasted lobby id, an accepted
// invite, or +connect_lobby on the command line), joins the lobby, checks the tag and talks to the lobby owner.
//
// SteamLink is the Stream the Tunnel frames ride on: channel 0 carries the byte stream as reliable messages of at
// most 60000 bytes; channel 1 carries a 1 s keep-alive and a goodbye, since ISteamNetworkingMessages has no
// disconnect event. A peer silent for 20 s counts as gone.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class SteamFriendHost {
    public ulong Friend,Lobby;public string Name;
    public override string ToString() {return Name+"  ("+Lobby+")";}
}

class SteamSession {
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool BoolFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn(IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PipeFn(int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool NextCallbackFn(int pipe,IntPtr msg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool CallResultFn(int pipe,ulong call,IntPtr buffer,int size,int expected,IntPtr failed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetterFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong IdFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool SelfBoolFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong CreateLobbyFn(IntPtr self,int type,int max);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong LobbyCallFn(IntPtr self,ulong lobby);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void LobbyVoidFn(IntPtr self,ulong lobby);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool SetLobbyDataFn(IntPtr self,ulong lobby,byte[] key,byte[] value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetLobbyDataFn(IntPtr self,ulong lobby,byte[] key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LobbyIntFn(IntPtr self,ulong lobby);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong LobbyMemberFn(IntPtr self,ulong lobby,int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool RichPresenceFn(IntPtr self,byte[] key,byte[] value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SelfVoidFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FriendCountFn(IntPtr self,int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong FriendByIndexFn(IntPtr self,int index,int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool GamePlayedFn(IntPtr self,ulong friend,IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FriendNameFn(IntPtr self,ulong friend);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr SelfPtrFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetIdFn(IntPtr identity,ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SendFn(IntPtr self,IntPtr identity,IntPtr data,uint size,int flags,int channel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReceiveFn(IntPtr self,int channel,IntPtr[] messages,int max);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool SessionFn(IntPtr self,IntPtr identity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ReleaseFn(IntPtr message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool ConfigFn(IntPtr self,int key,int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SessionInfoFn(IntPtr self,IntPtr identity,IntPtr info,IntPtr status);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);

    public const int AppId=480;
    const string Tag="sc2coop";
    // Callback ids (playbook table).
    const int GameLobbyJoinRequested=333,GameRichPresenceJoinRequested=337,LobbyEnter=504,LobbyChatUpdate=506,LobbyCreated=513,CallCompleted=703,SessionRequest=1251,SessionFailed=1252;
    // Send flags: NoNagle 1, Reliable 8. EResult OK 1, LimitExceeded 25.
    internal const int SendReliable=9,SendUnreliable=1;

    static SteamSession instance;static readonly object instanceGate=new object();
    IntPtr dll,user,matchmaking,friends,messages,utils;int pipe;
    BoolFn isRunning;InitFn initFlat;IntFn getPipe;VoidFn dispatchInit,shutdown;PipeFn runFrame,freeLast;NextCallbackFn nextCallback;CallResultFn callResult;
    IdFn getSteamId;SelfBoolFn loggedOn;CreateLobbyFn createLobby;LobbyCallFn joinLobby,getLobbyOwner;LobbyVoidFn leaveLobby,inviteDialog;
    SetLobbyDataFn setLobbyData;GetLobbyDataFn getLobbyData;LobbyIntFn memberCount;LobbyMemberFn memberByIndex;
    RichPresenceFn setRichPresence;SelfVoidFn clearRichPresence;FriendCountFn friendCount;FriendByIndexFn friendByIndex;GamePlayedFn gamePlayed;FriendNameFn friendName;SelfPtrFn personaName;
    SetIdFn setIdentity;IdFn getIdentity;SendFn send;ReceiveFn receive;SessionFn acceptSession,closeSession;ReleaseFn release;ConfigFn setConfig;SessionInfoFn sessionInfo;

    public ulong Me;public string MyName;
    readonly object apiGate=new object();
    readonly Dictionary<ulong,Tuple<int,ManualResetEvent,byte[][]>> pendingCalls=new Dictionary<ulong,Tuple<int,ManualResetEvent,byte[][]>>();
    readonly BlockingCollection<ulong> invites=new BlockingCollection<ulong>();
    readonly ConcurrentQueue<ulong> sessionRequests=new ConcurrentQueue<ulong>();
    readonly AutoResetEvent sessionRequested=new AutoResetEvent(false);
    volatile SteamLink active;
    Action<string> log;

    T F<T>(string name) where T:class {
        IntPtr p=GetProcAddress(dll,name);
        if(p==IntPtr.Zero)throw new Exception("steam_api64.dll has no "+name+". It is too old or not the expected build (see README: Steam).");
        return Marshal.GetDelegateForFunctionPointer(p,typeof(T)) as T;
    }

    // Loads and initialises Steam once per process (the client stays signed in to Spacewar until the launcher exits).
    public static SteamSession Get(Action<string> log) {
        lock(instanceGate) {
            if(instance==null){var s=new SteamSession();s.log=log;s.Init();instance=s;}
            instance.log=log;return instance;
        }
    }

    void Init() {
        if(!Environment.Is64BitProcess)throw new Exception("The Steam option needs the 64-bit launcher");
        string path=Path.Combine(Program.Base,"steam_api64.dll");
        if(!File.Exists(path))throw new Exception("Steam connections need steam_api64.dll next to SC2Coop.exe, and it is missing. See README (Steam) for where to get it, or use Host/Join by address.");
        dll=LoadLibrary(path);
        if(dll==IntPtr.Zero)throw new Exception("Could not load steam_api64.dll (Windows error "+Marshal.GetLastWin32Error()+")");
        isRunning=F<BoolFn>("SteamAPI_IsSteamRunning");initFlat=F<InitFn>("SteamAPI_InitFlat");getPipe=F<IntFn>("SteamAPI_GetHSteamPipe");shutdown=F<VoidFn>("SteamAPI_Shutdown");
        dispatchInit=F<VoidFn>("SteamAPI_ManualDispatch_Init");runFrame=F<PipeFn>("SteamAPI_ManualDispatch_RunFrame");nextCallback=F<NextCallbackFn>("SteamAPI_ManualDispatch_GetNextCallback");
        freeLast=F<PipeFn>("SteamAPI_ManualDispatch_FreeLastCallback");callResult=F<CallResultFn>("SteamAPI_ManualDispatch_GetAPICallResult");
        getSteamId=F<IdFn>("SteamAPI_ISteamUser_GetSteamID");loggedOn=F<SelfBoolFn>("SteamAPI_ISteamUser_BLoggedOn");
        createLobby=F<CreateLobbyFn>("SteamAPI_ISteamMatchmaking_CreateLobby");joinLobby=F<LobbyCallFn>("SteamAPI_ISteamMatchmaking_JoinLobby");leaveLobby=F<LobbyVoidFn>("SteamAPI_ISteamMatchmaking_LeaveLobby");
        setLobbyData=F<SetLobbyDataFn>("SteamAPI_ISteamMatchmaking_SetLobbyData");getLobbyData=F<GetLobbyDataFn>("SteamAPI_ISteamMatchmaking_GetLobbyData");getLobbyOwner=F<LobbyCallFn>("SteamAPI_ISteamMatchmaking_GetLobbyOwner");
        memberCount=F<LobbyIntFn>("SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");memberByIndex=F<LobbyMemberFn>("SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex");
        setRichPresence=F<RichPresenceFn>("SteamAPI_ISteamFriends_SetRichPresence");clearRichPresence=F<SelfVoidFn>("SteamAPI_ISteamFriends_ClearRichPresence");
        inviteDialog=F<LobbyVoidFn>("SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog");friendCount=F<FriendCountFn>("SteamAPI_ISteamFriends_GetFriendCount");
        friendByIndex=F<FriendByIndexFn>("SteamAPI_ISteamFriends_GetFriendByIndex");gamePlayed=F<GamePlayedFn>("SteamAPI_ISteamFriends_GetFriendGamePlayed");
        friendName=F<FriendNameFn>("SteamAPI_ISteamFriends_GetFriendPersonaName");personaName=F<SelfPtrFn>("SteamAPI_ISteamFriends_GetPersonaName");
        setIdentity=F<SetIdFn>("SteamAPI_SteamNetworkingIdentity_SetSteamID64");getIdentity=F<IdFn>("SteamAPI_SteamNetworkingIdentity_GetSteamID64");
        send=F<SendFn>("SteamAPI_ISteamNetworkingMessages_SendMessageToUser");receive=F<ReceiveFn>("SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel");
        acceptSession=F<SessionFn>("SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser");closeSession=F<SessionFn>("SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser");
        release=F<ReleaseFn>("SteamAPI_SteamNetworkingMessage_t_Release");
        // Optional: Steam's own ping estimate for the latency log.
        IntPtr infoFn=GetProcAddress(dll,"SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo");
        if(infoFn!=IntPtr.Zero)sessionInfo=(SessionInfoFn)Marshal.GetDelegateForFunctionPointer(infoFn,typeof(SessionInfoFn));setConfig=F<ConfigFn>("SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32");

        if(!isRunning())throw new Exception("Steam isn't running. Start Steam and sign in, then try again.");
        // Spacewar: Steam reads the app id from the environment or from steam_appid.txt in the working directory.
        Environment.SetEnvironmentVariable("SteamAppId",AppId.ToString());Environment.SetEnvironmentVariable("SteamGameId",AppId.ToString());
        try{string id=Path.Combine(Program.Base,"steam_appid.txt");if(!File.Exists(id))File.WriteAllText(id,AppId.ToString());}catch{}
        IntPtr error=Marshal.AllocHGlobal(1024);
        try {
            Marshal.WriteByte(error,0,0);int r=initFlat(error);
            if(r!=0) {
                string detail=Marshal.PtrToStringAnsi(error);
                throw new Exception(r==2?"Steam isn't running. Start Steam and sign in, then try again.":r==3?"Steam needs an update for this launcher (version mismatch). Restart Steam and try again. "+detail:"Steam could not start Spacewar (AppID 480): "+detail);
            }
        } finally {Marshal.FreeHGlobal(error);}
        dispatchInit();pipe=getPipe();
        user=F<GetterFn>("SteamAPI_SteamUser_v023")();matchmaking=F<GetterFn>("SteamAPI_SteamMatchmaking_v009")();friends=F<GetterFn>("SteamAPI_SteamFriends_v018")();
        messages=F<GetterFn>("SteamAPI_SteamNetworkingMessages_SteamAPI_v002")();utils=F<GetterFn>("SteamAPI_SteamNetworkingUtils_SteamAPI_v004")();
        if(user==IntPtr.Zero||matchmaking==IntPtr.Zero||friends==IntPtr.Zero||messages==IntPtr.Zero)throw new Exception("Steam interfaces unavailable (steam_api64.dll version mismatch?)");
        if(!loggedOn(user))throw new Exception("Steam is running but not signed in (or offline). Sign in to Steam, then try again.");
        Me=getSteamId(user);MyName=Utf8(personaName(friends));
        // SendBufferSize 9, RecvBufferSize 47, RecvMaxMessageSize 49 (steamnetworkingtypes.h). Best effort.
        if(utils!=IntPtr.Zero)foreach(int key in new[]{9,47,49})try{setConfig(utils,key,32*1024*1024);}catch{}
        AppDomain.CurrentDomain.ProcessExit+=(s,e)=>{try{var a=active;if(a!=null)a.Dispose();shutdown();}catch{}};
        // The pump polls with Sleep(1); at Windows' default 15.6 ms timer that adds ~8 ms per direction on average.
        try{timeBeginPeriod(1);}catch{}
        new Thread(Pump){IsBackground=true,Name="steam-pump"}.Start();
        log("Steam ready: signed in as "+MyName+" (Spacewar, AppID 480)");
    }

    static string Utf8(IntPtr p) {
        if(p==IntPtr.Zero)return "";int n=0;while(Marshal.ReadByte(p,n)!=0)n++;
        var b=new byte[n];Marshal.Copy(p,b,0,n);return Encoding.UTF8.GetString(b);
    }
    static byte[] Z(string s) {return Encoding.UTF8.GetBytes(s+"\0");}

    // Callback and message pump: one thread drives manual dispatch and drains both message channels.
    void Pump() {
        IntPtr msg=Marshal.AllocHGlobal(32);var batch=new IntPtr[64];
        while(true) {
            try {
                lock(apiGate) {
                    runFrame(pipe);
                    while(nextCallback(pipe,msg)) {
                        try {
                            int id=Marshal.ReadInt32(msg,4);IntPtr param=Marshal.ReadIntPtr(msg,8);int size=Marshal.ReadInt32(msg,16);
                            if(id==CallCompleted) {
                                ulong call=(ulong)Marshal.ReadInt64(param,0);int expected=Marshal.ReadInt32(param,8);int resultSize=Marshal.ReadInt32(param,12);
                                Tuple<int,ManualResetEvent,byte[][]> waiter;
                                if(pendingCalls.TryGetValue(call,out waiter)) {
                                    IntPtr buffer=Marshal.AllocHGlobal(Math.Max(resultSize,1)),failed=Marshal.AllocHGlobal(4);
                                    try {
                                        Marshal.WriteInt32(failed,0);
                                        if(callResult(pipe,call,buffer,resultSize,expected,failed)&&Marshal.ReadByte(failed)==0){var b=new byte[resultSize];Marshal.Copy(buffer,b,0,resultSize);waiter.Item3[0]=b;}
                                    } finally {Marshal.FreeHGlobal(buffer);Marshal.FreeHGlobal(failed);}
                                    pendingCalls.Remove(call);waiter.Item2.Set();
                                }
                            } else Handle(id,param,size);
                        } finally {freeLast(pipe);}
                    }
                }
                for(int channel=0;channel<3;channel++) {
                    int n;lock(apiGate)n=receive(messages,channel,batch,batch.Length);
                    for(int i=0;i<n;i++) {
                        IntPtr m=batch[i];
                        try {
                            // SteamNetworkingMessage_t (x64): data 0, size 8, sender identity 16.
                            IntPtr data=Marshal.ReadIntPtr(m,0);int size=Marshal.ReadInt32(m,8);ulong from=getIdentity(m+16);
                            var payload=new byte[size];if(size>0)Marshal.Copy(data,payload,0,size);
                            var link=active;if(link!=null&&link.Peer==from)link.Deliver(channel,payload);
                        } finally {release(m);}
                    }
                }
            } catch(Exception e) {log("Steam pump: "+e.Message);Thread.Sleep(100);}
            Thread.Sleep(1);
        }
    }

    void Handle(int id,IntPtr p,int size) {
        switch(id) {
            case GameLobbyJoinRequested:invites.Add((ulong)Marshal.ReadInt64(p,0));break;
            case GameRichPresenceJoinRequested:{ulong lobby;if(ParseConnect(Marshal.PtrToStringAnsi(p+8),out lobby))invites.Add(lobby);break;}
            case LobbyChatUpdate: {
                ulong lobby=(ulong)Marshal.ReadInt64(p,0),changed=(ulong)Marshal.ReadInt64(p,8);uint state=(uint)Marshal.ReadInt32(p,24);
                var link=active;if(link!=null&&link.Lobby==lobby&&link.Peer==changed&&(state&0x1E)!=0)link.Lost("your buddy left the Steam lobby");
                break;
            }
            case SessionRequest: {
                // The guest accepts only its lobby owner; the host decides in Host() (lobby members only).
                ulong who=getIdentity(p);var link=active;
                if(link!=null&&link.Peer==who)acceptSession(messages,link.Identity);
                else if(link==null){sessionRequests.Enqueue(who);sessionRequested.Set();}
                break;
            }
            case SessionFailed:{ulong who=getIdentity(p);var link=active;if(link!=null&&link.Peer==who)link.Lost("Steam reported the connection failed");break;}
        }
    }

    public static bool ParseConnect(string text,out ulong lobby) {
        lobby=0;if(string.IsNullOrEmpty(text))return false;
        var parts=text.Split(new[]{' ','\t'},StringSplitOptions.RemoveEmptyEntries);
        for(int i=0;i+1<parts.Length;i++)if(parts[i]=="+connect_lobby"||parts[i]=="-steamjoin")return ulong.TryParse(parts[i+1],out lobby);
        return false;
    }

    // Issues an async Steam call and waits for its result. The waiter is registered under the same lock as the call,
    // so the pump cannot dispatch the result before anyone listens for it.
    byte[] Await(Func<ulong> start,int expected,TimeSpan timeout,WaitHandle cancel,string what) {
        var waiter=Tuple.Create(expected,new ManualResetEvent(false),new byte[1][]);ulong call;
        lock(apiGate){call=start();if(call!=0)pendingCalls[call]=waiter;}
        if(call==0)throw new Exception(what+" failed to start (Steam returned no call handle)");
        int which=WaitHandle.WaitAny(new[]{waiter.Item2,cancel},timeout);
        if(which!=0){lock(apiGate)pendingCalls.Remove(call);throw new Exception(which==1?"Cancelled":what+" timed out");}
        if(waiter.Item3[0]==null)throw new Exception(what+" failed");
        return waiter.Item3[0];
    }

    string LobbyData(ulong lobby,string key) {lock(apiGate)return Utf8(getLobbyData(matchmaking,lobby,Z(key)));}

    // Host: friends-only lobby, then the first lobby member that messages us becomes the peer.
    public SteamLink Host(string mapSha,TimeSpan timeout,WaitHandle cancel,bool copyId) {
        if(active!=null)throw new Exception("A Steam session is already active");
        ulong stale;while(sessionRequests.TryDequeue(out stale)){}
        var created=Await(()=>createLobby(matchmaking,1,2),LobbyCreated,TimeSpan.FromSeconds(30),cancel,"Creating the Steam lobby");
        int result=BitConverter.ToInt32(created,0);ulong lobby=BitConverter.ToUInt64(created,8);
        if(result!=1||lobby==0)throw new Exception("Steam could not create a lobby (result "+result+")");
        bool done=false;
        try {
            lock(apiGate) {
                setLobbyData(matchmaking,lobby,Z(Tag),Z("1"));setLobbyData(matchmaking,lobby,Z("map"),Z(mapSha.Substring(0,16)));setLobbyData(matchmaking,lobby,Z("host"),Z(MyName));
                setRichPresence(friends,Z("connect"),Z("+connect_lobby "+lobby));setRichPresence(friends,Z("status"),Z("Hosting StarCraft II co-op"));
                // No automatic ActivateGameOverlayInviteDialog: it opens a Steam window over the game that the host had to close
                // first (2026-10-17 owner report). Friends find the host through Find, the lobby id, or Steam's own Join Game.
            }
            log("Steam lobby "+lobby+" is open (friends only). Your friend chooses \"Join a Steam friend\" and picks "+MyName+". You can also paste them the lobby id.");
            if(copyId)try{var t=new Thread(()=>System.Windows.Forms.Clipboard.SetText(lobby.ToString()));t.SetApartmentState(ApartmentState.STA);t.Start();t.Join();log("Lobby id copied to the clipboard (your friend can paste it instead of picking you).");}catch{}
            DateTime deadline=DateTime.UtcNow+timeout;ulong who;
            while(true) {
                while(sessionRequests.TryDequeue(out who)) {
                    bool member=false;
                    lock(apiGate){int n=memberCount(matchmaking,lobby);for(int i=0;i<n;i++)if(memberByIndex(matchmaking,lobby,i)==who&&who!=Me)member=true;}
                    if(!member){log("Ignored a Steam connection from "+who+" (not in this lobby)");continue;}
                    var link=new SteamLink(this,who,lobby,true);
                    lock(apiGate){active=link;acceptSession(messages,link.Identity);}
                    log("Steam friend "+FriendName(who)+" connected");done=true;return link;
                }
                var left=deadline-DateTime.UtcNow;if(left<=TimeSpan.Zero)throw new Exception("Nobody joined the Steam lobby within "+(int)timeout.TotalMinutes+" minutes");
                if(WaitHandle.WaitAny(new WaitHandle[]{sessionRequested,cancel},left<TimeSpan.FromSeconds(1)?left:TimeSpan.FromSeconds(1))==1)throw new Exception("Cancelled");
            }
        } finally {if(!done)LeaveLobby(lobby);}
    }

    // Guest: lobby 0 waits for a Steam invite (or Join Game) instead.
    public SteamLink Join(ulong lobby,TimeSpan timeout,WaitHandle cancel) {
        if(active!=null)throw new Exception("A Steam session is already active");
        if(lobby==0) {
            ulong arg;if(ParseConnect(string.Join(" ",Environment.GetCommandLineArgs()),out arg))lobby=arg;
            else {
                log("Waiting for a Steam invite from your friend (or pick them from the list and press Start)…");
                DateTime deadline=DateTime.UtcNow+timeout;
                while(!invites.TryTake(out lobby,250)) {
                    if(cancel.WaitOne(0))throw new Exception("Cancelled");
                    if(DateTime.UtcNow>deadline)throw new Exception("No Steam invite arrived");
                }
            }
        }
        var entered=Await(()=>joinLobby(matchmaking,lobby),LobbyEnter,TimeSpan.FromSeconds(30),cancel,"Joining the Steam lobby");
        uint response=BitConverter.ToUInt32(entered,16);
        if(response!=1)throw new Exception("Could not join the Steam lobby: "+(response==2?"it no longer exists":response==3?"not allowed (friends only?)":response==4?"it is full":"Steam response "+response));
        bool done=false;
        try {
            if(LobbyData(lobby,Tag)!="1")throw new Exception("That Steam lobby isn't a StarCraft II co-op lobby (your friend may be playing the real Spacewar)");
            ulong owner;lock(apiGate)owner=getLobbyOwner(matchmaking,lobby);
            if(owner==0||owner==Me)throw new Exception("The Steam lobby has no host");
            var link=new SteamLink(this,owner,lobby,false);
            lock(apiGate){active=link;setRichPresence(friends,Z("status"),Z("Playing StarCraft II co-op"));}
            log("Joined "+FriendName(owner)+"'s Steam lobby; connecting through Steam…");done=true;return link;
        } finally {if(!done)LeaveLobby(lobby);}
    }

    // Friends whose Steam status shows a Spacewar lobby. The sc2coop tag is checked after joining.
    public List<SteamFriendHost> HostingFriends() {
        var list=new List<SteamFriendHost>();IntPtr info=Marshal.AllocHGlobal(64);
        try {
            lock(apiGate) {
                int n=friendCount(friends,4);
                for(int i=0;i<n;i++) {
                    ulong f=friendByIndex(friends,i,4);
                    if(!gamePlayed(friends,f,info))continue;
                    // FriendGameInfo_t: m_gameID 0 (app id in the low 24 bits), m_steamIDLobby 16.
                    ulong game=(ulong)Marshal.ReadInt64(info,0),lobby=(ulong)Marshal.ReadInt64(info,16);
                    if((game&0xFFFFFF)==AppId&&lobby!=0)list.Add(new SteamFriendHost{Friend=f,Lobby=lobby,Name=Utf8(friendName(friends,f))});
                }
            }
        } finally {Marshal.FreeHGlobal(info);}
        return list;
    }

    string FriendName(ulong id) {lock(apiGate){var n=Utf8(friendName(friends,id));return n==""?id.ToString():n;}}

    void LeaveLobby(ulong lobby) {lock(apiGate){try{leaveLobby(matchmaking,lobby);clearRichPresence(friends);}catch{}}}

    internal int Send(IntPtr identity,byte[] data,int offset,int count,int channel,int flags=SendReliable) {
        IntPtr buffer=Marshal.AllocHGlobal(Math.Max(count,1));
        try {Marshal.Copy(data,offset,buffer,count);return send(messages,identity,buffer,(uint)count,flags,channel);}
        finally {Marshal.FreeHGlobal(buffer);}
    }
    internal void Log(string s) {log(s);}
    // Steam's view of the session (SteamNetConnectionRealTimeStatus_t: state 0, ping 4, quality local 8 / remote 12,
    // pending reliable bytes 40). Null when the export is missing or the session isn't connected.
    internal string SteamEstimate(IntPtr identity) {
        if(sessionInfo==null)return null;
        IntPtr status=Marshal.AllocHGlobal(256);
        try {
            for(int i=0;i<256;i+=4)Marshal.WriteInt32(status,i,0);
            int state;lock(apiGate)state=sessionInfo(messages,identity,IntPtr.Zero,status);
            if(state!=3)return "Steam session state "+state;
            int ping=Marshal.ReadInt32(status,4);float local=BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(status,8)),0),remote=BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(status,12)),0);
            return "Steam estimate "+ping+" ms, delivery quality "+(local*100).ToString("0")+"%/"+(remote*100).ToString("0")+"%, unacked "+Marshal.ReadInt32(status,40)+" B";
        } catch(Exception e){return "Steam estimate unavailable ("+e.Message+")";}
        finally{Marshal.FreeHGlobal(status);}
    }
    internal void SetIdentity(IntPtr identity,ulong id) {setIdentity(identity,id);}
    internal void Finish(SteamLink link) {
        lock(apiGate) {
            try{closeSession(messages,link.Identity);}catch{}
            if(active==link)active=null;
        }
        LeaveLobby(link.Lobby);
    }
}

// Channels: 0 reliable tunnel frames, 1 control (keep-alive, ping, goodbye), 2 unreliable game UDP datagrams.
class SteamLink : Stream, IDatagramLink {
    const int Piece=60000;
    public readonly ulong Peer,Lobby;public readonly IntPtr Identity;
    readonly SteamSession steam;readonly bool host;
    readonly BlockingCollection<byte[]> inbox=new BlockingCollection<byte[]>();
    byte[] current;int at;volatile bool closed;long lastHeard;
    readonly object sendGate=new object();
    Thread keepAlive;
    // Launcher round trip over the Steam link (channel 1 ping/pong), reported every 15 s and at the end.
    readonly List<double> rtt=new List<double>(),all=new List<double>();

    internal SteamLink(SteamSession steam,ulong peer,ulong lobby,bool host) {
        this.steam=steam;Peer=peer;Lobby=lobby;this.host=host;
        Identity=Marshal.AllocHGlobal(160);for(int i=0;i<160;i++)Marshal.WriteByte(Identity,i,0);steam.SetIdentity(Identity,peer);
        lastHeard=DateTime.UtcNow.Ticks;
        keepAlive=new Thread(KeepAlive){IsBackground=true,Name="steam-keepalive"};keepAlive.Start();
    }

    Action<byte[]> onDatagram;
    public Action<byte[]> OnDatagram {set{onDatagram=value;}}
    public void SendDatagram(byte[] data,int count) {
        // Unreliable + NoNagle; a full send buffer or other failure just drops the packet, like UDP would.
        if(!closed)try{steam.Send(Identity,data,0,count,2,SteamSession.SendUnreliable);}catch{}
    }

    internal void Deliver(int channel,byte[] payload) {
        Interlocked.Exchange(ref lastHeard,DateTime.UtcNow.Ticks);
        if(channel==2){var d=onDatagram;if(d!=null&&!closed)d(payload);return;}
        if(channel==0){if(payload.Length>0&&!closed)try{inbox.Add(payload);}catch(InvalidOperationException){}}
        else if(payload.Length>0&&payload[0]==(byte)'g')Lost("your buddy closed the connection");
        else if(payload.Length==9&&payload[0]==(byte)'p'&&!closed){payload[0]=(byte)'q';try{Raw(payload,0,9,1);}catch{}}
        else if(payload.Length==9&&payload[0]==(byte)'q') {
            double ms=(System.Diagnostics.Stopwatch.GetTimestamp()-BitConverter.ToInt64(payload,1))*1000.0/System.Diagnostics.Stopwatch.Frequency;
            lock(rtt){rtt.Add(ms);all.Add(ms);}
        }
    }
    internal void Lost(string why) {
        if(closed)return;closed=true;Reason=why;try{inbox.CompleteAdding();}catch{}
        if(why!="closed")steam.Log("Steam link closed: "+why);
    }
    public string Reason;

    void KeepAlive() {
        var ping=new byte[9];ping[0]=(byte)'p';int second=0;
        while(!closed) {
            Thread.Sleep(1000);if(closed)break;
            Buffer.BlockCopy(BitConverter.GetBytes(System.Diagnostics.Stopwatch.GetTimestamp()),0,ping,1,8);
            try{Raw(ping,0,9,1);}catch{}
            if(++second%15==0)Report(false);
            if(DateTime.UtcNow.Ticks-Interlocked.Read(ref lastHeard)>TimeSpan.FromSeconds(20).Ticks)Lost("nothing heard from your buddy for 20 seconds");
        }
    }

    static string Stats(List<double> l) {
        var s=l.OrderBy(x=>x).ToList();
        return "avg "+s.Average().ToString("0")+" ms, median "+s[s.Count/2].ToString("0")+", min "+s[0].ToString("0")+", max "+s[s.Count-1].ToString("0")+" ("+s.Count+" samples)";
    }
    void Report(bool final) {
        string text;
        lock(rtt) {
            var l=final?all:rtt;
            if(l.Count==0){if(final)return;text="no replies yet (older launcher on the other side?)";}
            else text=Stats(l);
            if(!final)rtt.Clear();
        }
        string estimate=final?null:steam.SteamEstimate(Identity);
        steam.Log((final?"Latency over the whole session: round trip ":"Latency: round trip ")+text+(estimate!=null?"; "+estimate:""));
    }

    void Raw(byte[] data,int offset,int count,int channel) {
        lock(sendGate) {
            for(int tries=0;;tries++) {
                int r=steam.Send(Identity,data,offset,count,channel);
                if(r==1)return;
                // LimitExceeded: the send buffer is full; wait a little, as the playbook recommends.
                if(r==25&&tries<2000&&!closed){Thread.Sleep(5);continue;}
                throw new IOException("Steam send failed (result "+r+")");
            }
        }
    }

    public override void Write(byte[] buffer,int offset,int count) {
        if(closed)throw new IOException("Steam link closed"+(Reason!=null?": "+Reason:""));
        while(count>0){int n=Math.Min(count,Piece);Raw(buffer,offset,n,0);offset+=n;count-=n;}
    }

    public override int Read(byte[] buffer,int offset,int count) {
        if(current==null||at>=current.Length) {
            byte[] next;
            try{if(!inbox.TryTake(out next,Timeout.Infinite))return 0;}catch(InvalidOperationException){return 0;}
            current=next;at=0;
        }
        int n=Math.Min(count,current.Length-at);Buffer.BlockCopy(current,at,buffer,offset,n);at+=n;return n;
    }

    protected override void Dispose(bool disposing) {
        if(Identity!=IntPtr.Zero&&!disposedOnce) {
            disposedOnce=true;
            Report(true);
            if(!closed)try{Raw(new[]{(byte)'g'},0,1,1);Thread.Sleep(100);}catch{}
            Lost("closed");steam.Finish(this);
        }
        base.Dispose(disposing);
    }
    bool disposedOnce;

    public override bool CanRead{get{return true;}} public override bool CanWrite{get{return true;}} public override bool CanSeek{get{return false;}}
    public override long Length{get{throw new NotSupportedException();}} public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
    public override void Flush() { }
    public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
    public override void SetLength(long v){throw new NotSupportedException();}
}
