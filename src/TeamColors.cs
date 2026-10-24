// Team colors as the campaign defines them. StarCraft II's 16 base colors (CGame TeamColors), changed or extended by the
// campaign's mods in dependency order: <TeamColors index="N"> changes color N, <TeamColors> without index appends one.
// A campaign can, for example, rename color 11 (Dark Green) and add Dark Green again as 21.
// PlayerSetColorIndex uses these indices, so the launcher's list must match the campaign's.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

class TeamColor {public int Index;public string Name;public Color Swatch;public override string ToString(){return Name;}}

static class TeamColors {
    static readonly string[] BaseNames={"White","Red","Blue","Teal","Purple","Yellow","Orange","Green","Light Pink","Violet","Light Grey","Dark Green","Brown","Light Green","Dark Grey","Pink"};
    static readonly int[] BaseRgb={0xFFFFFF,0xB4141E,0x0042FF,0x1CA7EA,0x540081,0xEBE129,0xFE8A0E,0x168000,0xCCA6FC,0x1F01C9,0x525494,0x106246,0x4E2A04,0x96FF91,0x232323,0xE55BB0};
    static readonly Dictionary<string,List<TeamColor>> cache=new Dictionary<string,List<TeamColor>>(StringComparer.OrdinalIgnoreCase);

    public static List<TeamColor> Base() {
        return BaseNames.Select((n,i)=>new TeamColor{Index=i,Name=n,Swatch=Color.FromArgb(BaseRgb[i]|unchecked((int)0xFF000000))}).ToList();
    }
    // The campaign's colors as most missions see them: missions can depend on different mods (in one tested campaign
    // 26 of 29 load only Kit, two load the campaign's own mod with 9 more colors), so the table of the most common dependency set is
    // used. The base list when the folder or install can't be read.
    public static List<TeamColor> For(string install,string campaignDir) {
        if(string.IsNullOrEmpty(campaignDir)||!Directory.Exists(campaignDir)||string.IsNullOrEmpty(install))return Base();
        lock(cache){List<TeamColor> hit;if(cache.TryGetValue(campaignDir,out hit))return hit;}
        var list=Base();var names=new Dictionary<string,string>();var keys=new List<string>();
        for(int i=0;i<16;i++)keys.Add("UI/TeamColor"+i.ToString("00"));
        try {
            string mission=Directory.GetFileSystemEntries(campaignDir,"*.SC2Map").OrderBy(p=>p,StringComparer.OrdinalIgnoreCase)
                .Where(p=>!Regex.IsMatch(Path.GetFileName(p),@"^t(story|arcade)",RegexOptions.IgnoreCase))
                .Select(p=>{string deps="";try{deps=string.Join("|",Regex.Matches(Encoding.UTF8.GetString(MapArchive.ReadFile(p,"DocumentInfo")??new byte[0]),@"file:[^<]*").Cast<Match>().Select(m=>m.Value.ToLowerInvariant()));}catch{}return new{p,deps};})
                .GroupBy(x=>x.deps).OrderByDescending(g=>g.Count()).Select(g=>g.First().p).FirstOrDefault();
            if(mission!=null) {
                var order=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Visit(install,MapArchive.ReadFile(mission,"DocumentInfo"),order,seen);
                foreach(var rel in order)Apply(Path.Combine(install,rel),list,keys,names);
                Apply(mission,list,keys,names);
            }
            for(int i=0;i<list.Count;i++){string n;if(keys[i]!=null&&names.TryGetValue(keys[i],out n))list[i].Name=n;}
        } catch(Exception e){Program.Log("Could not read the campaign's team colors: "+e.Message);return Base();}
        lock(cache)cache[campaignDir]=list;
        return list;
    }
    public static string NameOf(List<TeamColor> list,int index){
        if(IsRgb(index))return "#"+(index&0xFFFFFF).ToString("X6");
        var c=list.FirstOrDefault(x=>x.Index==index);return c!=null?c.Name:"color "+index;
    }

    // Player colors are either a table index or any RGB, stored as RgbFlag|0xRRGGBB (profile, profile exchange, BuildMission).
    // An RGB becomes a new table entry appended to the mission copy's own data, so it needs that mission's table length.
    public const int RgbFlag=0x1000000;
    public static bool IsRgb(int color){return color>=RgbFlag;}
    public static int FromRgb(Color c){return RgbFlag|(c.ToArgb()&0xFFFFFF);}
    // "#RRGGBB" or a table index (profile file, SC2_COOP_COLOR / SC2_COOP_GUEST_COLOR).
    public static int Parse(string s) {
        s=(s??"").Trim();
        if(s.StartsWith("#"))return RgbFlag|int.Parse(s.Substring(1),NumberStyles.HexNumber);
        return int.Parse(s,CultureInfo.InvariantCulture);
    }
    public static string Format(int color){return IsRgb(color)?"#"+(color&0xFFFFFF).ToString("X6"):color.ToString(CultureInfo.InvariantCulture);}
    public static Color ToColor(int color,List<TeamColor> list) {
        if(IsRgb(color))return Color.FromArgb(color&0xFFFFFF|unchecked((int)0xFF000000));
        var c=list.FirstOrDefault(x=>x.Index==color);return c!=null?c.Swatch:Color.Gray;
    }
    // Number of team colors a mission sees: base 16 + every mod it loads + its own data.
    public static int CountFor(string install,string mission){return TableFor(install,mission).Count;}
    // The team colors one mission sees (index, swatch = Diffuse).
    public static List<TeamColor> TableFor(string install,string mission) {
        var list=Base();var keys=new List<string>(new string[16]);var names=new Dictionary<string,string>();
        var order=new List<string>();Visit(install,MapArchive.ReadFile(mission,"DocumentInfo"),order,new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach(var rel in order)Apply(Path.Combine(install,rel),list,keys,names);
        Apply(mission,list,keys,names);
        return list;
    }
    // A <TeamColors> entry (appended, no index) for an RGB: Diffuse and Emissive the color, Selected a bit lighter, like Kit's own.
    public static XElement Entry(int color) {
        float r=((color>>16)&255)/255f,g=((color>>8)&255)/255f,b=(color&255)/255f;var inv=CultureInfo.InvariantCulture;
        Func<float,float,float,string> v=(x,y,z)=>string.Format(inv,"{0:0.000000},{1:0.000000},{2:0.000000},1.000000",x,y,z);
        Func<float,float> up=x=>Math.Min(1f,x+0.2f);
        return new XElement("TeamColors",
            new XElement("Value",new XAttribute("index","Diffuse"),new XAttribute("value",v(r,g,b))),
            new XElement("Value",new XAttribute("index","Emissive"),new XAttribute("value",v(r,g,b))),
            new XElement("Value",new XAttribute("index","Selected"),new XAttribute("value",v(up(r),up(g),up(b)))));
    }

    // Full paths of a map's custom mods in dependency order (a mod's own dependencies first).
    public static List<string> ModPaths(string install,byte[] documentInfo) {
        var order=new List<string>();if(string.IsNullOrEmpty(install))return order;
        Visit(install,documentInfo,order,new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return order.Select(rel=>Path.Combine(install,rel)).ToList();
    }
    // Mods in dependency order (a mod's own dependencies first).
    static void Visit(string install,byte[] documentInfo,List<string> order,HashSet<string> seen) {
        if(documentInfo==null)return;
        foreach(Match m in Regex.Matches(Encoding.UTF8.GetString(documentInfo),@"<Value>([^<]*file:[^<]*)</Value>")) {
            string v=m.Groups[1].Value;string rel=v.Substring(v.IndexOf("file:")+5).Replace('/','\\');
            if(!rel.StartsWith("Mods\\",StringComparison.OrdinalIgnoreCase)||!seen.Add(rel))continue;
            string path=Path.Combine(install,rel);if(!Directory.Exists(path)&&!File.Exists(path))continue;
            byte[] inner=null;try{inner=MapArchive.ReadFile(path,"DocumentInfo");}catch{}
            Visit(install,inner,order,seen);order.Add(rel);
        }
    }
    static void Apply(string doc,List<TeamColor> list,List<string> keys,Dictionary<string,string> names) {
        byte[] data=null,strings=null;
        try{data=MapArchive.ReadFile(doc,@"Base.SC2Data\GameData\GameData.xml");}catch{}
        try{strings=MapArchive.ReadFile(doc,@"enUS.SC2Data\LocalizedData\GameStrings.txt");}catch{}
        if(strings!=null)foreach(Match m in Regex.Matches(Encoding.UTF8.GetString(strings),@"^(UI/TeamColor\w+)=(.+?)\r?$",RegexOptions.Multiline))names[m.Groups[1].Value]=m.Groups[2].Value;
        if(data==null)return;
        XDocument x;try{x=XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿'));}catch{return;}
        foreach(var game in x.Root.Elements("CGame").Where(g=>((string)g.Attribute("id")??"Dflt")=="Dflt"))
            foreach(var t in game.Elements("TeamColors")) {
                int index;TeamColor c;
                if(int.TryParse((string)t.Attribute("index"),out index)&&index>=0&&index<list.Count)c=list[index];
                else{c=new TeamColor{Index=list.Count,Name="Color "+list.Count,Swatch=Color.Gray};list.Add(c);keys.Add(null);index=c.Index;}
                var name=t.Element("Name");string key=name!=null?(string)name.Attribute("value"):(string)t.Attribute("Name");
                if(key!=null)keys[index]=key;
                var diffuse=t.Elements("Value").FirstOrDefault(v=>(string)v.Attribute("index")=="Diffuse");
                if(diffuse!=null) {
                    var p=((string)diffuse.Attribute("value")??"").Split(',');float r,g,b;var inv=CultureInfo.InvariantCulture;
                    if(p.Length>=3&&float.TryParse(p[0],NumberStyles.Float,inv,out r)&&float.TryParse(p[1],NumberStyles.Float,inv,out g)&&float.TryParse(p[2],NumberStyles.Float,inv,out b))
                        c.Swatch=Color.FromArgb(Clamp(r),Clamp(g),Clamp(b));
                }
            }
    }
    static int Clamp(float v){return Math.Max(0,Math.Min(255,(int)Math.Round(v*255)));}
}
