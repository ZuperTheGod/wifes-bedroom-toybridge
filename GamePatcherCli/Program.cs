// GamePatcher - command-line, cross-platform equivalent of ToyLauncher's "Patch Game..." button,
// for people on Linux/macOS where the WinForms GUI launcher can't run.
//
// Adds a small UDP telemetry broadcast to a compatible game's data file (data.win, game.unx,
// game.ios, etc. - any GameMaker data file UndertaleModLib can read), so ButtplugBridge can drive
// Buttplug.io / Intiface Central toys in sync with the game. Only ever ADDS a few lines after two
// existing lines it recognizes in one specific object (oFutaMatingPress) - never touches anything
// else in the game, and always backs up the original file first.
//
// Usage:
//   GamePatcher <path-to-game-exe-or-data-file> [--yes] [--check] [--hmv] [--touch-controls]
//   GamePatcher <path-to-game-exe-or-data-file> --check-mod-system
//
//   <path> can be:
//     - the game's data file directly (data.win / game.unx / game.ios / ...), or
//     - the game's main executable - the data file is assumed to be "data.win" or "game.unx"
//       in the same folder (whichever exists).
//
//   --check             only report whether the game is compatible / already patched, don't
//                       change anything.
//   --yes               skip the confirmation prompt (for scripting).
//   --hmv               apply the HMV mode patch instead of the toy telemetry patch - see
//                       GamePatcher.cs's HMV MODE section for what this adds.
//   --touch-controls    apply the touch-controls patch instead of the toy telemetry patch - adds
//                       touch equivalents (long-press, drag-to-scroll) for mods (e.g. ModRoom)
//                       whose settings only work with a mouse's right-click/scroll wheel.
//   --check-mod-system  read-only: reports which custom-character mod system this game uses
//                       (Vanilla's single custom/ folder, or ModRoom's split custom_futas/
//                       custom_wives/ folders) - see GamePatcher.cs's CUSTOM MOD SYSTEM
//                       DETECTION section. Never patches anything; standalone from the flags
//                       above.

using GamePatcherCli;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: GamePatcher <path-to-game-exe-or-data-file> [--yes] [--check] [--hmv] [--touch-controls]");
    Console.WriteLine("   or: GamePatcher <path-to-game-exe-or-data-file> --check-mod-system");
    Console.WriteLine();
    Console.WriteLine("  <path>              the game's data file (data.win/game.unx/game.ios/...) or its");
    Console.WriteLine("                      main executable (the data file is then looked for alongside it).");
    Console.WriteLine("  --check             only report compatibility/patch status, don't change anything.");
    Console.WriteLine("  --yes               skip the confirmation prompt.");
    Console.WriteLine("  --hmv               apply the HMV mode patch (lets an external tool drive thrust");
    Console.WriteLine("                      rhythm and background color in real time, e.g. to sync to a");
    Console.WriteLine("                      song's beat) instead of the toy telemetry patch.");
    Console.WriteLine("  --touch-controls    apply the touch-controls patch (adds long-press/drag-to-scroll");
    Console.WriteLine("                      equivalents for right-click/mouse-wheel-only settings, e.g. in");
    Console.WriteLine("                      ModRoom) instead of the toy telemetry patch.");
    Console.WriteLine("  --check-mod-system  read-only: report which custom-character mod system this game");
    Console.WriteLine("                      uses (Vanilla vs ModRoom-style) - never patches anything.");
    Console.WriteLine("  --dump-code <event-name> <output-file>");
    Console.WriteLine("                      read-only diagnostic: decompiles one code entry (e.g.");
    Console.WriteLine("                      gml_Object_oFutaMatingPress_Create_0) and writes its GML to a");
    Console.WriteLine("                      file - useful for investigating a patch failure's real cause.");
    Console.WriteLine("  --dump-version      read-only diagnostic: reports the GameMaker version this data");
    Console.WriteLine("                      file was compiled with - useful for spotting a version mismatch");
    Console.WriteLine("                      when swapping a data file into a different game's shell/APK.");
    Console.WriteLine("  --touch-code <event-name>[,<event-name>...]");
    Console.WriteLine("                      re-encodes one or more code entries through our own compiler with");
    Console.WriteLine("                      no intentional behavior change - can resolve a GameMaker-version-");
    Console.WriteLine("                      mismatch crash on code we'd otherwise never touch. Always re-test");
    Console.WriteLine("                      after using this; it is not guaranteed to fix anything.");
    Console.WriteLine("  --custom-alts       apply the custom-character-alts + click-scroll patch instead of");
    Console.WriteLine("                      the toy telemetry patch - adds numbered alt looks for custom");
    Console.WriteLine("                      characters (custom_data_1.futa/.spouse etc, right-click the");
    Console.WriteLine("                      portrait-swap button to cycle) and click-scroll arrows for the");
    Console.WriteLine("                      custom character list. Specific to this vanilla install's own");
    Console.WriteLine("                      func_load_custom - not ModRoom-style builds.");
    return args.Length < 1 ? 1 : 0;
}

string inputPath = args[0];
bool checkOnly = args.Contains("--check");
bool skipConfirm = args.Contains("--yes");
bool hmvMode = args.Contains("--hmv");
bool touchControlsMode = args.Contains("--touch-controls");
bool customAltsMode = args.Contains("--custom-alts");
bool checkModSystem = args.Contains("--check-mod-system");
bool dumpVersion = args.Contains("--dump-version");
int dumpCodeIdx = Array.IndexOf(args, "--dump-code");
int touchCodeIdx = Array.IndexOf(args, "--touch-code");
bool touchAllCode = args.Contains("--touch-all-code");

if (!File.Exists(inputPath))
{
    Console.WriteLine($"File not found: {inputPath}");
    return 1;
}

string? dataPath = ResolveDataFilePath(inputPath);
if (dataPath is null)
{
    Console.WriteLine($"Couldn't find a game data file (data.win / game.unx / game.ios) next to: {inputPath}");
    Console.WriteLine("If that path IS the data file itself, pass it directly.");
    return 1;
}

Console.WriteLine($"Game data file: {dataPath}");

if (dumpVersion)
{
    Console.WriteLine(GamePatcher.DumpVersionInfo(dataPath));
    return 0;
}

if (dumpCodeIdx >= 0)
{
    if (dumpCodeIdx + 2 >= args.Length)
    {
        Console.WriteLine("Usage: GamePatcher <path> --dump-code <event-name> <output-file>");
        return 1;
    }
    Console.WriteLine(GamePatcher.DumpCode(dataPath, args[dumpCodeIdx + 1], args[dumpCodeIdx + 2]));
    return 0;
}

if (touchCodeIdx >= 0)
{
    if (touchCodeIdx + 1 >= args.Length)
    {
        Console.WriteLine("Usage: GamePatcher <path> --touch-code <event-name>[,<event-name>...]");
        return 1;
    }
    string[] eventNames = args[touchCodeIdx + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    Console.WriteLine(GamePatcher.TouchCode(dataPath, eventNames));
    return 0;
}

int dumpPropsIdx = Array.IndexOf(args, "--dump-code-properties");
if (dumpPropsIdx >= 0)
{
    Console.WriteLine(GamePatcher.DumpCodeEntryProperties(dataPath, args[dumpPropsIdx + 1]));
    return 0;
}

if (touchAllCode)
{
    Console.WriteLine("This can take a while for a large data file (decompiling+recompiling every code entry)...");
    Console.WriteLine(GamePatcher.TouchAllCode(dataPath));
    return 0;
}

if (checkModSystem)
{
    var modSystemResult = GamePatcher.CheckCustomModSystem(dataPath);
    Console.WriteLine($"Compatible: {modSystemResult.Compatible}   System: {modSystemResult.System}   ({modSystemResult.Detail})");
    return modSystemResult.Compatible ? 0 : 1;
}

var status = hmvMode ? GamePatcher.CheckHmvStatus(dataPath)
    : touchControlsMode ? GamePatcher.CheckTouchControlsStatus(dataPath)
    : customAltsMode ? GamePatcher.CheckCustomAltsStatus(dataPath)
    : GamePatcher.CheckStatus(dataPath);
Console.WriteLine($"Compatible: {status.Compatible}   Already patched: {status.AlreadyPatched}   ({status.Detail})");

if (checkOnly)
{
    return status.Compatible ? 0 : 1;
}

if (!status.Compatible)
{
    return 1;
}

if (status.AlreadyPatched)
{
    Console.WriteLine("Nothing to do.");
    return 0;
}

if (!skipConfirm)
{
    Console.Write($"This will back up (as {Path.GetFileName(dataPath)}.bak, if not already present) " +
                   $"and patch {dataPath}. Continue? [y/N] ");
    string? answer = Console.ReadLine();
    if (string.IsNullOrEmpty(answer) || !answer.Trim().StartsWith('y'))
    {
        Console.WriteLine("Cancelled - nothing changed.");
        return 1;
    }
}

var outcome = hmvMode ? GamePatcher.PatchHmv(dataPath)
    : touchControlsMode ? GamePatcher.PatchTouchControls(dataPath)
    : customAltsMode ? GamePatcher.PatchCustomAlts(dataPath)
    : GamePatcher.Patch(dataPath);
Console.WriteLine($"{outcome.Result}: {outcome.Message}");
return outcome.Result is GamePatcher.PatchResult.Patched or GamePatcher.PatchResult.AlreadyPatched ? 0 : 1;

static string? ResolveDataFilePath(string inputPath)
{
    // If they pointed straight at a data file (any extension), just use it as-is - a cheap
    // sniff of the first 4 bytes ("FORM", GameMaker's chunk-format magic) confirms this without
    // relying on filename/extension at all.
    if (LooksLikeGameMakerDataFile(inputPath))
    {
        return inputPath;
    }

    string? dir = Path.GetDirectoryName(Path.GetFullPath(inputPath));
    if (dir is null) return null;

    foreach (string candidate in new[] { "data.win", "game.unx", "game.ios", "game.droid" })
    {
        string candidatePath = Path.Combine(dir, candidate);
        if (File.Exists(candidatePath))
        {
            return candidatePath;
        }
    }

    return null;
}

static bool LooksLikeGameMakerDataFile(string path)
{
    try
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        Span<byte> header = stackalloc byte[4];
        int read = stream.Read(header);
        return read == 4 && header[0] == (byte)'F' && header[1] == (byte)'O' && header[2] == (byte)'R' && header[3] == (byte)'M';
    }
    catch
    {
        return false;
    }
}
