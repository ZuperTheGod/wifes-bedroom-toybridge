using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using Underanalyzer.Decompiler;

namespace ApkPatcher;

// Same marker-based patch as AddButtplugTelemetry.csx / the ToyLauncher GUI's "Patch Game..."
// button (this file is identical between the two - GamePatcherCli is the cross-platform,
// command-line-only equivalent, for people on Linux/macOS where the WinForms GUI can't run).
// Only ever ADDS a few lines after two existing lines it recognizes in oFutaMatingPress -
// never touches anything else in the game. Works on data.win, game.unx, game.ios, etc. -
// UndertaleIO.Read auto-detects the format, the path/filename don't matter.
static class GamePatcher
{
    public enum PatchResult
    {
        AlreadyPatched,
        Patched,
        NotSupported,
        Error,
    }

    public record PatchOutcome(PatchResult Result, string Message);

    private const string CreateEventName = "gml_Object_oFutaMatingPress_Create_0";
    private const string DrawEventName = "gml_Object_oFutaMatingPress_Draw_0";

    private const string CreateMarker = "thrust_time = 0;";
    private const string CreatePatch =
        "\nbuttplug_socket = network_create_socket(network_socket_udp);" +
        "\nbuttplug_buffer = buffer_create(256, buffer_grow, 1);" +
        "\nbuttplug_last_send = 0;" +
        "\nbuttplug_port = 45735;";

    private const string DrawMarker = "thrust_prev = thrust;";
    private const string DrawPatch =
        "\nif (buttplug_socket >= 0)" +
        "\n{" +
        "\n    var _bp_now = current_time;" +
        "\n    if (_bp_now - buttplug_last_send >= 33)" +
        "\n    {" +
        "\n        buttplug_last_send = _bp_now;" +
        "\n        var _bp_insert = 0;" +
        "\n        if (insert)" +
        "\n        {" +
        "\n            _bp_insert = 1;" +
        "\n        }" +
        "\n        var _bp_orgasm = 0;" +
        "\n        if (orgasm)" +
        "\n        {" +
        "\n            _bp_orgasm = 1;" +
        "\n        }" +
        "\n        var _bp_msg = string(thrust) + \",\" + string(thrust_prev) + \",\" + string(thrust_speed) + \",\" + string(thrust_strength) + \",\" + string(_bp_insert) + \",\" + string(_bp_orgasm);" +
        "\n        buffer_seek(buttplug_buffer, buffer_seek_start, 0);" +
        "\n        buffer_write(buttplug_buffer, buffer_text, _bp_msg);" +
        "\n        network_send_udp_raw(buttplug_socket, \"127.0.0.1\", buttplug_port, buttplug_buffer, buffer_tell(buttplug_buffer));" +
        "\n    }" +
        "\n}";

    public static bool CanCheckSupport(string dataWinPath) => File.Exists(dataWinPath);

    /// <summary>Quick check (no modification) of whether a data.win looks like a compatible/already-patched game.</summary>
    public static (bool Compatible, bool AlreadyPatched, string Detail) CheckStatus(string dataWinPath)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            var drawCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == DrawEventName);
            if (createCode is null || drawCode is null)
            {
                return (false, false, "Not a compatible game (missing oFutaMatingPress).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            string drawText = new DecompileContext(globalContext, drawCode, settings).DecompileToString();
            bool already = drawText.Contains("buttplug_socket");
            return (true, already, already ? "Already patched." : "Compatible, not yet patched.");
        }
        catch (Exception ex)
        {
            return (false, false, $"Couldn't check: {ex.Message}");
        }
    }

    /// <summary>Diagnostic only, never modifies the data file: reports the GameMaker bytecode/
    /// runtime version this specific data file was compiled with (data.GeneralInfo), plus whether
    /// it's flagged for YYC (native-compiled code) vs VM bytecode. Two data files with different
    /// values here were built by different GameMaker Studio versions/export targets - swapping one
    /// into an APK shell built for the other can produce exactly this kind of runtime struct-
    /// construction crash, since struct/hash bytecode encoding isn't guaranteed stable across
    /// GameMaker versions.</summary>
    public static string DumpVersionInfo(string dataWinPath)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var info = data.GeneralInfo;
            var lines = new List<string>
            {
                $"BytecodeVersion: {data.GeneralInfo?.BytecodeVersion}",
                $"Info.Major/Minor/Release/Build: {info?.Major}.{info?.Minor}.{info?.Release}.{info?.Build}",
                $"GMS2 version fields if present:",
            };
            if (info is not null)
            {
                foreach (var prop in info.GetType().GetProperties())
                {
                    try
                    {
                        var val = prop.GetValue(info);
                        if (val is not null && (prop.Name.Contains("Version", StringComparison.OrdinalIgnoreCase)
                                                 || prop.Name.Contains("YYC", StringComparison.OrdinalIgnoreCase)
                                                 || prop.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase)))
                        {
                            lines.Add($"  {prop.Name} = {val}");
                        }
                    }
                    catch { /* some properties throw on access for unrelated reasons - skip */ }
                }
            }
            lines.Add($"Data.UnsupportedBytecodeVersion: {data.UnsupportedBytecodeVersion}");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>Diagnostic only, never modifies the data file: decompiles one named code entry
    /// (e.g. "gml_Object_oFutaMatingPress_Create_0") and writes the full GML text Underanalyzer
    /// produced to outputPath - for investigating decompile/recompile round-trip failures
    /// (CodeImportGroup.Import() re-parses this SAME text with UndertaleModLib's own compiler, so
    /// if that fails, this is what to look at) on a specific game build's own code.</summary>
    public static string DumpCode(string dataWinPath, string eventName, string outputPath)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var code = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == eventName);
            if (code is null)
            {
                return $"No code entry named '{eventName}' found in this data file.";
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            string text = new DecompileContext(globalContext, code, settings).DecompileToString();
            File.WriteAllText(outputPath, text);
            return $"Wrote {text.Length} chars ({text.Split('\n').Length} lines) to {outputPath}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>Diagnostic/repair tool: decompiles one or more named code entries and re-imports
    /// the EXACT same GML text through our own compiler with no intentional semantic changes -
    /// re-encodes them using OUR toolchain's bytecode generation instead of whatever GameMaker
    /// Studio version originally compiled them. Real motivation: swapping a data file across
    /// GameMaker point releases (e.g. into an Android APK shell built for a different version)
    /// can crash on otherwise-ordinary code due to version-dependent struct/hash encoding
    /// differences (confirmed - WB-ModRoom "Release 1" was compiled with GameMaker 2024.14,
    /// crashed only when its data.win was swapped into an Android shell built for 2024.13's
    /// runtime, on code we'd never touched at all) - passing the affected code through our own
    /// compiler once may normalize it to something the target runtime accepts. This does NOT
    /// guarantee a fix (our compiler could reproduce the same incompatibility, or a different
    /// one) - always re-verify with a real launch after using this, never assume success.
    /// Backs up the original first, same as every other patch in this file.</summary>
    public static string TouchCode(string dataWinPath, string[] eventNames)
    {
        try
        {
            string backupPath = dataWinPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(dataWinPath, backupPath);
            }

            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };
            var touched = new List<string>();
            var missing = new List<string>();

            foreach (var eventName in eventNames)
            {
                var code = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == eventName);
                if (code is null)
                {
                    missing.Add(eventName);
                    continue;
                }
                string text = new DecompileContext(globalContext, code, settings).DecompileToString();
                importGroup.QueueReplace(eventName, text);
                touched.Add(eventName);
            }

            if (touched.Count == 0)
            {
                return $"Nothing touched - none of the requested code entries were found: {string.Join(", ", missing)}";
            }

            importGroup.Import();

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            string result = $"Re-encoded {touched.Count} code entr{(touched.Count == 1 ? "y" : "ies")} through our own compiler: {string.Join(", ", touched)}. Backup: {backupPath}";
            if (missing.Count > 0) result += $"\nNot found (skipped): {string.Join(", ", missing)}.";
            return result;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>Diagnostic only: reflects over every public property of one UndertaleCode entry
    /// and prints its value - used to find the real parent/child signal (ParentEntry) rather than
    /// guessing from naming conventions.</summary>
    public static string DumpCodeEntryProperties(string dataWinPath, string eventName)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }
            var code = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == eventName);
            if (code is null) return $"Not found: {eventName}";
            var lines = new List<string>();
            foreach (var prop in code.GetType().GetProperties())
            {
                try
                {
                    var val = prop.GetValue(code);
                    lines.Add($"{prop.Name} = {val}");
                }
                catch (Exception ex) { lines.Add($"{prop.Name} = <error: {ex.Message}>"); }
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>Same idea as TouchCode, but for every top-level code entry in the file at once -
    /// an escalation used when touching a single function didn't resolve a version-mismatch
    /// crash, on the theory that the incompatibility might be in a global table (struct-type/
    /// function registry) GameMaker's original compiler built consistently across the WHOLE
    /// project, which a single-function patch can't fix in isolation. Skips auto-generated child
    /// entries (UndertaleCode.ParentEntry is non-null) - nested/anonymous functions, AND, found
    /// empirically, some named scripts too (e.g. "gml_Script_scrDebugMenus" whose real root is
    /// "gml_GlobalScript_scrDebugMenus" - a naming-convention guess like "@parent" in the name
    /// doesn't cover every case; ParentEntry is the actual authoritative signal) - since those get
    /// regenerated automatically when their parent is recompiled; trying to decompile one
    /// independently throws "Expected code entry to be root level" (a nested function's control
    /// flow can't be analyzed outside its parent's context). Genuinely risky: a decompile or
    /// recompile failure anywhere aborts the whole operation (nothing is written unless every
    /// entry succeeds), and even a fully successful recompile is not guaranteed to fix anything -
    /// always re-verify with a real launch after using this.</summary>
    public static string TouchAllCode(string dataWinPath)
    {
        try
        {
            string backupPath = dataWinPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(dataWinPath, backupPath);
            }

            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };

            int totalEntries = data.Code.Count(c => c is not null && c.Name?.Content is not null);
            var topLevelNames = data.Code
                .Where(c => c is not null && c.Name?.Content is not null && c.ParentEntry is null)
                .Select(c => c!.Name!.Content!)
                .Distinct()
                .ToList();

            var decompileFailures = new List<(string Name, string Error)>();
            int queued = 0;
            foreach (var name in topLevelNames)
            {
                var code = data.Code.First(c => c is not null && c.Name?.Content == name);
                try
                {
                    string text = new DecompileContext(globalContext, code, settings).DecompileToString();
                    importGroup.QueueReplace(name, text);
                    queued++;
                }
                catch (Exception ex)
                {
                    decompileFailures.Add((name, ex.Message));
                }
            }

            if (decompileFailures.Count > 0)
            {
                return $"Aborted before writing anything - {decompileFailures.Count} of {topLevelNames.Count} " +
                       $"entries failed to even DECOMPILE (not recompile - these can't be re-encoded at all):\n" +
                       string.Join("\n", decompileFailures.Take(20).Select(f => $"  {f.Name}: {f.Error}"));
            }

            try
            {
                importGroup.Import();
            }
            catch (Exception ex)
            {
                return $"Aborted before writing anything - recompile failed after successfully decompiling all " +
                       $"{queued} entries: {ex.Message}";
            }

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            return $"Re-encoded all {queued} top-level code entries through our own compiler (skipped " +
                   $"{totalEntries - topLevelNames.Count} auto-generated child entries, regenerated automatically " +
                   $"as part of their parent). Backup: {backupPath}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // Read-only diagnostic: decompiles every top-level code entry and reports which ones contain
    // the given substring - useful for finding every place a variable/function is referenced
    // across the whole game without guessing at event names.
    public static string FindCodeReferences(string dataWinPath, string substring)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;

            var topLevelNames = data.Code
                .Where(c => c is not null && c.Name?.Content is not null && c.ParentEntry is null)
                .Select(c => c!.Name!.Content!)
                .Distinct()
                .ToList();

            var matches = new List<string>();
            var failures = new List<(string Name, string Error)>();
            foreach (var name in topLevelNames)
            {
                var code = data.Code.First(c => c is not null && c.Name?.Content == name);
                try
                {
                    string text = new DecompileContext(globalContext, code, settings).DecompileToString();
                    if (text.Contains(substring))
                    {
                        matches.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add((name, ex.Message));
                }
            }

            string result = $"Searched {topLevelNames.Count} top-level code entries for \"{substring}\":\n" +
                             string.Join("\n", matches.Select(m => $"  {m}"));
            if (failures.Count > 0)
            {
                result += $"\n({failures.Count} entries failed to decompile and were skipped)";
            }
            return result;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }


    public static PatchOutcome Patch(string dataWinPath)
    {
        try
        {
            string backupPath = dataWinPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(dataWinPath, backupPath);
            }

            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            var drawCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == DrawEventName);
            if (createCode is null || drawCode is null)
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "This doesn't look like a compatible game (no oFutaMatingPress object found). " +
                    "This tool only knows how to patch Wife's Bedroom and compatible mods of it (e.g. ModRoom).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;

            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
            string drawText = new DecompileContext(globalContext, drawCode, settings).DecompileToString();

            bool createAlready = createText.Contains("buttplug_socket");
            bool drawAlready = drawText.Contains("buttplug_socket");

            if (createAlready && drawAlready)
            {
                return new PatchOutcome(PatchResult.AlreadyPatched, "This game is already patched for toy support - nothing to do.");
            }

            if (!createAlready)
            {
                if (!createText.Contains(CreateMarker))
                {
                    return new PatchOutcome(PatchResult.NotSupported,
                        "Couldn't find the expected code in the Create event - this game's version may not be compatible.");
                }
                createText = createText.Replace(CreateMarker, CreateMarker + CreatePatch);
            }

            if (!drawAlready)
            {
                if (!drawText.Contains(DrawMarker))
                {
                    return new PatchOutcome(PatchResult.NotSupported,
                        "Couldn't find the expected code in the Draw event - this game's version may not be compatible.");
                }
                drawText = drawText.Replace(DrawMarker, DrawMarker + DrawPatch);
            }

            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };
            if (!createAlready) importGroup.QueueReplace(CreateEventName, createText);
            if (!drawAlready) importGroup.QueueReplace(DrawEventName, drawText);
            importGroup.Import();

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            return new PatchOutcome(PatchResult.Patched,
                $"Patched successfully! A backup of the original was saved as:\n{backupPath}");
        }
        catch (Exception ex)
        {
            return new PatchOutcome(PatchResult.Error, $"Something went wrong while patching: {ex.Message}");
        }
    }

    // ================================================================================
    // HMV MODE - a separate, optional patch (never applied unless explicitly requested).
    // Lets an external tool drive thrust rhythm and background color in real time, e.g. to
    // sync the game to a song's beat. Purely additive and fails safe: if the external tool
    // never sends anything (or stops sending), the game behaves exactly as it always has -
    // nothing about existing gameplay is touched, only new optional code paths are added.
    //
    // How it works: oFutaMatingPress already drives its own "thrust" animation every frame
    // from three plain instance variables (thrust_speed/thrust_strength/thrust_middle) that
    // feed a cosine oscillator (see thrust_set/thrust_time in Draw_0) - nothing about that
    // formula needs to change. This patch just overwrites those three variables, at the top
    // of Step_0, whenever fresh data has arrived on a new UDP listener socket within the last
    // 500ms - otherwise they're left completely alone. A new GameMaker "Async Networking"
    // event (added to the object fresh, not modifying an existing one) is what receives that
    // data; the object doesn't listen for anything until this patch adds that event. The
    // background's tint (already a real draw_sprite_ext parameter, just always called with
    // the literal "no tint" white constant beforehand) is repointed at a variable the same
    // packet also updates.
    //
    // Wire format (sent to UDP port 45736, one packet per update - see HmvSendPort below):
    //   float32 thrust_speed, float32 thrust_strength, float32 thrust_middle, uint32 bgr_color
    // (16 bytes total, little-endian - matches Python's struct.pack("<3fI", ...))

    public const int HmvPort = 45736; // keep in sync with the literal in HmvCreatePatch below

    // Clickable speaker icons (see SPEAKER PATCH below) ping the companion tool on this port when
    // clicked, reusing the same already-bound hmv_socket to send (no second socket needed) - kept
    // in sync with the literal in HmvSpeakerCreatePatch below.
    public const int HmvPingPort = 45737;

    private const string HmvCreateMarker = "thrust_time = 0;";
    private const string HmvCreatePatch =
        "\nhmv_socket = network_create_socket_ext(network_socket_udp, 45736);" +
        "\nhmv_active = false;" +
        "\nhmv_last_packet_time = 0;" +
        "\nhmv_thrust_speed = thrust_speed;" +
        "\nhmv_thrust_strength = thrust_strength;" +
        "\nhmv_thrust_middle = thrust_middle;" +
        "\nhmv_background_color = 16777215;";

    // ------------------------------------------------------------------------------------------
    // SPEAKER PATCH - two clickable speaker icons drawn near the bed. Clicking either just fires
    // one small UDP "ping" packet at 127.0.0.1:45737 - it does NOT open a file dialog, do drag and
    // drop, or anything else inside GML (GameMaker has no built-in for real OS file drag-and-drop,
    // and get_open_filename()-style dialogs are a much bigger surface to trust than one UDP send).
    // Instead, the companion app (ToyLauncherQt) listens on that port and pops up its own picker
    // (drag/drop + Browse + paste-path all trivial there) - see ToyLauncherQt/main.py.
    //
    // Placement (hmv_speaker_l/r_x/y below) is a best guess ("off to the side of the bed," room is
    // 880x512, oFutaMatingPress/oBackground both sit centered around it) - NOT yet confirmed
    // against the actual rendered scene. Expect to retune after a real look, same as thrust-feel
    // tuning was refined after real playtests rather than guessed once and left alone.
    private const string HmvSpeakerCreatePatch =
        "\nhmv_ping_port = 45737;" +
        "\nhmv_ping_buffer = buffer_create(1, buffer_fixed, 1);" +
        "\nhmv_speaker_size = 48;" +
        "\nhmv_speaker_l_x = 32;" +
        "\nhmv_speaker_l_y = 32;" +
        "\nhmv_speaker_r_x = 800;" +
        "\nhmv_speaker_r_y = 32;" +
        "\nhmv_speaker_pulse = 0;" +
        "\nhmv_speaker_l_hover = false;" +
        "\nhmv_speaker_r_hover = false;";

    // Prepended to Step_0 BEFORE HmvStepPrepend (order between the two doesn't matter - independent
    // state - kept as its own block for clarity/easy removal if speakers are ever dropped).
    private const string HmvSpeakerStepPrepend =
        "var _hmv_mx = mouse_x;" +
        "\nvar _hmv_my = mouse_y;" +
        "\nhmv_speaker_l_hover = (_hmv_mx >= hmv_speaker_l_x && _hmv_mx <= hmv_speaker_l_x + hmv_speaker_size && _hmv_my >= hmv_speaker_l_y && _hmv_my <= hmv_speaker_l_y + hmv_speaker_size);" +
        "\nhmv_speaker_r_hover = (_hmv_mx >= hmv_speaker_r_x && _hmv_mx <= hmv_speaker_r_x + hmv_speaker_size && _hmv_my >= hmv_speaker_r_y && _hmv_my <= hmv_speaker_r_y + hmv_speaker_size);" +
        "\nif (hmv_speaker_pulse > 0)" +
        "\n{" +
        "\n    hmv_speaker_pulse -= 0.05;" +
        "\n    if (hmv_speaker_pulse < 0) { hmv_speaker_pulse = 0; }" +
        "\n}" +
        "\nif (mouse_check_button_pressed(mb_left) && (hmv_speaker_l_hover || hmv_speaker_r_hover))" +
        "\n{" +
        "\n    hmv_speaker_pulse = 1;" +
        "\n    buffer_seek(hmv_ping_buffer, buffer_seek_start, 0);" +
        "\n    buffer_write(hmv_ping_buffer, buffer_u8, 165);" +
        "\n    network_send_udp_raw(hmv_socket, \"127.0.0.1\", hmv_ping_port, hmv_ping_buffer, 1);" +
        "\n}\n";

    // Appended at the very end of oFutaMatingPress's Draw_0 (so the icons sit on top of everything
    // else drawn that frame). Procedural draw primitives, not an imported sprite - deliberately
    // avoids adding a brand-new sprite/object/room-instance asset via UndertaleModLib, which every
    // patch so far in this project has avoided (only ever adding code to EXISTING objects/events).
    // Two near-identical blocks (left/right) rather than a loop, matching this file's existing
    // style elsewhere (e.g. RightClickMarker1/2) of just writing exactly-two-of-something twice.
    private const string HmvSpeakerDrawAppend =
        "\n// HMV speaker icons - drawn last so they sit on top of everything else this frame" +
        "\nvar _hmv_s = hmv_speaker_size;" +
        "\nvar _hmv_glow_l = hmv_speaker_pulse;" +
        "\nif (hmv_speaker_l_hover) { _hmv_glow_l = max(_hmv_glow_l, 0.5); }" +
        "\nif (hmv_active) { _hmv_glow_l = max(_hmv_glow_l, 0.3); }" +
        "\ndraw_set_alpha(0.55 + _hmv_glow_l * 0.45);" +
        "\ndraw_set_color(make_color_rgb(60 + _hmv_glow_l*140, 60 + _hmv_glow_l*140, 75 + _hmv_glow_l*140));" +
        "\ndraw_rectangle(hmv_speaker_l_x, hmv_speaker_l_y + _hmv_s*0.15, hmv_speaker_l_x + _hmv_s*0.55, hmv_speaker_l_y + _hmv_s, false);" +
        "\ndraw_set_color(c_black);" +
        "\ndraw_rectangle(hmv_speaker_l_x, hmv_speaker_l_y + _hmv_s*0.15, hmv_speaker_l_x + _hmv_s*0.55, hmv_speaker_l_y + _hmv_s, true);" +
        "\ndraw_circle(hmv_speaker_l_x + _hmv_s*0.28, hmv_speaker_l_y + _hmv_s*0.4, _hmv_s*0.14, false);" +
        "\ndraw_circle(hmv_speaker_l_x + _hmv_s*0.28, hmv_speaker_l_y + _hmv_s*0.78, _hmv_s*0.16, false);" +
        "\ndraw_set_color(make_color_rgb(200 + _hmv_glow_l*55, 200 + _hmv_glow_l*55, 215 + _hmv_glow_l*40));" +
        "\ndraw_circle(hmv_speaker_l_x + _hmv_s*0.28, hmv_speaker_l_y + _hmv_s*0.4, _hmv_s*0.06, false);" +
        "\ndraw_circle(hmv_speaker_l_x + _hmv_s*0.28, hmv_speaker_l_y + _hmv_s*0.78, _hmv_s*0.07, false);" +
        "\nif (_hmv_glow_l > 0.05)" +
        "\n{" +
        "\n    draw_set_alpha(_hmv_glow_l * 0.5);" +
        "\n    draw_set_color(c_white);" +
        "\n    draw_circle(hmv_speaker_l_x + _hmv_s*0.28, hmv_speaker_l_y + _hmv_s*0.55, _hmv_s*0.75 + _hmv_glow_l*12, true);" +
        "\n}" +
        "\nvar _hmv_glow_r = hmv_speaker_pulse;" +
        "\nif (hmv_speaker_r_hover) { _hmv_glow_r = max(_hmv_glow_r, 0.5); }" +
        "\nif (hmv_active) { _hmv_glow_r = max(_hmv_glow_r, 0.3); }" +
        "\ndraw_set_alpha(0.55 + _hmv_glow_r * 0.45);" +
        "\ndraw_set_color(make_color_rgb(60 + _hmv_glow_r*140, 60 + _hmv_glow_r*140, 75 + _hmv_glow_r*140));" +
        "\ndraw_rectangle(hmv_speaker_r_x, hmv_speaker_r_y + _hmv_s*0.15, hmv_speaker_r_x + _hmv_s*0.55, hmv_speaker_r_y + _hmv_s, false);" +
        "\ndraw_set_color(c_black);" +
        "\ndraw_rectangle(hmv_speaker_r_x, hmv_speaker_r_y + _hmv_s*0.15, hmv_speaker_r_x + _hmv_s*0.55, hmv_speaker_r_y + _hmv_s, true);" +
        "\ndraw_circle(hmv_speaker_r_x + _hmv_s*0.28, hmv_speaker_r_y + _hmv_s*0.4, _hmv_s*0.14, false);" +
        "\ndraw_circle(hmv_speaker_r_x + _hmv_s*0.28, hmv_speaker_r_y + _hmv_s*0.78, _hmv_s*0.16, false);" +
        "\ndraw_set_color(make_color_rgb(200 + _hmv_glow_r*55, 200 + _hmv_glow_r*55, 215 + _hmv_glow_r*40));" +
        "\ndraw_circle(hmv_speaker_r_x + _hmv_s*0.28, hmv_speaker_r_y + _hmv_s*0.4, _hmv_s*0.06, false);" +
        "\ndraw_circle(hmv_speaker_r_x + _hmv_s*0.28, hmv_speaker_r_y + _hmv_s*0.78, _hmv_s*0.07, false);" +
        "\nif (_hmv_glow_r > 0.05)" +
        "\n{" +
        "\n    draw_set_alpha(_hmv_glow_r * 0.5);" +
        "\n    draw_set_color(c_white);" +
        "\n    draw_circle(hmv_speaker_r_x + _hmv_s*0.28, hmv_speaker_r_y + _hmv_s*0.55, _hmv_s*0.75 + _hmv_glow_r*12, true);" +
        "\n}" +
        "\ndraw_set_alpha(1);" +
        "\ndraw_set_color(c_white);";

    // Prepended (not inserted after a marker) so it's the very first thing Step_0 does each
    // frame, before any of the game's own logic reads thrust_speed/thrust_strength/thrust_middle.
    private const string HmvStepPrepend =
        "if (hmv_active)" +
        "\n{" +
        "\n    if ((current_time - hmv_last_packet_time) > 500)" +
        "\n    {" +
        "\n        hmv_active = false;" +
        "\n        hmv_background_color = 16777215;" +
        "\n    }" +
        "\n    else" +
        "\n    {" +
        "\n        thrust_speed = hmv_thrust_speed;" +
        "\n        thrust_strength = hmv_thrust_strength;" +
        "\n        thrust_middle = hmv_thrust_middle;" +
        "\n    }" +
        "\n}\n";

    private const string HmvAsyncNetworkingEventName = "gml_Object_oFutaMatingPress_Other_68";
    private const string HmvAsyncNetworkingCode =
        // Explicit ds_map_find_value(...) rather than the async_load[? "key"] shorthand - the
        // shorthand caused a real, observed runtime error ("unable to convert string \"type\"
        // to int64") on this game's exact toolchain, apparently from the compiler failing to
        // infer that async_load is a string-keyed map. The explicit function form sidesteps
        // whatever that inference gap is entirely, and is functionally identical otherwise.
        "if (ds_map_find_value(async_load, \"type\") == network_type_data && ds_map_find_value(async_load, \"id\") == hmv_socket)" +
        "\n{" +
        "\n    var _buf = ds_map_find_value(async_load, \"buffer\");" +
        "\n    if (buffer_get_size(_buf) >= 16)" +
        "\n    {" +
        "\n        hmv_thrust_speed = buffer_read(_buf, buffer_f32);" +
        "\n        hmv_thrust_strength = buffer_read(_buf, buffer_f32);" +
        "\n        hmv_thrust_middle = buffer_read(_buf, buffer_f32);" +
        "\n        hmv_background_color = buffer_read(_buf, buffer_u32);" +
        "\n        hmv_active = true;" +
        "\n        hmv_last_packet_time = current_time;" +
        "\n    }" +
        "\n}";

    private const string BackgroundEventName = "gml_Object_oBackground_Draw_0";
    private const string BackgroundNoTintLiteral = "16777215";
    private const string BackgroundTintExpr = "oFutaMatingPress.hmv_background_color";

    public static (bool Compatible, bool AlreadyPatched, string Detail) CheckHmvStatus(string dataWinPath)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            var stepCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == "gml_Object_oFutaMatingPress_Step_0");
            var bgCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == BackgroundEventName);
            if (createCode is null || stepCode is null || bgCode is null)
            {
                return (false, false, "Not a compatible game (missing oFutaMatingPress/oBackground).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
            bool already = createText.Contains("hmv_socket");
            return (true, already, already ? "Already patched." : "Compatible, not yet patched.");
        }
        catch (Exception ex)
        {
            return (false, false, $"Couldn't check: {ex.Message}");
        }
    }

    public static PatchOutcome PatchHmv(string dataWinPath)
    {
        try
        {
            string backupPath = dataWinPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(dataWinPath, backupPath);
            }

            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var futaObj = data.GameObjects.FirstOrDefault(o => o is not null && o.Name?.Content == "oFutaMatingPress");
            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            var stepCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == "gml_Object_oFutaMatingPress_Step_0");
            var futaDrawCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == DrawEventName);
            var bgCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == BackgroundEventName);
            if (futaObj is null || createCode is null || stepCode is null || futaDrawCode is null || bgCode is null)
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "This doesn't look like a compatible game (missing oFutaMatingPress/oBackground).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;

            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
            if (createText.Contains("hmv_socket"))
            {
                return new PatchOutcome(PatchResult.AlreadyPatched, "HMV mode is already patched in - nothing to do.");
            }
            if (!createText.Contains(HmvCreateMarker))
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "Couldn't find the expected code in the Create event - this game's version may not be compatible.");
            }
            createText = createText.Replace(HmvCreateMarker, HmvCreateMarker + HmvCreatePatch + HmvSpeakerCreatePatch);

            string stepText = new DecompileContext(globalContext, stepCode, settings).DecompileToString();
            stepText = HmvSpeakerStepPrepend + HmvStepPrepend + stepText;

            string futaDrawText = new DecompileContext(globalContext, futaDrawCode, settings).DecompileToString();
            futaDrawText += HmvSpeakerDrawAppend;

            string bgText = new DecompileContext(globalContext, bgCode, settings).DecompileToString();
            bgText = bgText.Replace(BackgroundNoTintLiteral, BackgroundTintExpr);

            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };
            importGroup.QueueReplace(CreateEventName, createText);
            importGroup.QueueReplace("gml_Object_oFutaMatingPress_Step_0", stepText);
            importGroup.QueueReplace(DrawEventName, futaDrawText);
            importGroup.QueueReplace(BackgroundEventName, bgText);
            importGroup.QueueReplace(HmvAsyncNetworkingEventName, HmvAsyncNetworkingCode);
            importGroup.Import();

            // The new code entry now exists (CodeImportGroup created it), but nothing on the
            // object calls it yet - GameMaker only runs code that's wired to a registered event.
            // Register a new Async Networking event (EventType.Other=7, EventSubtypeOther.
            // AsyncNetworking=68) pointing at it, using the exact same action-wrapper field
            // values this game's own compiler already uses for every other event (verified
            // directly against this game's existing events rather than assumed).
            var newCode = data.Code.ByName(HmvAsyncNetworkingEventName);
            if (newCode is null)
            {
                return new PatchOutcome(PatchResult.Error, "HMV async-networking code entry wasn't created as expected.");
            }

            var asyncEvent = new UndertaleGameObject.Event { EventSubtype = (uint)EventSubtypeOther.AsyncNetworking };
            var asyncAction = new UndertaleGameObject.EventAction
            {
                LibID = 1,
                ID = 603,
                Kind = 7,
                UseRelative = false,
                IsQuestion = false,
                UseApplyTo = false,
                ExeType = 2,
                ActionName = null,
                ArgumentCount = 0,
                Who = -1,
                Relative = false,
                IsNot = false,
                UnknownAlwaysZero = 0,
                CodeId = newCode,
            };
            asyncEvent.Actions.Add(asyncAction);
            futaObj.Events[(int)EventType.Other].Add(asyncEvent);

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            return new PatchOutcome(PatchResult.Patched,
                $"HMV mode patched successfully! A backup of the original was saved as:\n{backupPath}");
        }
        catch (Exception ex)
        {
            return new PatchOutcome(PatchResult.Error, $"Something went wrong while patching HMV mode: {ex.Message}");
        }
    }

    // ================================================================================
    // TOUCH CONTROLS - a separate, optional patch (never applied unless explicitly requested).
    // Fixes mods (confirmed specifically in ModRoom - vanilla's own Android build has its own
    // touch-adapted controls already and doesn't need this) whose settings rely on PC-only
    // inputs that have literally no touchscreen equivalent: right-click (portrait/outfit
    // toggles) and the mouse scroll wheel (custom character/background selection menus). Adds a
    // long-press-equals-right-click and a vertical-drag-equals-scroll-wheel alternative
    // alongside the existing mouse controls - PC mouse/wheel behavior is completely unchanged,
    // this only adds new ways to trigger the exact same existing code paths.
    //
    // Only patches oFutaMatingPress_Step_0 (adds long-press tracking + wires it into the two
    // existing mouse_check_button_pressed(2) checks) and the shared menu_scroll_update()
    // function inside Create_0 (used by every scrollable menu - background, custom wife/futa
    // pickers, etc. - so this one function fix covers all of them at once). Gracefully reports
    // NotSupported rather than erroring if a given data file doesn't have these exact patterns -
    // this is intentionally narrow/specific to what was actually found in ModRoom, not a general
    // "make everything touch friendly" claim.

    private const string TouchCreateMarker = "thrust_time = 0;";
    private const string TouchCreatePatch =
        "\ntouch_hold_start = 0;" +
        "\ntouch_long_press_fired = false;" +
        "\ntouch_long_press_synthetic = false;";

    private const string RightClickMarker1 = "if (mouse_check_button_pressed(2) && cursor_id == 9)";
    private const string RightClickReplacement1 = "if ((mouse_check_button_pressed(2) || touch_long_press_synthetic) && cursor_id == 9)";

    private const string RightClickMarker2 = "if (mouse_check_button_pressed(2))";
    private const string RightClickReplacement2 = "if (mouse_check_button_pressed(2) || touch_long_press_synthetic)";

    // Prepended to Step_0 (not inserted after a marker) so the long-press tracking runs before
    // anything else that checks touch_long_press_synthetic this frame.
    private const string TouchStepPrepend =
        "if (mouse_check_button(mb_left))" +
        "\n{" +
        "\n    if (touch_hold_start == 0)" +
        "\n    {" +
        "\n        touch_hold_start = current_time;" +
        "\n        touch_long_press_synthetic = false;" +
        "\n    }" +
        "\n    else if (!touch_long_press_fired && (current_time - touch_hold_start) >= 450)" +
        "\n    {" +
        "\n        touch_long_press_fired = true;" +
        "\n        touch_long_press_synthetic = true;" +
        "\n    }" +
        "\n    else" +
        "\n    {" +
        "\n        touch_long_press_synthetic = false;" +
        "\n    }" +
        "\n}" +
        "\nelse" +
        "\n{" +
        "\n    touch_hold_start = 0;" +
        "\n    touch_long_press_fired = false;" +
        "\n    touch_long_press_synthetic = false;" +
        "\n}\n";

    private const string MenuScrollFunctionName = "menu_scroll_update";
    private const string MenuScrollOriginal =
        "function menu_scroll_update(arg0, arg1, arg2)\n" +
        "{\n" +
        "    if (mouse_wheel_up() || mouse_wheel_down())\n" +
        "    {\n" +
        "        arg0.scroll += mouse_wheel_down() - mouse_wheel_up();\n" +
        "        arg0.scroll = median(arg0.scroll, 0, max(0, arg1 - arg2));\n" +
        "    }\n" +
        "    arg0.scroll_lerp = lerp(arg0.scroll_lerp, arg0.scroll, 1);\n" +
        "    if (abs(arg0.scroll - arg0.scroll_lerp) < 0.01)\n" +
        "    {\n" +
        "        arg0.scroll_lerp = arg0.scroll;\n" +
        "    }\n" +
        "}";
    private const string MenuScrollReplacement =
        "function menu_scroll_update(arg0, arg1, arg2)\n" +
        "{\n" +
        "    if (mouse_wheel_up() || mouse_wheel_down())\n" +
        "    {\n" +
        "        arg0.scroll += mouse_wheel_down() - mouse_wheel_up();\n" +
        "        arg0.scroll = median(arg0.scroll, 0, max(0, arg1 - arg2));\n" +
        "    }\n" +
        "    if (mouse_check_button(mb_left))\n" +
        "    {\n" +
        "        if (!variable_struct_exists(arg0, \"touch_drag_last_y\"))\n" +
        "        {\n" +
        "            arg0.touch_drag_last_y = mouse_y;\n" +
        "            arg0.touch_drag_accum = 0;\n" +
        "        }\n" +
        "        else\n" +
        "        {\n" +
        "            arg0.touch_drag_accum += (mouse_y - arg0.touch_drag_last_y);\n" +
        "            arg0.touch_drag_last_y = mouse_y;\n" +
        "            var _row_height = 64;\n" +
        "            while (arg0.touch_drag_accum <= -_row_height)\n" +
        "            {\n" +
        "                arg0.scroll -= 1;\n" +
        "                arg0.touch_drag_accum += _row_height;\n" +
        "            }\n" +
        "            while (arg0.touch_drag_accum >= _row_height)\n" +
        "            {\n" +
        "                arg0.scroll += 1;\n" +
        "                arg0.touch_drag_accum -= _row_height;\n" +
        "            }\n" +
        "            arg0.scroll = median(arg0.scroll, 0, max(0, arg1 - arg2));\n" +
        "        }\n" +
        "    }\n" +
        "    else if (variable_struct_exists(arg0, \"touch_drag_last_y\"))\n" +
        "    {\n" +
        "        variable_struct_remove(arg0, \"touch_drag_last_y\");\n" +
        "    }\n" +
        "    arg0.scroll_lerp = lerp(arg0.scroll_lerp, arg0.scroll, 1);\n" +
        "    if (abs(arg0.scroll - arg0.scroll_lerp) < 0.01)\n" +
        "    {\n" +
        "        arg0.scroll_lerp = arg0.scroll;\n" +
        "    }\n" +
        "}";

    // A second known variant of the SAME function, found on a different, actively-developed fork
    // (WB-ModRoom "Release 1", 2026-07-27) - confirmed by direct decompile+diff that this is the
    // ONLY difference from MenuScrollOriginal above: the wheel-direction subtraction is reversed
    // (that fork's own choice of scroll direction). The replacement below preserves that exact
    // same direction rather than silently flipping it back - only the drag-to-scroll addition is
    // new behavior, matching this patch's "only ever ADDS, never changes existing behavior" rule.
    private const string MenuScrollOriginalFlipped =
        "function menu_scroll_update(arg0, arg1, arg2)\n" +
        "{\n" +
        "    if (mouse_wheel_up() || mouse_wheel_down())\n" +
        "    {\n" +
        "        arg0.scroll += mouse_wheel_up() - mouse_wheel_down();\n" +
        "        arg0.scroll = median(arg0.scroll, 0, max(0, arg1 - arg2));\n" +
        "    }\n" +
        "    arg0.scroll_lerp = lerp(arg0.scroll_lerp, arg0.scroll, 1);\n" +
        "    if (abs(arg0.scroll - arg0.scroll_lerp) < 0.01)\n" +
        "    {\n" +
        "        arg0.scroll_lerp = arg0.scroll;\n" +
        "    }\n" +
        "}";
    private const string MenuScrollReplacementFlipped =
        "function menu_scroll_update(arg0, arg1, arg2)\n" +
        "{\n" +
        "    if (mouse_wheel_up() || mouse_wheel_down())\n" +
        "    {\n" +
        "        arg0.scroll += mouse_wheel_up() - mouse_wheel_down();\n" +
        "        arg0.scroll = median(arg0.scroll, 0, max(0, arg1 - arg2));\n" +
        "    }\n" +
        "    if (mouse_check_button(mb_left))\n" +
        "    {\n" +
        "        if (!variable_struct_exists(arg0, \"touch_drag_last_y\"))\n" +
        "        {\n" +
        "            arg0.touch_drag_last_y = mouse_y;\n" +
        "            arg0.touch_drag_accum = 0;\n" +
        "        }\n" +
        "        else\n" +
        "        {\n" +
        "            arg0.touch_drag_accum += (mouse_y - arg0.touch_drag_last_y);\n" +
        "            arg0.touch_drag_last_y = mouse_y;\n" +
        "            var _row_height = 64;\n" +
        "            while (arg0.touch_drag_accum <= -_row_height)\n" +
        "            {\n" +
        "                arg0.scroll -= 1;\n" +
        "                arg0.touch_drag_accum += _row_height;\n" +
        "            }\n" +
        "            while (arg0.touch_drag_accum >= _row_height)\n" +
        "            {\n" +
        "                arg0.scroll += 1;\n" +
        "                arg0.touch_drag_accum -= _row_height;\n" +
        "            }\n" +
        "            arg0.scroll = median(arg0.scroll, 0, max(0, arg1 - arg2));\n" +
        "        }\n" +
        "    }\n" +
        "    else if (variable_struct_exists(arg0, \"touch_drag_last_y\"))\n" +
        "    {\n" +
        "        variable_struct_remove(arg0, \"touch_drag_last_y\");\n" +
        "    }\n" +
        "    arg0.scroll_lerp = lerp(arg0.scroll_lerp, arg0.scroll, 1);\n" +
        "    if (abs(arg0.scroll - arg0.scroll_lerp) < 0.01)\n" +
        "    {\n" +
        "        arg0.scroll_lerp = arg0.scroll;\n" +
        "    }\n" +
        "}";

    // Custom character discovery fix - a SEPARATE real bug from the touch-input one above, found
    // by reading this exact code: file_find_first (directory ENUMERATION) is well-documented not
    // to work against files bundled inside an Android APK - only opening a file by an already-
    // known name works there. custom_futas/custom_wives are discovered by enumerating their
    // folder with file_find_first, so on Android this silently finds nothing, custom_sprite_
    // loaded/custom_wife_sprite_loaded never become true, and the CUSTOM option stays disabled -
    // even though the actual bundled files are all individually present and readable. Fixed by
    // reading a manifest file (one folder name per line, generated by ApkPatcher's --include-mods
    // at bundle time - see Program.cs) instead of enumerating, when that manifest exists; falls
    // back to the original file_find_first behavior otherwise, so PC (which never has a
    // manifest) is completely unaffected.

    // IMPORTANT: working_directory has a trailing slash on Android ("assets/") but NOT on PC -
    // confirmed directly (a diagnostic probe logged working_directory=[assets/] on device, and
    // separately confirmed file_exists() straight up fails given a resulting DOUBLE slash, e.g.
    // working_directory + "/custom_wives/..." = "assets//custom_wives/..." on Android - single
    // slash works, double doesn't). This affects the ORIGINAL game's own path construction too,
    // not just the new manifest-reading code, so both need the same normalize-first treatment:
    // strip any trailing slash off working_directory, then always add exactly one back.

    // SECOND Android-only bug found the same way (diagnostic probe): directory_exists() returns
    // false on Android for bundled asset folders even given a correctly single-slash path - a
    // known Android/APK-assets limitation, same family as file_find_first not enumerating.
    // file_exists() on a specific file inside that same folder works fine, so the manifest-driven
    // replacement loops below no longer gate on directory_exists(_full_path) at all - they call
    // check_custom_futa()/check_custom_wife() unconditionally for every manifest-listed name and
    // let THOSE functions' own internal file_exists() checks (confirmed working) decide validity,
    // same as what the original code effectively achieved via directory_exists on PC.
    // strip any trailing slash off working_directory, then always add exactly one back.
    private const string WorkDirNormalize = "var _wd = working_directory; if (string_char_at(_wd, string_length(_wd)) == \"/\") { _wd = string_copy(_wd, 1, string_length(_wd) - 1); }\n";

    private const string CustomFutaScanOriginal =
        "var _file = file_find_first(working_directory + \"/custom_futas/*\", 16);\n" +
        "while (_file != \"\")\n" +
        "{\n" +
        "    var _full_path = working_directory + \"/custom_futas/\" + _file;\n" +
        "    if (directory_exists(_full_path))\n" +
        "    {\n" +
        "        check_struct = check_custom_futa(_full_path);\n" +
        "        if (check_struct.custom_portrait != 0 && check_struct.custom_mating_press != 0 && check_struct.custom_cowgirls != 0 && check_struct.custom_xray != 0)\n" +
        "        {\n" +
        "            custom_sprite_loaded = true;\n" +
        "            tutorial = false;\n" +
        "            show_debug_message(\"SUCCESS: Loaded \" + _file);\n" +
        "            ds_list_add(custom_futas_folder, _full_path);\n" +
        "        }\n" +
        "    }\n" +
        "    _file = file_find_next();\n" +
        "}\n" +
        "file_find_close();";
    private const string CustomFutaScanReplacement =
        WorkDirNormalize +
        "var _futa_names = [];\n" +
        "if (file_exists(_wd + \"/custom_futas/_manifest.txt\"))\n" +
        "{\n" +
        "    var _mf_futa = file_text_open_read(_wd + \"/custom_futas/_manifest.txt\");\n" +
        "    while (!file_text_eof(_mf_futa))\n" +
        "    {\n" +
        "        var _mf_line = string_replace_all(string_replace_all(file_text_readln(_mf_futa), \"\\r\", \"\"), \"\\n\", \"\");\n" +
        "        if (_mf_line != \"\") { array_push(_futa_names, _mf_line); }\n" +
        "    }\n" +
        "    file_text_close(_mf_futa);\n" +
        "}\n" +
        "else\n" +
        "{\n" +
        "    var _ff_futa = file_find_first(_wd + \"/custom_futas/*\", 16);\n" +
        "    while (_ff_futa != \"\")\n" +
        "    {\n" +
        "        array_push(_futa_names, _ff_futa);\n" +
        "        _ff_futa = file_find_next();\n" +
        "    }\n" +
        "    file_find_close();\n" +
        "}\n" +
        "for (var _fi = 0; _fi < array_length(_futa_names); _fi++)\n" +
        "{\n" +
        "    var _file = _futa_names[_fi];\n" +
        "    var _full_path = _wd + \"/custom_futas/\" + _file;\n" +
        "    check_struct = check_custom_futa(_full_path);\n" +
        "    if (check_struct.custom_portrait != 0 && check_struct.custom_mating_press != 0 && check_struct.custom_cowgirls != 0 && check_struct.custom_xray != 0)\n" +
        "    {\n" +
        "        custom_sprite_loaded = true;\n" +
        "        tutorial = false;\n" +
        "        show_debug_message(\"SUCCESS: Loaded \" + _file);\n" +
        "        ds_list_add(custom_futas_folder, _full_path);\n" +
        "    }\n" +
        "}";

    private const string CustomWifeScanOriginal =
        "_file = file_find_first(working_directory + \"/custom_wives\" + \"/*\", 16);\n" +
        "while (_file != \"\")\n" +
        "{\n" +
        "    var _full_path = working_directory + \"/custom_wives/\" + _file;\n" +
        "    if (directory_exists(_full_path))\n" +
        "    {\n" +
        "        check_struct = check_custom_wife(_full_path);\n" +
        "        if (check_struct.custom_data != 0)\n" +
        "        {\n" +
        "            if (check_struct.custom_wife_cowgirl != 0 && check_struct.custom_wife_reverse_cowgirl != 0 && check_struct.custom_wife_mating_press != 0)\n" +
        "            {\n" +
        "                custom_wife_has_portrait = check_struct.custom_wife_portrait;\n" +
        "                custom_wife_sprite_loaded = true;\n" +
        "                show_debug_message(\"SUCCESS: Loaded \" + _file);\n" +
        "                ds_list_add(custom_wives_folder, _full_path);\n" +
        "            }\n" +
        "        }\n" +
        "    }\n" +
        "    _file = file_find_next();\n" +
        "}\n" +
        "file_find_close();";
    private const string CustomWifeScanReplacement =
        WorkDirNormalize +
        "var _wife_names = [];\n" +
        "if (file_exists(_wd + \"/custom_wives/_manifest.txt\"))\n" +
        "{\n" +
        "    var _mf_wife = file_text_open_read(_wd + \"/custom_wives/_manifest.txt\");\n" +
        "    while (!file_text_eof(_mf_wife))\n" +
        "    {\n" +
        "        var _mf_line2 = string_replace_all(string_replace_all(file_text_readln(_mf_wife), \"\\r\", \"\"), \"\\n\", \"\");\n" +
        "        if (_mf_line2 != \"\") { array_push(_wife_names, _mf_line2); }\n" +
        "    }\n" +
        "    file_text_close(_mf_wife);\n" +
        "}\n" +
        "else\n" +
        "{\n" +
        "    var _ff_wife = file_find_first(_wd + \"/custom_wives\" + \"/*\", 16);\n" +
        "    while (_ff_wife != \"\")\n" +
        "    {\n" +
        "        array_push(_wife_names, _ff_wife);\n" +
        "        _ff_wife = file_find_next();\n" +
        "    }\n" +
        "    file_find_close();\n" +
        "}\n" +
        "for (var _wi = 0; _wi < array_length(_wife_names); _wi++)\n" +
        "{\n" +
        "    var _file = _wife_names[_wi];\n" +
        "    var _full_path = _wd + \"/custom_wives/\" + _file;\n" +
        "    check_struct = check_custom_wife(_full_path);\n" +
        "    if (check_struct.custom_data != 0)\n" +
        "    {\n" +
        "        if (check_struct.custom_wife_cowgirl != 0 && check_struct.custom_wife_reverse_cowgirl != 0 && check_struct.custom_wife_mating_press != 0)\n" +
        "        {\n" +
        "            custom_wife_has_portrait = check_struct.custom_wife_portrait;\n" +
        "            custom_wife_sprite_loaded = true;\n" +
        "            show_debug_message(\"SUCCESS: Loaded \" + _file);\n" +
        "            ds_list_add(custom_wives_folder, _full_path);\n" +
        "        }\n" +
        "    }\n" +
        "}";

    // Structural variant of the two rewrites above, for forks whose loop BODIES differ from
    // ModRoom 3.2's (DeepRoom 3.5 adds selector lists, portrait previews and 1.4 "legacy spouse"
    // support inside them). Only the enumeration scaffolding is replaced - the opening lines and
    // the closing file_find_next()/file_find_close() - and whatever is in between is kept
    // verbatim. On PC (no manifest) the original directory_exists() filter still applies.
    private const string ScanLoopFooter = "    _file = file_find_next();\n}\nfile_find_close();";

    private const string FutaScanHeader =
        "var _file = file_find_first(working_directory + \"/custom_futas/*\", 16);\n" +
        "while (_file != \"\")\n" +
        "{\n" +
        "    var _full_path = working_directory + \"/custom_futas/\" + _file;\n" +
        "    if (directory_exists(_full_path))\n";
    private const string FutaScanHeaderReplacement =
        WorkDirNormalize +
        "var _futa_names = [];\n" +
        "var _futa_manifest = file_exists(_wd + \"/custom_futas/_manifest.txt\");\n" +
        "if (_futa_manifest)\n" +
        "{\n" +
        "    var _mf_futa = file_text_open_read(_wd + \"/custom_futas/_manifest.txt\");\n" +
        "    while (!file_text_eof(_mf_futa))\n" +
        "    {\n" +
        "        var _mf_line = string_replace_all(string_replace_all(file_text_readln(_mf_futa), \"\\r\", \"\"), \"\\n\", \"\");\n" +
        "        if (_mf_line != \"\") { array_push(_futa_names, _mf_line); }\n" +
        "    }\n" +
        "    file_text_close(_mf_futa);\n" +
        "}\n" +
        "else\n" +
        "{\n" +
        "    var _ff_futa = file_find_first(_wd + \"/custom_futas/*\", 16);\n" +
        "    while (_ff_futa != \"\")\n" +
        "    {\n" +
        "        array_push(_futa_names, _ff_futa);\n" +
        "        _ff_futa = file_find_next();\n" +
        "    }\n" +
        "    file_find_close();\n" +
        "}\n" +
        "for (var _fi = 0; _fi < array_length(_futa_names); _fi++)\n" +
        "{\n" +
        "    var _file = _futa_names[_fi];\n" +
        "    var _full_path = _wd + \"/custom_futas/\" + _file;\n" +
        "    if (_futa_manifest || directory_exists(_full_path))\n";

    private const string WifeScanHeader =
        "_file = file_find_first(working_directory + \"/custom_wives\" + \"/*\", 16);\n" +
        "while (_file != \"\")\n" +
        "{\n" +
        "    if (directory_exists(working_directory + \"/custom_wives/\" + _file))\n" +
        "    {\n" +
        "        var _full_path = working_directory + \"/custom_wives/\" + _file;\n";
    private const string WifeScanHeaderReplacement =
        WorkDirNormalize +
        "var _wife_names = [];\n" +
        "var _wife_manifest = file_exists(_wd + \"/custom_wives/_manifest.txt\");\n" +
        "if (_wife_manifest)\n" +
        "{\n" +
        "    var _mf_wife = file_text_open_read(_wd + \"/custom_wives/_manifest.txt\");\n" +
        "    while (!file_text_eof(_mf_wife))\n" +
        "    {\n" +
        "        var _mf_line2 = string_replace_all(string_replace_all(file_text_readln(_mf_wife), \"\\r\", \"\"), \"\\n\", \"\");\n" +
        "        if (_mf_line2 != \"\") { array_push(_wife_names, _mf_line2); }\n" +
        "    }\n" +
        "    file_text_close(_mf_wife);\n" +
        "}\n" +
        "else\n" +
        "{\n" +
        "    var _ff_wife = file_find_first(_wd + \"/custom_wives\" + \"/*\", 16);\n" +
        "    while (_ff_wife != \"\")\n" +
        "    {\n" +
        "        array_push(_wife_names, _ff_wife);\n" +
        "        _ff_wife = file_find_next();\n" +
        "    }\n" +
        "    file_find_close();\n" +
        "}\n" +
        "for (var _wi = 0; _wi < array_length(_wife_names); _wi++)\n" +
        "{\n" +
        "    var _file = _wife_names[_wi];\n" +
        "    if (_wife_manifest || directory_exists(_wd + \"/custom_wives/\" + _file))\n" +
        "    {\n" +
        "        var _full_path = _wd + \"/custom_wives/\" + _file;\n";

    // Swaps one loop's header and the first footer after it, keeping the body. Null = no match.
    private static string? RewriteScanLoop(string text, string header, string headerReplacement)
    {
        int start = text.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return null;
        int footer = text.IndexOf(ScanLoopFooter, start + header.Length, StringComparison.Ordinal);
        if (footer < 0) return null;
        return text[..start] + headerReplacement + text[(start + header.Length)..footer] + "}" + text[(footer + ScanLoopFooter.Length)..];
    }

    // Pill menu full layout on Android (ModRoom-specific - vanilla Wife's Bedroom has no pill menu
    // at all, confirmed directly by checking, so this gracefully no-ops there). The pill menu
    // already goes through the same shared menu_scroll_update() the touch-controls patch above
    // fixes, but drag-to-scroll on Android was separately reported as still not reliably working
    // there (see NOTES.txt's earlier open item).
    //
    // FIRST ATTEMPT (worth recording): just raised the single-column visible-row cap from 11 to
    // all 14, reasoning the extra 3 rows (ending at y=276) still fit before the next UI (bottom
    // icon-button column at y=377-497). User reported this STILL required scrolling in practice
    // and asked for something that can't possibly run off-screen instead: two columns of 7, not
    // one column of 14. Replaced with that - a real 2-column grid (7 rows each) instead of trying
    // to fit a taller single column, which sidesteps the whole "does it fit vertically" question.
    // This also means pill_menu.scroll/scroll_lerp are no longer used for this menu AT ALL (no
    // partial-row scroll math needed when nothing can ever be off-screen), so the whole original
    // block is replaced wholesale rather than patched in a few spots, and the Step_0 call to
    // menu_scroll_update(pill_menu, ...) is removed entirely rather than just neutralized.
    private const string PillMenuOriginal =
        "comment = \"pill menu\";\n" +
        "if (pill_menu_toggle)\n" +
        "{\n" +
        "    var pill_data = [[\"Contraceptive\", \"contraceptive_pill\"], [\"Mega Sperm\", \"mega_sperm_pill\"], [\"Equine Penis\", \"equine_pill\"], [\"Knotted Penis\", \"knotted_pill\"], [\"Extra Thick\", \"extra_thick_pill\"], [\"Diphallia\", \"diphallia_pill\"], [\"Ovulation\", \"ovulation_pill\"], [\"Stamina\", \"stamina_pill\"], [\"Leaky\", \"leaky_pill\"], [\"Hyper Breeding\", \"hyper_breeding_pill\"], [\"Quickshot\", \"quickshot_pill\"], [\"Blockage\", \"blockage_pill\"], [\"Deceleration\", \"reverse_speed_pill\"], [\"Self Edge\", \"edge_addict_pill\"]];\n" +
        "    var _count = array_length(pill_data);\n" +
        "    var _visible = min(_count, 11);\n" +
        "    total_pill_amount = _count;\n" +
        "    draw_set_halign(1);\n" +
        "    draw_set_valign(1);\n" +
        "    if (pill_menu.scroll > 0)\n" +
        "    {\n" +
        "        draw_sprite_ext(sButtonArrows, 0, room_width - 56 - (sprite_get_width(sButtonArrows) / 2), 109, 0.5, 0.5, -90, 16777215, 1);\n" +
        "    }\n" +
        "    if (pill_menu.scroll < (total_pill_amount - 11))\n" +
        "    {\n" +
        "        draw_sprite_ext(sButtonArrows, 1, room_width - 56 - (sprite_get_width(sButtonArrows) / 2), (120 + (12 * _visible)) - 5, 0.5, 0.5, -90, 16777215, 1);\n" +
        "    }\n" +
        "    for (i = 0; i < _count; i++)\n" +
        "    {\n" +
        "        var _visual_pos = i - pill_menu.scroll_lerp;\n" +
        "        if (_visual_pos <= -1 || _visual_pos >= 11)\n" +
        "        {\n" +
        "            continue;\n" +
        "        }\n" +
        "        var b2_x = room_width - 62;\n" +
        "        var b2_y = 120 + (12 * _visual_pos);\n" +
        "        var button_press = point_in_rectangle(mouse_x, mouse_y, b2_x - 26, b2_y - 5, b2_x + 30, b2_y + 5);\n" +
        "        draw_sprite_ext(sButtonBack, 1, b2_x, b2_y, 4, 1, 0, 16777215, 0.5 + (1 * button_press));\n" +
        "        var btn_name = pill_data[i][0];\n" +
        "        var var_name = pill_data[i][1];\n" +
        "        var is_active = variable_instance_get(id, var_name);\n" +
        "        draw_set_color(is_active ? 16777215 : 8421504);\n" +
        "        draw_text_transformed(b2_x, b2_y, btn_name, 0.5, 0.5, 0);\n" +
        "        if (mouse_check_button_pressed(1) && button_press)\n" +
        "        {\n" +
        "            if (!(orgasm == true && var_name == \"blockage_pill\"))\n" +
        "            {\n" +
        "                variable_instance_set(id, var_name, !is_active);\n" +
        "            }\n" +
        "        }\n" +
        "    }\n" +
        "    draw_set_color(16777215);\n" +
        "    draw_set_valign(0);\n" +
        "}";

    // room_width - 62 is the original single column's x; the second column sits COLUMN_GAP_PX
    // further left. sButtonBack is drawn at scale 4 on its base sprite (16px wide -> ~64px
    // rendered), so 88px leaves a clear ~24px gap between columns - not pixel-measured against a
    // real render (no reliable way to screenshot into this specific menu - see NOTES.txt), but a
    // deliberately generous margin specifically because it couldn't be visually confirmed directly.
    private const string PillMenuReplacement =
        "comment = \"pill menu\";\n" +
        "if (pill_menu_toggle)\n" +
        "{\n" +
        "    var pill_data = [[\"Contraceptive\", \"contraceptive_pill\"], [\"Mega Sperm\", \"mega_sperm_pill\"], [\"Equine Penis\", \"equine_pill\"], [\"Knotted Penis\", \"knotted_pill\"], [\"Extra Thick\", \"extra_thick_pill\"], [\"Diphallia\", \"diphallia_pill\"], [\"Ovulation\", \"ovulation_pill\"], [\"Stamina\", \"stamina_pill\"], [\"Leaky\", \"leaky_pill\"], [\"Hyper Breeding\", \"hyper_breeding_pill\"], [\"Quickshot\", \"quickshot_pill\"], [\"Blockage\", \"blockage_pill\"], [\"Deceleration\", \"reverse_speed_pill\"], [\"Self Edge\", \"edge_addict_pill\"]];\n" +
        "    var _count = array_length(pill_data);\n" +
        "    total_pill_amount = _count;\n" +
        "    draw_set_halign(1);\n" +
        "    draw_set_valign(1);\n" +
        "    var _rows = ceil(_count / 2);\n" +
        "    for (i = 0; i < _count; i++)\n" +
        "    {\n" +
        "        var _col = i div _rows;\n" +
        "        var _row = i mod _rows;\n" +
        "        var b2_x = (room_width - 62) - (_col * 88);\n" +
        "        var b2_y = 120 + (12 * _row);\n" +
        "        var button_press = point_in_rectangle(mouse_x, mouse_y, b2_x - 26, b2_y - 5, b2_x + 30, b2_y + 5);\n" +
        "        draw_sprite_ext(sButtonBack, 1, b2_x, b2_y, 4, 1, 0, 16777215, 0.5 + (1 * button_press));\n" +
        "        var btn_name = pill_data[i][0];\n" +
        "        var var_name = pill_data[i][1];\n" +
        "        var is_active = variable_instance_get(id, var_name);\n" +
        "        draw_set_color(is_active ? 16777215 : 8421504);\n" +
        "        draw_text_transformed(b2_x, b2_y, btn_name, 0.5, 0.5, 0);\n" +
        "        if (mouse_check_button_pressed(1) && button_press)\n" +
        "        {\n" +
        "            if (!(orgasm == true && var_name == \"blockage_pill\"))\n" +
        "            {\n" +
        "                variable_instance_set(id, var_name, !is_active);\n" +
        "            }\n" +
        "        }\n" +
        "    }\n" +
        "    draw_set_color(16777215);\n" +
        "    draw_set_valign(0);\n" +
        "}";

    // No longer needed at all once the pill menu never scrolls - removed entirely rather than
    // neutralized, since pill_menu.scroll/scroll_lerp aren't referenced anywhere above anymore.
    private const string PillMenuScrollUpdateMarker =
        "if (pill_menu_toggle)\n" +
        "{\n" +
        "    menu_scroll_update(pill_menu, total_pill_amount, 11);\n" +
        "}\n";
    private const string PillMenuScrollUpdateReplacement = "";

    public static (bool Compatible, bool AlreadyPatched, string Detail) CheckTouchControlsStatus(string dataWinPath)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            var stepCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == "gml_Object_oFutaMatingPress_Step_0");
            if (createCode is null || stepCode is null)
            {
                return (false, false, "Not a compatible game (missing oFutaMatingPress).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
            if (createText.Contains("touch_long_press_synthetic"))
            {
                return (true, true, "Already patched.");
            }

            // Scroll-drag and right-click long-press are each patched independently below (best-
            // effort, same philosophy as the custom-character-discovery/pill-menu fixes further
            // down) - a build only needs ONE of the two known patterns present to be considered
            // compatible, since a fork may have restructured one but not the other (confirmed
            // happening in practice - WB-ModRoom "Release 1" has a scroll-update variant but a
            // completely restructured right-click check).
            bool hasScroll = createText.Contains(MenuScrollOriginal) || createText.Contains(MenuScrollOriginalFlipped);
            string stepText = new DecompileContext(globalContext, stepCode, settings).DecompileToString();
            bool hasRightClick = stepText.Contains(RightClickMarker1) && stepText.Contains(RightClickMarker2);

            if (!hasScroll && !hasRightClick)
            {
                return (false, false,
                    "Couldn't find the expected mouse-wheel menu code or right-click code - this is specific " +
                    "to ModRoom-style right-click/scroll-wheel controls, and may not apply to this game/version.");
            }

            string detail = "Compatible, not yet patched.";
            if (!hasScroll) detail += " (Scroll-drag touch equivalent isn't available on this build - only right-click long-press will be added.)";
            if (!hasRightClick) detail += " (Right-click long-press touch equivalent isn't available on this build - only scroll-drag will be added.)";
            return (true, false, detail);
        }
        catch (Exception ex)
        {
            return (false, false, $"Couldn't check: {ex.Message}");
        }
    }

    public static PatchOutcome PatchTouchControls(string dataWinPath)
    {
        try
        {
            string backupPath = dataWinPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(dataWinPath, backupPath);
            }

            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            var stepCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == "gml_Object_oFutaMatingPress_Step_0");
            if (createCode is null || stepCode is null)
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "This doesn't look like a compatible game (missing oFutaMatingPress).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;

            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
            if (createText.Contains("touch_long_press_synthetic"))
            {
                return new PatchOutcome(PatchResult.AlreadyPatched, "Touch controls are already patched in - nothing to do.");
            }
            if (!createText.Contains(TouchCreateMarker))
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "Couldn't find the expected code in the Create event - this game's version may not be compatible.");
            }

            // Scroll-drag (Create_0's menu_scroll_update) and right-click long-press (Step_0) are
            // each best-effort/independent, same philosophy as futaScan/wifeScan/pillMenu below -
            // a fork may have restructured one but not the other (confirmed happening in practice:
            // WB-ModRoom "Release 1" has a scroll-update variant - see MenuScrollOriginalFlipped -
            // but a completely restructured right-click check this patch doesn't recognize at
            // all). Only fail outright if NEITHER piece has anything to patch.
            bool scrollFlipped = !createText.Contains(MenuScrollOriginal) && createText.Contains(MenuScrollOriginalFlipped);
            bool scrollPatched = createText.Contains(MenuScrollOriginal) || createText.Contains(MenuScrollOriginalFlipped);

            string stepText = new DecompileContext(globalContext, stepCode, settings).DecompileToString();
            bool rightClickPatched = stepText.Contains(RightClickMarker1) && stepText.Contains(RightClickMarker2);

            if (!scrollPatched && !rightClickPatched)
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "Couldn't find the expected mouse-wheel menu code or right-click code - this is specific to " +
                    "ModRoom-style right-click/scroll-wheel controls, and may not apply to this game/version.");
            }

            createText = createText.Replace(TouchCreateMarker, TouchCreateMarker + TouchCreatePatch);
            if (scrollPatched)
            {
                createText = scrollFlipped
                    ? createText.Replace(MenuScrollOriginalFlipped, MenuScrollReplacementFlipped)
                    : createText.Replace(MenuScrollOriginal, MenuScrollReplacement);
            }

            // Best-effort, not required: if this exact code isn't found (a different ModRoom
            // version, say), the touch-input fix above still applies fine on its own - this just
            // silently skips the custom-character-discovery fix rather than failing the whole
            // patch over it.
            bool futaScanPatched = createText.Contains(CustomFutaScanOriginal);
            if (futaScanPatched) createText = createText.Replace(CustomFutaScanOriginal, CustomFutaScanReplacement);
            bool wifeScanPatched = createText.Contains(CustomWifeScanOriginal);
            if (wifeScanPatched) createText = createText.Replace(CustomWifeScanOriginal, CustomWifeScanReplacement);
            // Neither exact ModRoom 3.2 loop matched - try the body-preserving rewrite (DeepRoom).
            if (!futaScanPatched && !wifeScanPatched)
            {
                string? futaRewritten = RewriteScanLoop(createText, FutaScanHeader, FutaScanHeaderReplacement);
                string? bothRewritten = futaRewritten is null ? null : RewriteScanLoop(futaRewritten, WifeScanHeader, WifeScanHeaderReplacement);
                if (bothRewritten is not null)
                {
                    createText = bothRewritten;
                    futaScanPatched = wifeScanPatched = true;
                }
            }

            // Best-effort, same style as futaScan/wifeScan above: requires ALL of the pill-menu
            // markers (across both Draw_0 and Step_0) to be present before touching any of them,
            // since applying just some would leave the menu in an inconsistent half-fixed state
            // (e.g. showing all 14 rows but still letting scroll drift away from 0).
            var touchDrawCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == DrawEventName);
            string? drawText = null;
            bool pillMenuPatched = false;
            if (touchDrawCode is not null)
            {
                drawText = new DecompileContext(globalContext, touchDrawCode, settings).DecompileToString();
                pillMenuPatched = drawText.Contains(PillMenuOriginal) && stepText.Contains(PillMenuScrollUpdateMarker);
                if (pillMenuPatched)
                {
                    drawText = drawText.Replace(PillMenuOriginal, PillMenuReplacement);
                }
            }

            // Right-click long-press is only wired up (and its tracking-state prepend added) when
            // the expected pattern was actually found - see scrollPatched/rightClickPatched above.
            if (rightClickPatched)
            {
                stepText = stepText.Replace(RightClickMarker1, RightClickReplacement1);
                stepText = stepText.Replace(RightClickMarker2, RightClickReplacement2);
            }
            if (pillMenuPatched) stepText = stepText.Replace(PillMenuScrollUpdateMarker, PillMenuScrollUpdateReplacement);
            if (rightClickPatched) stepText = TouchStepPrepend + stepText;

            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };
            importGroup.QueueReplace(CreateEventName, createText);
            importGroup.QueueReplace("gml_Object_oFutaMatingPress_Step_0", stepText);
            if (touchDrawCode is not null) importGroup.QueueReplace(DrawEventName, drawText!);
            importGroup.Import();

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            string scrollNote = scrollPatched
                ? " Scroll-drag added for menus."
                : " Scroll-wheel menu code didn't match what was expected, so scroll-drag was skipped.";
            string rightClickNote = rightClickPatched
                ? " Long-press added as a right-click equivalent."
                : " Right-click code didn't match what was expected, so long-press was skipped.";
            string scanNote = (futaScanPatched || wifeScanPatched)
                ? $" Also fixed custom character discovery on Android (futas: {futaScanPatched}, wives: {wifeScanPatched})."
                : " Custom character discovery code didn't match what was expected, so that part was skipped - touch controls were still patched.";
            string pillNote = pillMenuPatched
                ? " Pill menu is now laid out as two columns of 7 (no scrolling needed, nothing can run off-screen)."
                : " Pill menu code didn't match what was expected (or this game has no pill menu), so that part was skipped.";
            return new PatchOutcome(PatchResult.Patched,
                $"Touch controls patched successfully!{scrollNote}{rightClickNote}{scanNote}{pillNote} A backup of the original was saved as:\n{backupPath}");
        }
        catch (Exception ex)
        {
            return new PatchOutcome(PatchResult.Error, $"Something went wrong while patching touch controls: {ex.Message}");
        }
    }

    // ================================================================================
    // CUSTOM CHARACTER ALTS + CLICK-SCROLL BUTTONS - ported from WB-ModRoom "Release 1" into
    // vanilla per explicit user request, after WB-ModRoom .6 itself turned out to be permanently
    // unpatchable for Android (see DECOMPILER UPGRADE history in NOTES.txt - a genuine bug in
    // that fork's own compiled code that survives even a full whole-file recompile through our
    // own toolchain, meaning it's a limitation in the Android runtime binaries, not fixable by
    // any patching tool). WB-ModRoom .6 is built on vanilla's own func_load_custom system
    // (confirmed by decompiling both, not guessed) - NOT ModRoom V3.2's incompatible separate
    // check_custom_futa system - which is what makes this port tractable: extending vanilla's
    // existing function, not bridging two foreign architectures.
    //
    // ALTS: a custom character folder can optionally include numbered variant files
    // (custom_data_1.futa/.spouse, custom_portrait_1.png, etc - WB-ModRoom .6's own exact naming
    // convention, confirmed by decompiling its func_load_custom) - loaded by RIGHT-clicking the
    // existing portrait-swap button (reuses that button's hover zone rather than adding new
    // screen UI - lower risk, no new art needed; left-click there still does its original swap-
    // portrait job unchanged). Missing alt files fall back to whatever the base (non-alt) load
    // already set, matching WB-ModRoom .6's own stated fallback behavior. This is a SEPARATE
    // concept from vanilla's pre-existing single "_alt" sprites (custom_portrait_alt.png etc,
    // the lover/partner couple-display slot) - those are untouched.
    //
    // CLICK-SCROLL: two "<"/">" text buttons flank the custom character portrait row, doing
    // exactly what mouse-wheel already does to custom_menu_pos (the only existing input for this,
    // confirmed by decompiling Draw_0) - purely additive alternate input, zero risk to existing
    // wheel behavior.

    private const string CustomAltsLoadSignatureMarker =
        "function func_load_custom(arg0)\n" +
        "{\n" +
        "    var sprite_origin_x = 80;";
    private const string CustomAltsLoadSignatureReplacement =
        "function func_load_custom(arg0, arg1 = -1)\n" +
        "{\n" +
        "    var alt_suffix = \"\";\n" +
        "    if (arg1 > 0)\n" +
        "    {\n" +
        "        alt_suffix = \"_\" + string(arg1);\n" +
        "    }\n" +
        "    var sprite_origin_x = 80;";

    // One marker/replacement pair per file WB-ModRoom .6 itself applies its alt suffix to
    // (confirmed by decompiling its func_load_custom - NOT the pre-existing separate "_alt"
    // sprites). The two custom_data.* markers include the "ini_open(...)" prefix specifically to
    // stay distinct from the earlier file_exists(...) TYPE-DETECTION check for the same filename
    // (confirmed by direct count: "custom_data.futa"/"custom_data.spouse" each appear exactly
    // twice in vanilla's func_load_custom - once in that type check, which must NOT get an alt
    // suffix, and once in the actual ini_open load, which must) - a bare-literal marker would
    // have incorrectly matched both. The sprite filename markers are safe as exact lines: each is
    // confirmed to appear exactly twice (once in the futa case, once identically in the spouse
    // case - one Replace() correctly covers both) or once (futa-only fields), with no other
    // occurrence anywhere else in the function.
    private static readonly (string Marker, string Replacement)[] CustomAltsFileMarkers =
    {
        ("ini_open(string(arg0) + \"/custom_data.futa\");",
         "ini_open(string(arg0) + \"/custom_data\" + alt_suffix + \".futa\");"),
        ("ini_open(string(arg0) + \"/custom_data.spouse\");",
         "ini_open(string(arg0) + \"/custom_data\" + alt_suffix + \".spouse\");"),
        ("var file_check = string(arg0) + \"/custom_portrait.png\";",
         "var file_check = string(arg0) + \"/custom_portrait\" + alt_suffix + \".png\";"),
        ("file_check = string(arg0) + \"/custom_mating_press.png\";",
         "file_check = string(arg0) + \"/custom_mating_press\" + alt_suffix + \".png\";"),
        ("file_check = string(arg0) + \"/custom_reverse_cowgirl.png\";",
         "file_check = string(arg0) + \"/custom_reverse_cowgirl\" + alt_suffix + \".png\";"),
        ("file_check = string(arg0) + \"/custom_deepthroat.png\";",
         "file_check = string(arg0) + \"/custom_deepthroat\" + alt_suffix + \".png\";"),
        ("file_check = string(arg0) + \"/custom_condom.png\";",
         "file_check = string(arg0) + \"/custom_condom\" + alt_suffix + \".png\";"),
        ("file_check = string(arg0) + \"/custom_condom_broken.png\";",
         "file_check = string(arg0) + \"/custom_condom_broken\" + alt_suffix + \".png\";"),
        ("file_check = string(arg0) + \"/custom_xray.png\";",
         "file_check = string(arg0) + \"/custom_xray\" + alt_suffix + \".png\";"),
        ("file_check = string(arg0) + \"/custom_xray_broken.png\";",
         "file_check = string(arg0) + \"/custom_xray_broken\" + alt_suffix + \".png\";"),
    };

    // Tracks the currently-selected alt (0 = base) per slot, reset to 0 whenever a NEW character
    // is freshly selected from the menu (not when merely re-loading for alt-cycling) - inserted
    // right where the rest of the custom-menu state already gets declared.
    private const string CustomAltsGlobalsMarker = "custom_menu_pos_lerp = 0;";
    private const string CustomAltsGlobalsReplacement =
        "custom_menu_pos_lerp = 0;\ncustom_current_lover_alt = 0;\ncustom_current_partner_alt = 0;";

    // func_set_custom_lover()/func_set_custom_partner() always reload using whatever the current
    // alt slot is (0 on a fresh select, or the cycled value when re-called by the alt-cycle click
    // handler below) - so ONLY the fresh-selection call sites reset the slot back to 0, not these
    // functions themselves (which get called again, deliberately, when cycling).
    private const string CustomAltsLoverCallMarker =
        "func_load_custom(ds_list_find_value(custom_lover_folders, custom_lover_selected));";
    private const string CustomAltsLoverCallReplacement =
        "func_load_custom(ds_list_find_value(custom_lover_folders, custom_lover_selected), custom_current_lover_alt);";
    private const string CustomAltsPartnerCallMarker =
        "func_load_custom(ds_list_find_value(custom_partner_folders, custom_partner_selected));";
    private const string CustomAltsPartnerCallReplacement =
        "func_load_custom(ds_list_find_value(custom_partner_folders, custom_partner_selected), custom_current_partner_alt);";

    // The 4 remaining func_load_custom(...) call sites in Create_0 (the initial folder-scan/type-
    // detection loop, and the three menu-thumbnail pre-load loops for lovers/partners/bedrooms) -
    // NOT modified above, so left calling with only one argument, relying on arg1's new default
    // value (-1). Confirmed by real-device testing (not assumed): the SAME data.win reached the
    // main loop cleanly on an Android emulator but instantly force-closed (silently, no GML error
    // dialog - i.e. a native-level crash, not a caught script error) on a real phone. The default-
    // parameter mechanism itself is the prime suspect (an unusual-enough GML construct that our
    // recompiled bytecode might encode in a way real hardware's runtime handles differently than
    // the emulator's, similar in spirit to the earlier WB-ModRoom .6 GameMaker-version bytecode
    // incompatibility, though NOT the same specific bug). Rather than fully proving the exact
    // mechanism, the safe fix is defensive: make every call site pass exactly 2 arguments, so the
    // default-value code path is never exercised at all, regardless of which theory is right.
    private const string CustomAltsScanCallMarker = "func_load_custom(_file);";
    private const string CustomAltsScanCallReplacement = "func_load_custom(_file, -1);";
    private const string CustomAltsPreloadLoverCallMarker =
        "func_load_custom(ds_list_find_value(custom_lover_folders, i));";
    private const string CustomAltsPreloadLoverCallReplacement =
        "func_load_custom(ds_list_find_value(custom_lover_folders, i), -1);";
    private const string CustomAltsPreloadPartnerCallMarker =
        "func_load_custom(ds_list_find_value(custom_partner_folders, i));";
    private const string CustomAltsPreloadPartnerCallReplacement =
        "func_load_custom(ds_list_find_value(custom_partner_folders, i), -1);";
    private const string CustomAltsPreloadBedroomCallMarker =
        "func_load_custom(ds_list_find_value(custom_bedroom_folders, i));";
    private const string CustomAltsPreloadBedroomCallReplacement =
        "func_load_custom(ds_list_find_value(custom_bedroom_folders, i), -1);";

    private const string CustomAltsLoverSelectMarker =
        "                        custom_lover_selected = i - 1;\n" +
        "                        func_set_custom_lover();";
    private const string CustomAltsLoverSelectReplacement =
        "                        custom_lover_selected = i - 1;\n" +
        "                        custom_current_lover_alt = 0;\n" +
        "                        func_set_custom_lover();";
    private const string CustomAltsPartnerSelectMarker =
        "                        custom_partner_selected = i - 1;\n" +
        "                        func_set_custom_partner();";
    private const string CustomAltsPartnerSelectReplacement =
        "                        custom_partner_selected = i - 1;\n" +
        "                        custom_current_partner_alt = 0;\n" +
        "                        func_set_custom_partner();";

    // Right-click on the existing portrait-swap button's hover zone cycles the alt of whichever
    // character is currently displayed (alt_portrait mirrors the SAME flag the swap button's own
    // left-click already uses to know which one that is) - wraps back to 0 if the next-numbered
    // alt's data file doesn't exist. Left-click's existing swap-portrait behavior is untouched.
    private const string CustomAltsCycleMarker =
        "            if (mouse_check_button_pressed(1))\n" +
        "            {\n" +
        "                alt_portrait = !alt_portrait;\n" +
        "                body_jiggle = 0.025;\n" +
        "                audio_play_sound(sndCloth, 0, 0, 0.6, 0, random_range(0.8, 1.2));\n" +
        "            }\n";
    private const string CustomAltsCycleReplacement =
        CustomAltsCycleMarker +
        "            if (mouse_check_button_pressed(2))\n" +
        "            {\n" +
        "                if (alt_portrait == false && custom_lover_selected > -1)\n" +
        "                {\n" +
        "                    var _alt_folder = ds_list_find_value(custom_lover_folders, custom_lover_selected);\n" +
        "                    custom_current_lover_alt += 1;\n" +
        "                    if (!file_exists(string(_alt_folder) + \"/custom_data_\" + string(custom_current_lover_alt) + \".futa\"))\n" +
        "                    {\n" +
        "                        custom_current_lover_alt = 0;\n" +
        "                    }\n" +
        "                    func_set_custom_lover();\n" +
        "                    body_jiggle = 0.025;\n" +
        "                    audio_play_sound(sndCloth, 0, 0, 0.6, 0, random_range(0.8, 1.2));\n" +
        "                }\n" +
        "                else if (alt_portrait == true && custom_partner_selected > -1)\n" +
        "                {\n" +
        "                    var _alt_folder = ds_list_find_value(custom_partner_folders, custom_partner_selected);\n" +
        "                    custom_current_partner_alt += 1;\n" +
        "                    if (!file_exists(string(_alt_folder) + \"/custom_data_\" + string(custom_current_partner_alt) + \".spouse\"))\n" +
        "                    {\n" +
        "                        custom_current_partner_alt = 0;\n" +
        "                    }\n" +
        "                    func_set_custom_partner();\n" +
        "                    body_jiggle = 0.025;\n" +
        "                    audio_play_sound(sndCloth, 0, 0, 0.6, 0, random_range(0.8, 1.2));\n" +
        "                }\n" +
        "            }\n";

    // Click-scroll: inserted right after the existing mouse-wheel handling for custom_menu_pos,
    // doing the exact same median-clamped adjustment that wheel input already does.
    private const string ClickScrollMarker =
        "        if ((mouse_wheel_up() || mouse_wheel_down()) && abs(custom_menu_pos - custom_menu_pos_lerp) < 0.3)\n" +
        "        {\n" +
        "            custom_menu_pos += (mouse_wheel_down() - mouse_wheel_up());\n" +
        "            custom_menu_pos = median(custom_menu_pos, 0, tab_size - 1);\n" +
        "        }\n";
    private const string ClickScrollReplacement =
        ClickScrollMarker +
        "        if (tab_size > 1)\n" +
        "        {\n" +
        "            var _cs_y = (room_height - 64) + (32 * custom_scale);\n" +
        "            var _cs_left_hover = point_in_rectangle(mouse_x, mouse_y, 20, _cs_y - 20, 60, _cs_y + 20);\n" +
        "            var _cs_right_hover = point_in_rectangle(mouse_x, mouse_y, room_width - 60, _cs_y - 20, room_width - 20, _cs_y + 20);\n" +
        "            draw_set_halign(1);\n" +
        "            draw_text(40, _cs_y, \"<\");\n" +
        "            draw_text(room_width - 40, _cs_y, \">\");\n" +
        "            draw_set_halign(0);\n" +
        "            if (mouse_check_button_pressed(1) && _cs_left_hover == true)\n" +
        "            {\n" +
        "                custom_menu_pos = median(custom_menu_pos - 1, 0, tab_size - 1);\n" +
        "            }\n" +
        "            if (mouse_check_button_pressed(1) && _cs_right_hover == true)\n" +
        "            {\n" +
        "                custom_menu_pos = median(custom_menu_pos + 1, 0, tab_size - 1);\n" +
        "            }\n" +
        "        }\n";

    public static (bool Compatible, bool AlreadyPatched, string Detail) CheckCustomAltsStatus(string dataWinPath)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            var drawCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == DrawEventName);
            if (createCode is null || drawCode is null)
            {
                return (false, false, "Not a compatible game (missing oFutaMatingPress).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
            if (createText.Contains("alt_suffix"))
            {
                return (true, true, "Already patched.");
            }
            if (!createText.Contains(CustomAltsLoadSignatureMarker))
            {
                return (false, false,
                    "Couldn't find the expected func_load_custom code in the Create event - this game's " +
                    "version may not be compatible (this patch is specific to this vanilla install's own " +
                    "func_load_custom, not ModRoom-style check_custom_futa builds).");
            }
            if (!createText.Contains(CustomAltsScanCallMarker) || !createText.Contains(CustomAltsPreloadLoverCallMarker) ||
                !createText.Contains(CustomAltsPreloadPartnerCallMarker) || !createText.Contains(CustomAltsPreloadBedroomCallMarker))
            {
                return (false, false,
                    "Found func_load_custom but not all its expected call sites in the Create event - this " +
                    "game's version may not be compatible.");
            }
            string drawText = new DecompileContext(globalContext, drawCode, settings).DecompileToString();
            if (!drawText.Contains(CustomAltsCycleMarker) || !drawText.Contains(ClickScrollMarker) ||
                !drawText.Contains(CustomAltsLoverSelectMarker) || !drawText.Contains(CustomAltsPartnerSelectMarker))
            {
                return (false, false,
                    "Couldn't find the expected portrait-swap button, menu-scroll, or character-select code " +
                    "in the Draw event - this game's version may not be compatible.");
            }
            return (true, false, "Compatible, not yet patched.");
        }
        catch (Exception ex)
        {
            return (false, false, $"Couldn't check: {ex.Message}");
        }
    }

    public static PatchOutcome PatchCustomAlts(string dataWinPath)
    {
        try
        {
            string backupPath = dataWinPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(dataWinPath, backupPath);
            }

            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            var drawCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == DrawEventName);
            if (createCode is null || drawCode is null)
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "This doesn't look like a compatible game (missing oFutaMatingPress).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;

            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
            if (createText.Contains("alt_suffix"))
            {
                return new PatchOutcome(PatchResult.AlreadyPatched, "Custom alts / click-scroll are already patched in - nothing to do.");
            }
            if (!createText.Contains(CustomAltsLoadSignatureMarker))
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "Couldn't find the expected func_load_custom code in the Create event - this game's " +
                    "version may not be compatible.");
            }
            if (!createText.Contains(CustomAltsGlobalsMarker) || !createText.Contains(CustomAltsLoverCallMarker) ||
                !createText.Contains(CustomAltsPartnerCallMarker) || !createText.Contains(CustomAltsScanCallMarker) ||
                !createText.Contains(CustomAltsPreloadLoverCallMarker) || !createText.Contains(CustomAltsPreloadPartnerCallMarker) ||
                !createText.Contains(CustomAltsPreloadBedroomCallMarker))
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "Found func_load_custom but not all the other expected markers in the Create event - " +
                    "this game's version may not be compatible.");
            }

            string drawText = new DecompileContext(globalContext, drawCode, settings).DecompileToString();
            if (!drawText.Contains(CustomAltsCycleMarker) || !drawText.Contains(ClickScrollMarker) ||
                !drawText.Contains(CustomAltsLoverSelectMarker) || !drawText.Contains(CustomAltsPartnerSelectMarker))
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "Couldn't find the expected portrait-swap button, menu-scroll, or character-select code " +
                    "in the Draw event - this game's version may not be compatible.");
            }

            createText = createText.Replace(CustomAltsLoadSignatureMarker, CustomAltsLoadSignatureReplacement);
            foreach (var (marker, replacement) in CustomAltsFileMarkers)
            {
                createText = createText.Replace(marker, replacement);
            }
            createText = createText.Replace(CustomAltsGlobalsMarker, CustomAltsGlobalsReplacement);
            createText = createText.Replace(CustomAltsLoverCallMarker, CustomAltsLoverCallReplacement);
            createText = createText.Replace(CustomAltsPartnerCallMarker, CustomAltsPartnerCallReplacement);
            createText = createText.Replace(CustomAltsScanCallMarker, CustomAltsScanCallReplacement);
            createText = createText.Replace(CustomAltsPreloadLoverCallMarker, CustomAltsPreloadLoverCallReplacement);
            createText = createText.Replace(CustomAltsPreloadPartnerCallMarker, CustomAltsPreloadPartnerCallReplacement);
            createText = createText.Replace(CustomAltsPreloadBedroomCallMarker, CustomAltsPreloadBedroomCallReplacement);

            drawText = drawText.Replace(CustomAltsCycleMarker, CustomAltsCycleReplacement);
            drawText = drawText.Replace(ClickScrollMarker, ClickScrollReplacement);
            drawText = drawText.Replace(CustomAltsLoverSelectMarker, CustomAltsLoverSelectReplacement);
            drawText = drawText.Replace(CustomAltsPartnerSelectMarker, CustomAltsPartnerSelectReplacement);

            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };
            importGroup.QueueReplace(CreateEventName, createText);
            importGroup.QueueReplace(DrawEventName, drawText);
            importGroup.Import();

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            return new PatchOutcome(PatchResult.Patched,
                "Custom alts + click-scroll buttons patched successfully! Right-click the portrait-swap " +
                "button to cycle a character's numbered alt looks (custom_data_1.futa/.spouse, etc.); " +
                $"click-scroll arrows added to the custom character list. A backup of the original was saved as:\n{backupPath}");
        }
        catch (Exception ex)
        {
            return new PatchOutcome(PatchResult.Error, $"Something went wrong while patching custom alts: {ex.Message}");
        }
    }

    // ================================================================================
    // ANDROID CUSTOM CHARACTER DISCOVERY (vanilla) - a SEPARATE, independent patch from
    // --custom-alts above (not folded into it, so it can be applied to an install that already
    // has --custom-alts patched, without needing to re-detect/re-apply that first).
    //
    // Real gap found 2026-07-29: vanilla's OWN custom-character discovery code is wrapped in
    // "if (mobile == false)" - on Android it does not run AT ALL, regardless of anything bundled
    // into the APK (confirmed by decompiling and reading the actual condition, not assumed). This
    // isn't something we broke - the base game apparently never shipped Android support for
    // custom characters, likely for the same reason ModRoom's own enumeration-based discovery
    // doesn't work on Android either (GameMaker's file_find_first/directory_exists don't work
    // against files bundled inside an APK - the exact same limitation --touch-controls's
    // custom-character-discovery fix already works around for ModRoom's check_custom_futa system,
    // via a manifest instead of enumeration). This patch does the equivalent for VANILLA's own
    // func_load_custom-based system: reads a manifest (assets/custom/_manifest.txt, written by
    // ApkPatcher's --include-mods when bundling a "custom" folder - see Program.cs) and, for each
    // listed name, runs the EXACT SAME per-folder logic the existing PC-only discovery loop
    // already uses (func_load_custom + switch on custom_load_type) - reusing that logic verbatim,
    // only the SOURCE of the folder-name list changes from enumeration to a manifest.
    private const string AndroidCustomDiscoveryMarker =
        "    if (ds_list_size(custom_lover_folders) > 0 || ds_list_size(custom_partner_folders) > 0 || ds_list_size(custom_bedroom_folders) > 0)\n" +
        "    {\n" +
        "        custom_sprite_loaded = true;\n" +
        "        tutorial = false;\n" +
        "    }\n" +
        "}\n";
    // NOTE: this deliberately does NOT gate on the game's own "mobile" variable, and deliberately
    // does NOT chain onto the existing block as "else if" either - decompiling confirmed:
    // (1) "mobile" is hardcoded to `mobile = false;` at the top of this same event and never
    //     reassigned anywhere else in the entire data file (checked every top-level code entry),
    //     so `mobile == true` can never be reached on any platform, PC or Android.
    // (2) BECAUSE of (1), the existing PC block's own condition (`mobile == false`) is ALWAYS
    //     true, on every platform - which means an "else if" attached to it could NEVER run
    //     either, no matter what condition it checked, since the first branch of an if/else-if
    //     always wins once true. A standalone "if" below is required so this really runs
    //     independently, using the real engine-provided os_type constant to detect the platform.
    private const string AndroidCustomDiscoveryReplacement =
        AndroidCustomDiscoveryMarker +
        "if (os_type == os_android || os_type == os_ios)\n" +
        "{\n" +
        // working_directory already ends with a trailing slash on Android ("assets/") but not on
        // PC ("C:\...\game_folder") - appending "/custom/..." unconditionally would produce a
        // double slash ("assets//custom/...") on Android, which file_exists() fails to resolve.
        // Stripping any trailing slash first makes this work regardless of platform convention.
        "    var _acd_wd = working_directory;\n" +
        "    if (string_char_at(_acd_wd, string_length(_acd_wd)) == \"/\")\n" +
        "    {\n" +
        "        _acd_wd = string_copy(_acd_wd, 1, string_length(_acd_wd) - 1);\n" +
        "    }\n" +
        "    var _acd_manifest_path = _acd_wd + \"/custom/_manifest.txt\";\n" +
        "    if (file_exists(_acd_manifest_path))\n" +
        "    {\n" +
        "        var _acd_mf = file_text_open_read(_acd_manifest_path);\n" +
        "        while (!file_text_eof(_acd_mf))\n" +
        "        {\n" +
        "            var _acd_line = string_replace_all(string_replace_all(file_text_readln(_acd_mf), \"\\r\", \"\"), \"\\n\", \"\");\n" +
        "            if (_acd_line != \"\")\n" +
        "            {\n" +
        "                var _acd_file = _acd_wd + \"/custom/\" + _acd_line;\n" +
        "                func_load_custom(_acd_file, -1);\n" +
        "                switch (custom_load_type)\n" +
        "                {\n" +
        "                    case UnknownEnum.Value_1:\n" +
        "                        ds_list_add(custom_lover_folders, _acd_file);\n" +
        "                        break;\n" +
        "                    case UnknownEnum.Value_2:\n" +
        "                        ds_list_add(custom_partner_folders, _acd_file);\n" +
        "                        break;\n" +
        "                    case UnknownEnum.Value_3:\n" +
        "                        ds_list_add(custom_bedroom_folders, _acd_file);\n" +
        "                        break;\n" +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "        file_text_close(_acd_mf);\n" +
        "        if (ds_list_size(custom_lover_folders) > 0 || ds_list_size(custom_partner_folders) > 0 || ds_list_size(custom_bedroom_folders) > 0)\n" +
        "        {\n" +
        "            custom_sprite_loaded = true;\n" +
        "            tutorial = false;\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    public static (bool Compatible, bool AlreadyPatched, string Detail) CheckAndroidCustomDiscoveryStatus(string dataWinPath)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            if (createCode is null)
            {
                return (false, false, "Not a compatible game (missing oFutaMatingPress).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
            if (createText.Contains("_acd_manifest_path"))
            {
                return (true, true, "Already patched.");
            }
            if (!createText.Contains(AndroidCustomDiscoveryMarker))
            {
                return (false, false,
                    "Couldn't find the expected custom-character discovery code in the Create event - this " +
                    "is specific to this vanilla install's own func_load_custom-based discovery, not " +
                    "ModRoom-style builds (which already get Android discovery support via --touch-controls).");
            }
            return (true, false, "Compatible, not yet patched.");
        }
        catch (Exception ex)
        {
            return (false, false, $"Couldn't check: {ex.Message}");
        }
    }

    public static PatchOutcome PatchAndroidCustomDiscovery(string dataWinPath)
    {
        try
        {
            string backupPath = dataWinPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(dataWinPath, backupPath);
            }

            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            if (createCode is null)
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "This doesn't look like a compatible game (missing oFutaMatingPress).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
            if (createText.Contains("_acd_manifest_path"))
            {
                return new PatchOutcome(PatchResult.AlreadyPatched, "Android custom character discovery is already patched in - nothing to do.");
            }
            if (!createText.Contains(AndroidCustomDiscoveryMarker))
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "Couldn't find the expected custom-character discovery code in the Create event - this " +
                    "game's version may not be compatible.");
            }

            createText = createText.Replace(AndroidCustomDiscoveryMarker, AndroidCustomDiscoveryReplacement);

            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };
            importGroup.QueueReplace(CreateEventName, createText);
            importGroup.Import();

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            return new PatchOutcome(PatchResult.Patched,
                "Android custom character discovery patched successfully! On Android, characters bundled " +
                "into assets/custom/ (via ApkPatcher --include-mods, with a manifest) will now actually be " +
                $"found and loaded - previously vanilla's own discovery code never ran on mobile at all. A backup of the original was saved as:\n{backupPath}");
        }
        catch (Exception ex)
        {
            return new PatchOutcome(PatchResult.Error, $"Something went wrong while patching Android custom discovery: {ex.Message}");
        }
    }

    // ================================================================================
    // PHONE MODS FOLDER + IN-GAME DOWNLOADER - lets a phone add custom characters AFTER the APK
    // was built, with no PC and no rebuild. Independent of --android-custom-discovery and of
    // --touch-controls' manifest discovery (those find characters bundled INSIDE the APK).
    //
    // Everything inside an APK's assets/ is read-only and can't be enumerated, but the game's
    // save area (game_save_id - on Android the app's own private files folder) is an ordinary
    // writable filesystem where file_find_first DOES work. Three pieces:
    //
    //   1. Create: scans <save area>/phone_mods/ (a few folder levels deep, so a zip with or
    //      without a wrapping folder, or with several packs, all work) and registers every
    //      pack folder it finds.
    //   2. Create: unpacks an optional starter zip. If the APK carries
    //      assets/phone_mods_seed.zip (ApkPatcher --phone-seed) it is copied out and unzipped
    //      once, on first launch. zip_unzip only works on a real file, hence the copy via
    //      buffer_load/buffer_save.
    //   3. Draw + two new async events: an "ADD MOD" button on the title screen (Android/iOS
    //      only) asks for a link, downloads it with http_get_file, unzips it into
    //      phone_mods/dl_<time>/ and registers what it finds straight away, no restart.
    //      Links must be https:// - the APK has no cleartext-traffic permission. Only .zip:
    //      GameMaker has no rar/7z reader.
    //
    // The players can't reach this folder with a file manager (it is private app storage), so
    // the downloader is the only way in besides the starter zip.
    //
    // TWO MOD SYSTEMS, two ways of registering a pack:
    //   - Vanilla (func_load_custom): call func_load_custom and file the folder by
    //     custom_load_type. Anchors on the same end-of-PC-discovery-block text as
    //     --android-custom-discovery; composes with it in either order.
    //   - ModRoom-style (ModRoom 3.2, DeepRoom): every fork registers a character differently
    //     (DeepRoom also fills selector name/portrait lists and has a "1.4 legacy spouse"
    //     branch), so nothing is hard-coded: the patch lifts the BODY of the game's own
    //     custom_futas and custom_wives discovery loops out of the decompiled Create event and
    //     replays it for each pack found in phone_mods, with the path swapped in. A pack is a
    //     lover if it has custom_data.futa, a partner if it has custom_data.wife/.spouse - so
    //     players don't have to sort mods into type folders. Must be applied BEFORE
    //     --touch-controls, which rewrites those loops.
    //   - ModRoom-style, everything that is not a character: backgrounds, dialogue packs and
    //     texture packs are found by the game's OWN startup scans (oBackground,
    //     oDialogueManager, func_list_texture_packs), which enumerate custom_bedrooms/,
    //     dialogue_packs/ and texture_packs/ by relative path - and relative paths resolve to
    //     the save area first. So ModRoomRoute() just copies those out of the unzipped
    //     download into <save area>/<that folder>/ and they show up on the next launch. A
    //     link straight to a .json is saved as a dialogue pack.
    //   - ModRoom-style, voice packs: not a pack type in this game - they are moan_fast /
    //     moan_slow / orgasm folders that func_load_custom_futa reads from inside ONE lover's
    //     own folder. ADD MOD keeps each voice under <save area>/phone_voices/<name>/, and a
    //     VOICES button on the title screen opens a picker (which voice, then which lover)
    //     that copies those folders into the lover's folder. For a lover bundled in the APK
    //     the copy goes to the same relative path in the save area, which the game checks
    //     first. Lovers only: the wife loaders never read moan folders.
    private const string PhoneModsDrawEventAnchor =
        "    draw_text_color(80, room_height - 32, func_set_lang(7, \"CREDITS\"), 16777215, 16777215, 16777215, 16777215, title_scale * title_alpha);\n";
    private const string PhoneModsDialogEventName = "gml_Object_oFutaMatingPress_Other_63";
    private const string PhoneModsHttpEventName = "gml_Object_oFutaMatingPress_Other_62";
    private const uint PhoneModsAsyncDialogSubtype = 63;
    private const uint PhoneModsAsyncHttpSubtype = 62;
    private const string PhoneModsOnMobile = "(os_type == os_android || os_type == os_ios)";
    private const string PhoneModsButtonRect = "room_width - 150, room_height - 46, room_width - 10, room_height - 18";

    // GML that leaves the save area path, without its trailing slash, in _pm_save.
    private const string PhoneModsSavePath =
        "var _pm_save = game_save_id;\n" +
        "if (string_char_at(_pm_save, string_length(_pm_save)) == \"/\")\n" +
        "{\n" +
        "    _pm_save = string_copy(_pm_save, 1, string_length(_pm_save) - 1);\n" +
        "}\n";

    private static string Indent(string gml) =>
        string.Concat(gml.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => "    " + line + "\n"));

    private static string Indent(string gml, int levels)
    {
        for (int i = 0; i < levels; i++) gml = Indent(gml);
        return gml;
    }

    // GML that walks the folder named by the variable `rootVar` breadth-first and registers every
    // pack it finds, leaving the number registered in _pm_added. `isPack` is a GML condition on
    // _pm_dir; `register` is GML that registers the pack at _pm_dir (folder name in _pm_name) and
    // bumps _pm_added. Written as a loop over a queue rather than a recursive function for two
    // reasons: the games' own loading code runs file_find_first itself (for moans), which would
    // clobber an enumeration left open across the call, and this way no new script function has
    // to be created in the data file.
    private static string PhoneModsScan(string rootVar, string isPack, string register) =>
        "var _pm_added = 0;\n" +
        "var _pm_queue = [" + rootVar + "];\n" +
        "var _pm_names = [\"mod\"];\n" +
        "var _pm_depths = [0];\n" +
        "var _pm_qi = 0;\n" +
        "while (_pm_qi < array_length(_pm_queue))\n" +
        "{\n" +
        "    var _pm_dir = _pm_queue[_pm_qi];\n" +
        "    var _pm_name = _pm_names[_pm_qi];\n" +
        "    var _pm_depth = _pm_depths[_pm_qi];\n" +
        "    _pm_qi++;\n" +
        "    if (" + isPack + ")\n" +
        "    {\n" +
        Indent(register, 2) +
        "    }\n" +
        "    else if (_pm_depth < 4)\n" +
        "    {\n" +
        "        var _pm_find = file_find_first(_pm_dir + \"/*\", 16);\n" +
        "        while (_pm_find != \"\")\n" +
        "        {\n" +
        "            if (_pm_find != \".\" && _pm_find != \"..\")\n" +
        "            {\n" +
        "                array_push(_pm_queue, _pm_dir + \"/\" + _pm_find);\n" +
        "                array_push(_pm_names, _pm_find);\n" +
        "                array_push(_pm_depths, _pm_depth + 1);\n" +
        "            }\n" +
        "            _pm_find = file_find_next();\n" +
        "        }\n" +
        "        file_find_close();\n" +
        "    }\n" +
        "}\n";

    // ---- vanilla: func_load_custom. The 1/2/3 are its custom_load_type values. ----
    private const string VanillaIsPack =
        "file_exists(_pm_dir + \"/custom_data.futa\") || file_exists(_pm_dir + \"/custom_data.spouse\") || file_exists(_pm_dir + \"/custom_data.bedroom\")";
    private const string VanillaRegister =
        "func_load_custom(_pm_dir, -1);\n" +
        "if (custom_load_type == 1)\n" +
        "{\n" +
        "    ds_list_add(custom_lover_folders, _pm_dir);\n" +
        "    _pm_added++;\n" +
        "}\n" +
        "else if (custom_load_type == 2)\n" +
        "{\n" +
        "    ds_list_add(custom_partner_folders, _pm_dir);\n" +
        "    _pm_added++;\n" +
        "}\n" +
        "else if (custom_load_type == 3)\n" +
        "{\n" +
        "    ds_list_add(custom_bedroom_folders, _pm_dir);\n" +
        "    _pm_added++;\n" +
        "}\n";
    private const string VanillaAfterScan =
        "if (_pm_added > 0)\n" +
        "{\n" +
        "    custom_sprite_loaded = true;\n" +
        "    tutorial = false;\n" +
        "}\n";

    // ---- ModRoom-style: replay the game's own discovery loop bodies. ----
    private const string ModRoomIsPack =
        "file_exists(_pm_dir + \"/custom_data.futa\") || file_exists(_pm_dir + \"/custom_data.wife\") || file_exists(_pm_dir + \"/custom_data.spouse\")";
    private const string FutaLoopOpen =
        "var _file = file_find_first(working_directory + \"/custom_futas/*\", 16);\n" +
        "while (_file != \"\")\n" +
        "{\n";
    private const string WifeLoopOpen =
        "_file = file_find_first(working_directory + \"/custom_wives\" + \"/*\", 16);\n" +
        "while (_file != \"\")\n" +
        "{\n";
    private const string PhoneModsLoopFooter = "    _file = file_find_next();\n}\nfile_find_close();";

    // The text between a discovery loop's opening and its file_find_next(), with the path the
    // loop builds for the current folder replaced by _pm_path. The directory_exists() test is
    // dropped: the scanner only hands over folders it already knows are packs.
    private static (string? Inner, int EndIndex) ExtractLoopBody(string createText, string loopOpen, string folder)
    {
        int start = createText.IndexOf(loopOpen, StringComparison.Ordinal);
        if (start < 0) return (null, -1);
        int bodyStart = start + loopOpen.Length;
        int footer = createText.IndexOf(PhoneModsLoopFooter, bodyStart, StringComparison.Ordinal);
        if (footer < 0) return (null, -1);
        string inner = createText[bodyStart..footer]
            .Replace("working_directory + \"/" + folder + "/\" + _file", "_pm_path")
            .Replace("directory_exists(_pm_path)", "true")
            .Replace("directory_exists(_full_path)", "true");
        return (inner, footer + PhoneModsLoopFooter.Length);
    }

    private static string ModRoomRegister(string futaBody, string wifeBody) =>
        "var _file = _pm_name;\n" +
        "var _pm_path = _pm_dir;\n" +
        "if (file_exists(_pm_dir + \"/custom_data.futa\"))\n" +
        "{\n" +
        "    var _pm_before = ds_list_size(custom_futas_folder);\n" +
        futaBody +
        "    if (ds_list_size(custom_futas_folder) > _pm_before)\n" +
        "    {\n" +
        "        _pm_added++;\n" +
        "    }\n" +
        "}\n" +
        "else\n" +
        "{\n" +
        "    var _pm_before_w = ds_list_size(custom_wives_folder);\n" +
        wifeBody +
        "    if (ds_list_size(custom_wives_folder) > _pm_before_w)\n" +
        "    {\n" +
        "        _pm_added++;\n" +
        "    }\n" +
        "}\n";

    // GML that copies the folder tree at `src` to `dst` (GML expressions). GameMaker has no
    // folder move or copy, only file_copy.
    private static string PhoneModsCopyTree(string src, string dst) =>
        "var _pm_csrc = [" + src + "];\n" +
        "var _pm_cdst = [" + dst + "];\n" +
        "var _pm_ci = 0;\n" +
        "while (_pm_ci < array_length(_pm_csrc))\n" +
        "{\n" +
        "    var _pm_cs = _pm_csrc[_pm_ci];\n" +
        "    var _pm_cd = _pm_cdst[_pm_ci];\n" +
        "    _pm_ci++;\n" +
        "    if (!directory_exists(_pm_cd))\n" +
        "    {\n" +
        "        directory_create(_pm_cd);\n" +
        "    }\n" +
        "    var _pm_cn = [];\n" +
        "    var _pm_cf = file_find_first(_pm_cs + \"/*\", 16);\n" +
        "    while (_pm_cf != \"\")\n" +
        "    {\n" +
        "        if (_pm_cf != \".\" && _pm_cf != \"..\")\n" +
        "        {\n" +
        "            array_push(_pm_cn, _pm_cf);\n" +
        "        }\n" +
        "        _pm_cf = file_find_next();\n" +
        "    }\n" +
        "    file_find_close();\n" +
        "    for (var _pm_cx = 0; _pm_cx < array_length(_pm_cn); _pm_cx++)\n" +
        "    {\n" +
        "        if (directory_exists(_pm_cs + \"/\" + _pm_cn[_pm_cx]))\n" +
        "        {\n" +
        "            array_push(_pm_csrc, _pm_cs + \"/\" + _pm_cn[_pm_cx]);\n" +
        "            array_push(_pm_cdst, _pm_cd + \"/\" + _pm_cn[_pm_cx]);\n" +
        "        }\n" +
        "        else\n" +
        "        {\n" +
        "            file_copy(_pm_cs + \"/\" + _pm_cn[_pm_cx], _pm_cd + \"/\" + _pm_cn[_pm_cx]);\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    private const string PhoneModsNoRoute = "var _pm_bg = 0;\nvar _pm_dlg = 0;\nvar _pm_tex = 0;\nvar _pm_voice = 0;\n";

    // GML that walks the unzipped folder `rootVar` (its display name in `rootName`) and installs
    // everything that is not a character, leaving counts in _pm_bg / _pm_dlg / _pm_tex. Needs
    // _pm_save. A folder holding custom_data.bedroom is a background; any .json outside a
    // character is a dialogue pack; a folder with three or more .png and no data file is a texture
    // pack - but only when the download has no characters at all, since character zips often
    // carry a few loose preview images.
    private static string ModRoomRoute(string rootVar, string rootName) =>
        PhoneModsNoRoute +
        "var _pm_rchars = 0;\n" +
        "var _pm_tsrc = [];\n" +
        "var _pm_tdst = [];\n" +
        "var _pm_rq = [" + rootVar + "];\n" +
        "var _pm_rn = [" + rootName + "];\n" +
        "var _pm_rdep = [0];\n" +
        "var _pm_ri = 0;\n" +
        "while (_pm_ri < array_length(_pm_rq))\n" +
        "{\n" +
        "    var _pm_rd = _pm_rq[_pm_ri];\n" +
        "    var _pm_rname = _pm_rn[_pm_ri];\n" +
        "    var _pm_rdepth = _pm_rdep[_pm_ri];\n" +
        "    _pm_ri++;\n" +
        "    if (" + ModRoomIsPack.Replace("_pm_dir", "_pm_rd") + ")\n" +
        "    {\n" +
        "        _pm_rchars++;\n" +
        "    }\n" +
        "    else if (file_exists(_pm_rd + \"/custom_data.bedroom\"))\n" +
        "    {\n" +
        Indent(PhoneModsCopyTree("_pm_rd", "_pm_save + \"/custom_bedrooms/\" + _pm_rname"), 2) +
        "        _pm_bg++;\n" +
        "    }\n" +
        "    else\n" +
        "    {\n" +
        "        var _pm_re = [];\n" +
        "        var _pm_rf = file_find_first(_pm_rd + \"/*\", 16);\n" +
        "        while (_pm_rf != \"\")\n" +
        "        {\n" +
        "            if (_pm_rf != \".\" && _pm_rf != \"..\")\n" +
        "            {\n" +
        "                array_push(_pm_re, _pm_rf);\n" +
        "            }\n" +
        "            _pm_rf = file_find_next();\n" +
        "        }\n" +
        "        file_find_close();\n" +
        "        var _pm_png = 0;\n" +
        "        var _pm_subs = [];\n" +
        "        for (var _pm_x = 0; _pm_x < array_length(_pm_re); _pm_x++)\n" +
        "        {\n" +
        "            var _pm_en = _pm_re[_pm_x];\n" +
        "            var _pm_el = string_lower(_pm_en);\n" +
        "            if (directory_exists(_pm_rd + \"/\" + _pm_en))\n" +
        "            {\n" +
        "                array_push(_pm_subs, _pm_en);\n" +
        "            }\n" +
        "            else if (string_copy(_pm_el, string_length(_pm_el) - 4, 5) == \".json\")\n" +
        "            {\n" +
        "                if (!directory_exists(_pm_save + \"/dialogue_packs\"))\n" +
        "                {\n" +
        "                    directory_create(_pm_save + \"/dialogue_packs\");\n" +
        "                }\n" +
        "                file_copy(_pm_rd + \"/\" + _pm_en, _pm_save + \"/dialogue_packs/\" + _pm_en);\n" +
        "                _pm_dlg++;\n" +
        "            }\n" +
        "            else if (string_copy(_pm_el, string_length(_pm_el) - 3, 4) == \".png\")\n" +
        "            {\n" +
        "                _pm_png++;\n" +
        "            }\n" +
        "        }\n" +
        "        var _pm_isvoice = false;\n" +
        "        for (var _pm_z = 0; _pm_z < array_length(_pm_subs); _pm_z++)\n" +
        "        {\n" +
        "            if (string_lower(_pm_subs[_pm_z]) == \"moan_fast\" || string_lower(_pm_subs[_pm_z]) == \"moan_slow\")\n" +
        "            {\n" +
        "                _pm_isvoice = true;\n" +
        "            }\n" +
        "        }\n" +
        "        if (_pm_isvoice)\n" +
        "        {\n" +
        Indent(PhoneModsCopyTree("_pm_rd", "_pm_save + \"/phone_voices/\" + _pm_rname"), 3) +
        "            _pm_voice++;\n" +
        "        }\n" +
        "        else if (_pm_png >= 3)\n" +
        "        {\n" +
        "            array_push(_pm_tsrc, _pm_rd);\n" +
        "            array_push(_pm_tdst, _pm_save + \"/texture_packs/\" + _pm_rname);\n" +
        "        }\n" +
        "        else if (_pm_rdepth < 4)\n" +
        "        {\n" +
        "            for (var _pm_y = 0; _pm_y < array_length(_pm_subs); _pm_y++)\n" +
        "            {\n" +
        "                array_push(_pm_rq, _pm_rd + \"/\" + _pm_subs[_pm_y]);\n" +
        "                array_push(_pm_rn, _pm_subs[_pm_y]);\n" +
        "                array_push(_pm_rdep, _pm_rdepth + 1);\n" +
        "            }\n" +
        "        }\n" +
        "    }\n" +
        "}\n" +
        "if (_pm_rchars == 0)\n" +
        "{\n" +
        "    for (var _pm_t = 0; _pm_t < array_length(_pm_tsrc); _pm_t++)\n" +
        "    {\n" +
        Indent(PhoneModsCopyTree("_pm_tsrc[_pm_t]", "_pm_tdst[_pm_t]"), 2) +
        "        _pm_tex++;\n" +
        "    }\n" +
        "}\n";

    // GML that leaves the file name at the end of pm_url (no query string) in _pm_fn.
    private const string PhoneModsUrlFileName =
        "var _pm_fn = pm_url;\n" +
        "if (string_pos(\"?\", _pm_fn) > 0)\n" +
        "{\n" +
        "    _pm_fn = string_copy(_pm_fn, 1, string_pos(\"?\", _pm_fn) - 1);\n" +
        "}\n" +
        "while (string_pos(\"/\", _pm_fn) > 0)\n" +
        "{\n" +
        "    _pm_fn = string_delete(_pm_fn, 1, string_pos(\"/\", _pm_fn));\n" +
        "}\n" +
        "_pm_fn = string_replace_all(_pm_fn, \"%20\", \"_\");\n";

    // The pm_* instance variables are set on every platform, outside the os check: the Draw
    // code and the async events read them, and reading a never-assigned instance variable is a
    // silent crash on Android (seen twice before in this project).
    private static string PhoneModsCreateBlock(string scan, string afterScan, string routeSeed) =>
        "pm_status = \"\";\n" +
        "pm_url = \"\";\n" +
        "pm_pick = 0;\n" +
        "pm_pick_list = [];\n" +
        "pm_pick_paths = [];\n" +
        "pm_pick_page = 0;\n" +
        "pm_voice_sel = \"\";\n" +
        "pm_voice_name = \"\";\n" +
        "pm_dialog = -1;\n" +
        "pm_http = -1;\n" +
        "if " + PhoneModsOnMobile + "\n" +
        "{\n" +
        Indent(PhoneModsSavePath) +
        // Deliberately NOT named "custom": once <save area>/custom exists, vanilla's own PC
        // discovery loop finds it too (relative paths resolve to the save area first) and every
        // pack gets listed twice - seen for real on the second launch of the first test build.
        "    var _pm_root = _pm_save + \"/phone_mods\";\n" +
        "    if (!directory_exists(_pm_root))\n" +
        "    {\n" +
        "        directory_create(_pm_root);\n" +
        "    }\n" +
        "    var _pm_wd = working_directory;\n" +
        "    if (string_char_at(_pm_wd, string_length(_pm_wd)) == \"/\")\n" +
        "    {\n" +
        "        _pm_wd = string_copy(_pm_wd, 1, string_length(_pm_wd) - 1);\n" +
        "    }\n" +
        "    if (file_exists(_pm_wd + \"/phone_mods_seed.zip\") && !file_exists(_pm_save + \"/phone_mods_seed.done\"))\n" +
        "    {\n" +
        "        var _pm_buf = buffer_load(_pm_wd + \"/phone_mods_seed.zip\");\n" +
        "        if (_pm_buf >= 0)\n" +
        "        {\n" +
        "            buffer_save(_pm_buf, _pm_save + \"/phone_mods_seed_tmp.zip\");\n" +
        "            buffer_delete(_pm_buf);\n" +
        "            zip_unzip(_pm_save + \"/phone_mods_seed_tmp.zip\", _pm_root + \"/seed/\");\n" +
        "            file_delete(_pm_save + \"/phone_mods_seed_tmp.zip\");\n" +
        Indent(routeSeed, 3) +
        "        }\n" +
        "        var _pm_done = file_text_open_write(_pm_save + \"/phone_mods_seed.done\");\n" +
        "        file_text_write_string(_pm_done, \"1\");\n" +
        "        file_text_close(_pm_done);\n" +
        "    }\n" +
        Indent(scan) +
        Indent(afterScan) +
        "}\n";

    // Vanilla: mirrors the game's own CREDITS button (left side) on the right side of the title
    // screen. Both sit outside the x range the "click to begin" hitbox covers (128 ..
    // room_width - 128), so tapping this never also starts the game.
    private const string PhoneModsDrawBlock =
        "    if " + PhoneModsOnMobile + "\n" +
        "    {\n" +
        "        var _pm_hover = point_in_rectangle(mouse_x, mouse_y, room_width - 124, room_height - 48, room_width - 36, room_height - 16);\n" +
        "        draw_sprite_ext(sButtonBack, 0, room_width - 80, room_height - 32, 8, 2, 0, 16777215, (0.5 + (0.5 * _pm_hover)) * (title_scale * title_alpha));\n" +
        "        draw_text_color(room_width - 80, room_height - 32, \"ADD MOD\", 16777215, 16777215, 16777215, 16777215, title_scale * title_alpha);\n" +
        "        if (pm_status != \"\")\n" +
        "        {\n" +
        "            draw_set_halign(2);\n" +
        "            draw_set_valign(2);\n" +
        // Upper-cased because the game's fonts have no lowercase glyphs.
        "            draw_sprite_ext(sButtonBack, 0, room_width - 8 - (string_width_ext(string_upper(pm_status), 14, 220) / 2), room_height - 56 - (string_height_ext(string_upper(pm_status), 14, 220) / 2), (string_width_ext(string_upper(pm_status), 14, 220) / 14) + 1, (string_height_ext(string_upper(pm_status), 14, 220) / 14) + 1, 0, 16777215, 0.5 * title_scale * title_alpha);\n" +
        "            draw_text_ext_color(room_width - 8, room_height - 56, string_upper(pm_status), 14, 220, 16777215, 16777215, 16777215, 16777215, title_scale * title_alpha);\n" +
        "            draw_set_halign(1);\n" +
        "            draw_set_valign(1);\n" +
        "        }\n" +
        "        if (title == true && _pm_hover && mouse_check_button_pressed(1) && pm_dialog == -1 && pm_http == -1)\n" +
        "        {\n" +
        "            pm_dialog = get_string_async(\"Paste the link to a mod .zip (it must start with https://)\", \"\");\n" +
        "        }\n" +
        "    }\n";

    // ModRoom-style forks have no CREDITS button to sit beside and their "click to begin" can
    // cover the whole screen (DeepRoom's does), so the tap is taken at the very top of the Draw
    // event and swallowed with mouse_clear, and the button is drawn at the very end with plain
    // primitives instead of a sprite the fork might not have.
    private const string PhoneModsDrawTopGeneric =
        "if (" + PhoneModsOnMobile + " && title == true && pm_dialog == -1 && pm_http == -1 && mouse_check_button_pressed(1) && point_in_rectangle(mouse_x, mouse_y, " + PhoneModsButtonRect + "))\n" +
        "{\n" +
        "    pm_dialog = get_string_async(\"Paste the link to a mod .zip (it must start with https://)\", \"\");\n" +
        "    mouse_clear(1);\n" +
        "}\n";
    private const string PhoneModsDrawBottomGeneric =
        "\nif (" + PhoneModsOnMobile + " && title == true)\n" +
        "{\n" +
        "    var _pm_ha = draw_get_halign();\n" +
        "    var _pm_va = draw_get_valign();\n" +
        "    var _pm_hover = point_in_rectangle(mouse_x, mouse_y, " + PhoneModsButtonRect + ");\n" +
        "    draw_set_alpha(0.45 + (0.3 * _pm_hover));\n" +
        "    draw_rectangle_color(" + PhoneModsButtonRect + ", 0, 0, 0, 0, false);\n" +
        "    draw_set_alpha(1);\n" +
        "    draw_set_halign(1);\n" +
        "    draw_set_valign(1);\n" +
        "    draw_text_color(room_width - 80, room_height - 32, \"ADD MOD\", 16777215, 16777215, 16777215, 16777215, 1);\n" +
        "    if (pm_status != \"\")\n" +
        "    {\n" +
        "        draw_set_halign(2);\n" +
        "        draw_set_valign(2);\n" +
        "        var _pm_sw = string_width_ext(string_upper(pm_status), 14, 260);\n" +
        "        var _pm_sh = string_height_ext(string_upper(pm_status), 14, 260);\n" +
        "        draw_set_alpha(0.75);\n" +
        "        draw_rectangle_color(room_width - 16 - _pm_sw, room_height - 56 - _pm_sh, room_width - 4, room_height - 48, 0, 0, 0, 0, false);\n" +
        "        draw_set_alpha(1);\n" +
        "        draw_text_ext_color(room_width - 10, room_height - 52, string_upper(pm_status), 14, 260, 16777215, 16777215, 16777215, 16777215, 1);\n" +
        "    }\n" +
        "    draw_set_halign(_pm_ha);\n" +
        "    draw_set_valign(_pm_va);\n" +
        "}\n";

    // ---- voice picker (ModRoom-style). pm_pick: 0 closed, 1 choosing a voice, 2 choosing a lover.
    private const string PhoneModsVoicesRect = "10, room_height - 46, 150, room_height - 18";

    // Shared by the tap handler and the drawing code so the rows line up exactly.
    private const string PhoneModsPickLayout =
        "var _pk_rows = max(3, floor((room_height - 84) / 24));\n" +
        "var _pk_x1 = (room_width / 2) - 170;\n" +
        "var _pk_x2 = (room_width / 2) + 170;\n" +
        "var _pk_y0 = 40;\n" +
        "var _pk_by = room_height - 34;\n" +
        "var _pk_first = pm_pick_page * _pk_rows;\n";

    // Prepended to the Draw event, ahead of the game's own click code. While the picker is open
    // every tap is swallowed, so nothing behind it reacts.
    private static readonly string PhoneModsPickerTop =
        "if (" + PhoneModsOnMobile + " && title == true && pm_dialog == -1 && pm_http == -1)\n" +
        "{\n" +
        Indent(PhoneModsPickLayout) +
        "    if (pm_pick > 0)\n" +
        "    {\n" +
        "        if (mouse_check_button_pressed(1))\n" +
        "        {\n" +
        "            var _pk_hit = -1;\n" +
        "            for (var _pk_i = 0; _pk_i < _pk_rows; _pk_i++)\n" +
        "            {\n" +
        "                if (point_in_rectangle(mouse_x, mouse_y, _pk_x1, _pk_y0 + (_pk_i * 24), _pk_x2, _pk_y0 + (_pk_i * 24) + 21))\n" +
        "                {\n" +
        "                    _pk_hit = _pk_first + _pk_i;\n" +
        "                }\n" +
        "            }\n" +
        "            if (_pk_hit >= 0 && _pk_hit < array_length(pm_pick_list))\n" +
        "            {\n" +
        "                if (pm_pick == 1)\n" +
        "                {\n" +
        "                    pm_voice_sel = pm_pick_paths[_pk_hit];\n" +
        "                    pm_voice_name = pm_pick_list[_pk_hit];\n" +
        "                    pm_pick_list = [];\n" +
        "                    pm_pick_paths = [];\n" +
        "                    for (var _pk_l = 0; _pk_l < ds_list_size(custom_futas_folder); _pk_l++)\n" +
        "                    {\n" +
        "                        var _pk_lp = ds_list_find_value(custom_futas_folder, _pk_l);\n" +
        "                        var _pk_ln = _pk_lp;\n" +
        "                        while (string_pos(\"/\", _pk_ln) > 0)\n" +
        "                        {\n" +
        "                            _pk_ln = string_delete(_pk_ln, 1, string_pos(\"/\", _pk_ln));\n" +
        "                        }\n" +
        "                        array_push(pm_pick_list, _pk_ln);\n" +
        "                        array_push(pm_pick_paths, _pk_lp);\n" +
        "                    }\n" +
        "                    pm_pick_page = 0;\n" +
        "                    pm_pick = 2;\n" +
        "                    if (array_length(pm_pick_list) == 0)\n" +
        "                    {\n" +
        "                        pm_pick = 0;\n" +
        "                        pm_status = \"There are no custom lovers to give a voice to.\";\n" +
        "                    }\n" +
        "                }\n" +
        "                else\n" +
        "                {\n" +
        Indent(PhoneModsSavePath, 5) +
        // A lover bundled in the APK is read-only; its mirror path in the save area is not, and
        // the game looks there first.
        "                    var _pk_dst = pm_pick_paths[_pk_hit];\n" +
        "                    if (string_pos(_pm_save, _pk_dst) != 1)\n" +
        "                    {\n" +
        "                        var _pk_cut = string_pos(\"custom_futas/\", _pk_dst);\n" +
        "                        if (_pk_cut > 0)\n" +
        "                        {\n" +
        "                            _pk_dst = _pm_save + \"/\" + string_copy(_pk_dst, _pk_cut, string_length(_pk_dst));\n" +
        "                        }\n" +
        "                    }\n" +
        // Clear the previous voice first so two packs don't end up mixed together.
        "                    var _pk_kinds = [\"moan_fast\", \"moan_slow\", \"orgasm\"];\n" +
        "                    for (var _pk_k = 0; _pk_k < 3; _pk_k++)\n" +
        "                    {\n" +
        "                        var _pk_old = [];\n" +
        "                        var _pk_of = file_find_first(_pk_dst + \"/\" + _pk_kinds[_pk_k] + \"/*.ogg\", 0);\n" +
        "                        while (_pk_of != \"\")\n" +
        "                        {\n" +
        "                            array_push(_pk_old, _pk_of);\n" +
        "                            _pk_of = file_find_next();\n" +
        "                        }\n" +
        "                        file_find_close();\n" +
        "                        for (var _pk_o = 0; _pk_o < array_length(_pk_old); _pk_o++)\n" +
        "                        {\n" +
        "                            file_delete(_pk_dst + \"/\" + _pk_kinds[_pk_k] + \"/\" + _pk_old[_pk_o]);\n" +
        "                        }\n" +
        "                    }\n" +
        Indent(PhoneModsCopyTree("pm_voice_sel", "_pk_dst"), 5) +
        "                    pm_status = \"Voice \" + pm_voice_name + \" given to \" + pm_pick_list[_pk_hit] + \". Choose that lover again to hear it.\";\n" +
        "                    pm_pick = 0;\n" +
        "                }\n" +
        "            }\n" +
        "            else if (point_in_rectangle(mouse_x, mouse_y, _pk_x1, _pk_by, _pk_x1 + 90, _pk_by + 22))\n" +
        "            {\n" +
        "                if (pm_pick_page > 0)\n" +
        "                {\n" +
        "                    pm_pick_page--;\n" +
        "                }\n" +
        "            }\n" +
        "            else if (point_in_rectangle(mouse_x, mouse_y, _pk_x2 - 90, _pk_by, _pk_x2, _pk_by + 22))\n" +
        "            {\n" +
        "                if ((_pk_first + _pk_rows) < array_length(pm_pick_list))\n" +
        "                {\n" +
        "                    pm_pick_page++;\n" +
        "                }\n" +
        "            }\n" +
        "            else if (point_in_rectangle(mouse_x, mouse_y, (room_width / 2) - 50, _pk_by, (room_width / 2) + 50, _pk_by + 22))\n" +
        "            {\n" +
        "                pm_pick = 0;\n" +
        "            }\n" +
        "            mouse_clear(1);\n" +
        "        }\n" +
        "    }\n" +
        "    else if (mouse_check_button_pressed(1) && point_in_rectangle(mouse_x, mouse_y, " + PhoneModsVoicesRect + "))\n" +
        "    {\n" +
        Indent(PhoneModsSavePath, 2) +
        "        pm_pick_list = [];\n" +
        "        pm_pick_paths = [];\n" +
        "        var _pk_vf = file_find_first(_pm_save + \"/phone_voices/*\", 16);\n" +
        "        while (_pk_vf != \"\")\n" +
        "        {\n" +
        "            if (_pk_vf != \".\" && _pk_vf != \"..\")\n" +
        "            {\n" +
        "                array_push(pm_pick_list, _pk_vf);\n" +
        "                array_push(pm_pick_paths, _pm_save + \"/phone_voices/\" + _pk_vf);\n" +
        "            }\n" +
        "            _pk_vf = file_find_next();\n" +
        "        }\n" +
        "        file_find_close();\n" +
        "        pm_pick_page = 0;\n" +
        "        if (array_length(pm_pick_list) == 0)\n" +
        "        {\n" +
        "            pm_status = \"No voice packs yet. Add one with ADD MOD.\";\n" +
        "        }\n" +
        "        else\n" +
        "        {\n" +
        "            pm_pick = 1;\n" +
        "        }\n" +
        "        mouse_clear(1);\n" +
        "    }\n" +
        "}\n";

    // Appended after the ADD MOD drawing, so the open picker covers it.
    private static readonly string PhoneModsPickerBottom =
        "\nif (" + PhoneModsOnMobile + " && title == true)\n" +
        "{\n" +
        "    var _pk_ha = draw_get_halign();\n" +
        "    var _pk_va = draw_get_valign();\n" +
        "    draw_set_halign(1);\n" +
        "    draw_set_valign(1);\n" +
        "    var _pk_vh = point_in_rectangle(mouse_x, mouse_y, " + PhoneModsVoicesRect + ");\n" +
        "    draw_set_alpha(0.45 + (0.3 * _pk_vh));\n" +
        "    draw_rectangle_color(" + PhoneModsVoicesRect + ", 0, 0, 0, 0, false);\n" +
        "    draw_set_alpha(1);\n" +
        "    draw_text_color(80, room_height - 32, \"VOICES\", 16777215, 16777215, 16777215, 16777215, 1);\n" +
        "    if (pm_pick > 0)\n" +
        "    {\n" +
        Indent(PhoneModsPickLayout, 2) +
        "        draw_set_alpha(0.9);\n" +
        "        draw_rectangle_color(0, 0, room_width, room_height, 0, 0, 0, 0, false);\n" +
        "        draw_set_alpha(1);\n" +
        "        var _pk_title = \"CHOOSE A VOICE PACK\";\n" +
        "        if (pm_pick == 2)\n" +
        "        {\n" +
        "            _pk_title = \"GIVE \" + string_upper(string_copy(pm_voice_name, 1, 20)) + \" TO WHICH LOVER?\";\n" +
        "        }\n" +
        "        draw_text_color(room_width / 2, 20, _pk_title, 16777215, 16777215, 16777215, 16777215, 1);\n" +
        "        for (var _pk_i = 0; _pk_i < _pk_rows; _pk_i++)\n" +
        "        {\n" +
        "            if ((_pk_first + _pk_i) < array_length(pm_pick_list))\n" +
        "            {\n" +
        "                var _pk_ry = _pk_y0 + (_pk_i * 24);\n" +
        "                var _pk_rh = point_in_rectangle(mouse_x, mouse_y, _pk_x1, _pk_ry, _pk_x2, _pk_ry + 21);\n" +
        "                draw_set_alpha(0.3 + (0.3 * _pk_rh));\n" +
        "                draw_rectangle_color(_pk_x1, _pk_ry, _pk_x2, _pk_ry + 21, 8421504, 8421504, 8421504, 8421504, false);\n" +
        "                draw_set_alpha(1);\n" +
        "                draw_text_color(room_width / 2, _pk_ry + 11, string_upper(string_copy(pm_pick_list[_pk_first + _pk_i], 1, 34)), 16777215, 16777215, 16777215, 16777215, 1);\n" +
        "            }\n" +
        "        }\n" +
        "        if (pm_pick_page > 0)\n" +
        "        {\n" +
        "            draw_text_color(_pk_x1 + 45, _pk_by + 11, \"< BACK\", 16777215, 16777215, 16777215, 16777215, 1);\n" +
        "        }\n" +
        "        draw_text_color(room_width / 2, _pk_by + 11, \"CANCEL\", 16777215, 16777215, 16777215, 16777215, 1);\n" +
        "        if ((_pk_first + _pk_rows) < array_length(pm_pick_list))\n" +
        "        {\n" +
        "            draw_text_color(_pk_x2 - 45, _pk_by + 11, \"MORE >\", 16777215, 16777215, 16777215, 16777215, 1);\n" +
        "        }\n" +
        "    }\n" +
        "    draw_set_halign(_pk_ha);\n" +
        "    draw_set_valign(_pk_va);\n" +
        "}\n";

    private static readonly string PhoneModsDialogEventCode =
        "if (ds_map_find_value(async_load, \"id\") == pm_dialog)\n" +
        "{\n" +
        "    pm_dialog = -1;\n" +
        "    if (ds_map_find_value(async_load, \"status\"))\n" +
        "    {\n" +
        "        var _pm_url = string(ds_map_find_value(async_load, \"result\"));\n" +
        "        _pm_url = string_replace_all(string_replace_all(string_replace_all(_pm_url, \" \", \"\"), \"\\n\", \"\"), \"\\r\", \"\");\n" +
        "        if (string_pos(\"https://\", _pm_url) == 1)\n" +
        "        {\n" +
        Indent(PhoneModsSavePath, 3) +
        "            pm_url = _pm_url;\n" +
        "            pm_http = http_get_file(_pm_url, _pm_save + \"/phone_mods_dl.zip\");\n" +
        "            pm_status = \"Downloading...\";\n" +
        "        }\n" +
        "        else if (_pm_url != \"\")\n" +
        "        {\n" +
        "            pm_status = \"That is not an https:// link.\";\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    // `route` must leave _pm_bg / _pm_dlg / _pm_tex; `looseJson` is GML run when the download
    // turned out not to be a zip (or empty, for forks with nowhere to put a bare .json).
    private static string PhoneModsHttpEventCode(string scan, string afterAdded, string route, string looseJson) =>
        "if (ds_map_find_value(async_load, \"id\") == pm_http)\n" +
        "{\n" +
        "    var _pm_state = ds_map_find_value(async_load, \"status\");\n" +
        "    if (_pm_state == 1)\n" +
        "    {\n" +
        "        var _pm_got = ds_map_find_value(async_load, \"sizeDownloaded\");\n" +
        "        if (!is_undefined(_pm_got))\n" +
        "        {\n" +
        "            pm_status = \"Downloading... \" + string(floor(_pm_got / 1024)) + \" KB\";\n" +
        "        }\n" +
        "    }\n" +
        "    else\n" +
        "    {\n" +
        "        pm_http = -1;\n" +
        Indent(PhoneModsSavePath, 2) +
        "        var _pm_zip = _pm_save + \"/phone_mods_dl.zip\";\n" +
        Indent(PhoneModsUrlFileName, 2) +
        // A 404 still "completes" and saves the error page, so the HTTP code is checked too.
        "        var _pm_code = ds_map_find_value(async_load, \"http_status\");\n" +
        "        if (_pm_state == 0 && _pm_code == 200 && file_exists(_pm_zip))\n" +
        "        {\n" +
        "            var _pm_dest = _pm_save + \"/phone_mods/dl_\" + string_replace_all(string_format(date_current_datetime(), 1, 6), \".\", \"_\");\n" +
        "            if (zip_unzip(_pm_zip, _pm_dest + \"/\") <= 0)\n" +
        "            {\n" +
        "                pm_status = \"That file is not a .zip the game can open.\";\n" +
        Indent(looseJson, 4) +
        "            }\n" +
        "            else\n" +
        "            {\n" +
        Indent(scan, 4) +
        Indent(route, 4) +
        "                var _pm_what = \"\";\n" +
        "                if (_pm_added > 0)\n" +
        "                {\n" +
        Indent(afterAdded, 5) +
        "                    _pm_what += string(_pm_added) + \" character(s) \";\n" +
        "                }\n" +
        "                else\n" +
        "                {\n" +
        "                    directory_destroy(_pm_dest);\n" +
        "                }\n" +
        "                if (_pm_bg > 0)\n" +
        "                {\n" +
        "                    _pm_what += string(_pm_bg) + \" background(s) \";\n" +
        "                }\n" +
        "                if (_pm_dlg > 0)\n" +
        "                {\n" +
        "                    _pm_what += string(_pm_dlg) + \" dialogue file(s) \";\n" +
        "                }\n" +
        "                if (_pm_tex > 0)\n" +
        "                {\n" +
        "                    _pm_what += string(_pm_tex) + \" texture pack(s) \";\n" +
        "                }\n" +
        "                if (_pm_voice > 0)\n" +
        "                {\n" +
        "                    _pm_what += string(_pm_voice) + \" voice pack(s) \";\n" +
        "                }\n" +
        "                if (_pm_what == \"\")\n" +
        "                {\n" +
        "                    pm_status = \"Nothing the game can use was found in that file.\";\n" +
        "                }\n" +
        "                else\n" +
        "                {\n" +
        "                    pm_status = \"Added \" + _pm_what;\n" +
        "                    if ((_pm_bg + _pm_dlg + _pm_tex) > 0)\n" +
        "                    {\n" +
        "                        pm_status += \"- restart the game to use backgrounds, dialogue and textures.\";\n" +
        "                    }\n" +
        "                    if (_pm_voice > 0)\n" +
        "                    {\n" +
        "                        pm_status += \"- tap VOICES to give a voice to a lover.\";\n" +
        "                    }\n" +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "        else\n" +
        "        {\n" +
        "            pm_status = \"Download failed (\" + string(_pm_code) + \"). Check the link.\";\n" +
        "        }\n" +
        "        if (file_exists(_pm_zip))\n" +
        "        {\n" +
        "            file_delete(_pm_zip);\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    // A link straight to a .json (how most dialogue packs are posted) is not a zip: keep it as
    // a dialogue pack under its own file name.
    private const string ModRoomLooseJson =
        "var _pm_fl = string_lower(_pm_fn);\n" +
        "if (string_copy(_pm_fl, string_length(_pm_fl) - 4, 5) == \".json\")\n" +
        "{\n" +
        "    if (!directory_exists(_pm_save + \"/dialogue_packs\"))\n" +
        "    {\n" +
        "        directory_create(_pm_save + \"/dialogue_packs\");\n" +
        "    }\n" +
        "    file_copy(_pm_zip, _pm_save + \"/dialogue_packs/\" + _pm_fn);\n" +
        "    pm_status = \"Added dialogue pack \" + _pm_fn + \" - restart the game to use it.\";\n" +
        "}\n";

    private sealed record PhoneModsTarget(UndertaleData Data, UndertaleGameObject Obj, string CreateText, string DrawText,
        bool ModRoomStyle, string FutaBody, string WifeBody, int InsertAt);

    // Reads everything the patch touches, or says in plain words why this data file can't take it.
    private static (PhoneModsTarget? Target, bool AlreadyPatched, string? Problem) ReadPhoneModsTarget(string dataWinPath)
    {
        UndertaleData data;
        using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
        {
            data = UndertaleIO.Read(stream);
        }

        var obj = data.GameObjects.ByName("oFutaMatingPress");
        var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
        var drawCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == DrawEventName);
        if (obj is null || createCode is null || drawCode is null)
        {
            return (null, false, "Not a compatible game (missing oFutaMatingPress).");
        }

        var globalContext = new GlobalDecompileContext(data);
        var settings = data.ToolInfo.DecompilerSettings;
        string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
        if (createText.Contains("pm_dialog"))
        {
            return (null, true, null);
        }
        if (createText.Contains("_pm_root"))
        {
            return (null, false,
                "This file has the earlier phone-mods patch (no in-game downloader). Patch a clean copy instead.");
        }
        if (obj.Events[(int)EventType.Other].Any(e => e.EventSubtype is PhoneModsAsyncDialogSubtype or PhoneModsAsyncHttpSubtype))
        {
            return (null, false, "This game already has its own Async HTTP or Dialog event - not safe to add another.");
        }
        string drawText = new DecompileContext(globalContext, drawCode, settings).DecompileToString();

        if (createText.Contains(AndroidCustomDiscoveryMarker))
        {
            if (!drawText.Contains(PhoneModsDrawEventAnchor))
            {
                return (null, false, "Couldn't find the title screen's CREDITS button in the Draw event to place the ADD MOD button beside.");
            }
            return (new PhoneModsTarget(data, obj, createText, drawText, false, "", "", -1), false, null);
        }

        var (futaBody, _) = ExtractLoopBody(createText, FutaLoopOpen, "custom_futas");
        var (wifeBody, wifeEnd) = ExtractLoopBody(createText, WifeLoopOpen, "custom_wives");
        if (futaBody is null || wifeBody is null)
        {
            bool rewritten = createText.Contains("_futa_names") || createText.Contains("_wife_names");
            return (null, false, rewritten
                ? "This file already has --touch-controls, which rewrites the code this patch copies. Apply --phone-mods first, then --touch-controls."
                : "Couldn't find a custom-character discovery loop this patch knows (vanilla's or ModRoom's) in the Create event.");
        }
        if (futaBody.Contains("UnknownEnum") || wifeBody.Contains("UnknownEnum"))
        {
            return (null, false, "This fork's discovery code uses an enum that can't be copied into a new event - not supported yet.");
        }
        if (!drawText.Contains("title == true"))
        {
            return (null, false, "Couldn't find the title screen code in the Draw event to place the ADD MOD button on.");
        }
        return (new PhoneModsTarget(data, obj, createText, drawText, true, futaBody, wifeBody, wifeEnd), false, null);
    }

    public static (bool Compatible, bool AlreadyPatched, string Detail) CheckPhoneModsStatus(string dataWinPath)
    {
        try
        {
            var (target, alreadyPatched, problem) = ReadPhoneModsTarget(dataWinPath);
            if (alreadyPatched)
            {
                return (true, true, "Already patched.");
            }
            if (target is null)
            {
                return (false, false, problem!);
            }
            return (true, false, target.ModRoomStyle ? "Compatible (ModRoom-style), not yet patched." : "Compatible (vanilla), not yet patched.");
        }
        catch (Exception ex)
        {
            return (false, false, $"Couldn't check: {ex.Message}");
        }
    }

    public static PatchOutcome PatchPhoneMods(string dataWinPath)
    {
        try
        {
            string backupPath = dataWinPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(dataWinPath, backupPath);
            }

            var (target, alreadyPatched, problem) = ReadPhoneModsTarget(dataWinPath);
            if (alreadyPatched)
            {
                return new PatchOutcome(PatchResult.AlreadyPatched, "The phone mods folder is already patched in - nothing to do.");
            }
            if (target is null)
            {
                return new PatchOutcome(PatchResult.NotSupported, problem!);
            }

            var data = target.Data;
            string createText, drawText, httpCode;
            if (target.ModRoomStyle)
            {
                string register = ModRoomRegister(target.FutaBody, target.WifeBody);
                // The copied bodies set custom_sprite_loaded / custom_wife_sprite_loaded themselves.
                createText = target.CreateText[..target.InsertAt] + "\n"
                    + PhoneModsCreateBlock(PhoneModsScan("_pm_root", ModRoomIsPack, register), "",
                        ModRoomRoute("_pm_root + \"/seed\"", "\"starter\""))
                    + target.CreateText[target.InsertAt..];
                drawText = PhoneModsPickerTop + PhoneModsDrawTopGeneric + target.DrawText + PhoneModsDrawBottomGeneric + PhoneModsPickerBottom;
                // A flat zip (files at the top level) takes its name from the link, minus ".zip".
                httpCode = PhoneModsHttpEventCode(PhoneModsScan("_pm_dest", ModRoomIsPack, register), "",
                    ModRoomRoute("_pm_dest", "string_copy(_pm_fn, 1, max(1, string_length(_pm_fn) - 4))"), ModRoomLooseJson);
            }
            else
            {
                createText = target.CreateText.Replace(AndroidCustomDiscoveryMarker,
                    AndroidCustomDiscoveryMarker + PhoneModsCreateBlock(PhoneModsScan("_pm_root", VanillaIsPack, VanillaRegister), VanillaAfterScan, ""));
                drawText = target.DrawText.Replace(PhoneModsDrawEventAnchor, PhoneModsDrawEventAnchor + PhoneModsDrawBlock);
                httpCode = PhoneModsHttpEventCode(PhoneModsScan("_pm_dest", VanillaIsPack, VanillaRegister),
                    "custom_sprite_loaded = true;\n", PhoneModsNoRoute, "");
            }

            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };
            importGroup.QueueReplace(CreateEventName, createText);
            importGroup.QueueReplace(DrawEventName, drawText);
            importGroup.QueueReplace(PhoneModsDialogEventName, PhoneModsDialogEventCode);
            importGroup.QueueReplace(PhoneModsHttpEventName, httpCode);
            importGroup.Import();

            // Same wiring as the HMV patch's async-networking event: the code entries exist now,
            // but GameMaker only runs code attached to a registered event on the object.
            foreach (var (name, subtype) in new[] { (PhoneModsDialogEventName, PhoneModsAsyncDialogSubtype), (PhoneModsHttpEventName, PhoneModsAsyncHttpSubtype) })
            {
                var newCode = data.Code.ByName(name);
                if (newCode is null)
                {
                    return new PatchOutcome(PatchResult.Error, $"The {name} code entry wasn't created as expected.");
                }
                var newEvent = new UndertaleGameObject.Event { EventSubtype = subtype };
                newEvent.Actions.Add(new UndertaleGameObject.EventAction
                {
                    LibID = 1,
                    ID = 603,
                    Kind = 7,
                    UseRelative = false,
                    IsQuestion = false,
                    UseApplyTo = false,
                    ExeType = 2,
                    ActionName = null,
                    ArgumentCount = 0,
                    Who = -1,
                    Relative = false,
                    IsNot = false,
                    UnknownAlwaysZero = 0,
                    CodeId = newCode,
                });
                target.Obj.Events[(int)EventType.Other].Add(newEvent);
            }

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            string style = target.ModRoomStyle ? "ModRoom-style" : "vanilla";
            return new PatchOutcome(PatchResult.Patched,
                $"Phone mods patched successfully ({style})! On Android the game now loads custom characters from its own " +
                "writable folder (<save area>/phone_mods/) and has an ADD MOD button on the title screen that " +
                $"downloads a mod .zip from an https:// link. A backup of the original was saved as:\n{backupPath}");
        }
        catch (Exception ex)
        {
            return new PatchOutcome(PatchResult.Error, $"Something went wrong while patching the phone mods folder: {ex.Message}");
        }
    }

    // ================================================================================
    // BUILT-IN TOY CLIENT (--toy-direct) - the game talks to Intiface Central itself, so no
    // separate bridge has to run. Made for Android, where the bridge otherwise needs Termux and
    // a proot Linux; works on any fork (vanilla, ModRoom, DeepRoom) because it hangs off the
    // same two anchors as the toy telemetry patch and reads the same game variables.
    //
    // It is a GML port of ButtplugBridge/Program.cs: a client speaking the Buttplug protocol
    // (message spec v3, JSON) to ws://127.0.0.1:<port>, with the same thrust -> intensity mapping
    // (|thrust delta| * 7 sampled every 33 ms, smoothed 0.35, orgasm floor + pulse) and the same
    // named power ranges as profiles.json.
    //   - Vibrate / Oscillate actuators (Hismith machines are Oscillate = stroke speed) get a
    //     ScalarCmd; LinearCmd devices get position + duration.
    //   - Toys stop when the scene ends, when insert goes false, and on the title screen (which
    //     plays a scene behind the door). That needs code that runs when no scene is active,
    //     which is why the main block is prepended to the Draw event and the telemetry anchor
    //     only stamps tb_scene_at.
    //   - Runs on Android/iOS. On PC it stays off unless a file named toy_direct.txt sits next
    //     to the game, so it never fights the external bridge.
    //   - Title screen, top right: connection state and a tap-to-cycle power range, saved to
    //     toy_direct.ini in the save area (which may also set port=, default 12345).
    //
    // WHY THE WEBSOCKET IS HAND-ROLLED over a raw TCP socket instead of using GameMaker's own
    // network_socket_ws: on Android the runner answers a WebSocket ping with an UNMASKED pong.
    // Seen for real on the emulator - a standards-checking server closed the connection with
    // "1002 protocol error: incorrect masking" exactly at each ping, while the same build on
    // Windows was fine. Buttplug servers ping, so the toy would have dropped out every few
    // seconds. Doing the upgrade handshake, framing and pong here behaves the same everywhere.
    // GameMaker also reported neither a disconnect event nor a send error when the server went
    // away, hence the application-level Ping every 4 s and the 12 s silence timeout.
    private const string ToyDirectAsyncEventName = "gml_Object_oFutaMatingPress_Other_68";
    private const uint ToyDirectAsyncNetworkingSubtype = 68;
    private static readonly string ToyDirectWsKey = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("WifesBedroomToy!"));

    // GML that sends `jsonExpr` (a GML string expression) as one masked WebSocket text frame.
    // The mask key is four zero bytes: legal, and it leaves the payload bytes as they are.
    private static string TbSend(string jsonExpr) =>
        "var _tb_p = " + jsonExpr + ";\n" +
        "var _tb_n = string_byte_length(_tb_p);\n" +
        "buffer_seek(tb_buf, buffer_seek_start, 0);\n" +
        "buffer_write(tb_buf, buffer_u8, 129);\n" +
        "if (_tb_n < 126)\n" +
        "{\n" +
        "    buffer_write(tb_buf, buffer_u8, 128 + _tb_n);\n" +
        "}\n" +
        "else\n" +
        "{\n" +
        "    buffer_write(tb_buf, buffer_u8, 254);\n" +
        "    buffer_write(tb_buf, buffer_u8, (_tb_n >> 8) & 255);\n" +
        "    buffer_write(tb_buf, buffer_u8, _tb_n & 255);\n" +
        "}\n" +
        "buffer_write(tb_buf, buffer_u32, 0);\n" +
        "buffer_write(tb_buf, buffer_text, _tb_p);\n" +
        "if (network_send_raw(tb_sock, tb_buf, buffer_tell(tb_buf)) < 0)\n" +
        "{\n" +
        "    tb_state = 0;\n" +
        "}\n";

    private static string TbIndent(string gml, int levels)
    {
        string pad = new string(' ', 4 * levels);
        return string.Concat(gml.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => pad + line + "\n"));
    }

    // GML that records the device struct held in variable `dev` (replacing any earlier entry
    // with the same index). Parallel arrays rather than structs, to stay well inside what the
    // decompile/recompile round trip is known to handle.
    private static string TbAddDevice(string dev) =>
        "var _tb_di = variable_struct_get(" + dev + ", \"DeviceIndex\");\n" +
        "for (var _tb_r = array_length(tb_dev_idx) - 1; _tb_r >= 0; _tb_r--)\n" +
        "{\n" +
        "    if (tb_dev_idx[_tb_r] == _tb_di)\n" +
        "    {\n" +
        "        array_delete(tb_dev_idx, _tb_r, 1);\n" +
        "        array_delete(tb_dev_name, _tb_r, 1);\n" +
        "        array_delete(tb_dev_scalars, _tb_r, 1);\n" +
        "        array_delete(tb_dev_linears, _tb_r, 1);\n" +
        "    }\n" +
        "}\n" +
        "var _tb_sc = [];\n" +
        "var _tb_ln = 0;\n" +
        "var _tb_dm = variable_struct_get(" + dev + ", \"DeviceMessages\");\n" +
        "if (is_struct(_tb_dm))\n" +
        "{\n" +
        "    var _tb_s = variable_struct_get(_tb_dm, \"ScalarCmd\");\n" +
        "    if (is_array(_tb_s))\n" +
        "    {\n" +
        "        for (var _tb_k = 0; _tb_k < array_length(_tb_s); _tb_k++)\n" +
        "        {\n" +
        "            array_push(_tb_sc, string(variable_struct_get(_tb_s[_tb_k], \"ActuatorType\")));\n" +
        "        }\n" +
        "    }\n" +
        "    var _tb_l = variable_struct_get(_tb_dm, \"LinearCmd\");\n" +
        "    if (is_array(_tb_l))\n" +
        "    {\n" +
        "        _tb_ln = array_length(_tb_l);\n" +
        "    }\n" +
        "}\n" +
        "array_push(tb_dev_idx, _tb_di);\n" +
        "array_push(tb_dev_name, string(variable_struct_get(" + dev + ", \"DeviceName\")));\n" +
        "array_push(tb_dev_scalars, _tb_sc);\n" +
        "array_push(tb_dev_linears, _tb_ln);\n";

    private const string ToyDirectCreateBlock =
        "\ntb_enabled = os_type == os_android || os_type == os_ios;" +
        "\nvar _tb_wd = working_directory;" +
        "\nif (string_char_at(_tb_wd, string_length(_tb_wd)) == \"/\")" +
        "\n{" +
        "\n    _tb_wd = string_copy(_tb_wd, 1, string_length(_tb_wd) - 1);" +
        "\n}" +
        "\nif (file_exists(_tb_wd + \"/toy_direct.txt\"))" +
        "\n{" +
        "\n    tb_enabled = true;" +
        "\n}" +
        "\ntb_sock = -1;" +
        "\ntb_state = 0;" +
        "\ntb_ws_open = false;" +
        "\ntb_retry_at = 0;" +
        "\ntb_msg_id = 10;" +
        "\ntb_last_ping = 0;" +
        "\ntb_ping_ms = 0;" +
        "\ntb_ping_every = 4000;" +
        "\ntb_frame_at = 0;" +
        "\ntb_last_rx = 0;" +
        "\ntb_dev_idx = [];" +
        "\ntb_dev_name = [];" +
        "\ntb_dev_scalars = [];" +
        "\ntb_dev_linears = [];" +
        "\ntb_buf = buffer_create(1024, buffer_grow, 1);" +
        "\ntb_rx = buffer_create(4096, buffer_grow, 1);" +
        "\ntb_rx_len = 0;" +
        "\ntb_scene_at = -100000;" +
        "\ntb_sample_at = 0;" +
        "\ntb_send_at = 0;" +
        "\ntb_last_thrust = 0;" +
        "\ntb_have_last = false;" +
        "\ntb_smooth = 0;" +
        "\ntb_sent = -1;" +
        "\ntb_running = false;" +
        "\ntb_pulse = 0;" +
        "\ntb_profile_names = [\"DEFAULT 0-100\", \"EASY 0-30\", \"MID 30-60\", \"HARD 60-100\", \"MIDEASY 0-50\", \"MIDHARD 50-100\"];" +
        "\ntb_profile_min = [0, 0, 0.3, 0.6, 0, 0.5];" +
        "\ntb_profile_max = [1, 0.3, 0.6, 1, 0.5, 1];" +
        "\ntb_profile = 0;" +
        "\ntb_port = 12345;" +
        "\nif (tb_enabled)" +
        "\n{" +
        "\n    ini_open(game_save_id + \"toy_direct.ini\");" +
        "\n    tb_profile = clamp(floor(ini_read_real(\"toy\", \"profile\", 0)), 0, 5);" +
        "\n    tb_port = floor(ini_read_real(\"toy\", \"port\", 12345));" +
        "\n    ini_close();" +
        "\n}";

    private const string ToyDirectSceneStamp = "\ntb_scene_at = current_time;";

    // Prepended to the Draw event, so it runs every frame whether or not a scene is active.
    private static readonly string ToyDirectDrawTop =
        "if (tb_enabled)\n" +
        "{\n" +
        "    var _tb_now = current_time;\n" +
        // The power tap is handled up here, ahead of the game's own click code, and swallowed
        // with mouse_clear: DeepRoom's "click to begin" covers the whole screen, so otherwise
        // changing the power also started the game.
        "    if (title == true && mouse_check_button_pressed(1) && point_in_rectangle(mouse_x, mouse_y, room_width - 124, 0, room_width, 44))\n" +
        "    {\n" +
        "        tb_profile = (tb_profile + 1) % array_length(tb_profile_names);\n" +
        "        ini_open(game_save_id + \"toy_direct.ini\");\n" +
        "        ini_write_real(\"toy\", \"profile\", tb_profile);\n" +
        "        ini_close();\n" +
        "        mouse_clear(1);\n" +
        "    }\n" +
        "    if (tb_state == 0 && _tb_now >= tb_retry_at)\n" +
        "    {\n" +
        "        if (tb_sock >= 0)\n" +
        "        {\n" +
        "            network_destroy(tb_sock);\n" +
        "        }\n" +
        "        tb_running = false;\n" +
        "        tb_ws_open = false;\n" +
        "        tb_rx_len = 0;\n" +
        "        tb_sock = network_create_socket(network_socket_tcp);\n" +
        "        network_connect_raw_async(tb_sock, \"127.0.0.1\", tb_port);\n" +
        "        tb_state = 1;\n" +
        "        tb_retry_at = _tb_now + 6000;\n" +
        "    }\n" +
        "    else if ((tb_state == 1 || tb_state == 2) && _tb_now >= tb_retry_at)\n" +
        "    {\n" +
        "        tb_state = 0;\n" +
        "    }\n" +
        "    if (tb_state == 3)\n" +
        "    {\n" +
        "        if ((_tb_now - tb_last_rx) >= 12000)\n" +
        "        {\n" +
        "            tb_state = 0;\n" +
        "            tb_retry_at = _tb_now;\n" +
        "        }\n" +
        // A gap between frames means the app was frozen (sent to the background): Android
        // stops the game dead, with no chance to send anything first - confirmed on the
        // emulator, where the last speed command simply stayed in force. Nothing in GML can
        // prevent that; the server's ping timeout (MaxPingTime) is what stops the machine,
        // and this only cleans up on the way back.
        // Wall-clock time, because current_time does not advance while Android has the app frozen.
        "        var _tb_wall = date_current_datetime() * 86400000;\n" +
        "        var _tb_frozen = tb_frame_at > 0 && (_tb_wall - tb_frame_at) >= 1500;\n" +
        "        tb_frame_at = _tb_wall;\n" +
        "        if (_tb_frozen && tb_running)\n" +
        "        {\n" +
        "            tb_running = false;\n" +
        "            tb_sent = -1;\n" +
        "            tb_smooth = 0;\n" +
        "            tb_have_last = false;\n" +
        TbIndent(TbSend("\"[{\\\"StopAllDevices\\\":{\\\"Id\\\":\" + string(tb_msg_id++) + \"}}]\""), 3) +
        "        }\n" +
        "        if ((_tb_now - tb_last_ping) >= tb_ping_every)\n" +
        "        {\n" +
        "            tb_last_ping = _tb_now;\n" +
        TbIndent(TbSend("\"[{\\\"Ping\\\":{\\\"Id\\\":\" + string(tb_msg_id++) + \"}}]\""), 3) +
        "        }\n" +
        "        var _tb_scene = (_tb_now - tb_scene_at) < 500;\n" +
        // os_is_paused(): the app is being sent to the background. The game stops running there,
        // so without this the machine would keep going at the last speed it was told.
        "        if (!_tb_scene || !insert || title == true || os_is_paused())\n" +
        "        {\n" +
        "            tb_have_last = false;\n" +
        "            tb_smooth = 0;\n" +
        "            if (tb_running)\n" +
        "            {\n" +
        "                tb_running = false;\n" +
        "                tb_sent = -1;\n" +
        TbIndent(TbSend("\"[{\\\"StopAllDevices\\\":{\\\"Id\\\":\" + string(tb_msg_id++) + \"}}]\""), 4) +
        "            }\n" +
        "        }\n" +
        "        else\n" +
        "        {\n" +
        "            if ((_tb_now - tb_sample_at) >= 33)\n" +
        "            {\n" +
        "                tb_sample_at = _tb_now;\n" +
        "                var _tb_delta = 0;\n" +
        "                if (tb_have_last)\n" +
        "                {\n" +
        "                    _tb_delta = abs(thrust - tb_last_thrust);\n" +
        "                }\n" +
        "                tb_last_thrust = thrust;\n" +
        "                tb_have_last = true;\n" +
        "                var _tb_target = clamp(_tb_delta * 7, 0, 1);\n" +
        "                if (orgasm)\n" +
        "                {\n" +
        "                    tb_pulse += 0.6283;\n" +
        "                    _tb_target = max(_tb_target, 0.45 + (0.25 * (0.5 + (0.5 * sin(tb_pulse)))));\n" +
        "                }\n" +
        "                else\n" +
        "                {\n" +
        "                    tb_pulse = 0;\n" +
        "                }\n" +
        "                tb_smooth += (clamp(_tb_target, 0, 1) - tb_smooth) * 0.35;\n" +
        "            }\n" +
        "            var _tb_lo = tb_profile_min[tb_profile];\n" +
        "            var _tb_hi = tb_profile_max[tb_profile];\n" +
        "            var _tb_want = _tb_lo + (clamp(tb_smooth, 0, 1) * (_tb_hi - _tb_lo));\n" +
        "            if ((_tb_now - tb_send_at) >= 66)\n" +
        "            {\n" +
        "                tb_send_at = _tb_now;\n" +
        "                var _tb_changed = !tb_running || abs(_tb_want - tb_sent) >= 0.01;\n" +
        "                var _tb_pos = _tb_lo + (clamp(thrust, 0, 1) * (_tb_hi - _tb_lo));\n" +
        "                var _tb_dur = round(clamp(((360 / max(thrust_speed, 0.5)) * (1000 / 60)) / 2, 100, 1800));\n" +
        "                var _tb_json = \"\";\n" +
        "                for (var _tb_i = 0; _tb_i < array_length(tb_dev_idx); _tb_i++)\n" +
        "                {\n" +
        "                    var _tb_types = tb_dev_scalars[_tb_i];\n" +
        "                    var _tb_part = \"\";\n" +
        "                    if (_tb_changed)\n" +
        "                    {\n" +
        "                        for (var _tb_k = 0; _tb_k < array_length(_tb_types); _tb_k++)\n" +
        "                        {\n" +
        "                            if (_tb_types[_tb_k] == \"Vibrate\" || _tb_types[_tb_k] == \"Oscillate\")\n" +
        "                            {\n" +
        "                                if (_tb_part != \"\")\n" +
        "                                {\n" +
        "                                    _tb_part += \",\";\n" +
        "                                }\n" +
        "                                _tb_part += \"{\\\"Index\\\":\" + string(_tb_k) + \",\\\"Scalar\\\":\" + string(_tb_want) + \",\\\"ActuatorType\\\":\\\"\" + _tb_types[_tb_k] + \"\\\"}\";\n" +
        "                            }\n" +
        "                        }\n" +
        "                    }\n" +
        "                    if (_tb_part != \"\")\n" +
        "                    {\n" +
        "                        if (_tb_json != \"\")\n" +
        "                        {\n" +
        "                            _tb_json += \",\";\n" +
        "                        }\n" +
        "                        _tb_json += \"{\\\"ScalarCmd\\\":{\\\"Id\\\":\" + string(tb_msg_id++) + \",\\\"DeviceIndex\\\":\" + string(tb_dev_idx[_tb_i]) + \",\\\"Scalars\\\":[\" + _tb_part + \"]}}\";\n" +
        "                    }\n" +
        "                    if (tb_dev_linears[_tb_i] > 0)\n" +
        "                    {\n" +
        "                        var _tb_vec = \"\";\n" +
        "                        for (var _tb_v = 0; _tb_v < tb_dev_linears[_tb_i]; _tb_v++)\n" +
        "                        {\n" +
        "                            if (_tb_vec != \"\")\n" +
        "                            {\n" +
        "                                _tb_vec += \",\";\n" +
        "                            }\n" +
        "                            _tb_vec += \"{\\\"Index\\\":\" + string(_tb_v) + \",\\\"Duration\\\":\" + string(_tb_dur) + \",\\\"Position\\\":\" + string(_tb_pos) + \"}\";\n" +
        "                        }\n" +
        "                        if (_tb_json != \"\")\n" +
        "                        {\n" +
        "                            _tb_json += \",\";\n" +
        "                        }\n" +
        "                        _tb_json += \"{\\\"LinearCmd\\\":{\\\"Id\\\":\" + string(tb_msg_id++) + \",\\\"DeviceIndex\\\":\" + string(tb_dev_idx[_tb_i]) + \",\\\"Vectors\\\":[\" + _tb_vec + \"]}}\";\n" +
        "                    }\n" +
        "                }\n" +
        "                if (_tb_json != \"\")\n" +
        "                {\n" +
        "                    tb_running = true;\n" +
        "                    tb_sent = _tb_want;\n" +
        TbIndent(TbSend("\"[\" + _tb_json + \"]\""), 5) +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "    }\n" +
       "}\n";

    // Appended to the Draw event. Upper-case only: the game's fonts have no lowercase glyphs.
    private const string ToyDirectDrawBottom =
        "\nif (tb_enabled && title == true)\n" +
        "{\n" +
        "    var _tb_ha = draw_get_halign();\n" +
        "    var _tb_va = draw_get_valign();\n" +
        "    draw_set_halign(2);\n" +
        "    draw_set_valign(0);\n" +
        "    var _tb_label = \"TOY: OPEN INTIFACE CENTRAL\";\n" +
        "    if (tb_state == 3)\n" +
        "    {\n" +
        "        _tb_label = \"TOY: SEARCHING...\";\n" +
        "        if (array_length(tb_dev_idx) > 0)\n" +
        "        {\n" +
        "            _tb_label = \"TOY: \" + string_upper(tb_dev_name[0]);\n" +
        "        }\n" +
        "    }\n" +
        "    _tb_label += \"\\nPOWER: \" + tb_profile_names[tb_profile] + \" (TAP)\";\n" +
        "    if (tb_state == 3 && tb_ping_ms <= 0)\n" +
        "    {\n" +
        "        _tb_label += \"\\nSAFETY: SET MAX PING TIME IN INTIFACE\";\n" +
        "    }\n" +
        "    var _tb_hit = point_in_rectangle(mouse_x, mouse_y, room_width - 124, 0, room_width, 44);\n" +
        "    draw_text_color(room_width - 6, 6, _tb_label, 16777215, 16777215, 16777215, 16777215, 0.65 + (0.35 * _tb_hit));\n" +
        "    draw_set_halign(_tb_ha);\n" +
        "    draw_set_valign(_tb_va);\n" +
        "}\n";

    // GML handling one complete Buttplug JSON message array held in _tb_text.
    private static readonly string ToyDirectHandleJson =
        "var _tb_msgs = -1;\n" +
        "if (string_char_at(_tb_text, 1) == \"[\")\n" +
        "{\n" +
        "    _tb_msgs = json_parse(_tb_text);\n" +
        "}\n" +
        "if (is_array(_tb_msgs))\n" +
        "{\n" +
        "    for (var _tb_m = 0; _tb_m < array_length(_tb_msgs); _tb_m++)\n" +
        "    {\n" +
        "        var _tb_msg = _tb_msgs[_tb_m];\n" +
        "        if (!is_struct(_tb_msg))\n" +
        "        {\n" +
        "            continue;\n" +
        "        }\n" +
        "        if (variable_struct_exists(_tb_msg, \"ServerInfo\"))\n" +
        "        {\n" +
        "            tb_last_ping = current_time;\n" +
        "            var _tb_mp = variable_struct_get(variable_struct_get(_tb_msg, \"ServerInfo\"), \"MaxPingTime\");\n" +
        "            tb_ping_ms = is_real(_tb_mp) ? _tb_mp : 0;\n" +
        "            tb_ping_every = 4000;\n" +
        "            if (tb_ping_ms > 0)\n" +
        "            {\n" +
        "                tb_ping_every = clamp(tb_ping_ms / 2, 100, 4000);\n" +
        "            }\n" +
        "            tb_dev_idx = [];\n" +
        "            tb_dev_name = [];\n" +
        "            tb_dev_scalars = [];\n" +
        "            tb_dev_linears = [];\n" +
        "            tb_running = false;\n" +
        "            tb_state = 3;\n" +
        TbIndent(TbSend("\"[{\\\"RequestDeviceList\\\":{\\\"Id\\\":2}},{\\\"StartScanning\\\":{\\\"Id\\\":3}}]\""), 3) +
        "        }\n" +
        "        else if (variable_struct_exists(_tb_msg, \"DeviceList\"))\n" +
        "        {\n" +
        "            var _tb_list = variable_struct_get(variable_struct_get(_tb_msg, \"DeviceList\"), \"Devices\");\n" +
        "            if (is_array(_tb_list))\n" +
        "            {\n" +
        "                for (var _tb_d = 0; _tb_d < array_length(_tb_list); _tb_d++)\n" +
        "                {\n" +
        "                    var _tb_dev = _tb_list[_tb_d];\n" +
        TbIndent(TbAddDevice("_tb_dev"), 5) +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "        else if (variable_struct_exists(_tb_msg, \"DeviceAdded\"))\n" +
        "        {\n" +
        "            var _tb_new = variable_struct_get(_tb_msg, \"DeviceAdded\");\n" +
        TbIndent(TbAddDevice("_tb_new"), 3) +
        "        }\n" +
        "        else if (variable_struct_exists(_tb_msg, \"DeviceRemoved\"))\n" +
        "        {\n" +
        "            var _tb_gone = variable_struct_get(variable_struct_get(_tb_msg, \"DeviceRemoved\"), \"DeviceIndex\");\n" +
        "            for (var _tb_g = array_length(tb_dev_idx) - 1; _tb_g >= 0; _tb_g--)\n" +
        "            {\n" +
        "                if (tb_dev_idx[_tb_g] == _tb_gone)\n" +
        "                {\n" +
        "                    array_delete(tb_dev_idx, _tb_g, 1);\n" +
        "                    array_delete(tb_dev_name, _tb_g, 1);\n" +
        "                    array_delete(tb_dev_scalars, _tb_g, 1);\n" +
        "                    array_delete(tb_dev_linears, _tb_g, 1);\n" +
        "                }\n" +
        "            }\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    private static readonly string ToyDirectAsyncCode =
        "\nif (ds_map_find_value(async_load, \"id\") == tb_sock)\n" +
        "{\n" +
        "    var _tb_type = ds_map_find_value(async_load, \"type\");\n" +
        "    if (_tb_type == network_type_non_blocking_connect)\n" +
        "    {\n" +
        "        if (ds_map_find_value(async_load, \"succeeded\"))\n" +
        "        {\n" +
        "            tb_state = 2;\n" +
        "            tb_retry_at = current_time + 6000;\n" +
        "            tb_last_rx = current_time;\n" +
        "            buffer_seek(tb_buf, buffer_seek_start, 0);\n" +
        "            buffer_write(tb_buf, buffer_text, \"GET / HTTP/1.1\\r\\nHost: 127.0.0.1:\" + string(tb_port) + \"\\r\\nUpgrade: websocket\\r\\nConnection: Upgrade\\r\\nSec-WebSocket-Key: " + ToyDirectWsKey + "\\r\\nSec-WebSocket-Version: 13\\r\\n\\r\\n\");\n" +
        "            network_send_raw(tb_sock, tb_buf, buffer_tell(tb_buf));\n" +
        "        }\n" +
        "        else\n" +
        "        {\n" +
        "            tb_state = 0;\n" +
        "        }\n" +
        "    }\n" +
        "    else if (_tb_type == network_type_disconnect)\n" +
        "    {\n" +
        "        tb_state = 0;\n" +
        "        tb_retry_at = current_time + 3000;\n" +
        "    }\n" +
        "    else if (_tb_type == network_type_data)\n" +
        "    {\n" +
        "        tb_last_rx = current_time;\n" +
        "        var _tb_size = ds_map_find_value(async_load, \"size\");\n" +
        "        buffer_copy(ds_map_find_value(async_load, \"buffer\"), 0, _tb_size, tb_rx, tb_rx_len);\n" +
        "        tb_rx_len += _tb_size;\n" +
        // TCP delivers a byte stream: one event may hold part of a frame or several frames.
        "        var _tb_go = true;\n" +
        "        while (_tb_go)\n" +
        "        {\n" +
        "            _tb_go = false;\n" +
        "            var _tb_text = \"\";\n" +
        "            var _tb_used = 0;\n" +
        "            if (!tb_ws_open)\n" +
        "            {\n" +
        // The HTTP upgrade reply ends at the first blank line; "HTTP/1.1 101" has 1,0,1 at 9..11.
        "                for (var _tb_h = 0; (_tb_h + 3) < tb_rx_len; _tb_h++)\n" +
        "                {\n" +
        "                    if (buffer_peek(tb_rx, _tb_h, buffer_u8) == 13 && buffer_peek(tb_rx, _tb_h + 1, buffer_u8) == 10 && buffer_peek(tb_rx, _tb_h + 2, buffer_u8) == 13 && buffer_peek(tb_rx, _tb_h + 3, buffer_u8) == 10)\n" +
        "                    {\n" +
        "                        _tb_used = _tb_h + 4;\n" +
        "                        break;\n" +
        "                    }\n" +
        "                }\n" +
        "                if (_tb_used > 0)\n" +
        "                {\n" +
        "                    if (buffer_peek(tb_rx, 9, buffer_u8) == 49 && buffer_peek(tb_rx, 10, buffer_u8) == 48 && buffer_peek(tb_rx, 11, buffer_u8) == 49)\n" +
        "                    {\n" +
        "                        tb_ws_open = true;\n" +
        TbIndent(TbSend("\"[{\\\"RequestServerInfo\\\":{\\\"Id\\\":1,\\\"ClientName\\\":\\\"Wifes Bedroom\\\",\\\"MessageVersion\\\":3}}]\""), 6) +
        "                    }\n" +
        "                    else\n" +
        "                    {\n" +
        "                        tb_state = 0;\n" +
        "                    }\n" +
        "                }\n" +
        "            }\n" +
        "            else if (tb_rx_len >= 2)\n" +
        "            {\n" +
        "                var _tb_b0 = buffer_peek(tb_rx, 0, buffer_u8);\n" +
        "                var _tb_len = buffer_peek(tb_rx, 1, buffer_u8) & 127;\n" +
        "                var _tb_hdr = 2;\n" +
        "                var _tb_ok = true;\n" +
        "                if (_tb_len == 126)\n" +
        "                {\n" +
        "                    _tb_hdr = 4;\n" +
        "                    _tb_ok = tb_rx_len >= 4;\n" +
        "                    if (_tb_ok)\n" +
        "                    {\n" +
        "                        _tb_len = (buffer_peek(tb_rx, 2, buffer_u8) << 8) | buffer_peek(tb_rx, 3, buffer_u8);\n" +
        "                    }\n" +
        "                }\n" +
        "                else if (_tb_len == 127)\n" +
        "                {\n" +
        "                    _tb_hdr = 10;\n" +
        "                    _tb_ok = tb_rx_len >= 10;\n" +
        "                    if (_tb_ok)\n" +
        "                    {\n" +
        "                        _tb_len = (buffer_peek(tb_rx, 6, buffer_u8) << 24) | (buffer_peek(tb_rx, 7, buffer_u8) << 16) | (buffer_peek(tb_rx, 8, buffer_u8) << 8) | buffer_peek(tb_rx, 9, buffer_u8);\n" +
        "                    }\n" +
        "                }\n" +
        "                if (_tb_ok && tb_rx_len >= (_tb_hdr + _tb_len))\n" +
        "                {\n" +
        "                    _tb_used = _tb_hdr + _tb_len;\n" +
        "                    var _tb_op = _tb_b0 & 15;\n" +
        "                    if (_tb_op == 1 && _tb_len > 0)\n" +
        "                    {\n" +
        "                        var _tb_copy = buffer_create(_tb_len + 1, buffer_fixed, 1);\n" +
        "                        buffer_copy(tb_rx, _tb_hdr, _tb_len, _tb_copy, 0);\n" +
        "                        buffer_poke(_tb_copy, _tb_len, buffer_u8, 0);\n" +
        "                        buffer_seek(_tb_copy, buffer_seek_start, 0);\n" +
        "                        _tb_text = buffer_read(_tb_copy, buffer_string);\n" +
        "                        buffer_delete(_tb_copy);\n" +
        "                    }\n" +
        "                    else if (_tb_op == 9 && _tb_len < 126)\n" +
        "                    {\n" +
        // Ping -> masked pong carrying the same payload.
        "                        buffer_seek(tb_buf, buffer_seek_start, 0);\n" +
        "                        buffer_write(tb_buf, buffer_u8, 138);\n" +
        "                        buffer_write(tb_buf, buffer_u8, 128 + _tb_len);\n" +
        "                        buffer_write(tb_buf, buffer_u32, 0);\n" +
        "                        for (var _tb_q = 0; _tb_q < _tb_len; _tb_q++)\n" +
        "                        {\n" +
        "                            buffer_write(tb_buf, buffer_u8, buffer_peek(tb_rx, _tb_hdr + _tb_q, buffer_u8));\n" +
        "                        }\n" +
        "                        network_send_raw(tb_sock, tb_buf, buffer_tell(tb_buf));\n" +
        "                    }\n" +
        "                    else if (_tb_op == 8)\n" +
        "                    {\n" +
        "                        tb_state = 0;\n" +
        "                        tb_retry_at = current_time + 3000;\n" +
        "                    }\n" +
        "                }\n" +
        "            }\n" +
        "            if (_tb_used > 0)\n" +
        "            {\n" +
        "                var _tb_rest = tb_rx_len - _tb_used;\n" +
        "                if (_tb_rest > 0)\n" +
        "                {\n" +
        "                    var _tb_tmp = buffer_create(_tb_rest, buffer_fixed, 1);\n" +
        "                    buffer_copy(tb_rx, _tb_used, _tb_rest, _tb_tmp, 0);\n" +
        "                    buffer_copy(_tb_tmp, 0, _tb_rest, tb_rx, 0);\n" +
        "                    buffer_delete(_tb_tmp);\n" +
        "                }\n" +
        "                tb_rx_len = _tb_rest;\n" +
        "                _tb_go = tb_rx_len > 0 && tb_state != 0;\n" +
        "            }\n" +
        "            if (_tb_text != \"\")\n" +
        "            {\n" +
        TbIndent(ToyDirectHandleJson, 4) +
        "            }\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    private sealed record ToyDirectTarget(UndertaleData Data, UndertaleGameObject Obj, string CreateText, string DrawText, string? ExistingAsyncText);

    private static (ToyDirectTarget? Target, bool AlreadyPatched, string? Problem) ReadToyDirectTarget(string dataWinPath)
    {
        UndertaleData data;
        using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
        {
            data = UndertaleIO.Read(stream);
        }

        var obj = data.GameObjects.ByName("oFutaMatingPress");
        var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
        var drawCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == DrawEventName);
        if (obj is null || createCode is null || drawCode is null)
        {
            return (null, false, "Not a compatible game (missing oFutaMatingPress).");
        }

        var globalContext = new GlobalDecompileContext(data);
        var settings = data.ToolInfo.DecompilerSettings;
        string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();
        if (createText.Contains("tb_sock"))
        {
            return (null, true, null);
        }
        string drawText = new DecompileContext(globalContext, drawCode, settings).DecompileToString();
        if (!createText.Contains(CreateMarker) || !drawText.Contains(DrawMarker))
        {
            return (null, false, "Couldn't find the thrust code this patch hooks into - this game's version may not be compatible.");
        }
        if (!drawText.Contains("title == true"))
        {
            return (null, false, "Couldn't find the title screen code in the Draw event to place the toy status on.");
        }

        var asyncCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == ToyDirectAsyncEventName);
        string? asyncText = asyncCode is null ? null : new DecompileContext(globalContext, asyncCode, settings).DecompileToString();
        return (new ToyDirectTarget(data, obj, createText, drawText, asyncText), false, null);
    }

    public static (bool Compatible, bool AlreadyPatched, string Detail) CheckToyDirectStatus(string dataWinPath)
    {
        try
        {
            var (target, alreadyPatched, problem) = ReadToyDirectTarget(dataWinPath);
            if (alreadyPatched)
            {
                return (true, true, "Already patched.");
            }
            return target is null ? (false, false, problem!) : (true, false, "Compatible, not yet patched.");
        }
        catch (Exception ex)
        {
            return (false, false, $"Couldn't check: {ex.Message}");
        }
    }

    public static PatchOutcome PatchToyDirect(string dataWinPath)
    {
        try
        {
            string backupPath = dataWinPath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(dataWinPath, backupPath);
            }

            var (target, alreadyPatched, problem) = ReadToyDirectTarget(dataWinPath);
            if (alreadyPatched)
            {
                return new PatchOutcome(PatchResult.AlreadyPatched, "The built-in toy client is already patched in - nothing to do.");
            }
            if (target is null)
            {
                return new PatchOutcome(PatchResult.NotSupported, problem!);
            }

            var data = target.Data;
            string createText = target.CreateText.Replace(CreateMarker, CreateMarker + ToyDirectCreateBlock);
            string drawText = ToyDirectDrawTop
                + target.DrawText.Replace(DrawMarker, DrawMarker + ToyDirectSceneStamp)
                + ToyDirectDrawBottom;

            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };
            importGroup.QueueReplace(CreateEventName, createText);
            importGroup.QueueReplace(DrawEventName, drawText);
            // The HMV patch owns an Async Networking event too; if it is there, share it.
            importGroup.QueueReplace(ToyDirectAsyncEventName, (target.ExistingAsyncText ?? "") + ToyDirectAsyncCode);
            importGroup.Import();

            if (target.ExistingAsyncText is null)
            {
                var newCode = data.Code.ByName(ToyDirectAsyncEventName);
                if (newCode is null)
                {
                    return new PatchOutcome(PatchResult.Error, "The async-networking code entry wasn't created as expected.");
                }
                var newEvent = new UndertaleGameObject.Event { EventSubtype = ToyDirectAsyncNetworkingSubtype };
                newEvent.Actions.Add(new UndertaleGameObject.EventAction
                {
                    LibID = 1,
                    ID = 603,
                    Kind = 7,
                    UseRelative = false,
                    IsQuestion = false,
                    UseApplyTo = false,
                    ExeType = 2,
                    ActionName = null,
                    ArgumentCount = 0,
                    Who = -1,
                    Relative = false,
                    IsNot = false,
                    UnknownAlwaysZero = 0,
                    CodeId = newCode,
                });
                target.Obj.Events[(int)EventType.Other].Add(newEvent);
            }

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            return new PatchOutcome(PatchResult.Patched,
                "Built-in toy client patched successfully! On Android the game now connects to Intiface Central " +
                "itself (ws://127.0.0.1:12345) - no separate bridge needed. On PC it stays off unless a file " +
                $"named toy_direct.txt is next to the game. A backup of the original was saved as:\n{backupPath}");
        }
        catch (Exception ex)
        {
            return new PatchOutcome(PatchResult.Error, $"Something went wrong while patching the built-in toy client: {ex.Message}");
        }
    }

    // ================================================================================
    // CUSTOM MOD SYSTEM DETECTION - read-only, no patching. Two genuinely different custom-
    // character systems exist across builds of this game, confirmed by directly decompiling both:
    //   - Vanilla Wife's Bedroom has its OWN system (func_load_custom/func_set_custom_lover/
    //     func_set_custom_partner) - one flat "custom/<name>/" folder, auto-detecting each
    //     subfolder's type by which data file is inside (custom_data.futa/.spouse/.bedroom).
    //   - ModRoom replaced this with a different system (check_custom_futa/check_custom_wife) -
    //     separate "custom_futas/"/"custom_wives/"/"custom_bedrooms/" folders, type declared by
    //     which folder a character sits in rather than by file extension.
    // The underlying per-character FILES turn out to be compatible between the two (same INI
    // fields, same simple sprite filenames) - confirmed by reading vanilla's func_load_custom
    // field-by-field against real ModRoom community packs already on disk. So knowing which
    // system a given data file uses is the key input for a folder-restructuring converter
    // (see ToyLauncherQt's Mods tab) rather than needing any deep content translation.
    public enum CustomModSystem
    {
        Vanilla,
        ModRoomStyle,
        Unknown,
    }

    public static (bool Compatible, CustomModSystem System, string Detail) CheckCustomModSystem(string dataWinPath)
    {
        try
        {
            UndertaleData data;
            using (var stream = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            var createCode = data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == CreateEventName);
            if (createCode is null)
            {
                return (false, CustomModSystem.Unknown, "Not a compatible game (missing oFutaMatingPress).");
            }

            var globalContext = new GlobalDecompileContext(data);
            var settings = data.ToolInfo.DecompilerSettings;
            string createText = new DecompileContext(globalContext, createCode, settings).DecompileToString();

            bool hasModRoomFolders = createText.Contains("custom_futas");
            bool hasVanillaLoader = createText.Contains("func_set_custom_lover");

            if (hasModRoomFolders)
            {
                return (true, CustomModSystem.ModRoomStyle,
                    "ModRoom-style: separate custom_futas/custom_wives/custom_bedrooms folders, type declared by folder.");
            }
            if (hasVanillaLoader)
            {
                return (true, CustomModSystem.Vanilla,
                    "Vanilla-style: single custom/ folder, type auto-detected per-subfolder by data file extension (.futa/.spouse/.bedroom).");
            }
            return (true, CustomModSystem.Unknown,
                "Neither known custom-mod system was recognized - this may be a different fork with its own scheme (don't assume compatibility).");
        }
        catch (Exception ex)
        {
            return (false, CustomModSystem.Unknown, $"Couldn't check: {ex.Message}");
        }
    }
}
