# Pitfalls

Traps found while building SC2Coop, so nobody has to find them twice. Each entry gives the symptom, the cause, and the
fix or workaround, with the date it was found. Most apply to any project that drives StarCraft II through the API or
rewrites campaign maps. How the launcher fits together: [ARCHITECTURE.md](ARCHITECTURE.md).

## Galaxy (the map script language)

- **A broken script fails silently.** One compile error disables the whole map script: no message, the map just has
  no triggers.
  - Symptoms: the guest sees a black world, test commands do nothing, nothing scripted happens.
  - Check: after a rewrite, make sure a test chat command still responds.
- **No block comments.** `/* ... */` is not Galaxy; only `//`. Replacing a call with `/* removed */` killed every vanilla
  mission script (2026-10-23).
- **Declare before use.** A function body can't reference a global or a function declared further down. Code
  inserted before the mission script that reads mission globals (`gv_...`) breaks the script.
  - Fix: put prototypes at the top and bodies after the mission script.
- **`text` is a type name.** A local variable called `text` breaks the script.
- **A void call can't be returned.** `return libNtve_gf_CreateUnitsWithDefaultFacing(...)` doesn't compile: the helper
  returns nothing. Wrappers of void library functions must be void.
- **Library triggers are globals.** A map script can `TriggerEnable(libCamp_gt_..., false)` a library trigger and
  register its own trigger on the same event, which is how a library's behaviour is replaced without editing the
  library.
  - Limit: triggers declared `static` in a library file (WoL@Kit's UI scripts) can't be reached that way.
- **`EventChatMessage` in a trigger with another event:** don't. Split a shared function from the chat trigger
  instead.

## The s2client API and the retail client

- **Multiplayer only on loopback.** API multiplayer games need every client's host address to be `127.0.0.1`, so the
  game ports have to be tunnelled between PCs.
- **The joiner's world clicks are dropped.**
  - The joining client ignores its own world clicks, but accepts the same clicks as feature-layer actions through
    its own API: hence the click bridge.
  - The creator's native world input needs `raw=true` in InterfaceOptions.
- **Objectives end the game.** Setting any objective to completed or failed ends the API game for both clients
  (status 5). The launcher shows a name suffix instead.
- **Crashes:**
  - `RequestObservation` crashes for guest player ids above 3 in some setups (the API's `3 - player` 1v1 assumption).
  - `RequestGameInfo` crashes on story (hub) maps.
- **Hub maps:** a hub only runs as a packed `.SC2Map`; component folders are rejected.
- **`GameOver` on a story map loads the next map**, and an API client then crashes or hangs on it. Clear it with
  `GameSetNextMap("")` and let the launcher decide.
- **One game per process.** A client that already played a game doesn't join another multiplayer game (the join hangs),
  so each game gets a fresh process.
- **A stale map copy breaks the join** (ChecksumError 11). A killed joiner keeps its copy of the shared map in its temp
  folder; the same map name later loads the stale copy.
  - Fix: name the map after its content hash, and clean the temp folder.
- **The first start sometimes fails:** SC2 may exit with -2000/-2001 right after starting. A retry works.
- **Joining has a 3-minute limit.** A first load of a big map can run past it on a slow disk.
- **Speed:**
  - API games run at Faster (1.4 game seconds per real second).
  - Trigger code can change it mid-game: `GameSetSpeedValueMinimum(c_gameSpeedSlower)`, `GameSetSpeedLocked(false)`,
    `GameSetSpeedValue(...)` (measured 2026-10-23: Normal 1.00, Slower 0.61).
  - The game menu's speed setting is single-player only.
- **Command-line display options override the player's settings.** `-displayMode 0 -windowwidth/-windowheight` forces
  windowed mode and may be saved over the player's choice. Only use them on test profiles.

## Campaign libraries (Blizzard's `libCamp`, WoL@Kit's `libWoLC`)

A mission the launcher starts is not a transition map: `GameIsTransitionMap()` is false, which the libraries read as
"started from the editor, or restarted". Several code paths then do surprising things (all found 2026-10-22/23):

- **Debug tech purchase.**
  - Every vanilla mission calls `libCamp_gf_PurchaseStorymodeTech()`, which then runs the debug "buy average tech":
    - credits recomputed from the expected rewards, minus purchases;
    - mission progress reset to the expected path;
    - the mission saved as the last successful one, so a defeat still paid the reward.
  - Fix: remove the call (from the hub it does nothing).
  - TRaynor01-03 run the same thing inside `libCamp_gf_LoadCampaignData`, which can't be removed from the mission.
    The launcher repairs the results afterwards: the defeat is saved as a defeat, and credits are restored after a
    replay.
- **Stale restart snapshots.**
  - Both libraries keep a snapshot of the campaign state from a mission's first start (vanilla: the mission's own
    bank; Kit: `<mission>|MissionSectionExist` in the campaign bank).
  - When the mission isn't a transition map, they load that snapshot whenever it exists, which undoes purchases made
    since an earlier attempt.
  - Fix: drop the snapshot on a start from the hub; keep it for a retry and for a Mission Archives replay (which
    plays from it).
- **Archive replays write old progress back.**
  - At the end of a vanilla replay, the save writes the replayed mission's snapshot into the campaign bank: available
    and completed missions, best times, mercenaries, tech. After replaying The Outlaws, it was the only mission on the
    star map and the Hyperion's rooms broke.
  - Fix: restore those sections, and the credits, from the copy made before the mission.
- **The hub pays rewards on every open.**
  - The hub pays the last mission's reward each time it opens, until a save marks it paid. So closing the hub without
    a launch paid it again next time.
  - Fix: save the host's hub shortly after it opens, and before it closes.
- **The buddy's hub can overwrite the host's.** A second, independent hub writes its own banks; whichever hub launched
  won, so the host's purchases vanished when the buddy picked.
  - Fix: one hub's state counts (the host's). The other hub's banks are discarded and its shops are view-only.
- **Defeat panel.** The library's defeat shows SC2's panel, whose "Return" loads the next map, which the launcher has
  cleared, so the game never ended. The launcher shows its own dialog instead.
- **Hub difficulty.** Mission select preselects player 1's game difficulty. A new API hub game is always Normal; set it
  from the campaign's saved difficulty.
- **Mission Archives (vanilla).** The library's own replay trigger launches past the hub script's wrapped launch, so
  the pick wasn't recorded. Replace the trigger.
- **UI reset after cinematics.** Some missions reset the UI frames at the start (e.g. after the opening cinematic),
  which hid the ping panel again. Re-show it periodically outside cinematics.
- **Strings.** Some mods' objective texts lack characters the English base has (e.g. a missing " (" before a counter).
  That's the mod, not a rewrite bug.

## Banks

- **All campaign progress is in player-1 banks.** In a 2-participant game, `BankLoad(name, player)` loads that player's
  bank from that player's PC, so the host's banks are the campaign. The guest's hub needs a copy.
- **No reload from disk.** A running game doesn't re-read a bank file changed on disk; `BankLoad` returns the cached
  bank.
- **Bank names collide.** Campaigns share bank names (`TCampaign` in every vanilla-based campaign). Keep one set of
  banks per campaign and swap it in.
- **Bank files** are XML. Vanilla campaign banks are unsigned and can be read and patched by the launcher. Some mods
  sign theirs (`<Signature>`); don't edit those.

## Windows input and display

- **Hooks trigger antivirus.** System-wide low-level hooks (`SetWindowsHookEx` `WH_MOUSE_LL`/`WH_KEYBOARD_LL`) in an
  unsigned exe got it quarantined by Windows Defender (`Trojan:Win32/Prowloc.A!cl`). Raw Input with a hidden sink window
  (`RIDEV_INPUTSINK`) listens without hooks. Even so, fresh console test builds are still flagged now and then; the
  remedy is code signing or a false-positive submission to Microsoft, never exclusions.
- **Raw Input details:**
  - `RAWINPUTHEADER` is 24 bytes on x64.
  - Injected input has a null device handle.
  - Raw Input can't swallow keys. To keep arrow keys from the game, register them as hotkeys (`RegisterHotKey`) while
    the game window is in front. The key-down then arrives as `WM_HOTKEY` (not raw input); the key-up still arrives as
    raw input.
- **Overlay windows may not paint.** A flood of `WM_INPUT` messages starves `WM_PAINT`, so overlays never painted (a
  black drag box). Call `InvalidateRect` + `UpdateWindow`.
- **Exclusive fullscreen + overlays = crashes.** Any window shown over a D3D9 game in exclusive fullscreen makes it lose
  its device; a failed reset crashes the game. `SHQueryUserNotificationState` returns `QUNS_RUNNING_D3D_FULL_SCREEN`
  for that case, so skip overlays then. Players should use Windowed (Fullscreen).
- **Arrow-key scrolling needs a known camera position.**
  - The bridge scrolls the camera itself, which needs the camera position.
  - Falling back to the map's own scroll after a minimap click makes the position stale again, and the keys flip back
    and forth between the two.
  - Fix: hold the key press until the next camera report.

## Testing

- **Window-only screenshots.** Capture only the game windows (PrintWindow on the process's window), never the whole
  desktop.
- **Don't steal focus.** Input tests that steal focus or inject keys interfere with a person using the PC (and can log
  their typing): run them only when nobody is using it.
- **A suspicious result can be a dead launcher.** A run that just stops mid-log is often Defender killing a fresh
  test exe; check `Get-MpThreatDetection` before debugging.
- **Leave space between scripted steps.** Scripted test steps race with long briefings: a scripted chat command 60 s
  after a pick can land while the briefing is still playing.
