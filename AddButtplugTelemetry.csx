// AddButtplugTelemetry.csx
//
// Adds a small UDP telemetry broadcast to Wife's Bedroom's oFutaMatingPress object, so an
// external companion program (ButtplugBridge, see the accompanying README) can drive
// Buttplug.io / Intiface Central toys in sync with the game's "thrust" animation.
//
// This script only ever ADDS a few lines after two existing lines it recognizes - it never
// replaces or ships any of the original game's code, so it's safe to redistribute this
// script (and ButtplugBridge.exe) on their own, separately from the game itself.
//
// Usage (in UndertaleModTool): open your own copy of data.win, then Scripts > run this file.
// It's idempotent - running it again on an already-patched file is a safe no-op.
//
// What it does, in plain terms:
//   - In oFutaMatingPress's Create event: opens a UDP socket and a small buffer once.
//   - In oFutaMatingPress's Draw event, right after the game computes "thrust" for this frame:
//     every ~33ms, sends "thrust,thrust_prev,thrust_speed,thrust_strength,insert,orgasm" as a
//     UDP text packet to 127.0.0.1:45735. That's it - no other game behavior is touched.

using System;
using System.Linq;

EnsureDataLoaded();

UndertaleModLib.Models.UndertaleCode createCode = Data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == "gml_Object_oFutaMatingPress_Create_0");
UndertaleModLib.Models.UndertaleCode drawCode = Data.Code.FirstOrDefault(c => c is not null && c.Name?.Content == "gml_Object_oFutaMatingPress_Draw_0");

if (createCode is null || drawCode is null)
{
    ScriptError("Could not find gml_Object_oFutaMatingPress_Create_0 / _Draw_0 in this data file.\n" +
                "This script targets Wife's Bedroom specifically - it won't work on other games.");
    return;
}

UndertaleModLib.Decompiler.GlobalDecompileContext globalContext = new(Data);
Underanalyzer.Decompiler.IDecompileSettings settings = Data.ToolInfo.DecompilerSettings;

string createText = new Underanalyzer.Decompiler.DecompileContext(globalContext, createCode, settings).DecompileToString();
string drawText = new Underanalyzer.Decompiler.DecompileContext(globalContext, drawCode, settings).DecompileToString();

bool createAlready = createText.Contains("buttplug_socket");
bool drawAlready = drawText.Contains("buttplug_socket");

if (createAlready && drawAlready)
{
    ScriptMessage("Buttplug telemetry patch is already applied to this data file. Nothing to do.");
    return;
}

const string createMarker = "thrust_time = 0;";
const string createPatch = "\nbuttplug_socket = network_create_socket(network_socket_udp);\nbuttplug_buffer = buffer_create(256, buffer_grow, 1);\nbuttplug_last_send = 0;\nbuttplug_port = 45735;";

if (!createAlready)
{
    if (!createText.Contains(createMarker))
    {
        ScriptError("Expected code not found in the Create event (game version mismatch?). Aborting without changes.");
        return;
    }
    createText = createText.Replace(createMarker, createMarker + createPatch);
}

const string drawMarker = "thrust_prev = thrust;";
const string drawPatch = "\nif (buttplug_socket >= 0)\n{\n    var _bp_now = current_time;\n    if (_bp_now - buttplug_last_send >= 33)\n    {\n        buttplug_last_send = _bp_now;\n        var _bp_insert = 0;\n        if (insert)\n        {\n            _bp_insert = 1;\n        }\n        var _bp_orgasm = 0;\n        if (orgasm)\n        {\n            _bp_orgasm = 1;\n        }\n        var _bp_msg = string(thrust) + \",\" + string(thrust_prev) + \",\" + string(thrust_speed) + \",\" + string(thrust_strength) + \",\" + string(_bp_insert) + \",\" + string(_bp_orgasm);\n        buffer_seek(buttplug_buffer, buffer_seek_start, 0);\n        buffer_write(buttplug_buffer, buffer_text, _bp_msg);\n        network_send_udp_raw(buttplug_socket, \"127.0.0.1\", buttplug_port, buttplug_buffer, buffer_tell(buttplug_buffer));\n    }\n}";

if (!drawAlready)
{
    if (!drawText.Contains(drawMarker))
    {
        ScriptError("Expected code not found in the Draw event (game version mismatch?). Aborting without changes.");
        return;
    }
    drawText = drawText.Replace(drawMarker, drawMarker + drawPatch);
}

UndertaleModLib.Compiler.CodeImportGroup importGroup = new(Data) { AutoCreateAssets = false };
if (!createAlready) importGroup.QueueReplace("gml_Object_oFutaMatingPress_Create_0", createText);
if (!drawAlready) importGroup.QueueReplace("gml_Object_oFutaMatingPress_Draw_0", drawText);
importGroup.Import();

ScriptMessage("Done! Buttplug telemetry patch applied.\n\n" +
              "Now: File > Save to write data.win, then run ButtplugBridge.exe (with Intiface Central's " +
              "server running) before or during play.");
