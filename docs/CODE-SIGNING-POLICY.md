# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

**Status:** applied for. Releases up to v0.2.0 are unsigned. Signed releases will say so in their release notes.

## What is signed

Only `SC2Coop.exe`, built from this repository's source by the [Build workflow](../.github/workflows/build.yml) on
GitHub's servers. The release zip also contains files that are not built here and are not signed by this project:

- `CascLib.dll`: [CascLib](https://github.com/ladislav-zezula/CascLib) (MIT), unchanged.
- `steam_api64.dll`: Valve's Steamworks library, unchanged, redistributed under the Steamworks SDK Access Agreement.

Every signing request is approved by hand. Nothing is signed automatically.

## Team roles

| Role | Members |
| --- | --- |
| Authors (commit without further review) | [Cryswar](https://github.com/Cryswar) |
| Reviewers (review every change from others, e.g. pull requests) | [Cryswar](https://github.com/Cryswar) |
| Approvers (approve every signing request) | [Cryswar](https://github.com/Cryswar) |

All team members use multi-factor authentication on GitHub and SignPath.

## Privacy policy

SC2Coop collects no telemetry and sends nothing about you or your PC to the project. It connects to other systems only
in these cases:

- **Your buddy's PC:** the game traffic of a co-op session goes directly to the buddy you chose, by the address you
  entered (Host/Join by address) or through Steam (below).
- **Steam (only when you pick a Steam connection):** the launcher uses your running Steam client to find your Steam
  friends and to carry the game traffic over Valve's network. It registers as Valve's free "Spacewar" test app (AppID
  480), so Steam shows you as playing Spacewar. Valve's [privacy policy](https://store.steampowered.com/privacy_agreement/)
  applies to that.
- **Update check:** when it starts, the launcher asks GitHub for this project's list of releases
  (`api.github.com/repos/Cryswar/sc2-coop/releases`) to tell you when a newer version is out. That request carries your IP
  address and the launcher's version, as every web request does. GitHub's
  [privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement) applies.
  To turn it off, set the environment variable `SC2_COOP_NO_UPDATE_CHECK=1`.
- **Router port (UPnP), when hosting by address:** if your router allows UPnP, the launcher asks it to open TCP port 6600
  to your PC while you host, and closes it again afterwards. To turn it off, set `SC2_COOP_NO_UPNP=1` and forward the port
  yourself, or use a Steam connection.

"Save logs…" writes a zip on your own PC only. Nothing is uploaded; you decide whether to send it to anyone.

## Files the launcher writes

- The folder you unzipped SC2Coop into (`local\`: run logs, built co-op maps; `steam_appid.txt`).
- `%LOCALAPPDATA%\SC2Coop`: settings and co-op campaign saves.
- `Documents\StarCraft II\Banks`: the campaign banks of the campaign being played. Banks of the same names that were
  there before are backed up first, into `local\`.

The StarCraft II install itself is only read, never changed.

## Uninstalling

SC2Coop has no installer. To remove it, delete the folder you unzipped it into and `%LOCALAPPDATA%\SC2Coop`. Co-op
campaign progress is in `%LOCALAPPDATA%\SC2Coop\saves`; keep that folder if you want it later.
