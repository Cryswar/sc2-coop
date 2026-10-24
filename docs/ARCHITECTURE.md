# How SC2Coop works

A map of the launcher for whoever works on it next: what runs where, in which order, and which file does what. The
traps found along the way are in [PITFALLS.md](PITFALLS.md); what a campaign author has to do is in
[PORTING.md](PORTING.md).

## The idea in one paragraph

StarCraft II has no campaign co-op, but the retail client has Blizzard's s2client API (`-listen`/`-port`, protobuf over
a websocket). Through it, any local map can be played as a multiplayer game. The launcher takes each campaign map from
the player's own install and rewrites it so that a second human is allied with player 1 and shares control of player 1's
army. Small Galaxy adapters fill the gaps a joining player has, and a cooperating launcher on each PC starts both games
and carries their traffic. The hub (the Hyperion) is not shared: each player runs their own copy as a solo game, and a
mission pick in either hub starts the mission for both. Nothing from the game is shipped; every map is built on each PC
from that PC's files, and both builds must come out byte-identical.

## Processes and connections

```
 host PC                                              guest PC
 ┌──────────────┐  s2client API   ┌───────────┐       ┌───────────┐   s2client API  ┌──────────────┐
 │ SC2Coop.exe  │<───(websocket)─>│ SC2 (P1)  │       │ SC2 (P6)  │<──(websocket)──>│ SC2Coop.exe  │
 │ (host)       │                 └─────┬─────┘       └─────┬─────┘                 │ (guest)      │
 │              │      game ports on 127.0.0.1 only         │      + click bridge    │              │
 │   Tunnel ────┼───────────────────────┘                   └────────────────────────┼─── Tunnel    │
 └──────┬───────┘                                                                    └──────┬───────┘
        └──────────── one TCP stream (port 6600) or Steam P2P (Spacewar, app 480) ──────────┘
                      carries: the 4 game ports + control messages (picks, banks, chat)
```

- **Game ports.** SC2 only plays API multiplayer games when every client uses a loopback host address. Each launcher
  impersonates the other side's game ports on its own `127.0.0.1` and forwards them through one stream. The guest
  connects to the host, so only the host needs a reachable port (UPnP is tried) or a Steam lobby (`Tunnel.cs`,
  `Steam.cs`, `Upnp.cs`).
- **Control messages** ride the same stream as JSON frames: profile exchange (name, color, separate-army and speed
  choices), setup check (launcher, maps and mods by SHA-256), hub picks, bank sync, hub chat, "mission over", retry.
- **Each launcher drives its own SC2** through the API: create/join the game, raw actions for the click bridge,
  status polling. The host's launcher also owns the campaign state.

## A campaign session, step by step (`CampaignRun` in `Campaign.cs`)

1. **Connect** (Steam lobby or TCP), compare setups, exchange profiles. The host's choices win for anything shared.
2. **Load the co-op save:** the host swaps this campaign's banks from `%LOCALAPPDATA%\SC2Coop\saves\<campaign>` into
   the game's bank folder, then sends them to the guest. Campaigns use the same bank names, so each one needs its own
   save.
3. **Hubs:** each launcher builds its hub map (`BuildKitHub`) and starts it as a solo API game. The hub script records
   a mission pick in a small `CoopHub` bank. The launcher polls that bank:
   - as soon as a pick appears, the other hub gets `-cooplaunch <mission>` so its briefing plays too;
   - the buddy's hub has view-only shops;
   - the host's hub saves its state 10 s after opening, and when it closes without a launch.
4. **Mission:** both launchers build the mission (`BuildMission`) and compare hashes. The host creates the
   2-participant game and the guest joins. The click bridge starts for the guest.
5. **End:** the mission's own victory/defeat (or the co-op "Mission failed" dialog: retry or return) ends the game. The
   host's launcher checks the banks (restoring the pre-mission autosave if one is damaged), stores the co-op save and
   sends the banks to the guest. Both hubs reopen, or the same mission restarts after "Retry".

## Map rewriting

Maps are MPQ archives. `MapArchive`/`Mpq` read and write them; `MapInfoPlayers` edits the player slots in `MapInfo`.
Everything else is text surgery on `MapScript.galaxy` with regexes, plus small data additions (team colors, the ping
models, banks in `BankList.xml`).

**Mission (`CampaignMaps.BuildMission`):**
- Guest slot: a copy of player 1's slot, or a second human slot the map author made (then: separate armies).
- Two campaign libraries are recognised: WoL@Kit (`libWoLC`/`libWCMI`/`libWCUI`) and Blizzard's `libCamp`. The same
  hooks have different names in each.
- Calls that would break an API game are rerouted:
  - objective completed/failed would end the game for both, so it becomes a name suffix instead;
  - `GameSetNextMap` is cleared;
  - defeat goes to the co-op retry dialog.
- Calls aimed at player 1 are mirrored to the guest: 13 camera functions (cutscenes), pings, and a skip on either
  player's Esc.
- Library calls that misbehave when a mission isn't loaded as a transition map are removed or repaired (see
  PITFALLS.md, "Campaign libraries").
- The adapters in `adapters/` are appended, and their init calls are hooked in after `InitTriggers()`.
- Test chat commands are added: `-coopwin`, `-coopwinnow`, `-cooplose`, `-coopobj`, `-coopspeed`, `-cooptrigger`,
  `-coopspawn`.

**Hub (`CampaignMaps.BuildKitHub`):**
- The library's mission launch is wrapped to record the pick, including A/B choices.
- Vanilla Mission Archives replays go through a copy of the library's replay trigger.
- Hub chat is redrawn and relayed.
- The buddy's hub shops are view-only, with purchases blocked where the library allows.
- Auto-save; the campaign difficulty becomes the default in mission select.

**Separate armies (`SeparateArmies.cs`):** for maps with a second human slot.
- Player 1's unit groups and ownership checks count both armies.
- Money and tech are mirrored to the guest.
- Rescued units and pickups go to whoever found them, alternate, or go to both.

## The adapters (Galaxy, `adapters/`)

| File | Job |
|---|---|
| `campaign_shared_control.galaxy` | Guest gets player 1's alliances with everyone (vision, hostility, help), re-applied every second. |
| `campaign_targeting.galaxy` | Guest's targeted commands: reports the targeting state in the guest's local `CoopBridge` bank; swaps the bridge's probe order for the real ability, per unit, from catalog data. |
| `campaign_guest_ui.galaxy` | Guest's resource panel (copy of player 1's), camera report for the bridge, pings and ping panel, ally view frame on the minimap. |
| `campaign_diagnostics.galaxy` | Test-only reports. |

## The click bridge (`ClickBridge.cs`)

The joining client drops its own world clicks. The guest's launcher therefore reads mouse and keyboard through Raw
Input, only while the guest's game window is in front, and replays world clicks through the guest's own API as
feature-layer actions:
- **Clicks:** select, box select, smart click, attack-move, abilities, build placement.
- **Coordinates:** a calibrated homography, adjusted for zoom.
- **Targeted commands:** a probe order that the map swaps for the real ability (see `campaign_targeting.galaxy`).
- **Arrow keys:** reserved as hotkeys while the window is in front, so the bridge can scroll the camera immediately.
  The position comes from the map's camera report.
- **Overlays:** the drag box, click markers and the ping panel are the launcher's own windows, transparent to clicks.
  They are skipped when the game runs in exclusive fullscreen.

## Files

| File | What's in it |
|---|---|
| `src/TrialLauncher.cs` | `Program` (entry, profile, CLI), `GameClient` (start SC2, API requests), `Session` (create/join a 2-player game, test hooks). |
| `src/TrialPreparation.cs` | Prepare: reads the install (CascLib), builds the single-mission map, adds the co-op models. |
| `src/Campaign.cs` | `CampaignMaps` (hub and mission rewriting), `CampaignRun` (the session loop), saves, bank sync, map checker. |
| `src/SeparateArmies.cs` | Separate-army script rewrites. |
| `src/ClickBridge.cs` | Guest input bridge and its overlays. |
| `src/Tunnel.cs`, `src/Steam.cs`, `src/Upnp.cs` | Transport. |
| `src/Curtain.cs` | Loading curtain between games; exclusive-fullscreen detection. |
| `src/TeamColors.cs` | The campaign's team color table (base colors plus mod changes). |
| `src/LauncherCore.cs`, `src/LauncherWindow.cs` | The window and its actions. |

## Testing

There is no unit test suite; the launcher is tested by playing:
- **Local mode** (`campaign <trial.json> local - <campaign dir>`) runs both players' games on one PC, against a test
  copy of the install.
- **Test hooks** (environment variables, all read in `Campaign.cs`/`TrialLauncher.cs`) script a session:
  - `SC2_COOP_TEST_LAUNCH` (missions to pick in turn), `SC2_COOP_TEST_WIN` (win or lose after N seconds);
  - `SC2_COOP_TEST_HUBCHAT`, `SC2_COOP_TEST_CHAT` (type chat lines);
  - `SC2_COOP_TEST_CRASH`, `SC2_COOP_TEST_STOP`;
  - `SC2_COOP_TEST_SPEED` (game-clock report).
- **Checks:** the launcher log, window-only screenshots, and the banks the games write.
- **Smoke test:** every mission of a campaign, picked from the hub and won by command.
