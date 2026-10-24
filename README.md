# SC2Coop

Two-player online co-op for StarCraft II: Wings of Liberty campaigns. Both players walk their own Hyperion, either one picks
the mission, and both play it together, sharing one army by default. Campaign progress, credits and upgrades follow the
host's save.

**Status: early test builds.** Expect rough edges; bug reports with the launcher's log are welcome.

## Download

Get `SC2Coop.zip` from [Releases](../../releases), unzip it into a folder of its own and run `SC2Coop.exe`. The full player
guide is [`package/README.txt`](package/README.txt) (also in the zip).

Both players need:
- Windows 10 or 11, StarCraft II through Battle.net, up to date (game version 5.0.17.98310), English (enUS).
- The same launcher version and the same campaign files.

## What works

- **Connections:** Steam friends (no ports to open), or host/join by address (TCP 6600).
- **Campaigns:** WoL@Kit campaigns, and Wings of Liberty mods that keep Blizzard's campaign scripts (e.g. UED). For
  missions authored with a second human player, each player gets their own army.
- **The hub:**
  - each player has their own Hyperion, with shared chat;
  - a mission picked on either side starts for both, with its briefing;
  - Mission Archives replays work;
  - the host buys upgrades, research and mercenaries; the buddy can view them.
- **Missions:**
  - shared control;
  - cutscenes for both players;
  - pings with a ping panel;
  - a "Mission failed" dialog with retry;
  - game speed choice;
  - per-campaign co-op saves, with a reset button.
- **The buddy's controls:** the game itself doesn't let a joining player click on units in the world. The launcher
  translates those clicks, drag boxes, targeted commands and arrow-key scrolling.

More documentation:
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): how the launcher works.
- [`docs/PITFALLS.md`](docs/PITFALLS.md): traps in Galaxy, the SC2 API, the campaign libraries and Windows input.
- [`docs/PORTING.md`](docs/PORTING.md): how to make a campaign co-op-ready.

## How it works (short)

The launcher starts both players' StarCraft II in the game's offline API mode (s2client, the interface the official AI
tools use) and joins them into one multiplayer game. Each launcher builds the mission from its own game files, adding small
co-op adapter scripts (`adapters/`) and rewriting the parts of the mission script that assume a single player. The game
traffic goes through a Steam networking or TCP tunnel between the two launchers. Nothing from the game is included in this
repository or sent between PCs.

## Building

1. Put the two third-party DLLs in `lib\` (they are not in the repository):
   - `CascLib.dll`: [CascLib](https://github.com/ladislav-zezula/CascLib) (MIT), 64-bit build;
   - `steam_api64.dll`: from Valve's [Steamworks SDK](https://partner.steamgames.com/) (`redistributable_bin\win64`).

   Or take both from a release zip.
2. Run `.\build.ps1` in PowerShell. It compiles `src\*.cs` with the C# compiler included in Windows (.NET Framework 4)
   and writes `out\SC2Coop\` and `out\SC2Coop.zip`.

Releasing: set `Program.Version` (in `src\TrialLauncher.cs`) to the new version, build, then publish with
`gh release create v<version> out\SC2Coop.zip`. Launchers compare their version with the newest release on GitHub and
show an update button when one is newer.

## License

MIT, see [LICENSE](LICENSE). Third-party:
- CascLib is MIT (see `package/third-party`).
- `steam_api64.dll` is Valve's, redistributed under the Steamworks SDK Access Agreement.

StarCraft II is a trademark of Blizzard Entertainment, and Steam of Valve Corporation. This project is not affiliated with
either.
