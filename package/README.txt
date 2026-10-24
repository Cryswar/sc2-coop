StarCraft II campaign co-op (experimental) - The Outlaws
========================================================

Two players share the player-1 army in Wings of Liberty mission "The Outlaws".
The host plays as player 1. The buddy plays as an allied player who controls the same units, buildings and minerals.

This is an early test build. Expect rough edges.

What you need (both players)
----------------------------
- Windows 10 or 11 and StarCraft II installed through Battle.net, up to date (game version 5.0.17.98310),
  English (enUS) game language.
- This folder. Nothing from the game is included or sent between PCs: each launcher builds the mission
  from its own StarCraft II files.
- Battle.net doesn't need to be running. The game is started by this launcher in offline API mode
  (the same mechanism Archipelago and the official SC2 AI tools use).

Setup (both players, once)
--------------------------
1. Unzip SC2Coop.zip into a folder of its own, anywhere (not inside the StarCraft II folder).
2. Run SC2Coop.exe. It finds your StarCraft II folder by itself (where the game last ran, or the usual install folders)
   and prepares in a few seconds; after an update it prepares again by itself. Your StarCraft II folder is only read,
   never changed. If it can't find the folder, click "Prepare…" and select it (usually
   C:\Program Files (x86)\StarCraft II).

Playing
-------
Host:
1. Pick the "Host by address" tile, click Start (or "Host via Steam", see below).
2. If Windows Firewall asks whether SC2Coop.exe may communicate, allow it on Private networks.
3. The log shows what to send your buddy. If automatic port forwarding (UPnP) worked, it shows a line
   "Tell your buddy to choose Join and enter: <address>". If not, see "Connecting" below.
4. Wait for your buddy. The game starts on both PCs once they connect.

Buddy:
1. Pick the "Join by address" tile, type the host's address, click Start (or "Join a Steam friend", see below).

Stop, closing the game, or the in-game End Game ends the
session for both (the launcher closes StarCraft II for you).

Connecting through Steam (no IP address, no router settings)
------------------------------------------------------------
Both players need Steam running and signed in, and must be Steam friends. Nobody needs to buy anything:
the launcher signs in to Valve's free "Spacewar" test app (AppID 480), so Steam shows you both as
"playing Spacewar" while connected. Traffic goes through Valve's relay network.
- Host: the "Host via Steam" tile, Start. The log shows the lobby id (also copied to the clipboard).
- Friend: the "Join a Steam friend" tile, click Find, pick the host, Start. Or paste the lobby id into the box.
  Or leave the box empty, press Start, and accept the host's Steam invite.
- If the log says steam_api64.dll is missing: see "Steam DLL" below.

Steam DLL: the Steam option uses Valve's steam_api64.dll (included, next to SC2Coop.exe; Steamworks SDK
redistributable). The launcher writes steam_appid.txt (480) next to itself on first use.

Campaign hub (experimental: WoL@Kit campaigns, and vanilla-based WoL mods such as UED)
-------------------------------------------------------------------------------------------------------
Instead of the single Outlaws mission, you can play through a custom Wings of Liberty campaign: each player walks
around their own copy of the Hyperion (the hub), and when either of you starts a mission, both hubs close and the
mission starts for both of you. When the mission ends, both hubs open again with the host's progress.
Two kinds of campaigns work: ones built on WoL@Kit, and ones that keep Blizzard's own Hyperion and campaign scripts
(most WoL mods, e.g. UED). The launcher tells them apart by itself.
- Both players need the same campaign files: the campaign folder (TStory01.SC2Map plus the mission maps) and its
  mods in StarCraft II's own Mods folder (e.g. C:\Program Files (x86)\StarCraft II\Mods), not just next to the
  campaign folder (for a WoL@Kit campaign: kit_liberty_story.SC2Mod plus the campaign's own mods). The files must be
  identical on both PCs. Right after you connect, the launchers compare SC2Coop.exe, the campaign maps and every mod
  they use; if anything differs, both logs list exactly what (e.g. "<mod>.sc2mod is a different version on each PC")
  and nothing starts. The first check reads all the campaign files (a few seconds); later ones only re-read changed
  files.
- Tick "Campaign", Browse to the campaign folder, choose Host/Join as usual, Start.
- The campaign progress is the host's (StarCraft II's campaign banks in Documents\StarCraft II\Banks). The buddy's
  banks of the same names are replaced by the host's; the buddy's originals are backed up first to
  local\campaign\bank-backup-<date> next to SC2Coop.exe.
- Each campaign has its own co-op save: the host's launcher keeps every campaign's banks in
  %LOCALAPPDATA%\SC2Coop\saves\<campaign name> and swaps the right ones in when you start, so playing a second campaign
  doesn't overwrite the first one's progress (many campaigns use the same bank names). The first campaign you play with
  this version keeps the progress already in the Banks folder; any other campaign you start later begins fresh.
- To start a campaign over: "Reset…" next to the campaign folder (on the PC that hosts). The next session starts from
  the first mission; the old progress is moved to %LOCALAPPDATA%\SC2Coop\saves-reset, not deleted.
- Upgrades, research and mercenaries are bought in the host's hub: they count for both of you (one shared army). The
  buddy can open those consoles to look, but not buy (vanilla-based campaigns block the purchase; in WoL@Kit campaigns
  changes made there are not kept). Either of you can pick the mission; the host's hub saves its state first, so the
  host's purchases are kept.
- When either of you starts a mission, the other hub starts the same one, so its briefing and loading transition play
  for both of you.
- Hub chat: what you type in your hub (Enter, text, Enter) shows up in your buddy's hub as "Name: text", the
  name in your color, and the other way round. (Each hub is its own game; the launchers pass the lines along.)
  Your "Color" also colors your name in the hub. In the hub the chat lines are drawn by the hub itself (same place
  and font); SC2's chat history (message log) also lists your buddy's lines as "-coopmsg ..." entries.
- To end: close your hub without starting a mission (or press Stop).
- Cutscenes inside missions: both players see the same camera shots, talking heads and conversations. Either
  player's Esc skips the cutscene for both. Objective and minimap pings show for both players.
- Between the hub and a mission, StarCraft II has to close and start again. Meanwhile the launcher covers the game
  window with a black "loading" screen showing the mission name; it disappears as soon as the game starts. To turn it
  off, set SC2_COOP_NO_CURTAIN=1.
- Display mode: use Windowed (Fullscreen) or Windowed (Options > Graphics > Display Mode). In exclusive Fullscreen,
  anything drawn over the game (the loading screen, the buddy's drag box, markers and ping panel) can make StarCraft II
  lose its graphics device and crash. When the launcher sees exclusive Fullscreen it logs a warning and stops drawing
  those for the rest of the session.
  The games start in your own display mode and window size (the game's options).
- Game speed: "Speed" in the launcher (the host's choice; Faster by default) applies to every mission. In a
  mission either player can type -coopspeed slower, slow, normal, fast or faster to change it for both (the game
  menu's speed setting is single-player only). Any speed works on any difficulty.
- If either player's StarCraft II crashes (or is closed) during a mission, the mission ends for both, it doesn't count,
  and both hubs reopen with the campaign as it was before the mission. Before every mission the host's launcher copies
  the campaign banks to local\campaign\autosaves (newest 10); a bank damaged by a crash is restored from there.
  A lost connection (internet drop) still ends the whole session; start it again (progress is kept).
- Losing a mission: both of you get a "Mission failed" box with "Retry mission" (the same mission starts again for
  both of you, straight away) and "Return to the hub"; either player can choose. The game menu has no Restart in
  co-op (StarCraft II treats it as a multiplayer game).
- Replaying a mission from the Mission Archives console works like a star map pick: both of you play it (the other
  hub starts the same replay at the same difficulty), it doesn't change the campaign progress, and both hubs reopen
  afterwards.
- Separate armies: a mission whose map has a second human player is played with an army each (the buddy plays that
  second player: own units, minerals, supply; allied with shared vision). Missions without one stay a shared army. When
  the campaign folder has such missions, the launcher shows a "Separate armies" row (the host's choice counts):
  "Units the mission gives" (rescues, reinforcements): who found them (whose units are nearest), take turns, or both get
  them. "Mission money": both get it, or split it. Unlocks and research apply to both players; for now both use the
  host's campaign progress.
- Not yet supported in co-op: the Simulator and the Arcade (you go back to the hub).
- Test/skip-ahead chat commands (either player, press Enter, type, Enter):
  In the hub:    -cooplaunch TRaynor02   starts that mission for both of you (no unlock check; in vanilla-based
                                         campaigns its briefing plays first).
  In a mission:  -coopwin     wins through the mission's normal ending (outro cinematic, score screen, Continue).
                 -coopwinnow  wins at once: the mission counts as completed, no cinematic or score screen.
                 -cooplose    ends the mission as a defeat.
                 -coopspeed normal   sets the game speed for both (slower, slow, normal, fast, faster).
                 -coopobj TRaynor01_3 [fail]   completes (or fails) that objective (WoL@Kit; in vanilla-based
                                               campaigns give the objective's number instead, e.g. -coopobj 2).

Connecting by address
---------------------
Only the host needs to be reachable, on TCP port 6600. In order of ease:
- Same house / LAN: the host's log shows "Same Wi-Fi/LAN: 192.168.x.x". The buddy enters that.
- Automatic: if the host's router allows UPnP, the launcher opens port 6600 while hosting and closes it afterwards.
- Manual port forward: in the host's router settings, forward TCP 6600 to the host PC's LAN address
  (shown in the log), then give the buddy the host's public IP address.
- Tailscale (works behind any router, including carrier-grade NAT): both install Tailscale
  (https://tailscale.com/download), sign in, and share the host's machine with the buddy. The buddy
  enters the host's Tailscale address (100.x.y.z).
If you use a different port, enter it as address:port.

How the buddy controls units
----------------------------
The game itself does not let a joining player click on units in the world, so the launcher translates
the buddy's world clicks for them:
- Left click: select (shift adds/removes, ctrl or double-click selects all of a type). Drag: box select.
- Right click: move/attack/gather (shift queues).
- Hotkeys and the command card work normally. Targeted commands (A-move, building placement, abilities,
  gather, repair) work through the launcher.
- Drag-selecting shows a green box drawn by the launcher (windowed or windowed-fullscreen mode only; it can't
  draw over exclusive fullscreen).
- Right-click moves are sent as the button goes down, and a small green + marks the spot at once.
- Attack-move and other targeted abilities go out as the button goes down too; a small red + marks the spot.
- The launcher remembers the last mode and host address you used.
- "Your name" is what StarCraft II shows for you (chat, pings); empty = your Windows user name. "Color" (campaign hub
  missions): the host's choice recolors the shared army (Campaign default = the campaign's blue), the buddy's choice is
  the color of the buddy's own pings and chat. Both launchers exchange these when connecting; both are remembered.
  With "Campaign" ticked the color list is the campaign's own (mods can rename and add colors), so what you pick is what you get in its missions. "Custom color..." at the end of
  the list takes any color (Windows color picker); it is added to each mission's own color table. Some units and
  buildings in campaign missions keep their own colors whatever you pick, and the minimap keeps its usual colors.
- Ping: Alt + left click in the world. The ping shows for both players. Hold Alt for a moment to get the ping panel
  (Attack / On my way / Retreat / Defend; for the buddy the launcher draws it) and click one: that ping goes where the
  panel opened. Each minimap shows your ally's view as a frame in your ally's color; Alt+V jumps to your ally's view.
- Medics and other units without an attack scan-move with the group on an attack-move.
- Camera: the arrow keys scroll the camera straight away (the launcher moves it and keeps the keys from the game while
  the game window is in front). Right after a minimap click or a camera hotkey, a press waits a moment (under a
  second) for the launcher to learn the new camera position; during cutscenes and with the chat line open the mission
  map scrolls instead, which reacts a little later.
- The buddy's top bar shows the host's minerals, gas and supply (the shared ones).
- Keep the game window 16:9 (e.g. 1280x720, 1920x1080, 2560x1440).

Updating
--------
Unzip a newer SC2Coop.zip over the old folder (your settings and co-op saves are kept) and start it; it prepares
again by itself when needed. Both players need the same version.
When a newer version is out, the launcher shows an "Update available" button at the top (it opens the download page).
To check, it asks GitHub for the list of releases when it starts; set SC2_COOP_NO_UPDATE_CHECK=1 to turn that off.

Reporting a problem
-------------------
Click "Save logs..." (under Start) and send the zip it makes, with what happened and roughly when. It holds the
launcher's logs of your recent sessions and StarCraft II's own logs (graphics and crash reports) from the last two
days. The logs contain player names, Steam lobby ids and folder paths, nothing else from your PC. Both players'
logs help: the problem may show on only one side.

Known limitations
-----------------
- Objectives (bonus and main) are not ticked off; their name gets " (Completed)" or " (Failed)" instead.
  A real tick-off makes StarCraft II's API end the game for the launcher, which used to cut off the buddy's
  mouse control and caused a long stall.
- Commands take about 0.1-0.15 s to start even on one PC (StarCraft II's own command delay), plus the
  network round trip shown in the launcher log as "Latency:".
  Over Steam the game's own network packets travel unreliably (like normal internet play), so a lost packet no
  longer stalls everything behind it. Both players need this launcher version for that; with an older one it
  falls back to the previous behaviour.
- Without "Campaign": one mission (The Outlaws). No saving in the middle of a mission.
- Campaign hub: the same applies to WoL@Kit missions (their objectives are routed the same way); the campaign
  still records bonus objectives normally. Only the host can click Continue on the victory screen.
- Clicks near cliffs may land slightly off. Clicking a building's rally point as the buddy is not supported yet.
- Not yet tested between two real PCs over the internet. Please report what happens.
- Logs: the local\runs folder next to SC2Coop.exe. Send launcher.log if something fails.
- Antivirus: SC2Coop.exe is unsigned and reads the buddy's mouse and keyboard while the game window is in front
  (that's how the buddy's clicks reach the game). Earlier versions did that with system-wide input hooks, which looked
  like a keylogger to Windows Defender and got quarantined as "Trojan:Win32/Prowloc.A!cl"; this version uses Windows'
  Raw Input instead, which hasn't been flagged so far. If SC2Coop.exe suddenly closes or disappears, check Windows
  Security > Protection history; you can restore it there (only do this for a copy you got from the host directly).

Third-party: CascLib (MIT, Ladislav Zezula), see third-party\CascLib-LICENSE.txt.
steam_api64.dll is (c) Valve Corporation, redistributed under the Steamworks SDK Access Agreement.
Steam and Spacewar are trademarks of Valve Corporation. This project is not affiliated with Valve.
StarCraft II is a trademark of Blizzard Entertainment. This project is not affiliated with Blizzard.
