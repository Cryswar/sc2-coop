using System.Reflection;

// The exe's version resource (Properties > Details): an unsigned exe without one looks more like a throwaway dropper to
// antivirus heuristics. The version follows Program.Version.
[assembly: AssemblyTitle("SC2Coop")]
[assembly: AssemblyProduct("SC2Coop")]
[assembly: AssemblyDescription("Two-player online co-op for StarCraft II campaigns")]
[assembly: AssemblyCompany("github.com/Cryswar/sc2-coop")]
[assembly: AssemblyCopyright("MIT License, see LICENSE")]
[assembly: AssemblyVersion(Program.Version+".0")]
[assembly: AssemblyFileVersion(Program.Version+".0")]
[assembly: AssemblyInformationalVersion(Program.Version)]
