// Separate armies: the guest is an allied player with its own army instead of a co-owner of player 1's.
// - A mission whose map has a second human (User) player besides player 1 is played this way: the guest takes the map
//   author's slot, with the forces the author placed there (CampaignMaps.BuildMission).
// - SC2_COOP_SEPARATE=1 (experiment) does it on a normal one-human map: the guest gets a cloned slot and copies of player 1's
//   starting units plus a flying Command Center.
// Mission script rewrites (both cases), applied by CampaignMaps.BuildMission:
// - Unit groups of player 1 (objective regions, counts, the defeat check) count both armies, and "owned by player 1" also
//   means "owned by the guest" (skipped when the map already counts the guest's units itself).
// - Money and tech given to player 1 (PlayerModifyProperty*, TechTree* setters) is mirrored to the guest; mission money can
//   be split instead (SeparateOptions.Money).
// - Units the mission gives player 1 once it is under way (rescues, reinforcements: UnitSetOwner, unit creation) go to the
//   player whose units are nearest, alternate, or go to both (SeparateOptions.Units). Heroes and anything given during a
//   cinematic stay player 1's.
// - Unit vision shared with player 1 is shared with the guest too; alliances with every other player are copied from
//   player 1 (except control and spending).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Host's choices for missions with separate armies; the guest's launcher uses the host's (CampaignRun.ExchangeProfiles).
class SeparateOptions {
    // Mission-given units: "nearest" (the player whose units are closest: who found them), "alternate", "both".
    public string Units="nearest";
    // Money the mission gives once under way: "both" (each player gets the full amount) or "split" (half each).
    public string Money="both";
    public static readonly string[] UnitChoices={"nearest","alternate","both"},MoneyChoices={"both","split"};
    public static string Clean(string v,string[] choices){return choices.Contains(v)?v:choices[0];}
}

static class SeparateArmies {
    static readonly string[][] Mirrored={
        new[]{"PlayerModifyPropertyFixed","int inProp, int inOper, fixed inVal","inProp, inOper, inVal"},
        new[]{"TechTreeAbilityAllow","abilcmd inAbilCmd, bool allow","inAbilCmd, allow"},
        new[]{"TechTreeBehaviorAllow","string behaviorType, bool allow","behaviorType, allow"},
        new[]{"TechTreeRequirementsEnable","bool enable","enable"},
        new[]{"TechTreeRestrictionsEnable","bool enable","enable"},
        new[]{"TechTreeUnitAllow","string unitType, bool allow","unitType, allow"},
        new[]{"TechTreeUpgradeAddLevel","string upgradeType, int levels","upgradeType, levels"},
        new[]{"TechTreeUpgradeAllow","string upgradeType, bool allow","upgradeType, allow"},
    };

    // Player-1 names in a script: 1, constants "= 1" and globals set once to 1 in InitGlobals (gv_pLAYER01_USER = 1;) whose
    // names say player 1 / user. Same for the guest's slot (the author's own name for it, e.g. gv_pLAYER01_USER2 = 14;).
    static List<string> Names(string s,int player) {
        var names=new List<string>{player.ToString()};
        string n=player.ToString();
        foreach(Match m in Regex.Matches(s,@"^(?:const int (gv_\w+) = "+n+@";|    (gv_\w+) = "+n+@";)",RegexOptions.Multiline)) {
            string name=m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value;
            if(!Regex.IsMatch(name,@"USER|PLAYER|_p\d+_|^gv_p\d+\b",RegexOptions.IgnoreCase))continue;
            if(!m.Groups[1].Success&&Regex.Matches(s,@"^\s*"+name+@" = ",RegexOptions.Multiline).Count!=1)continue;
            if(!m.Groups[1].Success&&!Regex.IsMatch(s,@"^int "+name+";",RegexOptions.Multiline))continue;
            names.Add(name);
        }
        return names.Distinct().ToList();
    }

    // Splits "f(a, g(b, c), "x,y")" at the top-level commas from an opening parenthesis; returns the index after the closing one.
    static int Args(string s,int open,List<string> args) {
        int depth=0,start=open+1;bool str=false;
        for(int i=open;i<s.Length;i++) {
            char c=s[i];
            if(str){if(c=='\\')i++;else if(c=='"')str=false;continue;}
            if(c=='"')str=true;
            else if(c=='(')depth++;
            else if(c==')'){depth--;if(depth==0){args.Add(s.Substring(start,i-start).Trim());return i+1;}}
            else if(c==','&&depth==1){args.Add(s.Substring(start,i-start).Trim());start=i+1;}
            else if(c==';'&&depth>0)return -1;
        }
        return -1;
    }
    // Rewrites calls name(...) whose argument at index `at` is one of `players`; rewrite gets the arguments, returns the new call.
    static string Calls(string s,string name,int count,int at,List<string> players,Func<List<string>,string> rewrite,ref int n) {
        var sb=new StringBuilder();int pos=0;
        foreach(Match m in Regex.Matches(s,@"\b"+name+@"\(")) {
            if(m.Index<pos)continue;
            var args=new List<string>();int end=Args(s,m.Index+m.Length-1,args);
            if(end<0||args.Count!=count||!players.Contains(args[at]))continue;
            sb.Append(s,pos,m.Index-pos).Append(rewrite(args));pos=end;n++;
        }
        return sb.Append(s,pos,s.Length-pos).ToString();
    }

    // Resource pickups done in game data (no script call): the pickup unit's auto-cast ability targets the unit that walks over
    // it and runs a CEffectModifyPlayer for that unit's player, e.g. the Liberty pallets' PickupMinerals100/PickupGas100
    // abilities (effects of the same names, 100 each). Liberty's two plus abilities in the map or its mods whose effect (or effect
    // set) is a player effect with resources: (ability, property 0 minerals / 1 vespene, amount). Abilities the mission script
    // already handles itself (AbilityCommand("name", ...)) are left out.
    public static List<Tuple<string,int,int>> Pickups(string install,SortedDictionary<string,byte[]> map,string script) {
        var list=new List<Tuple<string,int,int>>{Tuple.Create("PickupMinerals100",0,100),Tuple.Create("PickupGas100",1,100)};
        var docs=new List<string>();byte[] b;
        var files=new[]{@"Base.SC2Data\GameData\EffectData.xml",@"Base.SC2Data\GameData\AbilData.xml",@"Base.SC2Data\GameData.xml"};
        try {
            foreach(var mod in TeamColors.ModPaths(install,map.TryGetValue("DocumentInfo",out b)?b:null))
                foreach(var name in files)try{var d=MapArchive.ReadFile(mod,name);if(d!=null)docs.Add(Encoding.UTF8.GetString(d));}catch{}
        } catch(Exception e){Program.Log("Could not read the mods' pickup data: "+e.Message);}
        foreach(var name in files)if(map.TryGetValue(name,out b))docs.Add(Encoding.UTF8.GetString(b));
        string all=string.Join("\n",docs);
        var gains=new Dictionary<string,List<Tuple<int,int>>>();
        foreach(Match m in Regex.Matches(all,@"<CEffectModifyPlayer id=""(\w+)""[^>]*>(.*?)</CEffectModifyPlayer>",RegexOptions.Singleline)) {
            var r=Regex.Matches(m.Groups[2].Value,@"<Resources index=""(Minerals|Vespene)"" value=""(\d+)""").Cast<Match>()
                .Select(x=>Tuple.Create(x.Groups[1].Value=="Minerals"?0:1,int.Parse(x.Groups[2].Value))).Where(x=>x.Item2>0).ToList();
            if(r.Count>0)gains[m.Groups[1].Value]=r;
        }
        var sets=new Dictionary<string,List<string>>();
        foreach(Match m in Regex.Matches(all,@"<CEffectSet id=""(\w+)""[^>]*>(.*?)</CEffectSet>",RegexOptions.Singleline))
            sets[m.Groups[1].Value]=Regex.Matches(m.Groups[2].Value,@"<EffectArray value=""(\w+)""").Cast<Match>().Select(x=>x.Groups[1].Value).ToList();
        foreach(Match m in Regex.Matches(all,@"<CAbilEffectTarget id=""(\w+)""[^>]*>(.*?)</CAbilEffectTarget>",RegexOptions.Singleline)) {
            var e=Regex.Match(m.Groups[2].Value,@"<Effect index=""0"" value=""(\w+)""");if(!e.Success)continue;
            var effects=sets.ContainsKey(e.Groups[1].Value)?sets[e.Groups[1].Value]:new List<string>{e.Groups[1].Value};
            var r=effects.Where(gains.ContainsKey).SelectMany(x=>gains[x]).ToList();if(r.Count==0)continue;
            list.RemoveAll(x=>x.Item1==m.Groups[1].Value);
            foreach(var g in r)list.Add(Tuple.Create(m.Groups[1].Value,g.Item1,g.Item2));
        }
        list.RemoveAll(x=>script.Contains("AbilityCommand(\""+x.Item1+"\""));
        return list;
    }

    // decls go before the mission script (globals and prototypes, so the mission can call the wrappers), body after it (the
    // wrappers read mission globals such as gv_inCinematic, which must be declared first: a reference to a later global
    // makes the whole map script fail to load, silently).
    public static string Apply(string s,int guestSlot,bool authored,bool kit,SeparateOptions options,List<Tuple<string,int,int>> pickups,out string decls,out string body) {
        string code;s=Apply(s,guestSlot,authored,kit,options,pickups??new List<Tuple<string,int,int>>(),out code);
        var d=new StringBuilder();
        foreach(var line in code.Split('\n')) {
            if(Regex.IsMatch(line,@"^(const )?\w+ \w+ = [^;]*;$"))d.Append(line).Append('\n');
            var f=Regex.Match(line,@"^(\w+ Coop\w+\([^)]*\)) \{");if(f.Success)d.Append(f.Groups[1].Value).Append(";\n");
        }
        decls=d.ToString();
        body=string.Join("\n",code.Split('\n').Where(l=>!Regex.IsMatch(l,@"^(const )?\w+ \w+ = [^;]*;$")));
        return s;
    }
    static string Apply(string s,int guestSlot,bool authored,bool kit,SeparateOptions options,List<Tuple<string,int,int>> pickups,out string code) {
        options=options??new SeparateOptions();string guest=guestSlot.ToString();
        var p1=Names(s,1);var mine=Names(s,guestSlot);
        string P1="(?:"+string.Join("|",p1.Select(Regex.Escape))+")",G="(?:"+string.Join("|",mine.Select(Regex.Escape))+")";
        int groups=0,owners=0,mirrored=0,given=0,vision=0;
        // A map that already counts the guest's units (UnitGroup(..., guest, ...) somewhere) does its own counting.
        bool ownCounts=authored&&Regex.IsMatch(s,@"\bUnitGroup\([^;]*,\s*"+G+@"\s*,");
        if(!ownCounts) {
            s=Regex.Replace(s,@"\bUnitGroup\(([^,;()]*),\s*"+P1+@"\s*,",m=>{groups++;return "CoopUnitGroupBoth("+m.Groups[1].Value+", ";});
            s=Regex.Replace(s,@"\bUnitGetOwner\(((?:[^()]|\([^()]*\))*)\)\s*(==|!=)\s*"+P1+@"\b",
                m=>{owners++;return (m.Groups[2].Value=="!="?"!":"")+"CoopHostSide(UnitGetOwner("+m.Groups[1].Value+"))";});
        }
        s=Regex.Replace(s,@"\b("+string.Join("|",Mirrored.Select(c=>c[0]))+@")\(",m=>{mirrored++;return "CoopMirror"+m.Groups[1].Value+"(";});
        s=Regex.Replace(s,@"\bPlayerModifyPropertyInt\(",m=>{mirrored++;return "CoopMoneyInt(";});
        s=Calls(s,"UnitSetOwner",3,1,p1,a=>"CoopGiveUnit("+a[0]+", "+a[2]+")",ref given);
        s=Calls(s,"UnitCreate",6,3,p1,a=>"CoopUnitCreate("+a[0]+", "+a[1]+", "+a[2]+", "+a[4]+", "+a[5]+")",ref given);
        s=Calls(s,"libNtve_gf_CreateUnitsAtPoint2",5,3,p1,a=>"CoopCreateAtPoint2("+a[0]+", "+a[1]+", "+a[2]+", "+a[4]+")",ref given);
        s=Calls(s,"libNtve_gf_CreateUnitsWithDefaultFacing",5,3,p1,a=>"CoopCreateDefaultFacing("+a[0]+", "+a[1]+", "+a[2]+", "+a[4]+")",ref given);
        s=Calls(s,"libNtve_gf_RescueUnit",3,1,p1,a=>"CoopRescueUnit("+a[0]+", "+a[2]+")",ref given);
        s=Calls(s,"libNtve_gf_ShareVisionofUnit",3,2,p1,a=>"CoopShareVision("+a[0]+", "+a[1]+")",ref vision);
        // Resource pickups (Kit's campaign library pays whoever picked the cache up): the other player gets the same, or half each.
        int caches=0;s=Regex.Replace(s,@"\blibCamp_gf_DestroyCacheAndGivePlayerResources\(",m=>{caches++;return "CoopCacheResources(";});

        int units=Array.IndexOf(SeparateOptions.UnitChoices,SeparateOptions.Clean(options.Units,SeparateOptions.UnitChoices));
        int money=Array.IndexOf(SeparateOptions.MoneyChoices,SeparateOptions.Clean(options.Money,SeparateOptions.MoneyChoices));
        var sb=new StringBuilder("// Co-op separate armies (SeparateArmies.cs)\n");
        sb.Append("const int coopSepUnits = "+units+";\nconst int coopSepMoney = "+money+";\nbool coopSepLive = false;\nbool coopSepPanned = false;\nint coopSepNext = "+guest+";\n");
        sb.Append("bool CoopHostSide(int player) { return player == 1 || player == "+guest+"; }\n");
        sb.Append("unitgroup CoopUnitGroupBoth(string type, region r, unitfilter f, int max) {\n"+
            "    unitgroup g = UnitGroup(type, 1, r, f, max);\n"+
            "    unitgroup h = UnitGroup(type, "+guest+", r, f, max);\n"+
            "    int i = 1;\n"+
            "    while (i <= UnitGroupCount(h, c_unitCountAll)) { UnitGroupAdd(g, UnitGroupUnit(h, i)); i += 1; }\n"+
            "    return g;\n}\n");
        foreach(var c in Mirrored)
            sb.Append("void CoopMirror"+c[0]+"(int player, "+c[1]+") {\n    "+c[0]+"(player, "+c[2]+");\n    if (player == 1) { "+c[0]+"("+guest+", "+c[2]+"); }\n}\n");
        // Money: mirrored, except mission income once under way in split mode (half each, the odd one to player 1).
        sb.Append("void CoopMoneyInt(int player, int prop, int oper, int value) {\n"+
            "    int half = value / 2;\n"+
            "    if (player != 1) { PlayerModifyPropertyInt(player, prop, oper, value); return; }\n"+
            "    if (coopSepMoney == 1 && coopSepLive && oper == c_playerPropOperAdd && value > 0 && (prop == c_playerPropMinerals || prop == c_playerPropVespene)) {\n"+
            "        PlayerModifyPropertyInt(1, prop, oper, value - half);\n"+
            "        PlayerModifyPropertyInt("+guest+", prop, oper, half);\n"+
            "        return;\n"+
            "    }\n"+
            "    PlayerModifyPropertyInt(1, prop, oper, value);\n"+
            "    PlayerModifyPropertyInt("+guest+", prop, oper, value);\n}\n");
        // Who gets a unit the mission gives player 1. Before the mission is under way (opening scene), during cinematics and for
        // heroes: player 1. -1 = both.
        sb.Append("int CoopRecipient(string type, point p) {\n"+
            "    unit a;\n    unit b;\n    int r;\n"+
            "    if (!coopSepLive || gv_inCinematic || UnitTypeTestAttribute(type, c_unitAttributeHeroic)) { return 1; }\n"+
            "    if (coopSepUnits == 2) { return -1; }\n"+
            "    if (coopSepUnits == 1) { r = coopSepNext; if (r == 1) { coopSepNext = "+guest+"; } else { coopSepNext = 1; } return r; }\n"+
            "    a = UnitGroupClosestToPoint(UnitGroup(null, 1, RegionEntireMap(), UnitFilter(0, 0, (1 << c_targetFilterMissile), (1 << (c_targetFilterDead - 32)) | (1 << (c_targetFilterHidden - 32))), 0), p);\n"+
            "    b = UnitGroupClosestToPoint(UnitGroup(null, "+guest+", RegionEntireMap(), UnitFilter(0, 0, (1 << c_targetFilterMissile), (1 << (c_targetFilterDead - 32)) | (1 << (c_targetFilterHidden - 32))), 0), p);\n"+
            "    if (b == null) { return 1; }\n"+
            "    if (a == null) { return "+guest+"; }\n"+
            "    if (DistanceBetweenPoints(UnitGetPosition(b), p) < DistanceBetweenPoints(UnitGetPosition(a), p)) { return "+guest+"; }\n"+
            "    return 1;\n}\n");
        sb.Append("void CoopGiveUnit(unit u, bool changeColor) {\n"+
            "    int r = CoopRecipient(UnitGetType(u), UnitGetPosition(u));\n"+
            "    if (r == -1) {\n"+
            "        UnitSetOwner(u, 1, changeColor);\n"+
            "        UnitCreate(1, UnitGetType(u), 0, "+guest+", UnitGetPosition(u), UnitGetFacing(u));\n"+
            "        return;\n"+
            "    }\n"+
            "    UnitSetOwner(u, r, changeColor);\n}\n");
        // Rescues (libNtve_gf_RescueUnit: the stock "rescue" ownership change with its flash and sound).
        sb.Append("void CoopRescueUnit(unit u, bool changeColor) {\n"+
            "    int r = CoopRecipient(UnitGetType(u), UnitGetPosition(u));\n"+
            "    if (r == -1) {\n"+
            "        libNtve_gf_RescueUnit(u, 1, changeColor);\n"+
            "        UnitCreate(1, UnitGetType(u), 0, "+guest+", UnitGetPosition(u), UnitGetFacing(u));\n"+
            "        return;\n"+
            "    }\n"+
            "    libNtve_gf_RescueUnit(u, r, changeColor);\n}\n");
        // Resource caches: the picker gets them as before (floating text and all); the other player the same amount, or the two
        // split it. Resource ids equal the property ids (minerals 0, vespene 1).
        sb.Append("void CoopCacheResources(unit cache, int resource, int amount, int player) {\n"+
            "    int other = 1;\n"+
            "    int half = amount / 2;\n"+
            "    if (!CoopHostSide(player)) { libCamp_gf_DestroyCacheAndGivePlayerResources(cache, resource, amount, player); return; }\n"+
            "    if (player == 1) { other = "+guest+"; }\n"+
            "    if (coopSepMoney == 1) {\n"+
            "        libCamp_gf_DestroyCacheAndGivePlayerResources(cache, resource, amount - half, player);\n"+
            "        PlayerModifyPropertyInt(other, resource, c_playerPropOperAdd, half);\n"+
            "        return;\n"+
            "    }\n"+
            "    libCamp_gf_DestroyCacheAndGivePlayerResources(cache, resource, amount, player);\n"+
            "    PlayerModifyPropertyInt(other, resource, c_playerPropOperAdd, amount);\n}\n");
        // Unit creation: for "both" the guest's copy is made first, so the mission's UnitLastCreated() is player 1's as before.
        // The Natives create-units helpers are GUI actions without a result (void); UnitCreate returns the group.
        foreach(var c in new[]{
            new[]{"unitgroup","CoopUnitCreate","int count, string type, int flags, point p, fixed facing","UnitCreate(count, type, flags, {0}, p, facing)"},
            new[]{"void","CoopCreateAtPoint2","int count, string type, int flags, point p","libNtve_gf_CreateUnitsAtPoint2(count, type, flags, {0}, p)"},
            new[]{"void","CoopCreateDefaultFacing","int count, string type, int flags, point p","libNtve_gf_CreateUnitsWithDefaultFacing(count, type, flags, {0}, p)"}})
            sb.Append(c[0]+" "+c[1]+"("+c[2]+") {\n"+
                "    int r = CoopRecipient(type, p);\n"+
                "    if (r == -1) { "+string.Format(c[3],guest)+"; r = 1; }\n"+
                "    "+(c[0]=="void"?"":"return ")+string.Format(c[3],"r")+";\n}\n");
        sb.Append("void CoopShareVision(unit u, bool share) {\n"+
            "    libNtve_gf_ShareVisionofUnit(u, share, 1);\n"+
            "    libNtve_gf_ShareVisionofUnit(u, share, "+guest+");\n}\n");
        // Alliances with the other players are copied from player 1 by CoopSharedControlInit (both modes, every second).
        // Once a second from CoopCampaignTick (from its second tick on): the mission is under way.
        sb.Append("void CoopSeparateTick() {\n"+
            "    coopSepLive = true;\n"+
            "    if (!coopSepPanned && !gv_inCinematic) {\n"+
            "        coopSepPanned = true;\n"+
            "        if (UnitGroupCount(UnitGroup(null, "+guest+", RegionEntireMap(), UnitFilter(0, 0, 0, 0), 0), c_unitCountAlive) > 0) {\n"+
            "            CameraPan("+guest+", UnitGroupCenterOfGroup(UnitGroup(null, "+guest+", RegionEntireMap(), UnitFilter(0, 0, (1 << c_targetFilterMissile), 0), 0)), 0.0, -1.0, 10.0, false);\n"+
            "        }\n"+
            "    }\n}\n");
        // Pickups in game data (Pickups()): the picker already has the amount; the other player gets the same, or the picker
        // gives half of it to the other.
        sb.Append("void CoopPickupPay(int player, int other, int prop, int amount) {\n"+
            "    int half = amount / 2;\n"+
            "    if (coopSepMoney == 1) {\n"+
            "        PlayerModifyPropertyInt(player, prop, c_playerPropOperSubtract, half);\n"+
            "        PlayerModifyPropertyInt(other, prop, c_playerPropOperAdd, half);\n"+
            "        return;\n"+
            "    }\n"+
            "    PlayerModifyPropertyInt(other, prop, c_playerPropOperAdd, amount);\n}\n");
        sb.Append("bool CoopPickupUsed(bool testConds, bool runActions) {\n"+
            "    string e = AbilityCommandGetAbility(EventUnitAbility());\n"+
            "    int player = UnitGetOwner(EventUnitTargetUnit());\n"+
            "    int other = 1;\n"+
            "    if (!runActions || !CoopHostSide(player)) { return true; }\n"+
            "    if (player == 1) { other = "+guest+"; }\n");
        foreach(var p in pickups)sb.Append("    if (e == \""+p.Item1+"\") { CoopPickupPay(player, other, "+p.Item2+", "+p.Item3+"); }\n");
        sb.Append("    return true;\n}\n");
        sb.Append("void CoopSeparateStart() {\n    trigger pickup = TriggerCreate(\"CoopPickupUsed\");\n");
        if(!authored)sb.Append(
            "    unitgroup g = UnitGroup(null, 1, RegionEntireMap(), UnitFilter(0, 0, (1 << c_targetFilterStructure) | (1 << c_targetFilterMissile), (1 << (c_targetFilterDead - 32)) | (1 << (c_targetFilterHidden - 32))), 0);\n"+
            "    unitgroup cc = UnitGroup(\"CommandCenter\", 1, RegionEntireMap(), UnitFilter(0, 0, 0, 0), 1);\n"+
            "    int i = 1;\n"+
            "    unit u;\n"+
            "    while (i <= UnitGroupCount(g, c_unitCountAll)) {\n"+
            "        u = UnitGroupUnit(g, i);\n"+
            "        i += 1;\n"+
            "        if (UnitTypeTestAttribute(UnitGetType(u), c_unitAttributeHeroic)) { continue; }\n"+
            "        UnitCreate(1, UnitGetType(u), 0, "+guest+", PointWithOffset(UnitGetPosition(u), 1.0, -1.0), UnitGetFacing(u));\n"+
            "    }\n"+
            "    if (UnitGroupCount(cc, c_unitCountAll) > 0) {\n"+
            "        UnitCreate(1, \"CommandCenterFlying\", 0, "+guest+", PointWithOffset(UnitGetPosition(UnitGroupUnit(cc, 1)), 0.0, -7.0), 270.0);\n"+
            "    }\n");
        foreach(var name in pickups.Select(p=>p.Item1).Distinct())sb.Append("    TriggerAddEventUnitAbility(pickup, null, AbilityCommand(\""+name+"\", 0), c_unitAbilStageExecute, false);\n");
        // Campaign tech (armory upgrades, research) for the guest: Kit's library takes a player; Blizzard's
        // (libCamp_gf_TS_UpdateTechStates) only does player 1, so vanilla guests get just the mirrored mission tech for now.
        if(kit)sb.Append("    libWoLC_gf_WC_UpdateTechStates("+guest+");\n"+
            "    libWoLC_gf_WC_ApplyTech("+guest+");\n");
        sb.Append("}\n");
        code=sb.ToString();
        Program.Log("Separate armies (guest player "+guest+(authored?", the map's own slot":", cloned slot")+"): "+
            (ownCounts?"the map counts the guest's units itself":groups+" unit groups count both armies, "+owners+" owner checks")+", "+
            mirrored+" money/tech calls mirrored, "+given+" unit gifts ("+SeparateOptions.UnitChoices[units]+"), "+vision+" vision shares, "+caches+" scripted and "+pickups.Select(p=>p.Item1).Distinct().Count()+" data resource pickups; mission money: "+SeparateOptions.MoneyChoices[money]);
        return s;
    }
}
