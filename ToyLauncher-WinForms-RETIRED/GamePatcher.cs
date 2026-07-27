using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using Underanalyzer.Decompiler;

namespace ButtplugLauncher;

// Same marker-based patch as AddButtplugTelemetry.csx / the utmt-tool "markerpatch" command,
// embedded directly so the launcher can offer "Patch a Game..." without requiring UndertaleModTool
// or any command-line steps from the person running it. Only ever ADDS a few lines after two
// existing lines it recognizes in oFutaMatingPress - never touches anything else in the game.
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
}
