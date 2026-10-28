// Campaign hub flow (bare-bones): each player runs their own copy of the campaign hub (the Hyperion) as a solo
// API game; when either picks a mission, both hubs close and the two launchers play that mission together through
// the usual co-op session; afterwards the host's campaign banks are copied to the guest and both hubs open again.
//
// Facts this relies on (games/starcraft-2/NOTES.md, 2026-10-18): hubs run as solo API games when packed into an
// .SC2Map; launching a mission ends the hub game (API status 5) and the client then crashes trying to load the next
// map, so the hub client is quit at once; player-1 banks are sent from the host to the guest in 2-player games.
//
// Supported now: WoL@Kit campaigns (kit_liberty_story.SC2Mod), and campaigns on
// Blizzard's own WoL campaign library (vanilla-based mods such as UED). The hub's libWoLC_gf_LaunchMission (Kit) or
// libCamp_gf_LaunchMission (Blizzard) calls are wrapped to record the pick in a "CoopHub" bank.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml.Linq;

// Minimal BZip2 decoder (MPQ sectors in editor-saved maps are mostly bzip2). No randomized blocks, no CRC check.
static class BZip2 {
    class Bits {
        readonly byte[] d;int at;int bit;public Bits(byte[] data,int start){d=data;at=start;}
        public int Get(int n){int v=0;while(n-->0){if(at>=d.Length)throw new InvalidDataException("bzip2: truncated");v=(v<<1)|((d[at]>>(7-bit))&1);if(++bit==8){bit=0;at++;}}return v;}
    }
    public static byte[] Decompress(byte[] data,int start) {
        if(data.Length<start+4||data[start]!='B'||data[start+1]!='Z'||data[start+2]!='h')throw new InvalidDataException("bzip2: bad header");
        int blockMax=(data[start+3]-'0')*100000;var b=new Bits(data,start+4);var output=new MemoryStream();
        var tt=new int[blockMax];
        while(true) {
            long magic=((long)b.Get(24)<<24)|(long)b.Get(24);
            if(magic==0x177245385090L)break;
            if(magic!=0x314159265359L)throw new InvalidDataException("bzip2: bad block magic");
            b.Get(32);if(b.Get(1)!=0)throw new InvalidDataException("bzip2: randomized blocks unsupported");
            int origPtr=b.Get(24);
            var inUse=new bool[256];int used=b.Get(16);
            for(int i=0;i<16;i++)if((used&(0x8000>>i))!=0){int bits=b.Get(16);for(int j=0;j<16;j++)if((bits&(0x8000>>j))!=0)inUse[i*16+j]=true;}
            var seqToUnseq=new byte[256];int nInUse=0;for(int i=0;i<256;i++)if(inUse[i])seqToUnseq[nInUse++]=(byte)i;
            int alphaSize=nInUse+2,nGroups=b.Get(3),nSelectors=b.Get(15);
            if(nGroups<2||nGroups>6||nSelectors<1)throw new InvalidDataException("bzip2: bad tables");
            var mtfSel=new byte[nSelectors];for(int i=0;i<nSelectors;i++){int j=0;while(b.Get(1)==1){j++;if(j>=nGroups)throw new InvalidDataException("bzip2: bad selector");}mtfSel[i]=(byte)j;}
            var pos=new byte[nGroups];for(int i=0;i<nGroups;i++)pos[i]=(byte)i;
            var selector=new byte[nSelectors];
            for(int i=0;i<nSelectors;i++){int v=mtfSel[i];byte tmp=pos[v];while(v>0){pos[v]=pos[v-1];v--;}pos[0]=tmp;selector[i]=tmp;}
            var len=new int[nGroups][];
            for(int t=0;t<nGroups;t++){len[t]=new int[alphaSize];int curr=b.Get(5);
                for(int i=0;i<alphaSize;i++){while(true){if(curr<1||curr>20)throw new InvalidDataException("bzip2: bad code length");if(b.Get(1)==0)break;if(b.Get(1)==0)curr++;else curr--;}len[t][i]=curr;}}
            var limit=new int[nGroups][];var bas=new int[nGroups][];var perm=new int[nGroups][];var minLens=new int[nGroups];
            for(int t=0;t<nGroups;t++){
                int minLen=32,maxLen=0;for(int i=0;i<alphaSize;i++){minLen=Math.Min(minLen,len[t][i]);maxLen=Math.Max(maxLen,len[t][i]);}
                limit[t]=new int[25];bas[t]=new int[25];perm[t]=new int[258];minLens[t]=minLen;
                int pp=0;for(int i=minLen;i<=maxLen;i++)for(int j=0;j<alphaSize;j++)if(len[t][j]==i)perm[t][pp++]=j;
                for(int i=0;i<alphaSize;i++)bas[t][len[t][i]+1]++;
                for(int i=1;i<25;i++)bas[t][i]+=bas[t][i-1];
                int vec=0;for(int i=minLen;i<=maxLen;i++){vec+=bas[t][i+1]-bas[t][i];limit[t][i]=vec-1;vec<<=1;}
                for(int i=minLen+1;i<=maxLen;i++)bas[t][i]=((limit[t][i-1]+1)<<1)-bas[t][i];
                for(int i=maxLen+1;i<25;i++)limit[t][i]=int.MaxValue;
            }
            int groupNo=-1,groupPos=0,gSel=0;
            Func<int> getSym=()=>{
                if(groupPos==0){groupNo++;if(groupNo>=nSelectors)throw new InvalidDataException("bzip2: selector overflow");groupPos=50;gSel=selector[groupNo];}
                groupPos--;int zn=minLens[gSel],zvec=b.Get(zn);
                while(zvec>limit[gSel][zn]){zn++;if(zn>20)throw new InvalidDataException("bzip2: bad code");zvec=(zvec<<1)|b.Get(1);}
                return perm[gSel][zvec-bas[gSel][zn]];
            };
            var yy=new byte[256];for(int i=0;i<256;i++)yy[i]=(byte)i;
            var unzftab=new int[256];int nblock=0,eob=nInUse+1;
            int sym=getSym();
            while(sym!=eob) {
                if(sym<=1){int es=0,n=1;do{es+=sym==0?n:2*n;n<<=1;sym=getSym();}while(sym<=1);
                    byte uc=seqToUnseq[yy[0]];unzftab[uc]+=es;if(nblock+es>blockMax)throw new InvalidDataException("bzip2: block overflow");while(es-->0)tt[nblock++]=uc;continue;}
                int nn=sym-1;byte v2=yy[nn];Buffer.BlockCopy(yy,0,yy,1,nn);yy[0]=v2;
                byte c=seqToUnseq[v2];unzftab[c]++;if(nblock>=blockMax)throw new InvalidDataException("bzip2: block overflow");tt[nblock++]=c;sym=getSym();
            }
            if(origPtr>=nblock&&nblock>0)throw new InvalidDataException("bzip2: bad origPtr");
            var cftab=new int[257];for(int i=1;i<=256;i++)cftab[i]=cftab[i-1]+unzftab[i-1];
            for(int i=0;i<nblock;i++){int uc=tt[i]&0xff;tt[cftab[uc]]|=i<<8;cftab[uc]++;}
            int tPos=nblock>0?tt[origPtr]>>8:0;int last=-1,run=0;
            for(int k=0;k<nblock;k++){
                tPos=tt[tPos];int ch=tPos&0xff;tPos>>=8;
                if(run==4){for(int r=0;r<ch;r++)output.WriteByte((byte)last);run=0;last=-1;continue;}
                if(ch==last)run++;else{run=1;last=ch;}
                output.WriteByte((byte)ch);
            }
            for(int i=0;i<nblock;i++)tt[i]=0;
        }
        return output.ToArray();
    }
}

// Read-only MPQ (SC2Map/SC2Mod archives) or component folder -> file name -> bytes. Supports the formats seen in
// editor-saved maps: uncompressed, zlib and bzip2 sectors, single-unit files, sector CRC tables.
static class MapArchive {
    static readonly uint[] T=MakeTable();
    static uint[] MakeTable(){var t=new uint[1280];uint seed=0x100001;for(int i=0;i<256;i++)for(int j=0;j<5;j++){seed=(seed*125+3)%0x2aaaab;uint hi=(seed&65535)<<16;seed=(seed*125+3)%0x2aaaab;t[i+j*256]=hi|(seed&65535);}return t;}
    static uint Hash(string s,int type){unchecked{uint a=0x7fed7fed,b=0xeeeeeeee;foreach(byte c in Encoding.ASCII.GetBytes(s.ToUpperInvariant())){a=T[type*256+c]^(a+b);b=c+a+b+(b<<5)+3;}return a;}}
    static uint[] Decrypt(byte[] d,int at,int count,uint key){unchecked{var r=new uint[count];uint seed=0xeeeeeeee;
        for(int i=0;i<count;i++){seed+=T[0x400+(key&255)];uint v=BitConverter.ToUInt32(d,at+i*4)^(key+seed);r[i]=v;key=((~key<<21)+0x11111111)|(key>>11);seed=v+seed+(seed<<5)+3;}return r;}}

    public static SortedDictionary<string,byte[]> Read(string path){return Read(path,null);}
    // One file (e.g. DocumentInfo) without unpacking the whole archive; null if absent.
    public static byte[] ReadFile(string path,string name) {
        if(Directory.Exists(path)){string f=Path.Combine(path,name);return File.Exists(f)?File.ReadAllBytes(f):null;}
        byte[] v;return Read(path,name).TryGetValue(name,out v)?v:null;
    }
    static SortedDictionary<string,byte[]> Read(string path,string only) {
        var files=new SortedDictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        if(Directory.Exists(path)){foreach(var f in Directory.GetFiles(path,"*",SearchOption.AllDirectories))files[f.Substring(path.Length).TrimStart('\\','/').Replace('/','\\')]=File.ReadAllBytes(f);return files;}
        byte[] d=File.ReadAllBytes(path);int o=0;
        if(d.Length>16&&d[0]=='M'&&d[1]=='P'&&d[2]=='Q'&&d[3]==0x1b)o=(int)BitConverter.ToUInt32(d,8);
        if(d[o]!='M'||d[o+1]!='P'||d[o+2]!='Q'||d[o+3]!=0x1a)throw new InvalidDataException(Path.GetFileName(path)+" is not an MPQ archive");
        int sectorSize=512<<BitConverter.ToUInt16(d,o+14);
        int htPos=(int)BitConverter.ToUInt32(d,o+16),btPos=(int)BitConverter.ToUInt32(d,o+20),htn=(int)BitConverter.ToUInt32(d,o+24),btn=(int)BitConverter.ToUInt32(d,o+28);
        var ht=Decrypt(d,o+htPos,htn*4,Hash("(hash table)",3));var bt=Decrypt(d,o+btPos,btn*4,Hash("(block table)",3));
        Func<string,byte[]> readFile=name=>{
            uint a=Hash(name,1),b=Hash(name,2);int start=(int)(Hash(name,0)%(uint)htn);
            for(int k=0;k<htn;k++){int j=(start+k)%htn;if(ht[j*4+3]==0xFFFFFFFF)return null;
                if(ht[j*4]==a&&ht[j*4+1]==b){int bi=(int)ht[j*4+3];if(bi>=btn)continue;return ReadBlock(d,o,bt,bi,sectorSize,name);}}
            return null;
        };
        if(only!=null){var one=readFile(only);if(one!=null)files[only]=one;return files;}
        var listfile=readFile("(listfile)");if(listfile==null)throw new InvalidDataException(Path.GetFileName(path)+" has no listfile");
        foreach(var n in Encoding.UTF8.GetString(listfile).Split(new[]{'\r','\n',';'},StringSplitOptions.RemoveEmptyEntries)) {
            if(n=="(listfile)"||n=="(attributes)")continue;var data=readFile(n);if(data!=null)files[n]=data;
        }
        return files;
    }

    static byte[] ReadBlock(byte[] d,int o,uint[] bt,int bi,int sectorSize,string name) {
        int pos=o+(int)bt[bi*4];int csize=(int)bt[bi*4+1],fsize=(int)bt[bi*4+2];uint flags=bt[bi*4+3];
        if((flags&0x80000000)==0)return null;
        if((flags&0x00010000)!=0)throw new InvalidDataException(name+": encrypted MPQ files are not supported");
        if((flags&0x00000100)!=0)throw new InvalidDataException(name+": imploded MPQ files are not supported");
        bool compressed=(flags&0x00000200)!=0;
        if((flags&0x01000000)!=0)return compressed&&csize<fsize?Unpack(d,pos,csize,fsize,name):Slice(d,pos,fsize);
        if(!compressed)return Slice(d,pos,fsize);
        int nsec=(fsize+sectorSize-1)/sectorSize;var outp=new byte[fsize];int at=0;
        for(int k=0;k<nsec;k++) {
            int s0=(int)BitConverter.ToUInt32(d,pos+k*4),s1=(int)BitConverter.ToUInt32(d,pos+k*4+4);int want=Math.Min(sectorSize,fsize-k*sectorSize);
            byte[] sec=s1-s0<want?Unpack(d,pos+s0,s1-s0,want,name):Slice(d,pos+s0,want);
            Buffer.BlockCopy(sec,0,outp,at,want);at+=want;
        }
        return outp;
    }
    static byte[] Slice(byte[] d,int at,int n){var r=new byte[n];Buffer.BlockCopy(d,at,r,0,n);return r;}
    static byte[] Unpack(byte[] d,int at,int n,int expect,string name) {
        byte mask=d[at];byte[] data=Slice(d,at+1,n-1);
        if((mask&0x10)!=0){data=BZip2.Decompress(data,0);mask=(byte)(mask&~0x10);}
        if((mask&0x02)!=0){using(var z=new DeflateStream(new MemoryStream(data,2,data.Length-2),CompressionMode.Decompress))using(var m=new MemoryStream()){z.CopyTo(m);data=m.ToArray();}mask=(byte)(mask&~0x02);}
        if(mask!=0)throw new InvalidDataException(name+": unsupported MPQ compression 0x"+mask.ToString("x2"));
        if(data.Length!=expect)throw new InvalidDataException(name+": unpacked size mismatch");
        return data;
    }
}

// MapInfo player table (versions 17..39, port of mapinfo_codec.py): adds a user slot cloned from player 1.
// A campaign's name for its co-op save folder: the title in its metadata.txt (Custom Campaign Manager format), else the
// folder name (with the parent's when the folder has a generic name such as "campaign").
static class CampaignSaves {
    public static string Id(string campaignDir) {
        string dir=campaignDir.TrimEnd('\\','/'),name=null;
        try {
            string meta=Path.Combine(dir,"metadata.txt");
            if(File.Exists(meta))foreach(var line in File.ReadAllLines(meta))if(line.StartsWith("title=",StringComparison.OrdinalIgnoreCase)){name=line.Substring(6).Trim();break;}
        } catch { }
        if(string.IsNullOrEmpty(name)) {
            name=Path.GetFileName(dir);
            if(Regex.IsMatch(name,"^(campaign|maps)$",RegexOptions.IgnoreCase))name=Path.GetFileName(Path.GetDirectoryName(dir))+" "+name;
        }
        foreach(var c in Path.GetInvalidFileNameChars())name=name.Replace(c,'_');
        return name.Trim().TrimEnd('.');
    }
    // The host's co-op save of a campaign (CampaignRun.LoadSave): per Windows user, test runs in the workspace.
    public static string Dir(Settings settings,string campaignDir) {
        return Path.Combine(settings.Product?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SC2Coop"):Path.Combine(settings.Workspace,"campaign"),"saves",Id(campaignDir));
    }
    // Starts the campaign over: the save moves to saves-reset\<campaign> <time> (kept, not deleted) and an empty save takes its
    // place, so the next session starts from the first mission (an empty save clears the campaign's banks in the game's folder;
    // no save at all would keep them). Returns the backup folder, or null if there was no progress.
    public static string Reset(Settings settings,string campaignDir) {
        string dir=Dir(settings,campaignDir),backup=null;
        if(Directory.Exists(dir)&&Directory.EnumerateFileSystemEntries(dir).Any()) {
            backup=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir)),"saves-reset",Id(campaignDir)+" "+DateTime.Now.ToString("yyyy-MM-dd HHmmss"));
            Directory.CreateDirectory(Path.GetDirectoryName(backup));Directory.Move(dir,backup);
        }
        Directory.CreateDirectory(dir);
        return backup;
    }
}

static class MapInfoPlayers {
    class R {public byte[] d;public int at;public uint N(int size=4){ulong v=0;for(int i=0;i<size;i++)v|=(ulong)d[at+i]<<(8*i);at+=size;return (uint)v;}
        public void Text(){while(d[at]!=0)at++;at++;} public void Short(){int n=(int)N(2);if(n>=4096)throw new InvalidDataException("MapInfo short text");at+=n;}}
    public class Player {public int Slot,Control,Start,End;public string Race;}
    public static List<Player> Parse(byte[] data,out int countAt,out int playersEnd) {
        var r=new R{d=data};if(r.N()!=0x4d617049)throw new InvalidDataException("Wrong MapInfo magic");
        uint v=r.N();if(v<17||v>39)throw new InvalidDataException("Unsupported MapInfo version "+v);
        if(v>=24)r.N(8);r.N();r.N();if(r.N()==2)r.Text();if(v>=22&&r.N()==2)r.Text();if(v>=28)r.Text();if(v>=37)r.Text();
        if(v>=29)r.N();if(v>=11)r.N();if(v>=3)r.Text();r.Text();if(v>=12)for(int i=0;i<4;i++)r.N();if(v>=19)r.N();if(v>=14){r.N();r.Text();}
        if(v>=23)r.Short();if(v>=20)for(int i=0;i<6;i++)r.N();if(v>=35){r.Text();r.Short();}if(v>=39)r.Short();
        if(v>=18)r.N();if(v>=21)r.N();if(v>=30)r.N();if(v>=32)r.N();if(v>=25){r.N();r.N();}if(v>=31)r.N(1);
        if(v>=26){uint n=r.N();if(n>=1000)throw new InvalidDataException("MapInfo attributes");for(uint i=0;i<n;i++){r.Text();r.N();if(v>=27)r.N();if(v>=36)r.N();}}
        if(v>=38)r.Short();
        countAt=r.at;uint count=r.N();if(count>16)throw new InvalidDataException("MapInfo player count "+count);
        var list=new List<Player>();
        for(int i=0;i<count;i++){var p=new Player{Start=r.at};p.Slot=(int)r.N(1);p.Control=(int)r.N();r.N();int rs=r.at;r.Text();p.Race=Encoding.ASCII.GetString(data,rs,Math.Max(0,r.at-rs-1));r.N();r.N();r.N();r.Text();p.End=r.at;
            if(p.Slot>16||p.Control>4)throw new InvalidDataException("MapInfo player record");list.Add(p);}
        playersEnd=r.at;return list;
    }
    // Returns the new MapInfo and the guest slot (6 when free, else the lowest free slot 6..15).
    // Player 1's race as a RequestJoinGame race: 1 Terran, 2 Zerg, 3 Protoss (anything else: 1).
    public static ulong Player1Race(byte[] info) {
        int c,e;var p1=Parse(info,out c,out e).FirstOrDefault(p=>p.Slot==1);string r=p1==null?"":p1.Race??"";
        return r.StartsWith("Prot")?3UL:r.StartsWith("Zerg")?2UL:1UL;
    }
    // A second human (User) player besides player 1: the map author's slot for the guest's own army (separate armies); 0 if none.
    public static int SecondHuman(byte[] info) {
        int c,e;var p=Parse(info,out c,out e).FirstOrDefault(x=>x.Control==1&&x.Slot>1);return p==null?0:p.Slot;
    }
    // A slot's race as a RequestJoinGame race (see Player1Race).
    public static ulong RaceOf(byte[] info,int slot) {
        int c,e;var p=Parse(info,out c,out e).FirstOrDefault(x=>x.Slot==slot);string r=p==null?"":p.Race??"";
        return r.StartsWith("Prot")?3UL:r.StartsWith("Zerg")?2UL:1UL;
    }
    public static byte[] AddGuest(byte[] info,out int guest) {
        int countAt,end;var players=Parse(info,out countAt,out end);
        var p1=players.FirstOrDefault(p=>p.Slot==1);if(p1==null||p1.Control!=1)throw new InvalidDataException("Mission has no user-controlled player 1");
        guest=Enumerable.Range(6,10).FirstOrDefault(s=>players.All(p=>p.Slot!=s));if(guest==0)throw new InvalidDataException("No free player slot for the guest");
        var record=info.Skip(p1.Start).Take(p1.End-p1.Start).ToArray();record[0]=(byte)guest;
        var output=info.Take(end).Concat(record).Concat(info.Skip(end)).ToArray();
        Array.Copy(BitConverter.GetBytes(players.Count+1),0,output,countAt,4);
        int c2,e2;var check=Parse(output,out c2,out e2);if(check.Count!=players.Count+1||check.Last().Slot!=guest||check.Last().Control!=1)throw new InvalidDataException("Guest slot insert check failed");
        return output;
    }
}

static class CampaignMaps {
    static string Base{get{return Program.Base;}}
    static string Script(SortedDictionary<string,byte[]> f){return Encoding.UTF8.GetString(f["MapScript.galaxy"]);}
    static void AddBank(SortedDictionary<string,byte[]> f,string name,int player) {
        var banks=XDocument.Parse(Encoding.UTF8.GetString(f["BankList.xml"]));
        if(!banks.Root.Elements("Bank").Any(b=>(string)b.Attribute("Name")==name&&(string)b.Attribute("Player")==player.ToString()))
            banks.Root.Add(new XElement("Bank",new XAttribute("Name",name),new XAttribute("Player",player)));
        f["BankList.xml"]=Encoding.UTF8.GetBytes(banks.ToString());
    }
    // Custom mods a map needs (DocumentInfo "file:Mods\X" entries, recursively). Blizzard's own dependencies ("bnet:Name/0.0/999")
    // come from the game data; anything else must exist in the StarCraft II Mods folder on this PC.
    public static void CheckDependencies(string install,byte[] documentInfo,string what) {
        if(install==null)return;
        var missing=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMissing(install,documentInfo,missing,seen);
        if(missing.Count>0)throw new Exception(what+" needs "+(missing.Count==1?"this mod":"these mods")+" in "+Path.Combine(install,"Mods")+": "+string.Join(", ",missing)+". Copy them there (same files as your buddy's) and try again.");
    }
    static void CollectMissing(string install,byte[] documentInfo,List<string> missing,HashSet<string> seen) {
        foreach(Match m in Regex.Matches(Encoding.UTF8.GetString(documentInfo),@"<Value>([^<]*file:[^<]*)</Value>")) {
            string v=m.Groups[1].Value;if(Regex.IsMatch(v,@"^bnet:[^,]*/999,"))continue;
            string rel=v.Substring(v.IndexOf("file:")+5).Replace('/','\\');if(!rel.StartsWith("Mods\\",StringComparison.OrdinalIgnoreCase))continue;
            if(!seen.Add(rel))continue;string path=Path.Combine(install,rel);
            byte[] inner=null;
            try {
                if(Directory.Exists(path)){string f=Path.Combine(path,"DocumentInfo");if(File.Exists(f))inner=File.ReadAllBytes(f);}
                else if(File.Exists(path))inner=MapArchive.ReadFile(path,"DocumentInfo");
                else{missing.Add(rel.Substring(5));continue;}
            } catch {continue;}
            if(inner!=null)CollectMissing(install,inner,missing,seen);
        }
    }

    // The map's English display name (DocInfo/Name, e.g. "Liberation Day"), or the map name.
    public static string Title(string campaignDir,string name) {
        try {
            string p=FindMap(campaignDir,name);if(p==null)return name;
            byte[] s=MapArchive.ReadFile(p,@"enUS.SC2Data\LocalizedData\GameStrings.txt");if(s==null)return name;
            var m=Regex.Match(Encoding.UTF8.GetString(s),@"^DocInfo/Name=(.+?)
?$",RegexOptions.Multiline);return m.Success?m.Groups[1].Value:name;
        } catch{return name;}
    }

    // Mods (paths relative to the install, e.g. Mods\x.SC2Mod) a document depends on, recursively; missing ones included.
    public static HashSet<string> ModDependencies(string install,byte[] documentInfo) {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);CollectMissing(install,documentInfo,new List<string>(),seen);return seen;
    }

    // Separate-army choices for missions built in this run (the host's; CampaignRun sets them after connecting).
    public static SeparateOptions Separate=new SeparateOptions();
    // Per-player banks of the campaign (CampaignBanks), listed in the hub too.
    public static HashSet<string> HubPlayerBanks=new HashSet<string>();
    // Whether the last hub built runs on WoL@Kit's library (else Blizzard's libCamp).
    public static bool LastHubKit;
    // Mission game speed (Program.GameSpeeds name), applied a game second into each mission; -coopspeed changes it there.
    public static string GameSpeed="faster";
    // Missions made for separate armies (a second human player in the map), for the launcher's settings row.
    public static List<string> SeparateMissions(string campaignDir) {
        var list=new List<string>();
        foreach(var p in Directory.GetFileSystemEntries(campaignDir,"*.SC2Map").OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)) {
            if(Path.GetFileNameWithoutExtension(p).Equals("TStory01",StringComparison.OrdinalIgnoreCase))continue;
            try{var info=MapArchive.ReadFile(p,"MapInfo");if(info!=null&&MapInfoPlayers.SecondHuman(info)>0)list.Add(Path.GetFileNameWithoutExtension(p));}catch{}
        }
        return list;
    }
    public static string FindMap(string campaignDir,string name) {
        foreach(var p in Directory.GetFileSystemEntries(campaignDir,"*.SC2Map"))if(string.Equals(Path.GetFileNameWithoutExtension(p),name,StringComparison.OrdinalIgnoreCase))return p;
        return null;
    }
    // Player-1 bank names the campaign uses (hub + every mission): the set the host copies to the guest. Per-player banks
    // (PlayerBanks) are left out.
    public static HashSet<string> CampaignBanks(string campaignDir){HashSet<string> own;return CampaignBanks(campaignDir,out own);}
    // Per-player banks: names a map lists for player 1 and for another player too (e.g. WoL+'s "cryswolcoop" for players 1
    // and 14). The map keeps each player's own progress in them (credits, upgrades, unlocks), loading each player's from that
    // player's PC, so the launcher never copies them to the buddy or swaps them with a co-op save: they stay in each player's
    // own bank folder across sessions and campaigns.
    public static HashSet<string> CampaignBanks(string campaignDir,out HashSet<string> playerBanks) {
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);playerBanks=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var p in Directory.GetFileSystemEntries(campaignDir,"*.SC2Map")) {
            try {
                byte[] list;
                if(Directory.Exists(p)){string f=Path.Combine(p,"BankList.xml");if(!File.Exists(f))continue;list=File.ReadAllBytes(f);}
                else{var files=MapArchive.Read(p);if(!files.TryGetValue("BankList.xml",out list))continue;}
                var banks=XDocument.Parse(Encoding.UTF8.GetString(list)).Root.Elements("Bank").ToList();
                var others=new HashSet<string>(banks.Where(b=>(string)b.Attribute("Player")!="1").Select(b=>(string)b.Attribute("Name")),StringComparer.OrdinalIgnoreCase);
                foreach(var b in banks)if((string)b.Attribute("Player")=="1"){string n=(string)b.Attribute("Name");names.Add(n);if(others.Contains(n))playerBanks.Add(n);}
            } catch(Exception e){Program.Log("Campaign: could not read the bank list of "+Path.GetFileName(p)+": "+e.Message);}
        }
        names.RemoveWhere(n=>n.StartsWith("Coop",StringComparison.OrdinalIgnoreCase));
        playerBanks.RemoveWhere(n=>n.StartsWith("Coop",StringComparison.OrdinalIgnoreCase));
        names.ExceptWith(playerBanks);
        return names;
    }

    // Hub: record the mission pick in the "CoopHub" bank, and keep SC2's trigger debugger closed.
    public static string BuildKitHub(string campaignDir,string outDir){return BuildKitHub(campaignDir,outDir,-1,System.Drawing.Color.FromArgb(0,66,255),"");}
    // color: the player's team color in this hub (index or RGB, as BuildMission; -1 keeps Kit's blue). Kit's story init sets
    // player 1 to its campaign color (2), so the hub sets ours again afterwards; it colors this player's name in chat.
    // chatColor: the same color as RGB, for this player's own lines when the hub redraws the chat. tag: file name suffix
    // (local mode builds one hub per window). guestHub: the buddy's hub, whose shops (armory, research, mercenaries) are
    // view only: one army means the host's campaign progress counts, and purchases made in a second, separate hub game would
    // be lost (and vanilla WoL has no refunds).
    public static string BuildKitHub(string campaignDir,string outDir,int color,System.Drawing.Color chatColor,string tag){return BuildKitHub(campaignDir,outDir,color,chatColor,tag,false);}
    public static string BuildKitHub(string campaignDir,string outDir,int color,System.Drawing.Color chatColor,string tag,bool guestHub) {
        string src=FindMap(campaignDir,"TStory01");if(src==null)throw new Exception("No TStory01.SC2Map (the hub) in "+campaignDir);
        var f=MapArchive.Read(src);
        if(TeamColors.IsRgb(color)){int none=-1;AddCustomColors(f,TeamColors.CountFor(Program.CurrentInstall,src),ref color,ref none);}
        byte[] di;if(f.TryGetValue("DocumentInfo",out di))CheckDependencies(Program.CurrentInstall,di,"The campaign hub");
        string s=Script(f);
        // Two campaign libraries: WoL@Kit (libWoLC, missions by name) and Blizzard's own (libCamp, missions by MapID; vanilla-based
        // mods keep it). Kit's is a renamed copy of Blizzard's, so the hooks are the same with different names.
        int calls=Regex.Matches(s,@"\blibWoLC_gf_LaunchMission\(").Count;bool kit=calls>0;
        if(!kit)calls=Regex.Matches(s,@"\blibCamp_gf_LaunchMission\(").Count;
        if(calls==0)throw new Exception("This hub has no mission launch calls (libWoLC_gf_LaunchMission or libCamp_gf_LaunchMission); only WoL@Kit and vanilla-based WoL campaigns are supported so far");
        s=kit?Regex.Replace(s,@"\blibWoLC_gf_LaunchMission\(","CoopHubLaunchMission("):Regex.Replace(s,@"\blibCamp_gf_LaunchMission\(","CoopHubLaunchMissionId(");
        // Mission Archives replays launch from Kit's library (libWCUI_gt_WU_ArchivesLogicMissionLaunch), not this script.
        // DisplayArchives registers that trigger as the UIMissionLaunch campaign event; right after it, ours takes its place.
        int archives=Regex.Matches(s,@"\blibWCUI_gf_DisplayArchives\(").Count;
        s=Regex.Replace(s,@"\blibWCUI_gf_DisplayArchives\(","CoopHubDisplayArchives(");
        // The buddy's hub: shops open to look at, with a note; buying is blocked where the library allows it (Blizzard's: its
        // purchase triggers are turned off in CoopHubDebugInit). Kit's purchase triggers are its own (static), so there the note
        // says changes aren't kept (Kit has refunds; this hub's banks are discarded anyway).
        string shops=kit?@"\b(libWCUI_gf_Display(?:TechConsole|ResearchConsole|MercenaryConsole))\(":@"\b(libCamp_gf_Display(?:TechConsole|ResearchPanel|MercenaryPanel))\(";
        var shopNames=guestHub?Regex.Matches(s,shops).Cast<Match>().Select(m=>m.Groups[1].Value).Distinct().ToList():new List<string>();
        int shopCalls=guestHub?Regex.Matches(s,shops).Count:0;
        if(guestHub)s=Regex.Replace(s,shops,"CoopHubView_$1(");
        LastHubKit=kit;
        s=s.Replace("TriggerDebugWindowOpen(true)","TriggerDebugWindowOpen(false)");
        if(!s.Contains("    InitTriggers();"))throw new Exception("TStory01: unexpected script layout");
        s=s.Replace("    InitTriggers();","    InitTriggers();\n    CoopHubDebugInit();");
        s="void CoopHubLaunchMission(string lp_mission, bool lp_simulation, int lp_difficulty);\nvoid CoopHubDebugInit();\nvoid CoopHubRecord(string lp_mission, bool lp_simulation, int lp_difficulty);\n"+
            string.Concat(shopNames.Select(n=>"void CoopHubView_"+n+"(trigger lp_exitTrigger);\n"))+
            (kit?"void CoopHubDisplayArchives(trigger lp_exitTrigger, trigger lp_sceneFinishCallback);\n":"void CoopHubLaunchMissionId(int lp_mission, bool lp_simulation, int lp_difficulty);\n")+s+(kit?@"
// Mission Archives: the same mission and difficulty Kit's own launch trigger picks, recorded like a star map pick
// (simulation = replay). Only while Kit's archives launch trigger is on, as the event is Kit's for that panel only.
bool CoopHubArchivesLaunch(bool testConds, bool runActions) {
    string mission;
    int difficulty;
    if (!runActions || !TriggerIsEnabled(libWCUI_gt_WU_ArchivesLogicMissionLaunch)) { return true; }
    mission = DataTableGetString(true, libWCUI_gf_WU_ArchivesDataString(BattleReportPanelGetSelectedBattleReport(1)));
    if (libWoLC_gf_WC_CampaignMapIsZeratul(mission)) { difficulty = Kit_UITravelZeratul_LastSelectedDifficulty(); }
    else { difficulty = Kit_UITravel_LastSelectedDifficulty(); }
    libWoLC_gf_StorySimulationEnd();
    CoopHubLaunchMission(mission, true, difficulty);
    return true;
}
trigger coopHubArchivesLaunch = null;
void CoopHubDisplayArchives(trigger lp_exitTrigger, trigger lp_sceneFinishCallback) {
    libWCUI_gf_DisplayArchives(lp_exitTrigger, lp_sceneFinishCallback);
    if (coopHubArchivesLaunch == null) { coopHubArchivesLaunch = TriggerCreate(""CoopHubArchivesLaunch""); }
    libWoLC_gf_RegisterCampaignEvent(libWoLC_ge_WC_Event_UIMissionLaunch, coopHubArchivesLaunch);
}
":@"
// A mission's map file name without "".SC2Map"" (Blizzard's table has ""TRaynor01""; mods may add the extension).
string CoopHubMapName(int lp_mission) {
    string name = libCamp_gv_tS_MapFile[(lp_mission)];
    int at = StringFind(name, "".SC2Map"", c_stringNoCase);
    if (at > 1) { return StringSub(name, 1, at - 1); }
    return name;
}
// Blizzard's libCamp_gf_LaunchMission, done here so the mission it finally launches is known: an A/B choice mission
// (THanson03, TTosh03, TValerian02) runs its choice trigger first and may launch the other one. Recorded by file name
// without "".SC2Map"", like Kit's mission names.
bool coopHubExact = false;
void CoopHubLaunchMissionId(int lp_mission, bool lp_simulation, int lp_difficulty) {
    int picked = lp_mission;
    if ((libCamp_gv_tS_MissionABChoice[(lp_mission)] != null) && (lp_simulation == false) && !coopHubExact) {
        libCamp_gv_tS_MissionABChoiceResult = libCamp_ge_MapID_MapNone;
        TriggerExecute(libCamp_gv_tS_MissionABChoice[(lp_mission)], true, true);
        if (libCamp_gv_tS_MissionABChoiceResult == libCamp_ge_MapID_MapNone) { return; }
        picked = libCamp_gv_tS_MissionABChoiceResult;
    }
    CoopHubRecord(CoopHubMapName(picked), lp_simulation, lp_difficulty);
    if (((libCamp_gf_TS_GameFullyDownloaded() == true) || (lp_simulation == false)) && (libCamp_gv_tS_MissionBriefing[(picked)] != null)) {
        TriggerExecute(libCamp_gv_tS_MissionBriefing[(picked)], true, true);
    }
    libCamp_gf_PrepareForMission(picked, lp_simulation, false, lp_difficulty);
    GameOver(1, c_gameOverVictory, false, false);
    GameSetNextMap("""");
}
// A mission by name (-cooplaunch): its MapID from the library's file table. Exactly that mission: an A/B pair's choice
// dialog is skipped (-cooplaunch THanson03B plays B).
void CoopHubLaunchMission(string lp_mission, bool lp_simulation, int lp_difficulty) {
    int i = 1;
    while (i < 36) {
        if (StringEqual(CoopHubMapName(i), lp_mission, c_stringNoCase)) {
            coopHubExact = true;
            CoopHubLaunchMissionId(i, lp_simulation, lp_difficulty);
            coopHubExact = false;
            return;
        }
        i += 1;
    }
    UIDisplayMessage(PlayerGroupAll(), c_messageAreaSubtitle, StringToText(""No mission "" + lp_mission));
}
")+@"
// Test/skip-ahead command: chat ""-cooplaunch TRaynor02"" launches that mission at the campaign difficulty, like a pick
// on the star map (no lock check). The launcher adds ""replay <difficulty>"" for a Mission Archives replay the other
// player started (CampaignRun.FollowPick).
bool CoopHubChat(bool testConds, bool runActions) {
    string mission = StringWord(EventChatMessage(false), 2);
    int difficulty = "+(kit?"libWoLC_gv_pCX_CampaignDifficulty":"libCamp_gf_TS_CampaignDifficulty()")+@";
    bool replay = StringWord(EventChatMessage(false), 3) == ""replay"";
    if (!runActions || mission == null || mission == """") { return true; }
    if (replay && StringWord(EventChatMessage(false), 4) != """") { difficulty = StringToInt(StringWord(EventChatMessage(false), 4)); }"+(kit?@"
    if (replay) { libWoLC_gf_StorySimulationEnd(); }":"")+@"
    CoopHubLaunchMission(mission, replay, difficulty);
    return true;
}
// Hub chat relay. Each player's hub is a solo game, so the launchers carry chat between them: a line typed here is saved
// to the CoopHub bank (Chat section, ring of 16 by count n) for the launcher to send; the buddy's line comes back in as
// API chat ""-coopmsg R G B Name: line"" (the buddy's color, 0-255). That is a real chat line from this player, so SC2's
// own chat display is hidden in the hub and the hub draws the chat itself (a label where the chat area sits): both
// players' lines, names in their colors, each shown for 16 s like normal chat. The raw -coopmsg lines never show on
// screen; SC2's chat history still lists them (it records every chat message).
text[16] coopHubChatLines;
int[16] coopHubChatTimes;
int coopHubChatCount = 0;
int coopHubNow = 0;
int coopHubChatLabel = c_invalidDialogControlId;
color coopHubMyColor;
color CoopHubRgb(int r, int g, int b) {
    return Color(IntToFixed(r) * 100.0 / 255.0, IntToFixed(g) * 100.0 / 255.0, IntToFixed(b) * 100.0 / 255.0);
}
void CoopHubChatUiInit() {
    int d;
    // SC2's own chat display (GameUI.SC2Layout: centred, 720 x 300, bottom 380 above the screen bottom) is hidden;
    // ours takes its place and its font style (ChatDisplay: bottom-justified).
    DialogControlSetVisible(DialogControlHookupStandard(c_triggerControlTypePanel, ""UIContainer/FullscreenUpperContainer/ChatDisplay""), PlayerGroupAll(), false);
    d = DialogCreate(720, 300, c_anchorBottom, 0, 380, false);
    DialogSetImageVisible(d, false);
    DialogSetVisible(d, PlayerGroupAll(), true);
    coopHubChatLabel = DialogControlCreate(d, c_triggerControlTypeLabel);
    DialogControlSetSize(coopHubChatLabel, PlayerGroupAll(), 720, 300);
    DialogControlSetPosition(coopHubChatLabel, PlayerGroupAll(), c_anchorBottomLeft, 0, 0);
    DialogControlSetPropertyAsString(coopHubChatLabel, c_triggerControlPropertyStyle, PlayerGroupAll(), ""ChatDisplay"");
}
void CoopHubChatShow() {
    int i = coopHubChatCount - 8;
    int shown = 0;
    text all = null;
    if (coopHubChatLabel == c_invalidDialogControlId) { return; }
    if (i < 0) { i = 0; }
    while (i < coopHubChatCount) {
        if (coopHubNow - coopHubChatTimes[i % 16] < 64) {
            if (shown == 0) { all = coopHubChatLines[i % 16]; }
            else { all = all + StringToText(""<n/>"") + coopHubChatLines[i % 16]; }
            shown += 1;
        }
        i += 1;
    }
    if (shown == 0) { all = StringToText(""""); }
    DialogControlSetPropertyAsText(coopHubChatLabel, c_triggerControlPropertyText, PlayerGroupAll(), all);
}
void CoopHubChatAdd(text line) {
    coopHubChatLines[coopHubChatCount % 16] = line;
    coopHubChatTimes[coopHubChatCount % 16] = coopHubNow;
    coopHubChatCount += 1;
    CoopHubChatShow();
}
// Every 0.25 s: the clock for fading lines out.
bool CoopHubChatTick(bool testConds, bool runActions) {
    if (!runActions) { return true; }
    coopHubNow += 1;
    if (coopHubChatCount > 0 && coopHubNow % 2 == 0) { CoopHubChatShow(); }
    return true;
}
bool CoopHubChatIn(bool testConds, bool runActions) {
    string line = EventChatMessage(false);
    string rest;
    int at;
    if (!runActions) { return true; }
    at = 10 + StringLength(StringWord(line, 2)) + StringLength(StringWord(line, 3)) + StringLength(StringWord(line, 4)) + 3;
    rest = StringSub(line, at, StringLength(line));
    at = StringFind(rest, "": "", c_stringCase);
    if (at > 1) {
        CoopHubChatAdd(TextWithColor(StringToText(StringSub(rest, 1, at - 1)),
            CoopHubRgb(StringToInt(StringWord(line, 2)), StringToInt(StringWord(line, 3)), StringToInt(StringWord(line, 4))))
            + StringToText(StringSub(rest, at, StringLength(rest))));
    } else {
        CoopHubChatAdd(StringToText(rest));
    }
    return true;
}
bool CoopHubChatOut(bool testConds, bool runActions) {
    string line = EventChatMessage(false);
    bank b;
    int n;
    // Launcher and test commands (-coopmsg, -cooplaunch from the other player's pick, -coopsave) aren't chat lines.
    if (!runActions || StringSub(line, 1, 5) == ""-coop"") { return true; }
    CoopHubChatAdd(TextWithColor(PlayerName(1), coopHubMyColor) + StringToText("": "" + line));
    b = BankLoad(""CoopHub"", 1);
    n = BankValueGetAsInt(b, ""Chat"", ""n"");
    BankValueSetFromString(b, ""Chat"", ""l"" + IntToString(n % 16), line);
    BankValueSetFromInt(b, ""Chat"", ""n"", n + 1);
    BankSave(b);
    return true;
}
"+(Regex.IsMatch(s,@"^trigger gt_BridgeMissionArchivesClicked;",RegexOptions.Multiline)?@"// Test command: chat ""-cooparchives"" opens the Mission Archives as the bridge console does.
bool CoopHubArchivesTest(bool testConds, bool runActions) {
    if (runActions) { TriggerExecute(gt_BridgeMissionArchivesClicked, true, false); }
    return true;
}
":"")+(color>=0?@"// The player's color, after Kit's story init set its own (a second time in case that ran late).
bool CoopHubColor(bool testConds, bool runActions) {
    if (runActions) { PlayerSetColorIndex(1, "+color+@", true); }
    return true;
}
":"")+@"void CoopHubDebugInit() {
    coopHubMyColor = CoopHubRgb("+chatColor.R+", "+chatColor.G+", "+chatColor.B+@");"+(color>=0?@"
    TriggerAddEventTimeElapsed(TriggerCreate(""CoopHubColor""), 1.0, c_timeReal);
    TriggerAddEventTimeElapsed(TriggerCreate(""CoopHubColor""), 5.0, c_timeReal);":"")+@"
    TriggerAddEventTimeElapsed(TriggerCreate(""CoopHubDifficulty""), 1.0, c_timeReal);
    TriggerAddEventChatMessage(TriggerCreate(""CoopHubChat""), c_playerAny, ""-cooplaunch"", false);
    TriggerAddEventChatMessage(TriggerCreate(""CoopHubSave""), c_playerAny, ""-coopsave"", false);"+(guestHub?"":@"
    TriggerAddEventTimeElapsed(TriggerCreate(""CoopHubAutoSave""), 10.0, c_timeReal);")+(guestHub&&!kit?@"
    TriggerEnable(libCamp_gt_TSTechConsolePurchase, false);
    TriggerEnable(libCamp_gt_TS_MercUIPurchase, false);
    TriggerEnable(libCamp_gt_TS_ResearchUIPurchase, false);":"")+(kit?"":@"
    TriggerEnable(libCamp_gt_TS_BattleReportMissionReplay, false);
    TriggerAddEventBattleReportPanelPlayMission(TriggerCreate(""CoopHubArchivesReplay""), c_playerAny);")+(Regex.IsMatch(s,@"^trigger gt_BridgeMissionArchivesClicked;",RegexOptions.Multiline)?@"
    TriggerAddEventChatMessage(TriggerCreate(""CoopHubArchivesTest""), c_playerAny, ""-cooparchives"", true);":"")+@"
    TriggerAddEventChatMessage(TriggerCreate(""CoopHubChatIn""), c_playerAny, ""-coopmsg "", false);
    CoopHubChatUiInit();
    TriggerAddEventTimePeriodic(TriggerCreate(""CoopHubChatTick""), 0.25, c_timeReal);
    TriggerAddEventChatMessage(TriggerCreate(""CoopHubChatOut""), c_playerAny, """", false);
}
// ""-coopsave [mission]"" (typed by the launcher, not shown to the other hub): save this hub's campaign state (credits,
// purchases, research) the way a mission launch does, then confirm in the CoopHub bank (Saved/seq). Used on the host's hub
// before it closes for a mission the buddy picked, so the host's purchases since the last mission are kept. Also runs by
// itself 10 s after the host's hub opens: the hub pays the last mission's reward when it opens (until the next save marks
// it paid), so closing the hub without a launch (Stop, the buddy picking) paid it again next time (2026-10-22: credits
// grew by a mission reward each time).
void CoopHubSaveNow() {
    bank b;
    " + (kit?"libWoLC_gf_PS_SaveAllTransitionData":"libCamp_gf_TS_SaveAllTransitionData") + @"(false, false, false);
    b = BankLoad(""CoopHub"", 1);
    BankValueSetFromInt(b, ""Saved"", ""seq"", BankValueGetAsInt(b, ""Saved"", ""seq"") + 1);
    BankSave(b);
}
bool CoopHubSave(bool testConds, bool runActions) {
    if (!runActions) { return true; }"+(kit?@"
    // The mission the buddy picked starts from a fresh snapshot (see CoopHubLaunchMission).
    if (StringWord(EventChatMessage(false), 2) != """") { libWoLC_gf_WC_SetMissionSectionExists(StringWord(EventChatMessage(false), 2), false); }":"")+@"
    CoopHubSaveNow();
    return true;
}
// The mission screen preselects player 1's game difficulty, which StarCraft II carries from a mission back to the hub in
// solo play; this hub is a new API game, so it was always Normal (2026-10-23). The campaign's saved difficulty (the last
// one picked for a mission) instead, once the hub's story data has loaded.
bool CoopHubDifficulty(bool testConds, bool runActions) {
    int d = "+(kit?"libWoLC_gv_pCX_CampaignDifficulty":"libCamp_gf_TS_CampaignDifficulty()")+@";
    if (runActions && d > 0) { PlayerSetDifficulty(1, d); }
    return true;
}
bool CoopHubAutoSave(bool testConds, bool runActions) {
    if (runActions) { CoopHubSaveNow(); }
    return true;
}
// The buddy's hub: shops open to look at (BuildKitHub's guestHub).
void CoopHubShopNote() {
    CoopHubChatAdd(StringToText(""Co-op: view only. The host buys upgrades, research and mercenaries; they count for both of you."+(kit?" Changes made here are not kept.":"")+@"""));
}
"+(kit?"":@"// Mission Archives replay (Blizzard's library): its own trigger (libCamp_gt_TS_BattleReportMissionReplay, turned off)
// launches through the library, past CoopHubLaunchMissionId, so the pick wasn't recorded and the session ended (2026-10-23).
// The same steps, through ours.
bool CoopHubArchivesReplay(bool testConds, bool runActions) {
    int i = libCamp_gv_TSC_MISSION_FIRST;
    int report;
    int mission = libCamp_ge_MapID_MapNone;
    if (!runActions || !UIFrameVisible(1, c_syncFrameTypeBattleReportPanel)) { return true; }
    report = BattleReportPanelGetSelectedBattleReport(1);
    while (i <= libCamp_gv_TSC_MISSION_LAST) {
        if (libCamp_gv_tS_BattleReportMissions[(libCamp_gv_tS_MapID[i])] == report) { mission = libCamp_gv_tS_MapID[i]; break; }
        i += 1;
    }
    if (mission == libCamp_ge_MapID_MapNone) { return true; }
    libCamp_gf_TS_BattleReportHide();
    CoopHubLaunchMissionId(mission, true, EventBattleReportPanelDifficultySelected());
    return true;
}
")+string.Concat(shopNames.Select(n=>"void CoopHubView_"+n+"(trigger lp_exitTrigger) {\n    CoopHubShopNote();\n    "+n+"(lp_exitTrigger);\n}\n"))+@"
// Co-op launcher: record the pick for the launcher (the launch then goes on as before).
void CoopHubRecord(string lp_mission, bool lp_simulation, int lp_difficulty) {
    bank b = BankLoad(""CoopHub"", 1);
    BankValueSetFromString(b, ""Pick"", ""mission"", lp_mission);
    BankValueSetFromFlag(b, ""Pick"", ""simulation"", lp_simulation);
    BankValueSetFromInt(b, ""Pick"", ""difficulty"", lp_difficulty);
    BankValueSetFromInt(b, ""Pick"", ""seq"", BankValueGetAsInt(b, ""Pick"", ""seq"") + 1);
    BankSave(b);
}
"+(kit?@"void CoopHubLaunchMission(string lp_mission, bool lp_simulation, int lp_difficulty) {
    CoopHubRecord(lp_mission, lp_simulation, lp_difficulty);
    // The mission keeps a snapshot of the campaign state from its first start, for restarts; started by the launcher (not
    // a transition map) it loads that snapshot if there is one, so purchases since an earlier attempt were undone. Fresh
    // snapshot, as a launch from the hub makes. (Blizzard's library: CampaignRun drops the mission's bank.)
    if (!lp_simulation) { libWoLC_gf_WC_SetMissionSectionExists(lp_mission, false); }
    libWoLC_gf_LaunchMission(lp_mission, lp_simulation, lp_difficulty);
    // The launcher starts the mission itself; don't let this client load the next map first (seen as a double load).
    GameSetNextMap("""");
}
":"");
        f["MapScript.galaxy"]=Encoding.UTF8.GetBytes(s);AddBank(f,"CoopHub",1);
        // Per-player banks the hub doesn't list itself: a BankLoad of an unlisted bank gives an empty one, and a BankSave then
        // wipes the player's file. WoL+'s library pays victories in any map (PlayerLeft with victory), and the hub's mission
        // launch is player 1's victory: both players' cryswolcoop came back empty (2026-10-25).
        foreach(var n in HubPlayerBanks)AddBank(f,n,1);
        Directory.CreateDirectory(outDir);string target=Path.Combine(outDir,"TStory01-coop"+tag+".SC2Map");File.WriteAllBytes(target,Mpq.Pack(f));
        Program.Log("Campaign hub built ("+(kit?"WoL@Kit":"Blizzard campaign library")+", "+calls+" launch calls"+(archives>0?", "+archives+" Mission Archives calls":"")+" wrapped"+(guestHub?", "+shopCalls+" shop calls view only (buddy's hub)":"")+"): "+target);
        return target;
    }

    // Mission: the co-op adapters with generic hooks (any WoL-style mission script), guest in a free player slot.
    // Cutscenes and pings for both players. Mission scripts aim camera work and pings at player 1 only, so the guest kept their
    // own camera during cutscenes and never saw objective pings. Camera calls for player 1 are mirrored to the guest (same
    // shots, same input lock); pings for player 1 also go to the guest. Skippable sections need 0 = every player in the group
    // to press Esc; now one player's Esc skips for both. Returns the rewritten script; wrappers (to prepend) in 'code'.
    static readonly string[][] CameraCalls={
        new[]{"CameraApplyInfo","camerainfo c, fixed duration, fixed velocity, fixed decelerate, bool useTarget","c, duration, velocity, decelerate, useTarget"},
        new[]{"CameraPan","point p, fixed duration, fixed velocity, fixed decelerate, bool smart","p, duration, velocity, decelerate, smart"},
        new[]{"CameraSetValue","int type, fixed value, fixed duration, fixed velocity, fixed decelerate","type, value, duration, velocity, decelerate"},
        new[]{"CameraUseModel","unit u, string name, fixed duration","u, name, duration"},
        new[]{"CameraLockInput","bool lock","lock"},
        new[]{"CameraSetVerticalFieldOfView","bool value","value"},
        new[]{"CameraUseHeightSmoothing","bool value","value"},
        new[]{"CameraShakeStart","int position, int direction, fixed amplitude, fixed frequency, fixed randomPercent, fixed duration","position, direction, amplitude, frequency, randomPercent, duration"},
        new[]{"CameraShakeStop","",""},
        new[]{"CameraSave","",""},
        new[]{"CameraRestore","fixed duration, fixed velocity, fixed decelerate","duration, velocity, decelerate"},
        new[]{"CameraFollowUnitGroup","unitgroup group, bool follow, bool isOffset","group, follow, isOffset"},
        new[]{"CameraLookAt","point p, fixed duration, fixed velocity, fixed decelerate","p, duration, velocity, decelerate"},
    };
    static string CinematicsForBoth(string s,string guest,out string code) {
        var sb=new StringBuilder("// Co-op cutscenes and pings (Campaign.cs CinematicsForBoth)\nplayergroup CoopPlayerGroupSingle(int player) {\n    playergroup g = PlayerGroupSingle(player);\n    if (player == 1) { PlayerGroupAdd(g, "+guest+"); }\n    return g;\n}\n");
        foreach(var c in CameraCalls) {
            string decl="int player"+(c[1]==""?"":", "+c[1]),args=c[2]==""?"":", "+c[2];
            sb.Append("void Coop"+c[0]+"("+decl+") {\n    "+c[0]+"(player"+args+");\n    if (player == 1) { "+c[0]+"("+guest+args+"); }\n}\n");
        }
        code=sb.ToString();
        s=Regex.Replace(s,@"\b("+string.Join("|",CameraCalls.Select(c=>c[0]))+@")\(","Coop$1(");
        s=Regex.Replace(s,@"^.*\b(PingCreate|MinimapPing|libNtve_gf_CreatePingFacingAngle|TriggerSkippableBegin)\(.*$",m=>m.Value.Replace("PlayerGroupSingle(","CoopPlayerGroupSingle("),RegexOptions.Multiline);
        s=Regex.Replace(s,@"\bTriggerSkippableBegin\(([^;]*?), 0, ","TriggerSkippableBegin($1, 1, ");
        return s;
    }

    // Map-level Base.SC2Data\GameData\GameData.xml (the CGame catalog; loaded after every mod): the RGB colors as new
    // TeamColors entries after the mission's own table of 'count' colors (host first). Same input -> same bytes on both PCs.
    const string GameDataEntry=@"Base.SC2Data\GameData\GameData.xml";
    static void AddCustomColors(SortedDictionary<string,byte[]> f,int count,ref int hostColor,ref int guestColor) {
        byte[] old;XDocument x=f.TryGetValue(GameDataEntry,out old)?XDocument.Parse(Encoding.UTF8.GetString(old).TrimStart('﻿')):new XDocument(new XElement("Catalog"));
        var game=x.Root.Elements("CGame").FirstOrDefault(g=>(string)g.Attribute("id")=="Dflt");
        if(game==null){game=new XElement("CGame",new XAttribute("id","Dflt"));x.Root.Add(game);}
        foreach(var who in new[]{"host","guest"}) {
            int c=who=="host"?hostColor:guestColor;if(!TeamColors.IsRgb(c))continue;
            game.Add(TeamColors.Entry(c));Program.Log("Custom "+who+" color "+TeamColors.Format(c)+" added as team color "+count);
            if(who=="host")hostColor=count++;else guestColor=count++;
        }
        f[GameDataEntry]=Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"+x.Root.ToString());
    }

    public static string BuildMission(string campaignDir,string mission,int difficulty,string outDir,out int guest){return BuildMission(campaignDir,mission,difficulty,outDir,out guest,-1,-1);}
    // hostColor recolors player 1 (the shared army), guestColor the guest's own slot (pings, chat); -1 keeps the map's.
    // An RGB color (TeamColors.IsRgb) is appended to the map's own team color table and used by its new index.
    public static string BuildMission(string campaignDir,string mission,int difficulty,string outDir,out int guest,int hostColor,int guestColor) {
        string src=FindMap(campaignDir,mission);if(src==null)throw new Exception("Mission map "+mission+" not found in "+campaignDir);
        var f=MapArchive.Read(src);
        // The players' colors as RGB for the guest UI (ally view frames, pings): libNtve's color conversion only knows the
        // 16 stock colors, not the campaign's changed or added ones. -1 = the map's own color (converted in the map).
        var table=TeamColors.TableFor(Program.CurrentInstall,src);
        Func<int,int> rgb=c=>c<0?-1:TeamColors.ToColor(c,table).ToArgb()&0xFFFFFF;int hostRgb=rgb(hostColor),guestRgb=rgb(guestColor);
        if(TeamColors.IsRgb(hostColor)||TeamColors.IsRgb(guestColor))AddCustomColors(f,table.Count,ref hostColor,ref guestColor);
        byte[] di;if(f.TryGetValue("DocumentInfo",out di))CheckDependencies(Program.CurrentInstall,di,"Mission "+mission);
        // A map with a second human player is made for separate armies: the guest takes that slot. Otherwise the guest gets a
        // copy of player 1's slot (shared army).
        int second=MapInfoPlayers.SecondHuman(f["MapInfo"]);bool authored=second>0;
        if(authored)guest=second;else f["MapInfo"]=MapInfoPlayers.AddGuest(f["MapInfo"],out guest);
        string s=Script(f),marker="void InitMap () {";
        if(s.Split(new[]{marker},StringSplitOptions.None).Length!=2||!s.Contains("    InitTriggers();"))throw new Exception(mission+": unexpected script layout");
        // WoL@Kit mission (libWCMI/libWoLC) or one on Blizzard's campaign library (libCamp, vanilla-based campaigns).
        bool kit=Regex.IsMatch(s,@"\blib(WCMI|WoLC)_g[ft]_");
        s=Regex.Replace(s,@"\bObjective([GS])etState\(","CoopObjective$1etState(");
        string g=guest.ToString();
        string shared=File.ReadAllText(Path.Combine(Base,"campaign_shared_control.galaxy"));
        shared=shared.Replace("(6,","("+g+",").Replace(", 6,",", "+g+",").Replace("!= 6","!= "+g).Replace("(6)","("+g+")");
        string targeting=File.ReadAllText(Path.Combine(Base,"campaign_targeting.galaxy")).Replace("const int coopGuest = 6;","const int coopGuest = "+g+";");
        string ui=File.ReadAllText(Path.Combine(Base,"campaign_guest_ui.galaxy")).Replace("const int coopUiGuest = 6;","const int coopUiGuest = "+g+";")
            .Replace("const int coopUiOwnerRgb = -1;","const int coopUiOwnerRgb = "+hostRgb+";").Replace("const int coopUiGuestRgb = -1;","const int coopUiGuestRgb = "+guestRgb+";");
        string prefix="void CoopSharedControlInit();\nvoid CoopSharedControlUiInit();\nvoid CoopTargetingInit();\nvoid CoopGuestUiInit();\nvoid CoopCampaignInit();\n"+
            "int coopGuestPings = 0;\nint coopGuestCamKeys = 0;\nstring coopGuestPingLast = \"\";\nint coopObjectiveOutcomes = 0;\nint coopCampaignTicks = 0;\n"+
            "int CoopObjectiveGetState(int objective);\nvoid CoopObjectiveSetState(int objective, int state);\n";
        if(!Regex.IsMatch(s,@"^bool gv_inCinematic\b",RegexOptions.Multiline))prefix+="bool gv_inCinematic = false;\n";
        string cine;s=CinematicsForBoth(s,g,out cine);prefix+=cine;
        // Separate armies (SeparateArmies.cs): each player has their own army instead of sharing player 1's. Maps with a second
        // human player; SC2_COOP_SEPARATE=1 tries it on a normal map (experiment).
        bool separate=authored||Environment.GetEnvironmentVariable("SC2_COOP_SEPARATE")=="1";
        string sepBody="";
        if(separate) {
            string sepDecls;s=SeparateArmies.Apply(s,guest,authored,kit,Separate,SeparateArmies.Pickups(Program.CurrentInstall,f,s),out sepDecls,out sepBody);prefix+=sepDecls;
            if(Environment.GetEnvironmentVariable("SC2_COOP_TEST_GUESTEND")=="1")AddBank(f,"CoopGuestEnd",guest);
            shared=shared.Replace("libNtve_ge_AllianceSetting_AllyWithSharedVisionControlAndSpending","libNtve_ge_AllianceSetting_AllyWithSharedVision");
            // The guest keeps its own race and selection (the shared-army UI setup gives it player 1's race and Command Center).
            shared=Regex.Replace(shared,@"\r?\n\s*PlayerSetRace\([^;]*;","");
            shared=Regex.Replace(shared,@"\r?\n\s*UnitSelect\(UnitGroupUnit\(UnitGroup\(""CommandCenter""[^;]*;","");
            // The guest UI adapter copies player 1's minerals/supply into the guest's panel (shared army); the guest has its own now.
            ui=ui.Replace("\r\n","\n");string copy="    TriggerAddEventTimePeriodic(update, 0.125, c_timeGame);\n    TriggerExecute(update, false, false);\n";
            if(ui.Contains(copy))ui=ui.Replace(copy,"");else Program.Log(mission+": separate armies: the guest resource copy wasn't found in the UI adapter");
            Program.Log(mission+": separate armies"+(authored?"":" (experiment)"));
        }
        s=prefix+s;
        s=s.Replace(marker,marker+"\n    PlayerSetDifficulty(1, "+difficulty+");");
        s=s.Replace("    InitTriggers();","    InitTriggers();\n    CoopTargetingInit();\n    CoopCampaignInit();");
        string[] bigGroup=(Environment.GetEnvironmentVariable("SC2_COOP_TEST_BIGGROUP")??"").Split(',').Select(x=>x.Trim()).Where(x=>Regex.IsMatch(x,@"^\d+$")).ToArray();
        if(bigGroup.Length!=2)bigGroup=null;
        s+="\n"+sepBody+"\n"+shared+"\n"+targeting+"\n"+ui+@"
// Generic co-op start (no mission-specific markers): mirror player 1's alliances to the guest every second (missions
// change alliances as they go), and set up the guest's UI and camera once the mission's own setup has run.
// Sets the game speed (c_gameSpeed*), any speed on any difficulty: the campaign library's minimum for Hard and Brutal
// (Fast, Faster) is lifted and the speed unlocked.
void CoopSetSpeedValue(int v) {
    GameSetSpeedValueMinimum(c_gameSpeedSlower);
    GameSetSpeedLocked(false);
    GameSetSpeedValue(v);
}
bool CoopCampaignTick(bool testConds, bool runActions) {
    if (!runActions) { return true; }
    CoopSharedControlInit();
    // The launcher decides what loads after the mission. Kit's victory sets the story map as the next map, and a
    // StarCraft II kept open for the hub would otherwise try to load it as well (black, stuck hub).
    GameSetNextMap("""");
    coopCampaignTicks += 1;
    // The host's game speed, once the campaign library has set its minimum for the difficulty (lifted, CoopSetSpeed).
    if (coopCampaignTicks == 1) { CoopSetSpeedValue(" + Array.IndexOf(Program.GameSpeeds,GameSpeed) + @"); }
    if (coopCampaignTicks == 2) {
        CameraPan(" + g + @", CameraGetTarget(1), 0.0, -1.0, 10.0, false);
        CoopSharedControlUiInit();
" + (hostColor>=0?"\n        PlayerSetColorIndex(1, "+hostColor+", true);":"") + (guestColor>=0?"\n        PlayerSetColorIndex("+g+", "+guestColor+", true);":"") + @"
        // After the colors: the ally view frames take each player's color when they are created.
        CoopGuestUiInit();" + (separate?"\n        CoopSeparateStart();":"") + @"
    }" + (separate?"\n    if (coopCampaignTicks >= 2) { CoopSeparateTick(); }":"") + @"
    return true;
}
// Game speed (""-coopspeed faster"", either player): the game menu's speed setting is single-player only, and an API game
// is a multiplayer one.
void CoopSetSpeed(string name) {
    int v = -1;
    text label;
    if (name == ""slower"" || name == ""0"") { v = c_gameSpeedSlower; label = StringToText(""Slower""); }
    else if (name == ""slow"" || name == ""1"") { v = c_gameSpeedSlow; label = StringToText(""Slow""); }
    else if (name == ""normal"" || name == ""2"") { v = c_gameSpeedNormal; label = StringToText(""Normal""); }
    else if (name == ""fast"" || name == ""3"") { v = c_gameSpeedFast; label = StringToText(""Fast""); }
    else if (name == ""faster"" || name == ""4"") { v = c_gameSpeedFaster; label = StringToText(""Faster""); }
    if (v < 0) { UIDisplayMessage(PlayerGroupAll(), c_messageAreaSubtitle, StringToText(""Game speed: -coopspeed slower, slow, normal, fast or faster"")); return; }
    CoopSetSpeedValue(v);
    UIDisplayMessage(PlayerGroupAll(), c_messageAreaSubtitle, StringToText(""Game speed: "") + label);
}"+(Environment.GetEnvironmentVariable("SC2_COOP_TEST_SPEED")=="1"?@"
// Test (SC2_COOP_TEST_SPEED=1): game time every 5 real seconds in the CoopMission bank (Clock section), to measure the speed.
int coopClockN = 0;
bool CoopClock(bool testConds, bool runActions) {
    bank b = BankLoad(""CoopMission"", 1);
    coopClockN += 1;
    BankValueSetFromFixed(b, ""Clock"", ""t"" + IntToString(coopClockN), GameGetMissionTime());
    BankValueSetFromInt(b, ""Clock"", ""s"" + IntToString(coopClockN), GameGetSpeedValue());
    BankSave(b);
    return true;
}":"")+(bigGroup!=null?@"
// Test (SC2_COOP_TEST_BIGGROUP=<s>,<n>): s game seconds in, n player-1 marines at the guest's camera walk south, selected for the
// guest with attack targeting taken as on; the bridge (ClickBridge.TestProbeAt) then sends one attack probe. The CoopBridge bank's
// Test section counts what the marines do 3 game seconds after the probe was processed: attack, still on the first move, other.
bool CoopBigGroup(bool testConds, bool runActions) {
    point at = CameraGetTarget(coopGuest);
    unitgroup g;
    int issued = coopProbeIssued;
    int i = 1;
    int attack = 0;
    int moving = 0;
    int other = 0;
    unit u;
    bank b;
    UnitCreate(" + bigGroup[1] + @", ""Marine"", c_unitCreateIgnorePlacement, 1, at, 270.0);
    g = UnitLastCreatedGroup();
    UnitGroupIssueOrder(g, OrderTargetingPoint(AbilityCommand(""move"", 0), PointWithOffset(at, 0.0, -40.0)), c_orderQueueReplace);
    UnitClearSelection(coopGuest);
    UnitGroupSelect(g, coopGuest, true);
    Wait(1.0, c_timeGame);
    coopTargeting = true;
    coopTargetCmd = AbilityCommand(""attack"", 0);
    b = CoopBridgeBank();
    BankValueSetFromInt(b, ""Test"", ""created"", UnitGroupCount(g, c_unitCountAlive));
    BankValueSetFromFlag(b, ""Test"", ""ready"", true);
    BankSave(b);
    while (coopProbeIssued == issued) { Wait(0.5, c_timeGame); }
    Wait(3.0, c_timeGame);
    while (i <= UnitGroupCount(g, c_unitCountAll)) {
        u = UnitGroupUnit(g, i);
        i += 1;
        if (UnitOrderCount(u) > 0 && AbilityCommandGetAbility(OrderGetAbilityCommand(UnitOrder(u, 0))) == ""attack"") { attack += 1; }
        else if (UnitOrderCount(u) > 0 && AbilityCommandGetAbility(OrderGetAbilityCommand(UnitOrder(u, 0))) == ""move"") { moving += 1; }
        else { other += 1; }
    }
    b = CoopBridgeBank();
    BankValueSetFromInt(b, ""Test"", ""attack"", attack);
    BankValueSetFromInt(b, ""Test"", ""moving"", moving);
    BankValueSetFromInt(b, ""Test"", ""other"", other);
    BankSave(b);
    return true;
}":"")+@"
void CoopCampaignInit() {
    trigger t = TriggerCreate(""CoopCampaignTick"");
    TriggerAddEventTimePeriodic(t, 1.0, c_timeGame);
    TriggerAddEventChatMessage(TriggerCreate(""CoopDebugEnd""), c_playerAny, ""-coop"", false);"+(Environment.GetEnvironmentVariable("SC2_COOP_TEST_SPEED")=="1"?@"
    TriggerAddEventTimePeriodic(TriggerCreate(""CoopClock""), 5.0, c_timeReal);":"")+(bigGroup!=null?@"
    TriggerAddEventTimeElapsed(TriggerCreate(""CoopBigGroup""), " + bigGroup[0] + @".0, c_timeGame);":"")+@"
}
";
        // Test/skip-ahead commands (either player, in chat):
        //   -coopwin     the mission's own ending (outro cinematic, score screen, Continue)
        //   -coopwinnow  straight to the mission's victory bookkeeping, then Continue by itself (no cinematic)
        //   -cooplose    defeat
        bool cheat=Regex.IsMatch(s,@"^trigger gt_VictoryCheat;",RegexOptions.Multiline),victory=Regex.IsMatch(s,@"^trigger gt_Victory;",RegexOptions.Multiline);
        string setOver=Regex.IsMatch(s,@"^bool gv_gameOver\b",RegexOptions.Multiline)?"gv_gameOver = true;\n        ":"";
        // Kit (libWCMI) or Blizzard's library (libCamp, vanilla-based campaigns): same flow, different names. Blizzard's has no
        // "game is over" query, so the commands keep their own flag there.
        string lib=kit?"libWCMI":"libCamp";
        string now=setOver+(kit?"libWCMI_gf_GameOver(libWCMI_ge_CampaignMissionGameResultType_Victory);\n        ":"coopDebugOver = true;\n        ")+
            (victory?"TriggerExecute(gt_Victory, false, true);":lib+"_gf_EndCampaignMission(1, c_gameOverVictory);")+
            "\n        Wait(2.0, c_timeReal);\n        TriggerExecute("+(kit?"libWCMI_gt_WM_VictoryContinue":"libCamp_gt_TS_VSScreenContinue")+", true, false);";
        // -coopwin plays the mission's real ending: the victory sequence with the trigger the mission's own completion passes
        // (e.g. gt_VictoryQ: setup, outro cinematic, cleanup). The missions' cheat triggers pass gt_Victory, which skips
        // the cinematic (2026-10-22: -coopwin looked like -coopwinnow).
        string outro=Regex.Matches(s,@"\b"+lib+@"_gf_RunMissionVictorySequence\((gt_\w+)\)").Cast<Match>().Select(m=>m.Groups[1].Value).FirstOrDefault(a=>a!="gt_Victory");
        string win=outro!=null?setOver+(kit?"":"coopDebugOver = true;\n        ")+lib+"_gf_RunMissionVictorySequence("+outro+");":cheat?"TriggerExecute(gt_VictoryCheat, true, false);":now;
        s+=(kit?"":"\nbool coopDebugOver = false;")+@"
bool CoopDebugEnd(bool testConds, bool runActions) {
    string word = StringWord(EventChatMessage(false), 1);
    if (!runActions || "+(kit?"libWCMI_gf_GameIsOver()":"coopDebugOver")+@") { return true; }
    if (word == ""-coopwin"") {
        " + win + @"
    } else if (word == ""-coopwinnow"") {
        " + now + @"
    } else if (word == ""-cooplose"") {
        "+(kit?"":"coopDebugOver = true;\n        ")+lib+@"_gf_EndCampaignMission(1, c_gameOverDefeat);
    } else if (word == ""-coopspeed"") {
        CoopSetSpeed(StringCase(StringWord(EventChatMessage(false), 2), false));
    } else if (word == ""-coopcolor"") {
        // -coopcolor <player> <index>: set a player's team color and show what the game reports back.
        PlayerSetColorIndex(StringToInt(StringWord(EventChatMessage(false), 2)), StringToInt(StringWord(EventChatMessage(false), 3)), true);
        UIDisplayMessage(PlayerGroupAll(), c_messageAreaSubtitle, StringToText(""player "" + StringWord(EventChatMessage(false), 2) + "" color index now "" + IntToString(PlayerGetColorIndex(StringToInt(StringWord(EventChatMessage(false), 2)), false))));
    } else if (word == ""-cooptrigger"") {
        // -cooptrigger gt_Name_Func: run a mission trigger's actions now (testing: skip to a scripted event).
        TriggerExecute(TriggerCreate(StringWord(EventChatMessage(false), 2)), false, false);
    } else if (word == ""-coopspawn"") {
        // -coopspawn <player> <unit type> <x> <y>: create a unit (testing).
        UnitCreate(1, StringWord(EventChatMessage(false), 3), 0, StringToInt(StringWord(EventChatMessage(false), 2)),
            Point(StringToFixed(StringWord(EventChatMessage(false), 4)), StringToFixed(StringWord(EventChatMessage(false), 5))), 270.0);
    } else if (word == ""-coopobj"") {"+(kit?@"
        // -coopobj TRaynor01_2 [fail]: complete (or fail) a mission objective the way the mission script does.
        if (StringWord(EventChatMessage(false), 3) == ""fail"") {
            libWCMI_gf_ZS_SetMissionObjectiveState(StringWord(EventChatMessage(false), 2), libWoLC_ge_MissionObjectiveState_Failed);
        } else {
            libWCMI_gf_ZS_SetMissionObjectiveState(StringWord(EventChatMessage(false), 2), libWoLC_ge_MissionObjectiveState_Completed);
        }":@"
        // -coopobj <objective id> [fail]: complete (or fail) an objective by its engine id (vanilla objectives have no names).
        if (StringWord(EventChatMessage(false), 3) == ""fail"") {
            CoopObjectiveSetState(StringToInt(StringWord(EventChatMessage(false), 2)), c_objectiveStateFailed);
        } else {
            CoopObjectiveSetState(StringToInt(StringWord(EventChatMessage(false), 2)), c_objectiveStateCompleted);
        }")+@"
    }
    return true;
}
";
        // Kit missions complete/fail objectives through libWCMI_gf_ZS_SetMissionObjectiveState, which sets the engine objective
        // state; completed/failed ends the API game (guest loses control). Route those calls through CoopKitObjectiveState:
        // Kit's own bookkeeping (user data, progress) as before, but the engine state goes through CoopObjectiveSetState.
        // SC2_COOP_NO_OBJECTIVE_FIX=1 leaves them alone (to reproduce the problem).
        int kitObjectiveCalls=Regex.Matches(s,@"\blibWCMI_gf_ZS_SetMissionObjectiveState\(").Count;
        if(kitObjectiveCalls>0&&Environment.GetEnvironmentVariable("SC2_COOP_NO_OBJECTIVE_FIX")!="1") {
            s="void CoopKitObjectiveState(string lp_objective, int lp_state);\n"+Regex.Replace(s,@"\blibWCMI_gf_ZS_SetMissionObjectiveState\(","CoopKitObjectiveState(")+@"
void CoopKitObjectiveState(string lp_objective, int lp_state) {
    string inst = libWoLC_gf_PS_ObjectiveUserDataInstance(lp_objective);
    int o = DataTableGetObjective(true, libWoLC_gf_PS_ObjectiveTableString(lp_objective));
    if (lp_state == libWoLC_ge_MissionObjectiveState_Completed) {
        UserDataSetInt(""MissionObjective"", inst, ""Complete"", 1, 1);
        UserDataSetInt(""MissionObjective"", inst, ""Available"", 1, 0);
        libWoLC_gf_ZS_SetObjectiveProgressData(lp_objective, libWoLC_gf_MissionObjectiveItemCount(lp_objective));
        CoopObjectiveSetState(o, c_objectiveStateCompleted);
        return;
    }
    if (lp_state == libWoLC_ge_MissionObjectiveState_Failed) {
        UserDataSetInt(""MissionObjective"", inst, ""Complete"", 1, 0);
        CoopObjectiveSetState(o, c_objectiveStateFailed);
        return;
    }
    libWCMI_gf_ZS_SetMissionObjectiveState(lp_objective, lp_state);
}
";
            Program.Log(mission+": "+kitObjectiveCalls+" Kit objective state calls routed through the co-op objective handling");
        }
        // Defeat: the campaign library's defeat shows SC2's defeat panel, whose "Return to <hub>" loads the next map, which
        // the launcher clears, so the lost game never ended (2026-10-22). Defeats (the mission's own and -cooplose) get a
        // co-op dialog for both players instead: "Retry mission" (noted in the CoopMission bank for the launcher, which
        // starts the mission again) or "Return to the hub"; either ends the game for the launcher.
        int defeatCalls=Regex.Matches(s,@"\b"+lib+@"_gf_EndCampaignMission\(").Count;
        s="void CoopEndCampaignMission(int lp_player, int lp_type);\n"+Regex.Replace(s,@"\b"+lib+@"_gf_EndCampaignMission\(","CoopEndCampaignMission(")+@"
int coopDefeatDialog = c_invalidDialogId;
int coopRetryButton = c_invalidDialogControlId;
int coopHubButton = c_invalidDialogControlId;
bool coopEnding = false;
bool CoopDefeatClick(bool testConds, bool runActions) {
    bank b;
    if (!runActions || coopEnding) { return true; }
    if (EventDialogControl() == coopRetryButton) {
        b = BankLoad(""CoopMission"", 1);
        BankValueSetFromInt(b, ""End"", ""retry"", BankValueGetAsInt(b, ""End"", ""retry"") + 1);
        BankSave(b);
    } else if (EventDialogControl() != coopHubButton) {
        return true;
    }
    coopEnding = true;
    DialogSetVisible(coopDefeatDialog, PlayerGroupAll(), false);"+(kit?"":@"
    // The defeat in the campaign state, as Blizzard's own abort does (last mission: not successful, so the hub pays no
    // reward); the launcher decides what loads next.
    libCamp_gf_PrepareForStoryMode(c_gameOverDefeat, libCamp_gf_TS_CampaignDifficulty());
    GameSetNextMap("""");")+@"
    GameOver(1, c_gameOverDefeat, false, false);
    return true;
}
int CoopDefeatButton(int d, int anchor, int x, string label) {
    int c = DialogControlCreate(d, c_triggerControlTypeButton);
    DialogControlSetSize(c, PlayerGroupAll(), 260, 55);
    DialogControlSetPosition(c, PlayerGroupAll(), anchor, x, 35);
    DialogControlSetPropertyAsText(c, c_triggerControlPropertyText, PlayerGroupAll(), StringToText(label));
    return c;
}
void CoopEndCampaignMission(int lp_player, int lp_type) {
    int label;
    if (lp_type == c_gameOverVictory) { " + lib + @"_gf_EndCampaignMission(lp_player, lp_type); return; }
    if (coopDefeatDialog != c_invalidDialogId) { return; }
    // Not modal: a modal dialog also blocks other open panels (e.g. a Help panel left open) from being closed.
    coopDefeatDialog = DialogCreate(640, 260, c_anchorCenter, 0, 0, false);
    DialogSetTitle(coopDefeatDialog, StringToText(""Mission failed""));
    label = DialogControlCreate(coopDefeatDialog, c_triggerControlTypeLabel);
    DialogControlSetSize(label, PlayerGroupAll(), 560, 60);
    DialogControlSetPosition(label, PlayerGroupAll(), c_anchorTop, 0, 70);
    DialogControlSetPropertyAsText(label, c_triggerControlPropertyText, PlayerGroupAll(), StringToText(""Retry the mission together, or both go back to the hub. Either player can choose.""));
    coopRetryButton = CoopDefeatButton(coopDefeatDialog, c_anchorBottomLeft, 40, ""Retry mission"");
    coopHubButton = CoopDefeatButton(coopDefeatDialog, c_anchorBottomRight, 40, ""Return to the hub"");
    DialogSetVisible(coopDefeatDialog, PlayerGroupAll(), true);
    UISetCursorVisible(PlayerGroupAll(), true);
    TriggerAddEventDialogControl(TriggerCreate(""CoopDefeatClick""), c_playerAny, c_invalidDialogControlId, c_triggerControlEventTypeClick);
}
";
        Program.Log(mission+": "+defeatCalls+" defeat calls get the co-op retry dialog");
        // Blizzard's library: a mission that isn't loaded as a transition from the hub (the launcher starts it) runs the
        // library's debug "buy average tech" in libCamp_gf_PurchaseStorymodeTech: credits recomputed from the expected
        // rewards minus purchases, mission progress set to the expected path, and the mission saved as the last successful
        // one (so a defeat still paid its reward). From the hub the call does nothing; same here (2026-10-22: credits jumped
        // around between missions).
        if(!kit){int tech=Regex.Matches(s,@"\blibCamp_gf_PurchaseStorymodeTech\(\);").Count;
            s=Regex.Replace(s,@"\blibCamp_gf_PurchaseStorymodeTech\(\);","");  // (Galaxy has no /* */ comments)
            if(tech>0)Program.Log(mission+": the library's debug tech purchase skipped (as when started from the hub)");}
        f["MapScript.galaxy"]=Encoding.UTF8.GetBytes(s);AddBank(f,"CoopBridge",guest);AddBank(f,"CoopMission",1);TrialPreparation.AddCoopModels(f);
        Directory.CreateDirectory(outDir);string target=Path.Combine(outDir,mission+"-coop.SC2Map");File.WriteAllBytes(target,Mpq.Pack(f));
        Program.Log("Mission "+mission+" built (guest player "+guest+", difficulty "+difficulty+"): "+target);
        return target;
    }
}

// Map checker (CLI: campaign-check <campaign folder> [report.md]): what in each map's script is aimed at player 1, sorted
// into what the launcher handles by itself, what only the host can trigger, what would matter if each player had their own
// army, and other risks. Static text analysis of MapScript.galaxy: a guide for porting, not proof.
static class CampaignCheck {
    class Rule {public string Group,Name,Pattern,Note;public Rule(string g,string n,string p,string note){Group=g;Name=n;Pattern=p;Note=note;}}
    // {P1} = 1 or the map's player-1 constant (gv_p1_USER, gv_pLAYER01_USER, ...).
    static readonly Rule[] Rules={
        new Rule("Handled by the launcher","Objective state (engine)",@"\bObjective[GS]etState\(","Completed/failed would end the API game; routed through CoopObjectiveSetState."),
        new Rule("Handled by the launcher","Objective state (Kit)",@"\blibWCMI_gf_ZS_SetMissionObjectiveState\(","Routed through CoopKitObjectiveState."),
        new Rule("Handled by the launcher","Camera work for player 1",@"\b(CameraApplyInfo|CameraPan|CameraSetValue|CameraUseModel|CameraLockInput|CameraSetVerticalFieldOfView|CameraUseHeightSmoothing|CameraShakeStart|CameraShakeStop|CameraSave|CameraRestore|CameraFollowUnitGroup|CameraLookAt)\({P1},","Mirrored to the guest."),
        new Rule("Handled by the launcher","Pings for player 1",@"\b(PingCreate|MinimapPing|libNtve_gf_CreatePingFacingAngle)\(.*PlayerGroupSingle\({P1}\)","Also shown to the guest."),
        new Rule("Handled by the launcher","Skippable cutscenes",@"\bTriggerSkippableBegin\(","Either player's Esc skips."),
        new Rule("Handled by the launcher","Mission end",@"\blib(WCMI|Camp)_gf_(EndCampaignMission|RunMissionVictorySequence)\(","Kit or Blizzard campaign library; the launcher ends both games and syncs banks."),
        new Rule("Handled by the launcher","Next map",@"\bGameSetNextMap\(","Cleared; the launcher decides what loads next."),
        new Rule("Only the host can trigger","Player-1 input events",@"\bTriggerAddEvent(KeyPressed|MouseClicked|MouseMoved|ButtonPressed|DialogControl|UnitSelected|UnitClick|ChatMessage|TargetModeUpdate|CommandCardButton\w*)\(\w+, {P1},","Fires only for the host's input (e.g. tutorial 'select this', mission choice buttons). Fine for shared control; the guest's input doesn't count."),
        new Rule("Only the host can trigger","Selection forced for player 1",@"\b(UnitSelect|UnitGroupSelect|UnitClearSelection)\([^;]*\b{P1}\b","Changes only the host's selection."),
        new Rule("Only the host can trigger","UI shown to player 1 only",@"\b(?!PingCreate|MinimapPing|libNtve_gf_CreatePingFacingAngle|TriggerSkippableBegin|AIAttackWaveSetTargetPlayer)\w+\([^;]*PlayerGroupSingle\({P1}\)","The guest doesn't see this (dialog, message, UI change)."),
        new Rule("Matters for separate armies","Units of player 1",@"\b(UnitGroup|UnitCreate|libNtve_gf_CreateUnitsWithDefaultFacing|UnitSetOwner)\([^;]*,\s*{P1}\s*,","Counts/creates player-1 units. With a shared army the guest's actions count; with separate armies the guest's units would not."),
        new Rule("Matters for separate armies","Resources/properties of player 1",@"\bPlayer(Get|Set|Modify)PropertyInt\({P1},","Minerals, gas, supply of player 1 only."),
        new Rule("Matters for separate armies","Tech of player 1",@"\bTechTree\w+\({P1},","Unlocks/upgrades applied to player 1 only."),
        new Rule("Risks to check","Player-left events",@"\bTriggerAddEventPlayerLeft\(","Runs when a player leaves; check it doesn't fire on the guest or end the mission oddly."),
        new Rule("Risks to check","Direct game over",@"(?<!\w)GameOver\(","Ends the game outside the campaign library's flow."),
        new Rule("Risks to check","Trigger debugger",@"\bTriggerDebugWindowOpen\(true\)","Opens SC2's debugger window (the launcher minimizes it)."),
    };

    public static string Run(string campaignDir,out int total) {
        var sb=new StringBuilder();total=0;
        sb.AppendLine("# Co-op map check: "+Path.GetFileName(campaignDir.TrimEnd('\\','/')));sb.AppendLine();
        sb.AppendLine("Generated "+DateTime.Now.ToString("yyyy-MM-dd HH:mm")+" by SC2Coop campaign-check. Static analysis of each map's MapScript.galaxy: counts of calls aimed at player 1 (or everyone).");sb.AppendLine();
        var maps=Directory.GetFileSystemEntries(campaignDir,"*.SC2Map").OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToList();
        var totals=new Dictionary<Rule,int>();var perMap=new List<KeyValuePair<string,Dictionary<Rule,List<int>>>>();
        foreach(var p in maps) {
            string name=Path.GetFileNameWithoutExtension(p);byte[] script;
            try{script=MapArchive.ReadFile(p,"MapScript.galaxy");}catch(Exception e){sb.AppendLine("- "+name+": could not read ("+e.Message+")");continue;}
            if(script==null){sb.AppendLine("- "+name+": no MapScript.galaxy");continue;}
            string s=Encoding.UTF8.GetString(script);var lines=s.Split('\n');
            var p1=new List<string>{"1"};
            foreach(Match m in Regex.Matches(s,@"^(?:const )?int (gv_\w+)(?: = 1)?;",RegexOptions.Multiline))
                if(Regex.IsMatch(m.Groups[1].Value,@"USER|_p0?1_|^gv_p0?1\b|PLAYER_?0?1",RegexOptions.IgnoreCase)&&(m.Value.Contains("= 1")||!m.Value.StartsWith("const")))p1.Add(m.Groups[1].Value);
            string p1re="(?:"+string.Join("|",p1.Distinct().Select(Regex.Escape))+")";
            var hits=new Dictionary<Rule,List<int>>();
            foreach(var r in Rules) {
                var re=new Regex(r.Pattern.Replace("{P1}",p1re));var at=new List<int>();
                for(int i=0;i<lines.Length;i++)if(!lines[i].TrimStart().StartsWith("//")&&re.IsMatch(lines[i]))at.Add(i+1);
                if(at.Count>0){hits[r]=at;int t;totals.TryGetValue(r,out t);totals[r]=t+at.Count;}
            }
            perMap.Add(new KeyValuePair<string,Dictionary<Rule,List<int>>>(name,hits));
        }
        sb.AppendLine("## Summary ("+perMap.Count+" maps)");sb.AppendLine();
        foreach(var g in Rules.GroupBy(r=>r.Group)) {
            sb.AppendLine("### "+g.Key);sb.AppendLine();sb.AppendLine("| What | Calls | Maps | Meaning |");sb.AppendLine("|---|---|---|---|");
            foreach(var r in g){int t;totals.TryGetValue(r,out t);int maps2=perMap.Count(m=>m.Value.ContainsKey(r));
                sb.AppendLine("| "+r.Name+" | "+t+" | "+maps2+" | "+r.Note+" |");if(g.Key!="Handled by the launcher")total+=t;}
            sb.AppendLine();
        }
        sb.AppendLine("## Per map (lines in MapScript.galaxy; items the launcher handles are left out)");sb.AppendLine();
        foreach(var m in perMap) {
            var shown=m.Value.Where(kv=>kv.Key.Group!="Handled by the launcher").ToList();
            sb.AppendLine("### "+m.Key);
            if(shown.Count==0){sb.AppendLine();sb.AppendLine("Nothing beyond what the launcher handles.");sb.AppendLine();continue;}
            sb.AppendLine();
            foreach(var kv in shown)sb.AppendLine("- "+kv.Key.Group+" / "+kv.Key.Name+": "+kv.Value.Count+" (lines "+string.Join(", ",kv.Value.Take(12))+(kv.Value.Count>12?", ...":"")+")");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

// Connect-time check that both PCs will build the same campaign: this launcher, its adapters, every campaign map and every
// mod those maps use (recursively). File hashes are cached by path, size and write time, so only changed files are read.
static class CampaignFingerprint {
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};

    // modPaths: item key ("mod <name>") -> the mod's path, for FileLists.
    public static Dictionary<string,object> Compute(string install,string campaignDir,string cacheFile,out Dictionary<string,string> modPaths) {
        int t=Environment.TickCount;var cache=LoadCache(cacheFile);var items=new Dictionary<string,object>();
        items["launcher"]=Hash(System.Reflection.Assembly.GetEntryAssembly().Location,cache);
        items["adapters"]=TrialPreparation.Recipe();
        var mods=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var p in Directory.GetFileSystemEntries(campaignDir,"*.SC2Map")) {
            string hash=Hash(p,cache);items["map "+Path.GetFileName(p).ToLowerInvariant()]=hash;
            string deps;
            if(!cache.TryGetValue("deps|"+hash,out deps)) {
                byte[] di=null;try{di=MapArchive.ReadFile(p,"DocumentInfo");}catch(Exception e){Program.Log("Campaign check: could not read "+Path.GetFileName(p)+": "+e.Message);}
                deps=di==null?"":string.Join("|",CampaignMaps.ModDependencies(install,di));cache["deps|"+hash]=deps;
            }
            foreach(var d in deps.Split(new[]{'|'},StringSplitOptions.RemoveEmptyEntries))mods.Add(d);
        }
        modPaths=new Dictionary<string,string>();
        foreach(var m in mods){string path=Path.Combine(install,m),key="mod "+Path.GetFileName(m).ToLowerInvariant();modPaths[key]=path;items[key]=File.Exists(path)||Directory.Exists(path)?Hash(path,cache):"missing";}
        // Test hook: SC2_COOP_TEST_FINGERPRINT_SKEW=<item> changes that item, to see the mismatch report.
        string skew=Environment.GetEnvironmentVariable("SC2_COOP_TEST_FINGERPRINT_SKEW");if(!string.IsNullOrEmpty(skew))items[skew]="test-skew";
        try{Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));File.WriteAllText(cacheFile,Json.Serialize(cache));}catch{}
        Program.Log("Campaign check: "+items.Count+" items ("+mods.Count+" mods) hashed in "+((Environment.TickCount-t)/1000.0).ToString("0.0")+" s");
        return items;
    }

    // What differs, in words for the players; empty if both setups match.
    public static List<string> Compare(Dictionary<string,object> mine,Dictionary<string,object> theirs) {
        var problems=new List<string>();
        foreach(var key in mine.Keys.Union(theirs.Keys).OrderBy(k=>k)) {
            object a,b;mine.TryGetValue(key,out a);theirs.TryGetValue(key,out b);
            string x=a as string,y=b as string;if(x==y)continue;
            string what=key=="launcher"?"SC2Coop.exe":key=="adapters"?"The co-op adapter files next to SC2Coop.exe":key.Substring(key.IndexOf(' ')+1);
            if(key=="launcher"||key=="adapters")problems.Add(what+" differs: both players need the same launcher version (unzip the same SC2Coop zip).");
            else if(x=="missing")problems.Add(what+" is missing on your PC (it belongs in StarCraft II\\Mods).");
            else if(y=="missing")problems.Add(what+" is missing on your buddy's PC (it belongs in StarCraft II\\Mods).");
            else if(x==null)problems.Add(what+(key.StartsWith("mod ")?" is used only by your buddy's campaign files.":" is in your buddy's campaign folder but not in yours."));
            else if(y==null)problems.Add(what+(key.StartsWith("mod ")?" is used only by your campaign files.":" is in your campaign folder but not in your buddy's."));
            else problems.Add(what+" is a different version on each PC.");
        }
        return problems;
    }

    // Mods present on both PCs with different contents: their file lists are exchanged to say which files differ.
    public static List<string> DifferingMods(Dictionary<string,object> mine,Dictionary<string,object> theirs) {
        return mine.Keys.Where(k=>{if(!k.StartsWith("mod "))return false;object b;if(!theirs.TryGetValue(k,out b))return false;
            string x=mine[k] as string,y=b as string;return x!=null&&y!=null&&x!=y&&x!="missing"&&y!="missing";}).OrderBy(k=>k).ToList();
    }
    // Per mod: relative path -> first 16 hex digits of its SHA-256 (a packed .SC2Mod file is one entry). Cached hashes are reused.
    public static Dictionary<string,object> FileLists(IEnumerable<string> keys,Dictionary<string,string> modPaths,string cacheFile) {
        var cache=LoadCache(cacheFile);var lists=new Dictionary<string,object>();
        foreach(var key in keys) {
            string path;if(!modPaths.TryGetValue(key,out path))continue;
            var files=new Dictionary<string,object>();
            if(Directory.Exists(path))foreach(var f in FolderFiles(path,cache))files[f.Key]=f.Value.Substring(0,16);
            else if(File.Exists(path))files["(packed .SC2Mod file, "+new FileInfo(path).Length+" bytes)"]=FileHash(path,cache).Substring(0,16);
            lists[key]=files;
        }
        return lists;
    }
    // What differs inside one mod, in words: files only on one PC, files that differ, and the extracted-into-itself case.
    public static List<string> DescribeFiles(string key,Dictionary<string,object> mine,Dictionary<string,object> theirs) {
        string name=key.Substring(4);var lines=new List<string>();
        Func<IEnumerable<string>,string> list=xs=>{var l=xs.OrderBy(x=>x,StringComparer.Ordinal).ToList();return string.Join(", ",l.Take(8))+(l.Count>8?" (+"+(l.Count-8)+" more)":"");};
        var onlyMine=mine.Keys.Except(theirs.Keys).ToList();var onlyTheirs=theirs.Keys.Except(mine.Keys).ToList();
        var changed=mine.Keys.Intersect(theirs.Keys).Where(k=>(mine[k] as string)!=(theirs[k] as string)).ToList();
        lines.Add(name+": "+mine.Count+" files on your PC, "+theirs.Count+" on your buddy's.");
        if(onlyTheirs.Count>0)lines.Add(name+": "+onlyTheirs.Count+" file"+(onlyTheirs.Count==1?"":"s")+" only on your buddy's PC: "+list(onlyTheirs));
        if(onlyMine.Count>0)lines.Add(name+": "+onlyMine.Count+" file"+(onlyMine.Count==1?"":"s")+" only on your PC: "+list(onlyMine));
        if(changed.Count>0)lines.Add(name+": "+changed.Count+" file"+(changed.Count==1?"":"s")+" with different contents: "+list(changed));
        Func<List<string>,bool> nested=xs=>xs.Any(x=>x.StartsWith(name+"/",StringComparison.Ordinal));
        if(nested(onlyTheirs))lines.Add(name+": your buddy's copy contains the mod folder again inside itself (extracted one level too deep).");
        if(nested(onlyMine))lines.Add(name+": your copy contains the mod folder again inside itself (extracted one level too deep).");
        if(onlyMine.Count>0||onlyTheirs.Count>0)
            lines.Add(name+": unzipping over an old copy keeps files the new copy doesn't have. Delete the folder in StarCraft II\\Mods, then unzip a fresh copy so that "+name+"\\DocumentInfo is directly inside Mods.");
        return lines;
    }

    static Dictionary<string,string> LoadCache(string file) {
        try{if(File.Exists(file))return Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(file));}catch{}
        return new Dictionary<string,string>();
    }
    // A file's SHA-256, or for a component folder the SHA-256 over its sorted file names and file hashes.
    static string Hash(string path,Dictionary<string,string> cache) {
        if(!Directory.Exists(path))return FileHash(path,cache);
        var sb=new StringBuilder();
        foreach(var f in FolderFiles(path,cache))sb.Append(f.Key).Append(':').Append(f.Value).Append('\n');
        return Program.Sha(Encoding.UTF8.GetBytes(sb.ToString()));
    }
    // A component folder's files: relative path (lower case, '/') -> SHA-256. Windows/macOS folder clutter is left out, so
    // an Explorer desktop.ini or a Thumbs.db on one PC doesn't count as a different mod.
    static readonly string[] Clutter={"desktop.ini","thumbs.db",".ds_store"};
    static List<KeyValuePair<string,string>> FolderFiles(string path,Dictionary<string,string> cache) {
        return Directory.GetFiles(path,"*",SearchOption.AllDirectories).Where(f=>!Clutter.Contains(Path.GetFileName(f).ToLowerInvariant()))
            .Select(f=>new KeyValuePair<string,string>(f.Substring(path.Length).TrimStart('\\').Replace('\\','/').ToLowerInvariant(),FileHash(f,cache)))
            .OrderBy(f=>f.Key,StringComparer.Ordinal).ToList();
    }
    static string FileHash(string file,Dictionary<string,string> cache) {
        var info=new FileInfo(file);string key=file.ToLowerInvariant()+"|"+info.Length+"|"+info.LastWriteTimeUtc.Ticks,hash;
        if(cache.TryGetValue(key,out hash))return hash;
        using(var sha=System.Security.Cryptography.SHA256.Create())using(var s=File.OpenRead(file))hash=BitConverter.ToString(sha.ComputeHash(s)).Replace("-","").ToLowerInvariant();
        return cache[key]=hash;
    }
}

// Campaign-state snapshots for skipping ahead: every bank in a profile's Banks folder except the launcher's own (Coop*).
// Restoring a snapshot replaces the profile's campaign banks; the replaced ones are backed up first.
static class CampaignSnapshots {
    public static string BanksDir(Settings settings,string who) {
        return settings.Product?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"StarCraft II","Banks")
            :Path.Combine(settings.Workspace,"campaign","profile-"+who,"Documents","StarCraft II","Banks");
    }
    public static string Dir(Settings settings,string name) {
        if(name.StartsWith("autosave:"))return Path.Combine(settings.Workspace,"campaign","autosaves",name.Substring(9));
        return Path.Combine(settings.Workspace,"campaign","snapshots",name);
    }
    static IEnumerable<string> Campaign(string dir) {
        return Directory.Exists(dir)?Directory.GetFiles(dir,"*.SC2Bank").Where(p=>!Path.GetFileName(p).StartsWith("Coop",StringComparison.OrdinalIgnoreCase)):new string[0];
    }
    public static void Save(Settings settings,string name,string who) {
        string from=BanksDir(settings,who),to=Dir(settings,name);var files=Campaign(from).ToList();
        if(files.Count==0)throw new Exception("No campaign banks in "+from);
        if(Directory.Exists(to))Directory.Delete(to,true);Directory.CreateDirectory(to);
        foreach(var p in files)File.Copy(p,Path.Combine(to,Path.GetFileName(p)));
        Program.Log("Saved snapshot "+name+" ("+files.Count+" banks from "+who+"): "+string.Join(", ",files.Select(Path.GetFileNameWithoutExtension)));
    }
    public static void Load(Settings settings,string name,string who) {
        string from=Dir(settings,name),to=BanksDir(settings,who);var files=Campaign(from).ToList();
        if(files.Count==0)throw new Exception("No snapshot "+name+" in "+Path.GetDirectoryName(from));
        var old=Campaign(to).ToList();
        if(old.Count>0) {
            string backup=Path.Combine(settings.Workspace,"campaign","bank-backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(backup);
            foreach(var p in old){File.Copy(p,Path.Combine(backup,Path.GetFileName(p)),true);File.Delete(p);}
            Program.Log("Previous campaign banks backed up to "+backup);
        }
        Directory.CreateDirectory(to);foreach(var p in files)File.Copy(p,Path.Combine(to,Path.GetFileName(p)),true);
        Program.Log("Loaded snapshot "+name+" into "+who+" ("+files.Count+" banks)");
    }
    // Before every mission: a copy of the campaign banks in <Workspace>\campaign\autosaves (newest 10 kept). Restore one with
    // campaign-banks load autosave:<folder name>.
    public static string Autosave(Settings settings,string who,string label) {
        string root=Path.Combine(settings.Workspace,"campaign","autosaves"),dir=Path.Combine(root,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+label);
        var files=Campaign(BanksDir(settings,who)).ToList();if(files.Count==0)return null;
        Directory.CreateDirectory(dir);foreach(var p in files)File.Copy(p,Path.Combine(dir,Path.GetFileName(p)),true);
        foreach(var old in Directory.GetDirectories(root).OrderByDescending(d=>d,StringComparer.Ordinal).Skip(10))try{Directory.Delete(old,true);}catch{}
        return dir;
    }
    // Blizzard's library ends a Mission Archives replay that isn't a transition map (the launcher starts it) by writing the
    // replayed mission's old snapshot into the campaign bank: which missions are available and completed, best times,
    // mercenaries, tech (libCamp_gf_TS_SaveTransitionVars, SaveSimulation with GameIsTransitionMap() false). The campaign
    // went back to that mission's time (2026-10-23: after replaying The Outlaws it was the only mission on the star map, and
    // the Hyperion's rooms broke). Those sections come back from the copy made before the mission. Returns banks changed.
    static readonly string[] ReplaySections={"MissionAvailableAge","MissionCompletedAge","MissionCompletion","MissionBestTime","MercStatus","TechStatus"};
    public static int RestoreAfterReplay(Settings settings,string who,string fromDir,string mission) {
        int changed=0;string to=BanksDir(settings,who);
        foreach(var before in Campaign(fromDir)) {
            string name=Path.GetFileNameWithoutExtension(before);
            if(name.Equals(mission,StringComparison.OrdinalIgnoreCase)||name.Equals(mission+".SC2Map",StringComparison.OrdinalIgnoreCase))continue;
            string now=Path.Combine(to,Path.GetFileName(before));if(!File.Exists(now))continue;
            XDocument old,cur;try{old=XDocument.Load(before);cur=XDocument.Load(now);}catch{continue;}
            bool edit=false;
            foreach(var section in ReplaySections) {
                Func<XDocument,XElement> find=d=>d.Root.Elements("Section").FirstOrDefault(e=>(string)e.Attribute("name")==section);
                XElement o=find(old),c=find(cur);
                if(o==null&&c==null||o!=null&&c!=null&&XNode.DeepEquals(o,c))continue;
                if(c!=null&&o!=null)c.ReplaceWith(new XElement(o));else if(c!=null)c.Remove();else cur.Root.Add(new XElement(o));
                edit=true;
            }
            // Credits (StoryStage key 8): a replay of TRaynor01-03 also runs the library's debug tech purchase in
            // libCamp_gf_LoadCampaignData, which sets the credits to that mission's expected amount (2026-10-23: 0 after
            // replaying The Outlaws). A replay pays no reward, so the credits are the ones from before.
            Func<XDocument,XElement> credits=d=>d.Root.Elements("Section").Where(e=>(string)e.Attribute("name")=="StoryStage").Elements("Key").FirstOrDefault(k=>(string)k.Attribute("name")=="8");
            XElement oc=credits(old),cc=credits(cur);
            if(!(oc==null&&cc==null||oc!=null&&cc!=null&&XNode.DeepEquals(oc,cc))) {
                var stage=cur.Root.Elements("Section").FirstOrDefault(e=>(string)e.Attribute("name")=="StoryStage");
                if(cc!=null)cc.Remove();
                if(oc!=null){if(stage==null)cur.Root.Add(stage=new XElement("Section",new XAttribute("name","StoryStage")));stage.Add(new XElement(oc));}
                edit=true;
            }
            if(!edit)continue;
            using(var w=System.Xml.XmlWriter.Create(now,new System.Xml.XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true,IndentChars="    "}))cur.Save(w);
            changed++;
        }
        return changed;
    }
    // Campaign banks that are not readable XML any more (e.g. the game died while writing one).
    public static List<string> Unreadable(Settings settings,string who) {
        var bad=new List<string>();
        foreach(var p in Campaign(BanksDir(settings,who)))try{XDocument.Load(p);}catch{bad.Add(Path.GetFileName(p));}
        return bad;
    }
    public static void Restore(Settings settings,string who,string fromDir) {
        string to=BanksDir(settings,who);
        foreach(var p in Campaign(to).ToList())File.Delete(p);
        foreach(var p in Campaign(fromDir))File.Copy(p,Path.Combine(to,Path.GetFileName(p)),true);
    }
    public static IEnumerable<string> List(Settings settings) {
        string root=Path.Combine(settings.Workspace,"campaign","snapshots");
        return Directory.Exists(root)?Directory.GetDirectories(root).Select(Path.GetFileName):new string[0];
    }
}

class CampaignRun {
    readonly Settings settings;readonly string mode,host,campaignDir;readonly ManualResetEvent stop;readonly int basePort;
    bool Hosting{get{return mode=="host"||mode=="steam-host";}} bool Guest{get{return mode=="join"||mode=="steam-join";}} bool Local{get{return mode=="local";}}
    string Root(string who){return Path.Combine(settings.Workspace,"campaign","profile-"+who);}
    string Banks(string who){return settings.Product?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"StarCraft II","Banks"):Path.Combine(Root(who),"Documents","StarCraft II","Banks");}
    Tunnel tunnel;HashSet<string> bankNames,playerBanks;readonly Dictionary<string,string> hubMaps=new Dictionary<string,string>();readonly HashSet<string> backedUp=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};

    public CampaignRun(Settings settings,string mode,string host,string campaignDir,int basePort,ManualResetEvent stop){this.settings=settings;this.mode=mode;this.host=host;this.campaignDir=campaignDir;this.basePort=basePort;this.stop=stop;}

    public void Run() {
        string run=Path.Combine(settings.Workspace,"runs",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-campaign-"+mode);Directory.CreateDirectory(run);
        string log=Path.Combine(run,"launcher.log");Program.LogFile=log;
        if(!Directory.Exists(campaignDir))throw new Exception("Campaign folder not found: "+campaignDir);
        Program.CurrentInstall=settings.Install;GameClient.ForceWindowed=Local;
        // Test hook: SC2_COOP_TEST_STOP=<s> presses Stop after s seconds.
        string testStop=Environment.GetEnvironmentVariable("SC2_COOP_TEST_STOP");
        if(!string.IsNullOrEmpty(testStop)){int secs=int.Parse(testStop);new Thread(()=>{Thread.Sleep(secs*1000);Program.Log("Test: pressing Stop");stop.Set();}){IsBackground=true}.Start();}
        // Each player's hub carries their own color (local mode: one hub per window).
        bankNames=CampaignMaps.CampaignBanks(campaignDir,out playerBanks);Program.Log("Campaign banks synced host -> guest: "+string.Join(", ",bankNames.OrderBy(x=>x)));
        if(playerBanks.Count>0)Program.Log("Per-player banks (each player keeps their own; not synced or swapped): "+string.Join(", ",playerBanks.OrderBy(x=>x)));
        CampaignMaps.HubPlayerBanks=playerBanks;
        foreach(var who in Local?new[]{"host","guest"}:new[]{Hosting?"host":"guest"}){int c=ColorFor(who);
            hubMaps[who]=CampaignMaps.BuildKitHub(campaignDir,Path.Combine(settings.Workspace,"campaign","maps"),c,ChatRgb(c),Local?"-"+who:"",who=="guest");}
        hubTitle=CampaignMaps.Title(campaignDir,"TStory01");
        Session link=null;
        try {
            if(!Local) {
                var linkSettings=Copy(settings);linkSettings.MapSha256="campaign-kit-"+TrialPreparation.Recipe();
                link=new Session();int b=basePort;ulong lobby=0;string sc2Host;
                // Stop (GUI/Ctrl+C) must also end the wait for the other launcher.
                var stopLink=link;ThreadPool.RegisterWaitForSingleObject(stop,(o,t)=>stopLink.Stop.Set(),null,-1,true);
                // Steam join: the GUI passes the picked friend's lobby id (or "-" to wait for an invite); parse it as Session.Start does.
                if(mode=="steam-join"){string id=(host??"").Trim();if(id!=""&&id!="-"&&id!="friend"&&!ulong.TryParse(id,out lobby)&&!SteamSession.ParseConnect(id,out lobby))throw new Exception("Pick a Steam friend from the list, paste a lobby id, or leave it empty to wait for an invite");}
                tunnel=link.EstablishLink(linkSettings,mode,host,ref b,ref lobby,out sc2Host);tunnel.StartReading();
                CheckSameSetup();
                ExchangeProfiles();
            }
            if(Hosting||Local)LoadSave();
            if(Local)CopyBanks(Banks("host"),Banks("guest"));else if(Hosting)SendBanks();else ReceiveBanks(TimeSpan.FromMinutes(5));
            string mission=null;int difficulty=0;bool retry=false,replay=false;
            while(!stop.WaitOne(0)) {
                Program.LogFile=log;
                // After "Retry mission" in the defeat dialog the same mission starts again, without the hubs.
                if(!retry) {
                    var pick=HubStage();if(pick==null)break;
                    mission=(string)pick["mission"];difficulty=Convert.ToInt32(pick["difficulty"]);
                    replay=pick.ContainsKey("simulation")&&Convert.ToBoolean(pick["simulation"]);
                    if(mission.Equals("TSimulator",StringComparison.OrdinalIgnoreCase)||mission.Equals("TArcade",StringComparison.OrdinalIgnoreCase)) {
                        Program.Log("The "+mission.Substring(1)+" is not available in co-op yet; back to the hub.");continue;
                    }
                }
                if(Hosting||Local)ClearRetry();
                // A Mission Archives replay plays from the mission's snapshot, so that one stays.
                if(!retry&&!replay&&(Hosting||Local)&&!CampaignMaps.LastHubKit)DropMissionSnapshot(mission);
                missionReplay=replay;
                MissionStage(mission,difficulty);
                Program.LogFile=log;
                if(Hosting||Local){retry=RetryChosen();StoreSave();}
                if(Local)CopyBanks(Banks("host"),Banks("guest"));
                else if(Hosting){SendBanks();Send(new Dictionary<string,object>{{"type","next"},{"retry",retry}});}
                else {
                    ReceiveBanks(TimeSpan.FromMinutes(5));
                    var next=Take(Type("next"),60000);if(next==null)throw Gone("The host did not say what comes after the mission");
                    retry=Convert.ToBoolean(next["retry"]);
                }
                if(retry)Program.Log("Retrying "+mission+" (chosen after the defeat)");
            }
        } finally {
            Program.LogFile=log;
            if(saveLoaded)try{StoreSave();}catch(Exception e){Program.Log("Could not store this campaign's co-op save: "+e.Message);}
            DisposeSpares();Curtain.Hide();
            if(tunnel!=null)tunnel.Dispose();
            Program.Log("Campaign session ended");
        }
    }

    // Right after connecting: both launchers must have the same launcher version, adapters, campaign maps and mods, otherwise
    // a mission fails much later (join error 10, "differs from yours") with no hint why.
    // Names and colors: each launcher tells the other its player's choice; both build missions with the same colors.
    int hostColor=-1,guestColor=-1;
    void ExchangeProfiles() {
        Send(new Dictionary<string,object>{{"type","profile"},{"name",Program.MyName()},{"color",Program.PlayerColor},
            {"units",Program.SeparateUnits},{"money",Program.SeparateMoney},{"speed",Program.GameSpeed}});
        var theirs=Take(Type("profile"),30000);
        int mine=Program.PlayerColor,other=theirs==null?-1:Convert.ToInt32(theirs["color"]);
        hostColor=Hosting?mine:other;guestColor=Hosting?other:mine;
        // Separate-army choices are the host's.
        object u,m;var from=Hosting?null:theirs;
        CampaignMaps.Separate=new SeparateOptions{
            Units=SeparateOptions.Clean(from!=null&&from.TryGetValue("units",out u)?(string)u:Program.SeparateUnits,SeparateOptions.UnitChoices),
            Money=SeparateOptions.Clean(from!=null&&from.TryGetValue("money",out m)?(string)m:Program.SeparateMoney,SeparateOptions.MoneyChoices)};
        // So is the game speed (an older launcher sends none: the speed missions always had).
        object sp;CampaignMaps.GameSpeed=Program.CleanSpeed(from==null?Program.GameSpeed:from.TryGetValue("speed",out sp)?(string)sp:"faster");
        Program.Log("Mission game speed: "+CampaignMaps.GameSpeed+(Hosting?"":" (the host's choice)"));
        var colors=TeamColors.For(settings.Install,campaignDir);
        Program.Log("You play as "+Program.MyName()+(mine>=0?" ("+TeamColors.NameOf(colors,mine)+")":"")+"; your buddy as "+(theirs==null?"?":(string)theirs["name"])+(other>=0?" ("+TeamColors.NameOf(colors,other)+")":"")+".");
    }

    void CheckSameSetup() {
        string cacheFile=Path.Combine(settings.Workspace,"campaign","hash-cache.json");Dictionary<string,string> modPaths;
        var mine=CampaignFingerprint.Compute(settings.Install,campaignDir,cacheFile,out modPaths);
        Send(new Dictionary<string,object>{{"type","fingerprint"},{"items",mine}});
        var theirs=Take(Type("fingerprint"),120000);
        if(theirs==null)throw Gone("Your buddy's launcher did not report its campaign files (an older SC2Coop version?). Both players need the same version.");
        var theirItems=(Dictionary<string,object>)theirs["items"];
        var problems=CampaignFingerprint.Compare(mine,theirItems);
        if(problems.Count>0) {
            foreach(var p in problems)Program.Log("Setup difference: "+p);
            // Both launchers see the same differing mods and swap those mods' file lists, to say which files differ.
            var mods=CampaignFingerprint.DifferingMods(mine,theirItems);
            if(mods.Count>0) {
                var lists=CampaignFingerprint.FileLists(mods,modPaths,cacheFile);
                Send(new Dictionary<string,object>{{"type","fingerprint-files"},{"files",lists}});
                var their=Take(Type("fingerprint-files"),30000);
                if(their==null)Program.Log("Setup difference: your buddy's launcher sent no file list for "+string.Join(", ",mods.Select(m=>m.Substring(4)))+".");
                else {
                    var theirLists=(Dictionary<string,object>)their["files"];
                    foreach(var m in mods){object t;if(lists.ContainsKey(m)&&theirLists.TryGetValue(m,out t))
                        foreach(var line in CampaignFingerprint.DescribeFiles(m,(Dictionary<string,object>)lists[m],(Dictionary<string,object>)t))Program.Log("Setup difference: "+line);}
                }
            }
            throw new Exception(problems.Count+" difference"+(problems.Count==1?"":"s")+" between your setup and your buddy's (listed above). Fix "+(problems.Count==1?"it":"them")+" and start again.");
        }
        Program.Log("Setup check passed: same launcher, campaign maps and mods as your buddy ("+mine.Count+" items).");
    }

    // Control-message inbox: messages that arrive while waiting for something else are kept, not lost.
    readonly List<Dictionary<string,object>> pending=new List<Dictionary<string,object>>();
    Dictionary<string,object> Take(Func<Dictionary<string,object>,bool> want,int timeoutMs) {
        lock(pending){var hit=pending.FirstOrDefault(want);if(hit!=null){pending.Remove(hit);return hit;}}
        int end=Environment.TickCount+timeoutMs;
        while(true) {
            // Waits in short slices: Stop or a lost connection ends the wait at once (it used to hang up to 5 minutes, and
            // Stop did nothing; 2026-10-21 report). Callers get null; Gone() says why.
            int left=Math.Max(0,end-Environment.TickCount);string msg;
            if(tunnel.Controls.TryTake(out msg,Math.Min(left,250))) {
                var m=Json.Deserialize<Dictionary<string,object>>(msg);if(want(m))return m;
                lock(pending)pending.Add(m);
            }
            if(left==0||stop.WaitOne(0)||tunnel.Closed)return null;
        }
    }
    // The error for a wait that returned null: Stop, a lost connection, or the given reason.
    Exception Gone(string otherwise) {
        if(stop.WaitOne(0))return new Exception("Stopped");
        if(tunnel!=null&&tunnel.Closed)return new Exception("The connection to your buddy was lost (they may have stopped or closed the launcher)");
        return new Exception(otherwise);
    }
    // Hub round: both launchers advance it together at every hub stage. Pick/go/quit/ready/banks messages carry it, so a
    // message left over from an earlier round (e.g. both players pressed Start Mission) is never taken as new (2026-10-19).
    int round;
    Func<Dictionary<string,object>,bool> Type(params string[] types){int r=round;return m=>types.Contains((string)m["type"])&&m.ContainsKey("round")&&Convert.ToInt32(m["round"])==r;}
    void Send(Dictionary<string,object> m){m["round"]=round;tunnel.SendControl(Json.Serialize(m));}
    int missionSeq;

    static Settings Copy(Settings s){var c=Json.Deserialize<Settings>(Json.Serialize(s));c.ConfigDirectory=s.ConfigDirectory;return c;}

    // This launcher's player color; in local mode the guest window's comes from SC2_COOP_GUEST_COLOR.
    int ColorFor(string who) {
        if(Local&&who=="guest"){string gc=Environment.GetEnvironmentVariable("SC2_COOP_GUEST_COLOR");return gc!=null?TeamColors.Parse(gc):-1;}
        return Program.PlayerColor;
    }
    // A player color as RGB for chat names; the campaign default is Kit's player color (index 2).
    System.Drawing.Color ChatRgb(int color){return TeamColors.ToColor(color<0?2:color,TeamColors.For(settings.Install,campaignDir));}

    // --- hub stage --------------------------------------------------------------------------------------------
    // Echo: this hub was told to launch the mission the other player picked (FollowPick). Announced: its own pick was sent on.
    class Hub {public GameClient Client;public string Who;public int Seq,ChatSeen;public Dictionary<string,object> Pick;public bool Ended,Echo,Announced;}

    // One army: the host's campaign state counts. Before the host's hub closes for a mission the buddy picked, it saves its
    // state (BuildKitHub's -coopsave), as a pick in it would have; otherwise the host's purchases since the last mission
    // were lost. Waits up to 8 s for the hub's confirmation (CoopHub bank, Saved/seq). mission: the buddy's pick (Kit: it
    // starts from a fresh snapshot, as CoopHubLaunchMission does).
    void SaveHostHub(Hub h,string mission) {
        if(h==null||h.Who!="host"||h.Ended||h.Client.Process.HasExited)return;
        int before=ReadHubInt("host","Saved","seq");
        try{h.Client.Chat("-coopsave"+(mission!=null?" "+mission:""));}catch(Exception e){Program.Log("Could not ask the host's hub to save: "+e.Message);return;}
        for(int end=Environment.TickCount+8000;Environment.TickCount<end;Thread.Sleep(200))
            if(ReadHubInt("host","Saved","seq")>before){Program.Log("The host's hub saved its campaign state");return;}
        Program.Log("The host's hub did not confirm saving; purchases made there since the last mission may be missing");
    }

    // Both players see the launch: when one picks a mission, the other hub launches the same one (-cooplaunch, exactly that
    // mission, no A/B choice), so its mission briefing and the loading transition play there too instead of the hub just
    // closing (2026-10-22 report). The pick is known from the CoopHub bank as soon as it is made, before the briefing.
    // A Mission Archives replay is launched as one, at the difficulty picked for it.
    void FollowPick(Hub h,Dictionary<string,object> pick) {
        if(h==null||h.Ended||h.Echo||h.Announced||h.Client.Process.HasExited)return;
        h.Echo=true;string mission=(string)pick["mission"];
        bool replay=pick.ContainsKey("simulation")&&Convert.ToBoolean(pick["simulation"]);
        try{h.Client.Chat("-cooplaunch "+mission+(replay?" replay "+Convert.ToInt32(pick["difficulty"]):""));
            Program.Log("Launching "+mission+(replay?" (Mission Archives replay)":"")+" in this hub too (its briefing plays for both players)");}
        catch(Exception e){h.Echo=false;Program.Log("Could not launch "+mission+" in this hub: "+e.Message);}
    }
    // A hub launching the other player's pick: wait until its launch is done (up to 2 minutes, the length of a long briefing).
    // The host's hub saved its state with that launch, as with its own pick; if it never got there, it saves now.
    void FinishHub(Hub h,string mission) {
        if(h==null||h.Ended)return;
        if(h.Echo)for(int end=Environment.TickCount+120000;Environment.TickCount<end&&!stop.WaitOne(0)&&!HubEnded(h);)Thread.Sleep(300);
        if(!h.Ended)SaveHostHub(h,mission);
    }
    // A pick this hub just made (its CoopHub bank), not yet sent on.
    Dictionary<string,object> NewPick(Hub h) {
        if(h.Ended||h.Echo||h.Announced)return null;
        var p=ReadPick(h.Who);if(p==null||Convert.ToInt32(p["seq"])<=h.Seq)return null;
        h.Announced=true;return p;
    }
    int ReadHubInt(string who,string section,string key) {
        try {
            string path=Path.Combine(Banks(who),"CoopHub.SC2Bank");if(!File.Exists(path))return 0;
            var s=XDocument.Load(path).Root.Elements("Section").FirstOrDefault(x=>(string)x.Attribute("name")==section);if(s==null)return 0;
            var k=s.Elements("Key").FirstOrDefault(x=>(string)x.Attribute("name")==key);if(k==null)return 0;
            return (int?)k.Element("Value").Attribute("int")??0;
        } catch {return 0;}
    }

    // Hub chat (BuildKitHub's CoopHubChatOut): lines this hub's player typed since 'seen', from the CoopHub bank's Chat ring.
    List<string> ReadChat(string who,ref int seen) {
        var lines=new List<string>();string path=Path.Combine(Banks(who),"CoopHub.SC2Bank");if(!File.Exists(path))return lines;
        try {
            var section=XDocument.Load(path).Root.Elements("Section").FirstOrDefault(s=>(string)s.Attribute("name")=="Chat");if(section==null)return lines;
            Func<string,XElement> v=k=>section.Elements("Key").Where(e=>(string)e.Attribute("name")==k).Select(e=>e.Element("Value")).FirstOrDefault();
            var count=v("n");int n=count==null?0:int.Parse((string)count.Attribute("int"));
            if(seen<0||n<seen){seen=n;return lines;}
            for(int i=Math.Max(seen,n-16);i<n;i++){var l=v("l"+(i%16));if(l!=null)lines.Add((string)l.Attribute("string"));}
            seen=n;
        } catch{}  // the game may be writing the file; read again next time
        return lines;
    }
    // Shows the buddy's chat line in this hub (CoopHubChatIn), their name in their color.
    void HubChatIn(Hub h,string name,string line,System.Drawing.Color c) {
        if(h.Ended)return;
        try{h.Client.Chat("-coopmsg "+c.R+" "+c.G+" "+c.B+" "+name+": "+line);}catch(Exception e){Program.Log("Hub chat: "+e.Message);}
    }

    Dictionary<string,object> ReadPick(string who) {
        string path=Path.Combine(Banks(who),"CoopHub.SC2Bank");if(!File.Exists(path))return null;
        try {
            var section=XDocument.Load(path).Root.Elements("Section").FirstOrDefault(s=>(string)s.Attribute("name")=="Pick");if(section==null)return null;
            Func<string,XElement> v=k=>section.Elements("Key").Where(e=>(string)e.Attribute("name")==k).Select(e=>e.Element("Value")).FirstOrDefault();
            var d=new Dictionary<string,object>();d["mission"]=(string)v("mission").Attribute("string");
            d["difficulty"]=int.Parse((string)v("difficulty").Attribute("int"));d["seq"]=int.Parse((string)v("seq").Attribute("int"));
            // Replay from the Mission Archives (Kit saved its own simulation state with the pick; the flag is for the log).
            var sim=v("simulation");d["simulation"]=sim!=null&&sim.Attributes().Any(a=>a.Value=="1"||a.Value.Equals("true",StringComparison.OrdinalIgnoreCase));
            return d;
        } catch(Exception e){Program.Log("Campaign: could not read the hub pick: "+e.Message);return null;}
    }

    Hub StartHub(string who) {
        var h=new Hub{Who=who,ChatSeen=-1};var before=ReadPick(who);h.Seq=before==null?0:Convert.ToInt32(before["seq"]);
        ReadChat(who,ref h.ChatSeen);if(h.ChatSeen<0)h.ChatSeen=0;
        Directory.CreateDirectory(Root(who));
        h.Client=TakeSpare(who);if(h.Client!=null)Program.Log("Reusing the open StarCraft II for the hub ("+who+")");
        else h.Client=new GameClient(settings,Root(who),true,Program.Log);
        var created=h.Client.Request(1,Wire.Join(Wire.Msg(1,Wire.Str(1,hubMaps[who])),Wire.Msg(3,Wire.Num(1,1)),Wire.Num(6,1)),120).M(1);
        if(created.N(1)!=0)throw new Exception("Hub CreateGame failed: "+created.N(1)+" "+created.S(2));
        var joined=h.Client.Request(2,Wire.Join(Wire.Num(1,1),Wire.Msg(3,Wire.Num(1,settings.Raw?1UL:0)),Wire.Str(7,who=="guest"&&Local?"Buddy":Program.MyName())),300).M(2);
        if(joined.N(2)!=0)throw new Exception("Hub JoinGame failed: "+joined.N(2)+" "+joined.S(3));
        h.Client.Joined=true;Curtain.Hide();Program.Log("Hub open ("+who+"). Pick a mission on the star map / mission screen.");
        return h;
    }

    // True once the hub game is over (a mission was launched, or the player left).
    bool HubEnded(Hub h) {
        if(h.Ended)return true;
        if(h.Client.Process.HasExited){h.Ended=true;return true;}
        try{if(h.Client.Request(19,new byte[0],10).N(99)!=3)h.Ended=true;}catch{h.Ended=true;}
        if(h.Ended){var p=ReadPick(h.Who);if(p!=null&&Convert.ToInt32(p["seq"])>h.Seq)h.Pick=p;
            // Reuse: keep this StarCraft II for the mission. Otherwise close it at once (after a launch it doesn't quit by
            // itself; waiting for that cost 10 s per transition).
            if(ReuseForMission&&!h.Client.Process.HasExited){h.Client.Joined=false;spare[h.Who]=h.Client;}
            else try{h.Client.Dispose(1500);}catch{}}
        return h.Ended;
    }

    // Kit's library opens SC2's trigger debugger (TriggerDebugWindowOpen); close that window, it is not part of the game.
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f,IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    static readonly HashSet<int> debugLogged=new HashSet<int>();
    static readonly HashSet<IntPtr> debugHandled=new HashSet<IntPtr>();
    delegate bool EnumProc(IntPtr h,IntPtr l);
    static void CloseDebugWindows(int pid) {
        EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==(uint)pid){var t=new StringBuilder(64);GetWindowText(h,t,64);
            // Minimize it once; hiding it repeatedly fought the game and made both windows flicker (2026-10-19 report).
            if(t.ToString()=="Trigger Debugging"&&debugHandled.Add(h)){ShowWindow(h,6);
                if(debugLogged.Add(pid))Program.Log("Minimized StarCraft II's trigger debugger window (the campaign's scripts open it; it is harmless)");}}return true;},IntPtr.Zero);
    }

    void CloseHub(Hub h) {
        if(h==null||h.Ended)return;h.Ended=true;
        if(ReuseForMission&&!h.Client.Process.HasExited)try {
            // End the hub game (debug end_game, surrender) and keep the process for the mission.
            h.Client.Request(20,Wire.Msg(1,Wire.Msg(7,Wire.Num(1,1))),10);
            for(int i=0;i<20&&h.Client.Request(19,new byte[0],10).N(99)==3;i++)Thread.Sleep(250);
            h.Client.Joined=false;spare[h.Who]=h.Client;return;
        } catch(Exception e){Program.Log("Could not keep the hub's StarCraft II open: "+e.Message);}
        try{h.Client.Dispose(1500);}catch{}
    }

    // --- process reuse (SC2_COOP_REUSE=1, experimental) ----------------------------------------------------------
    // One StarCraft II per player stays open from hub to mission to hub: no window closing/reopening between them.
    // SC2_COOP_REUSE=hub: only mission -> hub (a solo game). =1: also hub -> mission (fails: the multiplayer join hangs).
    static bool Reuse{get{string r=Environment.GetEnvironmentVariable("SC2_COOP_REUSE");return r=="1"||r=="hub";}}
    static bool ReuseForMission{get{return Environment.GetEnvironmentVariable("SC2_COOP_REUSE")=="1";}}
    readonly Dictionary<string,GameClient> spare=new Dictionary<string,GameClient>();
    GameClient TakeSpare(string who) {
        GameClient c;if(!spare.TryGetValue(who,out c))return null;spare.Remove(who);
        try{ulong st=c.Request(19,new byte[0],10).N(99);if(!c.Process.HasExited&&(st==1||st==5))return c;Program.Log("Spare StarCraft II ("+who+") is in state "+st+"; starting a new one");}
        catch(Exception e){Program.Log("Spare StarCraft II ("+who+") unusable: "+e.Message);}
        try{c.Dispose(1500);}catch{}return null;
    }
    void DisposeSpares(){foreach(var c in spare.Values.ToList())try{c.Dispose(3000);}catch{}spare.Clear();}

    Dictionary<string,object> HubStage() {
        round++;
        lock(pending)pending.RemoveAll(m=>(string)m["type"]!="mission-over"&&(!m.ContainsKey("round")||Convert.ToInt32(m["round"])<round));
        var hubs=new List<Hub>();
        try {
            if(Local){hubs.Add(StartHub("host"));hubs.Add(StartHub("guest"));}else hubs.Add(StartHub(Hosting?"host":"guest"));
            TestLaunch(hubs[0]);TestHubChat(hubs);
            while(!stop.WaitOne(500)) {
                if(tunnel!=null&&tunnel.Closed)throw new Exception("Connection to your buddy was lost");
                Dictionary<string,object> m;
                while(tunnel!=null&&(m=Take(Type(Hosting?new[]{"pick","quit","chat","launching"}:new[]{"go","quit","chat","launching"}),0))!=null) {
                    string type=(string)m["type"];
                    if(type=="chat"){HubChatIn(hubs[0],(string)m["name"],(string)m["line"],m.ContainsKey("rgb")?System.Drawing.Color.FromArgb(Convert.ToInt32(m["rgb"])):ChatRgb(-1));continue;}
                    if(type=="launching"){Program.Log((Hosting?"Your buddy":"The host")+" is launching "+m["mission"]);FollowPick(hubs[0],m);continue;}
                    if(Hosting&&type=="pick"){Program.Log("Your buddy picked "+m["mission"]);FinishHub(hubs[0],(string)m["mission"]);ShowMissionCurtain(m);hubs.ForEach(CloseHub);
                        // The host's campaign state counts (one army). The buddy's launcher still sends its banks; they are not used.
                        if(Take(Type("banks"),60000)==null)throw Gone("Your buddy's launcher did not finish the pick");
                        Go(m);return m;}
                    if(Guest&&type=="go"){Program.Log("Starting "+m["mission"]+" (picked on the host's side or confirmed)");FinishHub(hubs[0],null);ShowMissionCurtain(m);hubs.ForEach(CloseHub);return m;}
                    if(type=="quit"){Program.Log((Hosting?"Your buddy":"The host")+" ended the campaign session.");SaveHostHub(hubs[0],null);hubs.ForEach(CloseHub);return null;}
                }
                // A pick made in a hub: the other hub launches it too (FollowPick), while this one plays its briefing.
                foreach(var h in hubs) {
                    var p=NewPick(h);if(p==null)continue;
                    if(Local)FollowPick(hubs.First(o=>o!=h),p);
                    else Send(new Dictionary<string,object>{{"type","launching"},{"mission",p["mission"]},{"simulation",p["simulation"]},{"difficulty",p["difficulty"]}});
                }
                // Hub chat out: to the buddy's launcher, or in local mode straight to the other window's hub.
                foreach(var h in hubs)foreach(var line in ReadChat(h.Who,ref h.ChatSeen)) {
                    if(Local)HubChatIn(hubs.First(o=>o!=h),h.Who=="guest"?"Buddy":Program.MyName(),line,ChatRgb(ColorFor(h.Who)));
                    else Send(new Dictionary<string,object>{{"type","chat"},{"name",Program.MyName()},{"line",line},{"rgb",ChatRgb(Program.PlayerColor).ToArgb()&0xFFFFFF}});
                }
                foreach(var h in hubs)if(!h.Ended)CloseDebugWindows(h.Client.Process.Id);
                if(!hubs[0].Ended)Curtain.Track(hubs[0].Client.Process);
                foreach(var h in hubs) {
                    // A hub following the other player's pick ends with that launch; the pick's own hub (or the other
                    // launcher) decides.
                    if(h.Echo||!HubEnded(h))continue;
                    if(h.Pick==null){Program.Log("The hub closed without a mission pick; ending the campaign session.");hubs.ForEach(CloseHub);if(tunnel!=null)Send(new Dictionary<string,object>{{"type","quit"}});return null;}
                    var pick=h.Pick;pick["type"]="pick";Program.Log((h.Who=="host"?"Host":"Guest")+" picked "+pick["mission"]+" (difficulty "+pick["difficulty"]+(Convert.ToBoolean(pick["simulation"])?", Mission Archives replay":"")+")");
                    ShowMissionCurtain(pick);
                    // A pick in the buddy's hub (local mode: the second window): the host's hub finishes the same launch (or
                    // saves its state) first. Local mode, host's pick: the second window finishes its briefing.
                    if(Local)FinishHub(hubs.First(o=>o!=h),h.Who=="guest"?(string)pick["mission"]:null);
                    hubs.ForEach(CloseHub);
                    if(Guest){Send(pick);SendBanks("guest");return WaitGo();}
                    if(Hosting)Go(pick);
                    return pick;
                }
            }
            // Stop pressed: the host's hub keeps purchases made since its last save; tell the other launcher, so it ends cleanly
            // instead of seeing a lost connection.
            if(Hosting||Local)SaveHostHub(hubs[0],null);
            if(tunnel!=null&&!tunnel.Closed)try{Send(new Dictionary<string,object>{{"type","quit"}});}catch{}
            return null;
        } finally {hubs.ForEach(CloseHub);}
    }

    // Test hooks. SC2_COOP_TEST_LAUNCH=TRaynor01,TRaynor02,...: the first hub (host's) launches the next mission of the list
    // by chat after SC2_COOP_TEST_LAUNCH_DELAY seconds (default 45). SC2_COOP_TEST_WIN=<s>[,now|cheat|lose]: player 1 types
    // the matching -coop command s seconds after joining each mission.
    Queue<string> testLaunches;
    void TestLaunch(Hub h) {
        if(testLaunches==null)testLaunches=new Queue<string>((Environment.GetEnvironmentVariable("SC2_COOP_TEST_LAUNCH")??"").Split(new[]{','},StringSplitOptions.RemoveEmptyEntries));
        if(testLaunches.Count==0||Guest)return;
        string mission=testLaunches.Dequeue();int delay=int.Parse(Environment.GetEnvironmentVariable("SC2_COOP_TEST_LAUNCH_DELAY")??"45");
        new Thread(()=>{if(stop.WaitOne(delay*1000)||h.Ended)return;Program.Log("Test: hub chat -cooplaunch "+mission);
            try{Program.Log("Test: chat result "+h.Client.Chat("-cooplaunch "+mission)+", API status "+h.Client.Request(19,new byte[0],10).N(99));}catch(Exception e){Program.Log("Test launch: "+e.Message);}}){IsBackground=true}.Start();
    }
    // SC2_COOP_TEST_HUBCHAT=<s>:<text>;...: the first hub (host's) types each text, s seconds after the previous step;
    // "guest:<text>" is typed in the second hub (local mode).
    void TestHubChat(List<Hub> hubs) {
        string spec=Environment.GetEnvironmentVariable("SC2_COOP_TEST_HUBCHAT");if(string.IsNullOrEmpty(spec)||Guest)return;
        var steps=spec.Split(';').Select(x=>{int i=x.IndexOf(':');return Tuple.Create(int.Parse(x.Substring(0,i)),x.Substring(i+1));}).ToList();
        new Thread(()=>{foreach(var st in steps){if(stop.WaitOne(st.Item1*1000))return;
            var h=st.Item2.StartsWith("guest:")&&hubs.Count>1?hubs[1]:hubs[0];if(h.Ended)return;string line=h==hubs[0]?st.Item2:st.Item2.Substring(6);
            Program.Log("Test: hub chat ("+h.Who+") "+line);try{h.Client.Chat(line);}catch(Exception e){Program.Log("Test hub chat: "+e.Message);return;}}}){IsBackground=true}.Start();
    }
    // SC2_COOP_TEST_CHAT=<s>:<text>;<s>:<text>...: player 1 types each text, each s seconds after the previous step
    // (the first counted from joining the mission). Runs in every mission.
    void TestChat(Session session) {
        string spec=Environment.GetEnvironmentVariable("SC2_COOP_TEST_CHAT");if(string.IsNullOrEmpty(spec)||Guest)return;
        var steps=spec.Split(';').Select(x=>{int i=x.IndexOf(':');return Tuple.Create(int.Parse(x.Substring(0,i)),x.Substring(i+1));}).ToList();
        new Thread(()=>{
            while(!session.Stop.WaitOne(500)&&!(session.Clients.Count>0&&session.Clients[0].Joined)){}
            foreach(var st in steps){if(session.Stop.WaitOne(st.Item1*1000))return;
                Program.Log("Test: mission chat "+st.Item2);try{session.Clients[0].Chat(st.Item2);}catch(Exception e){Program.Log("Test chat: "+e.Message);return;}}
        }){IsBackground=true}.Start();
    }
    void TestWin(Session session) {
        string spec=Environment.GetEnvironmentVariable("SC2_COOP_TEST_WIN");if(string.IsNullOrEmpty(spec)||Guest)return;
        var parts=spec.Split(',');int delay=int.Parse(parts[0]);string kind=parts.Length>1?parts[1]:"now";
        string command=kind=="cheat"?"-coopwin":kind=="lose"?"-cooplose":"-coopwinnow";
        new Thread(()=>{
            while(!session.Stop.WaitOne(500)&&!(session.Clients.Count>0&&session.Clients[0].Joined)){}
            if(session.Stop.WaitOne(delay*1000))return;
            Program.Log("Test: mission chat "+command);try{session.Clients[0].Chat(command);}catch(Exception e){Program.Log("Test win: "+e.Message);}
        }){IsBackground=true}.Start();
    }

    string hubTitle="Campaign";
    void ShowMissionCurtain(Dictionary<string,object> pick){Curtain.Show(CampaignMaps.Title(campaignDir,(string)pick["mission"]),"Starting the mission…");}

    void Go(Dictionary<string,object> pick){var go=new Dictionary<string,object>(pick);go["type"]="go";if(tunnel!=null)Send(go);}

    Dictionary<string,object> WaitGo() {
        var m=Take(Type("go","quit"),180000);  // the host's hub may still be playing the same launch (FinishHub)
        if(m==null)throw Gone("The host did not confirm the mission");
        return (string)m["type"]=="go"?m:null;
    }

    // --- mission stage ----------------------------------------------------------------------------------------
    void MissionStage(string mission,int difficulty) {
        int guest;string map;
        if(Local){hostColor=ColorFor("host");guestColor=ColorFor("guest");
            CampaignMaps.Separate=new SeparateOptions{Units=SeparateOptions.Clean(Program.SeparateUnits,SeparateOptions.UnitChoices),Money=SeparateOptions.Clean(Program.SeparateMoney,SeparateOptions.MoneyChoices)};CampaignMaps.GameSpeed=Program.GameSpeed;}
        try {map=CampaignMaps.BuildMission(campaignDir,mission,difficulty,Path.Combine(settings.Workspace,"campaign","maps",Local?"local":(Hosting?"host":"guest")),out guest,hostColor,guestColor);}
        catch(Exception e){Program.Log("Cannot play "+mission+" in co-op: "+e.Message);if(tunnel!=null)Send(new Dictionary<string,object>{{"type","ready"},{"hash","error"},{"error",e.Message}});return;}
        var ms=Copy(settings);ms.MapPath=map;ms.MapSha256=Program.Sha(File.ReadAllBytes(map));ms.Recipe=TrialPreparation.Recipe();
        if(!Local) {
            Curtain.Status("Waiting for your buddy…");
            // Both launchers built the mission from their own campaign files; they must be byte-identical.
            Send(new Dictionary<string,object>{{"type","ready"},{"hash",ms.MapSha256}});
            var ready=Take(Type("ready"),240000);string theirs=ready==null?null:(string)ready["hash"];  // the buddy may still be finishing the launch briefing (FinishHub)
            if(ready==null&&(stop.WaitOne(0)||tunnel.Closed))throw Gone("");
            if(theirs=="error"){Program.Log("Your buddy can't start "+mission+": "+(ready.ContainsKey("error")?ready["error"]:"unknown error")+" Back to the hub.");return;}
            if(theirs!=ms.MapSha256){Program.Log("Your buddy's "+mission+" differs from yours (different campaign files?); back to the hub.");return;}
        }
        var session=new Session{PresetTunnel=tunnel,PresetRoots=Local?new[]{Root("host"),Root("guest")}:new[]{Root(Hosting?"host":"guest")}};
        try{session.Race=MapInfoPlayers.Player1Race(MapArchive.ReadFile(map,"MapInfo"));if(session.Race!=1)Program.Log(mission+": joining as "+(session.Race==3?"Protoss":"Zerg")+" (player 1's race)");}catch(Exception e){Program.Log("Could not read player 1's race: "+e.Message);}
        // Separate armies in the map's own slot: the guest joins with that slot's race.
        bool guestEndsItself=Environment.GetEnvironmentVariable("SC2_COOP_SEPARATE")=="1";
        try{var info=MapArchive.ReadFile(map,"MapInfo");int second=MapInfoPlayers.SecondHuman(info);if(second==guest){session.GuestRace=MapInfoPlayers.RaceOf(info,second);guestEndsItself=true;}}catch(Exception e){Program.Log("Could not read the guest slot's race: "+e.Message);}
        string[] roles=Local?new[]{"host","guest"}:new[]{Hosting?"host":"guest"};
        if(Reuse) {
            var kept=ReuseForMission?roles.Select(TakeSpare).ToArray():new GameClient[roles.Length];
            if(kept.All(c=>c!=null)){session.PresetClients=kept;Program.Log("Reusing the open StarCraft II for the mission");}
            else foreach(var c in kept)if(c!=null)try{c.Dispose(1500);}catch{}
            session.KeepClients=true;
        }
        // The campaign state the mission starts from (the host's banks are the campaign); kept to undo a damaged bank.
        string autosave=null;if(Hosting||Local)try{autosave=CampaignSnapshots.Autosave(settings,"host",mission);}catch(Exception e){Program.Log("Could not back up the campaign banks: "+e.Message);}
        // The guest's own per-player banks (their own progress, written on the guest's PC): kept the same way on the guest's side.
        string guestAutosave=null;if((Guest||Local)&&playerBanks.Count>0)try{guestAutosave=CampaignSnapshots.Autosave(settings,"guest","guest-"+mission);}catch(Exception e){Program.Log("Could not back up your campaign banks: "+e.Message);}
        var stopWait=ThreadPool.RegisterWaitForSingleObject(stop,(o,t)=>session.Stop.Set(),null,-1,true);
        string sessionMode=Local?"local":(Hosting?(mode=="steam-host"?"steam-host":"host"):(mode=="steam-join"?"steam-join":"join"));
        int seq=++missionSeq;bool done=false;Thread watcher=null;
        if(tunnel!=null) {
            lock(pending)pending.RemoveAll(m=>(string)m["type"]=="mission-over"&&Convert.ToInt32(m["seq"])<seq);
            watcher=new Thread(()=>{while(!done){var m=Take(x=>(string)x["type"]=="mission-over"&&Convert.ToInt32(x["seq"])==seq,500);
                if(m!=null){
                    if(m.ContainsKey("crash")&&Convert.ToBoolean(m["crash"]))Program.Log("Your buddy's StarCraft II closed unexpectedly; ending your mission too. "+NotCounted);
                    else {
                        // Separate armies: the guest's slot gets player 1's result right after (CoopGuestVictory), and the map
                        // may still save that player's own banks then. Give this game a moment to end by itself.
                        if(guestEndsItself&&Guest)for(int end=Environment.TickCount+10000;!done&&Environment.TickCount<end;)Thread.Sleep(200);
                        if(done)return;
                        Program.Log("Your buddy's mission ended; closing yours.");
                    }
                    session.Stop.Set();
                    // A join still waiting for the buddy (up to 3 minutes) can't be cancelled through the API; close that game.
                    foreach(var c in session.Clients.ToArray())if(!c.Joined)try{c.Process.Kill();}catch{}
                    return;}}}){IsBackground=true};
            watcher.Start();
        }
        string title=CampaignMaps.Title(campaignDir,mission);
        session.Progress=stage=>{
            if(stage=="joining")Curtain.Status(Local?"Loading…":"Loading… (both players must finish loading)");
            else if(stage=="ready"){Curtain.Hide();var c=session.Clients.FirstOrDefault();if(c!=null)new Thread(()=>{while(!done&&!c.Process.HasExited){Curtain.Track(c.Process);Thread.Sleep(2000);}}){IsBackground=true}.Start();}
            else if(stage=="ended")Curtain.Show(hubTitle,"Returning to the campaign…");
        };
        TestWin(session);
        TestChat(session);
        TestCrash(session);
        try {session.Start(ms,sessionMode,Hosting||Local?"127.0.0.1":host,tunnel!=null?tunnel.BasePort:basePort,true,0);}
        catch(Exception e){
            // Both game windows failing (e.g. both joins timing out) arrive as one AggregateException: name each cause.
            var agg=e as AggregateException;
            Program.Log("Mission ended with an error: "+(agg!=null?string.Join(" | ",agg.Flatten().InnerExceptions.Select(x=>x.GetBaseException().Message).Distinct()):e.GetBaseException().Message));
        }
        finally {
            done=true;if(watcher!=null)watcher.Join(2000);stopWait.Unregister(null);
            if(tunnel!=null&&!tunnel.Closed)tunnel.SendControl(Json.Serialize(new Dictionary<string,object>{{"type","mission-over"},{"seq",seq},{"crash",session.Crashed}}));
        }
        if(session.Crashed)Program.Log(NotCounted);
        if(Reuse)for(int i=0;i<session.Clients.Count&&i<roles.Length;i++)if(!session.Clients[i].Process.HasExited)spare[roles[i]]=session.Clients[i];
        if(Hosting||Local) {
            var bad=CampaignSnapshots.Unreadable(settings,"host");
            if(bad.Count>0&&autosave!=null){CampaignSnapshots.Restore(settings,"host",autosave);
                Program.Log("Damaged campaign bank"+(bad.Count==1?"":"s")+" after the mission ("+string.Join(", ",bad)+"); restored the campaign as it was before "+mission+".");}
            else if(bad.Count>0)Program.Log("Damaged campaign bank"+(bad.Count==1?"":"s")+" after the mission: "+string.Join(", ",bad)+" (no backup to restore).");
            else if(missionReplay&&!CampaignMaps.LastHubKit&&autosave!=null)try {
                int n=CampaignSnapshots.RestoreAfterReplay(settings,"host",autosave,mission);
                if(n>0)Program.Log("Mission Archives replay: the campaign progress the replay wrote back was restored ("+n+" bank"+(n==1?"":"s")+")");
            } catch(Exception e){Program.Log("Could not restore the campaign progress after the replay: "+e.Message);}
        }
        if(guestAutosave!=null) {
            var bad=CampaignSnapshots.Unreadable(settings,"guest");
            if(bad.Count>0){CampaignSnapshots.Restore(settings,"guest",guestAutosave);
                Program.Log("Damaged bank"+(bad.Count==1?"":"s")+" on the guest's side after the mission ("+string.Join(", ",bad)+"); restored the guest's banks as they were before "+mission+".");}
        }
    }
    bool missionReplay;  // the mission being played is a Mission Archives replay
    const string NotCounted="The mission does not count; the campaign continues from where it was before the mission.";

    // Test hook: SC2_COOP_TEST_CRASH=<s>[,<client index>] kills that StarCraft II (default 0, this launcher's first) s seconds
    // after joining, like a crash. In local mode index 1 is the guest's window.
    void TestCrash(Session session) {
        string spec=Environment.GetEnvironmentVariable("SC2_COOP_TEST_CRASH");if(string.IsNullOrEmpty(spec))return;
        var parts=spec.Split(',');int delay=int.Parse(parts[0]),index=parts.Length>1?int.Parse(parts[1]):0;
        new Thread(()=>{
            while(!session.Stop.WaitOne(500)&&!(session.Clients.Count>index&&session.Clients[index].Joined)){}
            if(session.Stop.WaitOne(delay*1000))return;
            Program.Log("Test: killing StarCraft II client "+index);try{session.Clients[index].Process.Kill();}catch(Exception e){Program.Log("Test crash: "+e.Message);}
            // ",corrupt" also truncates the host's WCampaign bank, like a crash in the middle of writing it.
            if(parts.Length>2&&parts[2]=="corrupt")try{string b=Path.Combine(CampaignSnapshots.BanksDir(settings,"host"),"WCampaign.SC2Bank");File.WriteAllText(b,File.ReadAllText(b).Substring(0,200));Program.Log("Test: truncated "+b);}catch(Exception e){Program.Log("Test corrupt: "+e.Message);}
        }){IsBackground=true}.Start();
    }

    // Blizzard's library keeps a snapshot of the campaign state in each mission's own bank (named like the map) from its
    // first start, for restarts. A mission the launcher starts isn't a transition map, so the library loads that snapshot
    // whenever it exists: purchases made since an earlier attempt were undone and its old credits came back (2026-10-22).
    // A start from the hub (not a retry) gets a fresh snapshot, as from the hub in solo play. Kit: CoopHubLaunchMission.
    void DropMissionSnapshot(string mission) {
        foreach(var name in new[]{mission,mission+".SC2Map"}) {
            string p=Path.Combine(Banks("host"),name+".SC2Bank");
            try{if(File.Exists(p)){File.Delete(p);Program.Log(mission+" starts from the current campaign state (its old snapshot from an earlier start was dropped)");}}
            catch(Exception e){Program.Log("Could not drop "+mission+"'s old campaign snapshot: "+e.Message);}
        }
    }

    // --- retry after a defeat: the mission's dialog writes player 1's CoopMission bank (End/retry) on the host -----------
    string RetryBank{get{return Path.Combine(Banks("host"),"CoopMission.SC2Bank");}}
    void ClearRetry(){try{if(File.Exists(RetryBank))File.Delete(RetryBank);}catch(Exception e){Program.Log("Could not reset the retry choice: "+e.Message);}}
    bool RetryChosen() {
        try{return File.Exists(RetryBank)&&Regex.IsMatch(File.ReadAllText(RetryBank),@"<Key name=""retry"">\s*<Value int=""[1-9]");}
        catch(Exception e){Program.Log("Could not read the retry choice: "+e.Message);return false;}
    }

    // --- co-op saves: one set of campaign banks per campaign ------------------------------------------------------
    // The game keeps every API game's banks in one folder by bank name, and campaigns share names (TCampaign in every
    // campaign on Blizzard's scripts), so playing a second campaign would overwrite the first one's progress. The host's
    // launcher keeps each campaign's banks in %LOCALAPPDATA%\SC2Coop\saves\<campaign> (per Windows user, so a newer launcher
    // in another folder finds them; test runs: the workspace), swaps them in when a session starts and stores them back
    // after every mission and at the end. A campaign without a co-op save starts fresh: banks of the same names already in
    // the folder (editor test runs, other API tools, another campaign on the same scripts) would start it mid-campaign.
    // WriteBanks backs them up first.
    bool saveLoaded;
    string SaveDir{get{return CampaignSaves.Dir(settings,campaignDir);}}
    void LoadSave() {
        string dir=Banks("host");
        if(Directory.Exists(SaveDir)) {
            var m=PackBanks(SaveDir);WriteBanks(dir,(Dictionary<string,object>)m["files"],(List<string>)m["absent"]);
            Program.Log("Loaded the co-op save of "+Path.GetFileName(SaveDir)+" ("+((Dictionary<string,object>)m["files"]).Count+" banks)");
        } else {
            WriteBanks(dir,new Dictionary<string,object>(),bankNames);
            Program.Log("New co-op save for "+Path.GetFileName(SaveDir)+": the campaign starts from the beginning");
        }
        saveLoaded=true;StoreSave();
    }
    void StoreSave() {
        Directory.CreateDirectory(SaveDir);string dir=Banks("host");
        foreach(var n in bankNames) {
            string p=Path.Combine(dir,n+".SC2Bank"),q=Path.Combine(SaveDir,n+".SC2Bank");
            if(File.Exists(p))File.Copy(p,q,true);else if(File.Exists(q))File.Delete(q);
        }
    }

    // --- bank sync (host's campaign state wins) ---------------------------------------------------------------
    Dictionary<string,object> PackBanks(string dir) {
        var files=new Dictionary<string,object>();var absent=new List<string>();
        foreach(var n in bankNames){string p=Path.Combine(dir,n+".SC2Bank");if(File.Exists(p))files[n]=Convert.ToBase64String(File.ReadAllBytes(p));else absent.Add(n);}
        return new Dictionary<string,object>{{"type","banks"},{"files",files},{"absent",absent}};
    }
    void SendBanks(){SendBanks(Hosting?"host":"guest");}
    void SendBanks(string who) {
        var m=PackBanks(Banks(who));m["round"]=round;string text=Json.Serialize(m);
        if(text.Length>900000)throw new Exception("Campaign banks too large to send ("+text.Length/1024+" KB)");
        tunnel.SendControl(text);Program.Log("Sent campaign banks to your buddy ("+((Dictionary<string,object>)m["files"]).Count+" files)");
    }
    void ReceiveBanks(TimeSpan timeout){ReceiveBanks(timeout,Hosting?"host":"guest");}
    void ReceiveBanks(TimeSpan timeout,string who) {
        var m=Take(Type("banks"),(int)timeout.TotalMilliseconds);
        if(m==null)throw Gone("Did not receive the campaign state from the other launcher");
        WriteBanks(Banks(who),(Dictionary<string,object>)m["files"],((System.Collections.ArrayList)m["absent"]).Cast<string>());
    }
    void CopyBanks(string from,string to) {var m=PackBanks(from);WriteBanks(to,(Dictionary<string,object>)m["files"],(List<string>)m["absent"]);}
    void WriteBanks(string dir,Dictionary<string,object> files,IEnumerable<string> absent) {
        Directory.CreateDirectory(dir);
        if(!backedUp.Contains(dir)) {
            // The guest's own banks of the same names (e.g. their own campaign progress) are kept in a backup first.
            string backup=Path.Combine(settings.Workspace,"campaign","bank-backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach(var n in bankNames){string p=Path.Combine(dir,n+".SC2Bank");if(File.Exists(p)){Directory.CreateDirectory(backup);File.Copy(p,Path.Combine(backup,n+".SC2Bank"),true);}}
            if(Directory.Exists(backup))Program.Log("Your previous campaign banks were backed up to "+backup);
            backedUp.Add(dir);
        }
        foreach(var kv in files)File.WriteAllBytes(Path.Combine(dir,kv.Key+".SC2Bank"),Convert.FromBase64String((string)kv.Value));
        foreach(var n in absent){string p=Path.Combine(dir,n+".SC2Bank");if(File.Exists(p))File.Delete(p);}
        Program.Log("Campaign state updated ("+files.Count+" banks)");
    }
}
