// Automatic port forwarding for the host's tunnel port through the router's UPnP, using Windows' built-in
// HNetCfg.NATUPnP COM object (no extra dependencies). Best effort: many routers disable UPnP, and
// carrier-grade NAT can't be opened at all; the launcher then tells the host what to do instead.
using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

class Upnp : IDisposable {
    object mappings;int port;public string ExternalIp,LanIp;public bool Mapped;

    public static string DetectLanIp() {
        // Connecting a UDP socket sends nothing; it only selects the outgoing interface.
        try {using(var s=new UdpClient()){s.Connect("192.0.2.1",9);return ((IPEndPoint)s.Client.LocalEndPoint).Address.ToString();}}
        catch {return null;}
    }

    static object Get(object target,string name) {return target.GetType().InvokeMember(name,BindingFlags.GetProperty,null,target,null);}

    public static Upnp TryMap(int port,Action<string> log) {
        var u=new Upnp{port=port,LanIp=DetectLanIp()};
        try {
            var type=Type.GetTypeFromProgID("HNetCfg.NATUPnP");
            var nat=Activator.CreateInstance(type);
            // The collection can take a few seconds to appear while Windows discovers the router.
            for(int i=0;i<8&&u.mappings==null;i++){u.mappings=Get(nat,"StaticPortMappingCollection");if(u.mappings==null)System.Threading.Thread.Sleep(500);}
            if(u.mappings==null){log("Automatic port forwarding (UPnP) is not available on this router.");return u;}
            // Replace only a stale mapping of ours; never touch a rule someone else created for this port.
            object existing=null;
            try {existing=u.mappings.GetType().InvokeMember("Item",BindingFlags.GetProperty,null,u.mappings,new object[]{port,"TCP"});} catch { }
            if(existing!=null) {
                if((Get(existing,"Description") as string)!="SC2 campaign co-op"){log("Router already forwards TCP "+port+" (another rule); leaving it unchanged.");return u;}
                try {u.mappings.GetType().InvokeMember("Remove",BindingFlags.InvokeMethod,null,u.mappings,new object[]{port,"TCP"});} catch { }
            }
            var mapping=u.mappings.GetType().InvokeMember("Add",BindingFlags.InvokeMethod,null,u.mappings,new object[]{port,"TCP",port,u.LanIp,true,"SC2 campaign co-op"});
            u.Mapped=mapping!=null;
            if(u.Mapped)u.ExternalIp=Get(mapping,"ExternalIPAddress") as string;
            log(u.Mapped?"Router port "+port+" opened automatically (UPnP)":"The router refused automatic port forwarding (UPnP).");
        } catch(Exception e) {log("Automatic port forwarding failed: "+(e.InnerException??e).Message);}
        return u;
    }

    // Lists current TCP mappings on the router for `port` (used to verify add/remove).
    public static bool IsMapped(int port) {
        try {
            var nat=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.NATUPnP"));var maps=Get(nat,"StaticPortMappingCollection");if(maps==null)return false;
            var item=maps.GetType().InvokeMember("Item",BindingFlags.GetProperty,null,maps,new object[]{port,"TCP"});return item!=null;
        } catch {return false;}
    }

    public void Dispose() {
        if(!Mapped)return;
        try {mappings.GetType().InvokeMember("Remove",BindingFlags.InvokeMethod,null,mappings,new object[]{port,"TCP"});} catch { }
        Mapped=false;
    }
}
