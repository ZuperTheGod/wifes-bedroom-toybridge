using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using Underanalyzer.Decompiler;

namespace GamePatcherCli;

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
            if (!createText.Contains(MenuScrollOriginal))
            {
                return (false, false,
                    "Couldn't find the expected mouse-wheel menu code (menu_scroll_update) - this is specific " +
                    "to ModRoom's own right-click/scroll-wheel controls, and may not apply to this game/version.");
            }
            return (true, false, "Compatible, not yet patched.");
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
            if (!createText.Contains(MenuScrollOriginal))
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "Couldn't find the expected mouse-wheel menu code (menu_scroll_update) - this is specific to " +
                    "ModRoom's own right-click/scroll-wheel controls, and may not apply to this game/version.");
            }

            string stepText = new DecompileContext(globalContext, stepCode, settings).DecompileToString();
            if (!stepText.Contains(RightClickMarker1) || !stepText.Contains(RightClickMarker2))
            {
                return new PatchOutcome(PatchResult.NotSupported,
                    "Couldn't find the expected right-click code in the Step event - this game's version may not be compatible.");
            }

            createText = createText.Replace(TouchCreateMarker, TouchCreateMarker + TouchCreatePatch);
            createText = createText.Replace(MenuScrollOriginal, MenuScrollReplacement);

            // Best-effort, not required: if this exact code isn't found (a different ModRoom
            // version, say), the touch-input fix above still applies fine on its own - this just
            // silently skips the custom-character-discovery fix rather than failing the whole
            // patch over it.
            bool futaScanPatched = createText.Contains(CustomFutaScanOriginal);
            if (futaScanPatched) createText = createText.Replace(CustomFutaScanOriginal, CustomFutaScanReplacement);
            bool wifeScanPatched = createText.Contains(CustomWifeScanOriginal);
            if (wifeScanPatched) createText = createText.Replace(CustomWifeScanOriginal, CustomWifeScanReplacement);

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

            stepText = stepText.Replace(RightClickMarker1, RightClickReplacement1);
            stepText = stepText.Replace(RightClickMarker2, RightClickReplacement2);
            if (pillMenuPatched) stepText = stepText.Replace(PillMenuScrollUpdateMarker, PillMenuScrollUpdateReplacement);
            stepText = TouchStepPrepend + stepText;

            var importGroup = new CodeImportGroup(data) { AutoCreateAssets = false };
            importGroup.QueueReplace(CreateEventName, createText);
            importGroup.QueueReplace("gml_Object_oFutaMatingPress_Step_0", stepText);
            if (touchDrawCode is not null) importGroup.QueueReplace(DrawEventName, drawText!);
            importGroup.Import();

            using (var outStream = new FileStream(dataWinPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(outStream, data);
            }

            string scanNote = (futaScanPatched || wifeScanPatched)
                ? $" Also fixed custom character discovery on Android (futas: {futaScanPatched}, wives: {wifeScanPatched})."
                : " Custom character discovery code didn't match what was expected, so that part was skipped - touch controls were still patched.";
            string pillNote = pillMenuPatched
                ? " Pill menu is now laid out as two columns of 7 (no scrolling needed, nothing can run off-screen)."
                : " Pill menu code didn't match what was expected (or this game has no pill menu), so that part was skipped.";
            return new PatchOutcome(PatchResult.Patched,
                $"Touch controls patched successfully!{scanNote}{pillNote} A backup of the original was saved as:\n{backupPath}");
        }
        catch (Exception ex)
        {
            return new PatchOutcome(PatchResult.Error, $"Something went wrong while patching touch controls: {ex.Message}");
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
