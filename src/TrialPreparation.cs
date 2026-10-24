using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;

class TrialPreparation {
    const string OriginalExeHash="541a027834eb1c9d0004baa24568e3b07670df479f17256b475609a9158ac9f4";
    [DllImport("CascLib.dll",CharSet=CharSet.Ansi)] [return:MarshalAs(UnmanagedType.I1)] static extern bool CascOpenStorage(string path,uint locale,out IntPtr storage);
    [DllImport("CascLib.dll")] [return:MarshalAs(UnmanagedType.I1)] static extern bool CascCloseStorage(IntPtr storage);
    [DllImport("CascLib.dll",CharSet=CharSet.Ansi)] static extern IntPtr CascFindFirstFile(IntPtr storage,string mask,IntPtr found,IntPtr listfile);
    [DllImport("CascLib.dll")] [return:MarshalAs(UnmanagedType.I1)] static extern bool CascFindNextFile(IntPtr search,IntPtr found);
    [DllImport("CascLib.dll")] [return:MarshalAs(UnmanagedType.I1)] static extern bool CascFindClose(IntPtr search);
    [DllImport("CascLib.dll",CharSet=CharSet.Ansi)] [return:MarshalAs(UnmanagedType.I1)] static extern bool CascOpenFile(IntPtr storage,string name,uint locale,uint flags,out IntPtr file);
    [DllImport("CascLib.dll")] [return:MarshalAs(UnmanagedType.I1)] static extern bool CascGetFileSize64(IntPtr file,out ulong size);
    [DllImport("CascLib.dll")] [return:MarshalAs(UnmanagedType.I1)] static extern bool CascReadFile(IntPtr file,byte[] buffer,uint size,out uint actual);
    [DllImport("CascLib.dll")] [return:MarshalAs(UnmanagedType.I1)] static extern bool CascCloseFile(IntPtr file);
    [DllImport("CascLib.dll")] static extern uint GetCascError();
    public static int FindUnique(byte[] data,byte[] needle) {
        int found=-1;for(int i=0;i<=data.Length-needle.Length;i++){bool matches=true;for(int j=0;j<needle.Length;j++)if(data[i+j]!=needle[j]){matches=false;break;}if(matches){if(found!=-1)throw new Exception("Expected a unique import string");found=i;}}
        if(found<0)throw new Exception("Expected import string missing");return found;
    }
    public static void Prepare(string source,string destination) {
        source=Path.GetFullPath(source);destination=Path.GetFullPath(destination);
        string allowed=Path.GetFullPath(Path.Combine(Program.Base,"local/game"));
        if(!string.Equals(destination,allowed,StringComparison.OrdinalIgnoreCase))throw new Exception("Preparation writes only to this package's local/game directory");
        if(destination.StartsWith(source.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)||source.StartsWith(destination.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)||source.Equals(destination,StringComparison.OrdinalIgnoreCase))throw new Exception("Source and test copy must be separate directories");
        string executable=Path.Combine(source,"Versions/Base98310/SC2_x64.exe");
        if(!File.Exists(executable)||Program.Sha(File.ReadAllBytes(executable))!=OriginalExeHash)throw new Exception("This trial supports only an unmodified SC2 5.0.17.98310 executable");
        if(Directory.Exists(destination)&&Directory.EnumerateFileSystemEntries(destination).Any())throw new Exception("local/game already exists. Select its prepared trial.json instead; existing files will not be overwritten");
        Directory.CreateDirectory(destination);Program.Log("Copying SC2 into "+destination+". Original installation is read only.");
        var info=new ProcessStartInfo("robocopy.exe",Program.Q(source)+" "+Program.Q(destination)+" /E /COPY:DAT /R:1 /W:1 /XJ /NFL /NDL /NJH /NJS /NP");info.UseShellExecute=false;info.CreateNoWindow=true;
        using(var copy=Process.Start(info)){copy.WaitForExit();if(copy.ExitCode>=8)throw new Exception("SC2 copy failed (robocopy "+copy.ExitCode+")");}
        string copyExe=Path.Combine(destination,"Versions/Base98310/SC2_x64.exe");
        byte[] bytes=File.ReadAllBytes(copyExe);if(Program.Sha(bytes)!=OriginalExeHash)throw new Exception("Copied executable checksum mismatch");
        File.Copy(copyExe,copyExe+".original",false);
        int at=FindUnique(bytes,Encoding.ASCII.GetBytes("USER32.dll\0"));Array.Copy(Encoding.ASCII.GetBytes("sc2win.dll\0"),0,bytes,at,11);File.WriteAllBytes(copyExe,bytes);
        File.Copy(Path.Combine(Program.Base,"sc2win.dll"),Path.Combine(destination,"Versions/Base98310/sc2win.dll"),false);
        BuildMap(destination);
    }
    static SortedDictionary<string,byte[]> Extract(string install) {
        const string prefix="campaigns\\liberty.sc2campaign\\base.sc2maps\\maps\\campaign\\traynor02.sc2map\\";
        var files=new SortedDictionary<string,byte[]>(StringComparer.Ordinal);IntPtr storage;
        if(!CascOpenStorage(install,2,out storage))throw new Exception("Cannot read copied SC2 data: "+GetCascError());
        try {
            IntPtr buffer=Marshal.AllocHGlobal(4096);
            try {
                IntPtr search=CascFindFirstFile(storage,"*",buffer,IntPtr.Zero);if(search==IntPtr.Zero||search==new IntPtr(-1))throw new Exception("Cannot enumerate copied CASC storage");
                int scanned=0;
                try {do {
                    scanned++;if(scanned>2000000)throw new Exception("CASC enumeration exceeded its bounded scan limit");if(scanned%200000==0)Program.Log("Scanned "+scanned+" storage entries");
                    string name=Marshal.PtrToStringAnsi(buffer);
                    if(!name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))continue;
                    string relative=name.Substring(prefix.Length).ToLowerInvariant();
                    if(relative.Contains(".sc2data\\")&&!relative.StartsWith("base.sc2data\\")&&!relative.StartsWith("enus.sc2data\\"))continue;
                    IntPtr file;if(!CascOpenFile(storage,name,2,0,out file))throw new Exception("Cannot read mission file: "+relative);
                    try {ulong size;if(!CascGetFileSize64(file,out size)||size>256*1024*1024)throw new Exception("Invalid mission file size");byte[] data=new byte[(int)size];uint actual;if(!CascReadFile(file,data,(uint)size,out actual)||actual!=size)throw new Exception("Mission extraction failed");files.Add(relative,data);}finally{CascCloseFile(file);}
                }while(CascFindNextFile(search,buffer));}finally{CascFindClose(search);}
            }finally{Marshal.FreeHGlobal(buffer);}
        }finally{CascCloseStorage(storage);}
        if(files.Count!=41)throw new Exception("Unexpected stock mission content: "+files.Count+" files (expected 41)");
        return files;
    }
    // Product mode: read the player's own install (CASC, read-only) and build the map into this package's local folder.
    public static void PrepareProduct(string source) {
        source=Path.GetFullPath(source);
        string executable=Path.Combine(source,"Versions/Base98310/SC2_x64.exe");
        if(!File.Exists(executable))throw new Exception("No StarCraft II 5.0.17.98310 found in "+source+" (expected Versions/Base98310/SC2_x64.exe). Select the StarCraft II folder and make sure the game is up to date.");
        if(Program.Sha(File.ReadAllBytes(executable))!=OriginalExeHash)throw new Exception("Your StarCraft II executable is a different build than this launcher supports (5.0.17.98310)");
        BuildMap(source,true);
    }
    // Map recipe: bump RecipeVersion when BuildMap's script edits change; the adapter files are hashed.
    const int RecipeVersion=9;
    // Model entries the adapter scripts use (campaign_guest_ui.galaxy): "PingViewArea" frames the ally's camera on the
    // minimap, with Liberty's Hud_FOV model (defined like this in a Wings of Liberty+ mod; no stock entry
    // uses that asset). Merged into the map's own ModelData.xml, or added as one: standard-named catalog files in a map's
    // Base.SC2Data load by name (The Outlaws has a ModelData.xml and no GameData.xml catalog list).
    public static void AddCoopModels(IDictionary<string,byte[]> files) {
        const string path="base.sc2data\\gamedata\\modeldata.xml";
        string key=files.Keys.FirstOrDefault(k=>string.Equals(k.Replace('/','\\'),path,StringComparison.OrdinalIgnoreCase));
        var doc=key!=null?XDocument.Parse(Encoding.UTF8.GetString(files[key]).TrimStart((char)0xFEFF)):new XDocument(new XElement("Catalog"));
        if(doc.Root.Elements("CModel").Any(e=>(string)e.Attribute("id")=="PingViewArea"))return;
        doc.Root.Add(new XElement("CModel",new XAttribute("id","PingViewArea"),new XElement("Model",new XAttribute("value","Assets\\UI\\Feedback\\Hud_FOV\\Hud_FOV.m3"))));
        files[key??path]=Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"+doc.Root.ToString());
    }
    public static string Recipe() {
        var parts=new[]{"campaign_diagnostics.galaxy","campaign_shared_control.galaxy","campaign_targeting.galaxy","campaign_guest_ui.galaxy"}.Select(f=>File.ReadAllBytes(Path.Combine(Program.Base,f)));
        return RecipeVersion+"-"+Program.Sha(parts.SelectMany(x=>x).ToArray()).Substring(0,16);
    }
    public static void BuildMap(string install) {BuildMap(install,false);}
    public static void BuildMap(string install,bool product) {
        Program.Log("Reading stock The Outlaws from copied CASC data…");
        var files=Extract(install);byte[] info=files["mapinfo"],stockScript=files["mapscript.galaxy"];
        Program.Log("Stock MapInfo SHA256 "+Program.Sha(info)+"; script SHA256 "+Program.Sha(stockScript));
        if(Program.Sha(info)!="65b7c143ec090d79fc3c181993fc4bec2b74d6ea2596b4fc2659ef5dfa7c8615"||Program.Sha(stockScript)!="758ad9402fb7fbfaee5b02db8d5529fe3c2a4c638d5ee4ab06a4cbdc228eed64")throw new Exception("Stock mission identity mismatch");
        // Build-specific bounded edit; all existing faction records and the variant tail stay intact.
        if(BitConverter.ToUInt32(info,135)!=6)throw new Exception("Unexpected mission player metadata");
        // Clone the human's complete race/start/decal configuration. Both empty-race and cloned
        // variants still exhibited the input/guest-observation issues; race is not a proven cause.
        byte[] record=info.Skip(162).Take(47).ToArray();record[0]=6;
        info=Wire.Join(info.Take(377).ToArray(),record,info.Skip(377).ToArray());Array.Copy(BitConverter.GetBytes(7),0,info,135,4);files["mapinfo"]=info;
        string script=Encoding.UTF8.GetString(stockScript),marker="void InitMap () {";
        if(script.Split(new[]{marker},StringSplitOptions.None).Length!=2)throw new Exception("Mission initialization marker mismatch");
        // Every objective state read/write in the stock script goes through the coop layer: a completed or failed
        // objective ends the API game (see campaign_shared_control.galaxy).
        int objectiveCalls=Regex.Matches(script,@"\bObjective[GS]etState\(").Count;
        if(objectiveCalls!=19)throw new Exception("Objective call count mismatch ("+objectiveCalls+")");
        script=Regex.Replace(script,@"\bObjective([GS])etState\(","CoopObjective$1etState(");
        // The tutorial review opens the Help panel over both games ~20 s in (easy difficulties, no order given yet); in
        // co-op it covers the screen at the worst time. The tutorials stay reachable in the Help menu.
        string helpPopup="            HelpPanelDisplayPage(PlayerGroupAll(), c_helpPanelPageTutorials);\r\n";
        if(script.Split(new[]{helpPopup},StringSplitOptions.None).Length!=2){helpPopup=helpPopup.Replace("\r\n","\n");if(script.Split(new[]{helpPopup},StringSplitOptions.None).Length!=2)throw new Exception("Tutorial popup marker mismatch");}
        script=script.Replace(helpPopup,"            // coop: tutorial Help popup removed\n");
        script=script.Replace(marker,File.ReadAllText(Path.Combine(Program.Base,"campaign_diagnostics.galaxy"))+"\n"+marker+"\n    PlayerSetDifficulty(1, 2);");
        script="void CoopSharedControlInit();\nvoid CoopSharedControlUiInit();\nvoid CoopTargetingInit();\nvoid CoopGuestUiInit();\nint coopGuestPings = 0;\nint coopGuestCamKeys = 0;\nstring coopGuestPingLast = \"\";\nint coopWaveSendReturns = 0;\nint coopObjectiveOutcomes = 0;\nint CoopObjectiveGetState(int objective);\nvoid CoopObjectiveSetState(int objective, int state);\n"+script;
        script=Regex.Replace(script,@"(    AIAttackWaveSend\([^\n]*\);)","$1\n    coopWaveSendReturns += 1;");
        script=script.Replace("    InitTriggers();","    InitTriggers();\n    CoopDiagnosticsInit();\n    CoopTargetingInit();");
        script=script.Replace("    TriggerExecute(gt_Init02Players, true, true);","    TriggerExecute(gt_Init02Players, true, true);\n    CoopSharedControlInit();");
        // Set the guest's initial camera once, after the stock intro configures player 1's camera.
        string camera="    libNtve_gf_SwooshCamera(gv_pLAYER_01_USER,";
        var lines=script.Split('\n').ToList();int cameraAt=lines.FindIndex(x=>x.StartsWith(camera,StringComparison.Ordinal));if(cameraAt<0)throw new Exception("Stock starting camera marker missing");
        lines.Insert(cameraAt+1,"    CameraPan(6, CameraInfoGetTarget(CameraInfoFromId(1117)), 0.0, -1.0, 10.0, false);\n    CoopSharedControlUiInit();\n    CoopGuestUiInit();");script=string.Join("\n",lines);
        script+="\n"+File.ReadAllText(Path.Combine(Program.Base,"campaign_shared_control.galaxy"))+"\n"+File.ReadAllText(Path.Combine(Program.Base,"campaign_targeting.galaxy"))+"\n"+File.ReadAllText(Path.Combine(Program.Base,"campaign_guest_ui.galaxy"));files["mapscript.galaxy"]=Encoding.UTF8.GetBytes(script);
        var banks=XDocument.Parse(Encoding.UTF8.GetString(files["banklist.xml"]));foreach(int player in new[]{1,6})banks.Root.Add(new XElement("Bank",new XAttribute("Name","CoopCampaignProbe"),new XAttribute("Player",player)));
        banks.Root.Add(new XElement("Bank",new XAttribute("Name","CoopBridge"),new XAttribute("Player",6)));
        files["banklist.xml"]=Encoding.UTF8.GetBytes(banks.ToString());
        string maps=product?Path.Combine(Program.Base,"local"):Path.Combine(install,"Maps");Directory.CreateDirectory(maps);string target=Path.Combine(maps,"OutlawsCoopProbe.SC2Map");
        if(File.Exists(target))File.Copy(target,target+".before-trial-"+DateTime.Now.ToString("yyyyMMddHHmmssfff"),false);
        AddCoopModels(files);
        File.WriteAllBytes(target,Mpq.Pack(files));
        var settings=new Settings{Recipe=Recipe(),Install=install,Workspace=Path.Combine(Program.Base,"local"),MapSha256=Program.Sha(File.ReadAllBytes(target)),Product=product,MapPath=product?"local/OutlawsCoopProbe.SC2Map":null};
        File.WriteAllText(Path.Combine(Program.Base,"trial.json"),new JavaScriptSerializer().Serialize(settings));Program.Log("Trial map built; SHA256 "+settings.MapSha256);
    }
}

// MPQ v0, uncompressed single-unit blocks. Algorithm operates on the public MPQ table format.
class Mpq {
    static uint[] table=MakeTable();
    static uint[] MakeTable() {var t=new uint[1280];uint seed=0x100001;for(int i=0;i<256;i++)for(int j=0;j<5;j++){seed=(seed*125+3)%0x2aaaab;uint hi=(seed&65535)<<16;seed=(seed*125+3)%0x2aaaab;t[i+j*256]=hi|(seed&65535);}return t;}
    static uint Hash(string text,int type) {unchecked{uint a=0x7fed7fed,b=0xeeeeeeee;foreach(byte c in Encoding.ASCII.GetBytes(text.ToUpperInvariant())){a=table[type*256+c]^(a+b);b=c+a+b+(b<<5)+3;}return a;}}
    static byte[] Encrypt(byte[] bytes,uint key) {unchecked{uint seed=0xeeeeeeee;var result=new byte[bytes.Length];for(int i=0;i<bytes.Length;i+=4){seed+=table[0x400+(key&255)];uint value=BitConverter.ToUInt32(bytes,i);Array.Copy(BitConverter.GetBytes(value^(key+seed)),0,result,i,4);key=((~key<<21)+0x11111111)|(key>>11);seed=value+seed+(seed<<5)+3;}return result;}}
    public static byte[] Pack(SortedDictionary<string,byte[]> files) {
        files=new SortedDictionary<string,byte[]>(files,StringComparer.Ordinal);files["(listfile)"]=Encoding.ASCII.GetBytes(string.Join("\r\n",files.Keys.Concat(new[]{"(listfile)"}).OrderBy(x=>x,StringComparer.Ordinal))+"\r\n");
        int size=128;while(size<files.Count*2)size*=2;
        byte[] hashes=Enumerable.Repeat((byte)255,size*16).ToArray();
        using(var data=new MemoryStream())using(var blocks=new MemoryStream())using(var bw=new BinaryWriter(blocks)) {
            int index=0;foreach(var file in files) {
                int slot=(int)(Hash(file.Key,0)%(uint)size);while(BitConverter.ToUInt32(hashes,slot*16+12)!=uint.MaxValue)slot=(slot+1)%size;
                byte[] entry=Wire.Join(BitConverter.GetBytes(Hash(file.Key,1)),BitConverter.GetBytes(Hash(file.Key,2)),new byte[4],BitConverter.GetBytes(index++));Array.Copy(entry,0,hashes,slot*16,16);
                bw.Write((uint)(32+data.Length));bw.Write((uint)file.Value.Length);bw.Write((uint)file.Value.Length);bw.Write(0x81000000U);data.Write(file.Value,0,file.Value.Length);
            }
            byte[] h=Encrypt(hashes,Hash("(hash table)",3)),b=Encrypt(blocks.ToArray(),Hash("(block table)",3));
            using(var output=new MemoryStream())using(var w=new BinaryWriter(output)) {
                w.Write(new byte[]{77,80,81,26});w.Write(32U);w.Write((uint)(32+data.Length+h.Length+b.Length));w.Write((ushort)0);w.Write((ushort)3);w.Write((uint)(32+data.Length));w.Write((uint)(32+data.Length+h.Length));w.Write((uint)size);w.Write((uint)files.Count);w.Write(data.ToArray());w.Write(h);w.Write(b);return output.ToArray();
            }
        }
    }
}
