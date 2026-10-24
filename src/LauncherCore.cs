// Launcher actions and saved choices, independent of the window. LauncherWindow.cs only displays these and calls them;
// nothing here touches WinForms controls, so the window can change without touching how a game is started.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

class LaunchMode {
    public string Id,Title,Detail;
    public bool Address,Friend;
    public LaunchMode(string id,string title,string detail){Id=id;Title=title;Detail=detail;Address=id=="join";Friend=id=="steam-join";}
}

enum LaunchTone {Idle,Busy,Good,Error}

class LauncherController {
    // Order and ids as in last-mode.txt (owner request 2026-10-19: remember the last mode and address).
    public static readonly LaunchMode[] Modes={
        new LaunchMode("host","Host by address","You start the game; your buddy enters your address"),
        new LaunchMode("join","Join by address","Enter the host's address"),
        new LaunchMode("steam-host","Host via Steam","No address needed; your Steam friend joins you"),
        new LaunchMode("steam-join","Join a Steam friend","Pick a friend who is hosting, or wait for an invite"),
        new LaunchMode("local","Local test","Two game windows on this PC")};
    public static LaunchMode Mode(string id){return Modes.FirstOrDefault(m=>m.Id==id)??Modes[0];}

    public string Config{get{return Path.Combine(Program.Base,"trial.json");}}
    string CampaignFolderFile{get{return Path.Combine(Program.Base,"local","campaign-folder.txt");}}
    string ModeFile{get{return Path.Combine(Program.Base,"local","last-mode.txt");}}
    // Also kept per Windows user, so a fresh unzip of a newer launcher still remembers them.
    static string UserFile(string name){return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SC2Coop",name);}

    // Saved choices (filled by LoadChoices).
    public string ModeId="host",Address="",CampaignDir="";public bool Campaign;public string PlayerName="";public int PlayerColor=-1;
    // Separate-army choices (SeparateOptions ids), shown for campaigns with separate-army missions; the host's apply.
    public string SeparateUnits=SeparateOptions.UnitChoices[0],SeparateMoney=SeparateOptions.MoneyChoices[0],GameSpeed="faster";

    // Status for the window: (text, tone), and every log line. Raised on worker threads.
    public event Action<string,LaunchTone> StatusChanged;
    public event Action<string> LogLine;
    public event Action<string> LatencyChanged;
    public event Action<bool> RunningChanged;

    Session session;ManualResetEvent campaignStop;volatile bool running;
    public bool Running{get{return running;}}

    public LauncherController() {
        Program.UiLog=OnLog;
    }

    public void LoadChoices() {
        try {
            string f=File.Exists(CampaignFolderFile)?CampaignFolderFile:File.Exists(UserFile("campaign-folder.txt"))?UserFile("campaign-folder.txt"):null;
            if(f!=null)CampaignDir=File.ReadAllText(f).Trim();
        } catch { }
        try {
            // Line 1 mode id, line 2 address.
            string file=File.Exists(ModeFile)?ModeFile:File.Exists(UserFile("last-mode.txt"))?UserFile("last-mode.txt"):null;
            if(file!=null){var lines=File.ReadAllLines(file);if(lines.Length>0&&Modes.Any(m=>m.Id==lines[0].Trim()))ModeId=lines[0].Trim();if(lines.Length>1)Address=lines[1].Trim();}
        } catch { }
        Campaign=CampaignDir!="";
        Program.LoadProfile();PlayerName=Program.PlayerName??"";PlayerColor=Program.PlayerColor;SeparateUnits=Program.SeparateUnits;SeparateMoney=Program.SeparateMoney;GameSpeed=Program.GameSpeed;
    }

    void SaveChoices(string dir) {
        string text=ModeId+Environment.NewLine+Address+Environment.NewLine;
        foreach(var f in new[]{ModeFile,UserFile("last-mode.txt")})try{Directory.CreateDirectory(Path.GetDirectoryName(f));File.WriteAllText(f,text);}catch{}
        Program.PlayerName=PlayerName.Trim();Program.PlayerColor=PlayerColor;Program.SeparateUnits=SeparateUnits;Program.SeparateMoney=SeparateMoney;Program.GameSpeed=Program.CleanSpeed(GameSpeed);Program.SaveProfile();
        if(dir!=null)foreach(var f in new[]{CampaignFolderFile,UserFile("campaign-folder.txt")})try{Directory.CreateDirectory(Path.GetDirectoryName(f));File.WriteAllText(f,dir);}catch{}
    }

    // Prepared and valid for this launcher version? text: what to show.
    public bool Ready(out string text) {
        try {var st=Program.Load(Config);Program.Validate(st);text="Ready. Prepared from "+st.Install;return true;}
        catch(Exception e){text=File.Exists(Config)?"Prepare again: "+e.GetBaseException().Message:"Not prepared yet. Click Prepare and select your StarCraft II folder.";return false;}
    }

    // An install folder if it looks like one (the game's launcher exe and its Versions folder).
    static bool LooksLikeInstall(string p){return !string.IsNullOrEmpty(p)&&Directory.Exists(Path.Combine(p,"Versions"))&&File.Exists(Path.Combine(p,"StarCraft II.exe"));}
    // Startup: the folder to prepare from by itself when the launcher isn't prepared (first run, or after an update changed the
    // mission map): the folder it was prepared from before, else GuessInstall. Null: prepared, or nothing found (the player
    // uses Prepare as before).
    public string AutoPrepareFolder() {
        string text;if(Ready(out text))return null;
        try{var st=Program.Load(Config);if(LooksLikeInstall(st.Install))return st.Install;}catch{}
        string guess=GuessInstall();return LooksLikeInstall(guess)?guess:null;
    }
    public static string GuessInstall() {
        // The game writes the exe it last ran to Documents\StarCraft II\ExecuteInfo.txt ("executable = ...\Versions\BaseNNNNN\SC2_x64.exe").
        try {
            string info=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"StarCraft II","ExecuteInfo.txt");
            if(File.Exists(info))foreach(var line in File.ReadAllLines(info)) {
                int eq=line.IndexOf('=');if(eq<0||!line.Substring(0,eq).Trim().Equals("executable",StringComparison.OrdinalIgnoreCase))continue;
                string exe=line.Substring(eq+1).Trim();int v=exe.IndexOf(@"\Versions\",StringComparison.OrdinalIgnoreCase);
                if(v>0&&LooksLikeInstall(exe.Substring(0,v)))return exe.Substring(0,v);
            }
        } catch { }
        foreach(var key in new[]{@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\StarCraft II",@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\StarCraft II"})
            try {using(var k=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key)){var v=k==null?null:k.GetValue("InstallLocation") as string;if(!string.IsNullOrEmpty(v)&&Directory.Exists(v))return v;}} catch { }
        foreach(var p in new[]{@"C:\Program Files (x86)\StarCraft II",@"C:\Program Files\StarCraft II"})if(Directory.Exists(p))return p;
        return null;
    }

    // Reads the player's own install (never changed) and builds the map into this package's local folder.
    public async Task Prepare(string folder) {
        SetRunning(true);Status("Preparing… (reading your StarCraft II)",LaunchTone.Busy);
        try {await Task.Run(()=>TrialPreparation.PrepareProduct(folder));Program.Log("Prepared. Choose how to play, then Start.");Status("Prepared",LaunchTone.Good);}
        catch(Exception e){Program.Log("ERROR: "+e.GetBaseException().Message);}
        finally{SetRunning(false);}
    }

    public Task<List<SteamFriendHost>> FindFriends() {
        return Task.Run(()=>{
            var list=SteamSession.Get(Program.Log).HostingFriends();
            Program.Log(list.Count==0?"No Steam friend is hosting right now. Ask your friend to choose \"Host via Steam\" and press Start, then Find again (or leave the box empty and press Start to wait for an invite).":"Found "+list.Count+" Steam friend(s) hosting. Pick one and press Start.");
            return list;
        });
    }

    // What the friend box means: a picked friend's lobby, a pasted lobby id or "Name (lobby)", or "-" = wait for an invite.
    public static string SteamTarget(object picked,string text) {
        var host=picked as SteamFriendHost;
        if(host!=null&&text==host.ToString())return host.Lobby.ToString();
        text=(text??"").Trim();int open=text.LastIndexOf('('),close=text.LastIndexOf(')');
        if(open>=0&&close>open)text=text.Substring(open+1,close-open-1).Trim();
        return text==""?"-":text;
    }

    // Steam starts the launcher with +connect_lobby <id> when a friend accepts an invite or clicks Join Game.
    public static bool InviteFromCommandLine(out ulong lobby) {return SteamSession.ParseConnect(string.Join(" ",Environment.GetCommandLineArgs()),out lobby);}

    // Starts the chosen mode; returns when the game (or campaign) has ended. Errors are logged, not thrown.
    public async Task Start(string friendTarget) {
        try {
            var settings=Program.Load(Config);string m=ModeId;
            string address=m=="join"?Address.Trim():m=="steam-join"?friendTarget:"127.0.0.1";
            string dir=Campaign?CampaignDir.Trim():null;
            if(Campaign&&!Directory.Exists(dir))throw new Exception("Choose the campaign folder (Browse)");
            SaveChoices(dir);
            SetRunning(true);Status("Starting…",LaunchTone.Busy);Latency(null);
            if(Campaign) {
                campaignStop=new ManualResetEvent(false);var stopEvent=campaignStop;
                await Task.Run(()=>new CampaignRun(settings,m,address,dir,6500,stopEvent).Run());
            } else {
                session=new Session();var s=session;
                s.Progress=stage=>{if(stage=="joining")Status("Loading the mission…",LaunchTone.Busy);else if(stage=="ready")Status("In game",LaunchTone.Good);else if(stage=="ended")Status("Game over",LaunchTone.Idle);};
                await Task.Run(()=>s.Start(settings,m,address,6500,true,0));
            }
            if(lastTone!=LaunchTone.Error)Status("Stopped",LaunchTone.Idle);
        } catch(Exception error){Program.Log("ERROR: "+error.GetBaseException().Message);}
        finally{session=null;campaignStop=null;SetRunning(false);Latency(null);}
    }

    public void Stop() {var s=session;if(s!=null)s.Stop.Set();var c=campaignStop;if(c!=null)c.Set();if(running)Status("Stopping…",LaunchTone.Busy);}

    // Closing the window while a game runs stops it first; returns true when closing must wait.
    public bool StopForClose() {
        if(!running||(session==null&&campaignStop==null))return false;
        Stop();Program.Log("Stopping; close the launcher after the game has shut down.");return true;
    }

    // Status from the log: the same lines the log shows, so the status can't disagree with it.
    LaunchTone lastTone=LaunchTone.Idle;
    static readonly Regex LatencyLine=new Regex(@"^Latency: round trip avg (\d+) ms");
    void OnLog(string line) {
        var h=LogLine;if(h!=null)h(line);
        string s=line.Length>9&&line[2]==':'&&line[5]==':'?line.Substring(9):line;
        if(s.StartsWith("ERROR: ")){Status(s.Substring(7),LaunchTone.Error);return;}
        var lat=LatencyLine.Match(s);if(lat.Success){Latency(lat.Groups[1].Value+" ms round trip");return;}
        if(s.StartsWith("Waiting for your buddy")||s.StartsWith("Waiting for a Steam invite")||s.StartsWith("Steam lobby ")||s.StartsWith("Tell your buddy")||s.StartsWith("Your buddy needs to reach"))Status("Waiting for your buddy…",LaunchTone.Busy);
        else if(s.StartsWith("Tunnel connected")||(s.StartsWith("Steam friend ")&&s.EndsWith(" connected"))||s.StartsWith("Joined ")&&s.Contains("Steam lobby"))Status("Connected to your buddy",LaunchTone.Good);
        else if(s.StartsWith("Joining via "))Status("Starting StarCraft II…",LaunchTone.Busy);
        else if(s.StartsWith("READY"))Status("In game",LaunchTone.Good);
        else if(s.StartsWith("Hub open"))Status("Campaign open: pick a mission",LaunchTone.Good);
        else if(s.StartsWith("Starting ")&&s.Contains("picked"))Status("Starting the mission…",LaunchTone.Busy);
        else if(s.StartsWith("Connection to your buddy was lost"))Status("Connection to your buddy was lost",LaunchTone.Error);
    }
    void Status(string text,LaunchTone tone){lastTone=tone;var h=StatusChanged;if(h!=null)h(text,tone);}
    void Latency(string text){var h=LatencyChanged;if(h!=null)h(text);}
    void SetRunning(bool on){running=on;if(on)lastTone=LaunchTone.Busy;var h=RunningChanged;if(h!=null)h(on);}
}
