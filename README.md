# Wife's Bedroom Toy Bridge

Toy support (via [Intiface Central](https://intiface.com/central/)/buttplug.io) for
*Wife's Bedroom* and ModRoom-style forks of it, plus a launcher that also converts old
ModRoom-era custom character packs so they work on the current game.

## Download and install

Grab the latest installer from the **[Releases page](../../releases/latest)**
(`ToyBridgeLauncher-Setup.exe`). Run it, optionally let it install Intiface Central for you,
and it drops a Start Menu / desktop shortcut for **Toy Bridge Launcher**.

The installer only ships this project's own tooling - no game files. On first run, click
"Browse..." next to "Game:" and point it at your own copy of the game's `.exe`.

Full walkthrough of every tab (Play / Mods / Android) is in [`LAUNCHER.txt`](LAUNCHER.txt).

## What's in here

- **Toy bridge** - streams real-time in-game intensity to your toy over buttplug.io while you
  play, with selectable intensity profiles (see [`PROFILES.txt`](PROFILES.txt),
  [`TUNING.txt`](TUNING.txt)).
- **Game patcher** - one-click patches a Wife's Bedroom/ModRoom-style `data.win` to broadcast
  that telemetry; nothing else about the game is changed.
- **ModRoom character converter** - see below.
- **HMV mode** - drive toy intensity from any audio file instead of the game (see
  [`HMVMODE.txt`](HMVMODE.txt)).
- **Android support** - patches an `.apk` with toy support, optionally bundling a PC mod's
  content and mod folders for phone-only play (see [`ANDROID.txt`](ANDROID.txt)).
- Linux/macOS are supported too - see [`LINUX-MACOS.txt`](LINUX-MACOS.txt).

## ModRoom character converter

The ModRoom fork used its own custom-character system (`custom_futas/`, `custom_wives/`,
`custom_bedrooms/` folders). The current game has since grown its own, different custom-character
system (a single `custom/` folder, auto-detected by file type) - so packs built for ModRoom don't
just work by copying them over.

Most ModRoom-era packs turn out to use the same underlying file format, though - the mismatch is
mostly a folder layout problem, not a content problem. The Launcher's Mods tab has a
**"Convert ModRoom Characters to This Game..."** button that:

1. Scans a mods folder and reports which packs are portable (simple file-naming convention) vs.
   not (the older numbered sprite convention, which genuinely can't move without being redrawn).
2. Shows that report before touching anything.
3. On confirmation, copies (never moves) the portable packs into your current game's expected
   layout, renaming the wife-type data file (`.wife` -> `.spouse`) automatically where the two
   systems disagree.

Your original ModRoom mod folders are left untouched - only copies land in the new game's
`custom/` folder.

## Building from source

Source only ships here; compiled binaries aren't committed (see `.gitignore`). To build your own:

- **ToyLauncher** (the GUI): `ToyLauncherQt/` - Python/PySide6.
  `pip install -r requirements.txt`, then `pyinstaller ToyLauncher.spec`.
- **ButtplugBridge**, **GamePatcher**, **ApkPatcher**: .NET (see `ButtplugBridge/`,
  `GamePatcherCli/`, `ApkPatcher/`) - `dotnet publish` each as a self-contained single file.
- **HmvLive**: `HmvMode/` - `pyinstaller HmvLive.spec`.
- Windows installer: `installer/ToyBridgeLauncher.iss` via Inno Setup (`ISCC.exe`), after
  staging built exes into `installer/stage/`.

## What this project does not ship

No game files, no copyrighted assets, no signing keystores - see `.gitignore`. This is tooling
only; you need your own legitimate copy of the game.
