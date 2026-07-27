// ButtplugBridge - connects Wife's Bedroom's in-game "thrust" telemetry to Buttplug.io / Intiface Central.
//
// The game sends a small UDP text packet ~30x/sec while a sex scene is active:
//   "<thrust>,<thrust_prev>,<thrust_speed>,<thrust_strength>,<insert 0|1>,<orgasm 0|1>"
// This process listens for that on 127.0.0.1, maps it to vibration intensity / stroke position,
// and forwards it to whatever devices are connected via Intiface Central.
//
// Usage: ButtplugBridge.exe [--udp-port 45735] [--intiface ws://127.0.0.1:12345] [--profile <name>] [--list-profiles]
//
// Intensity profiles: see profiles.json (created next to this exe on first run) and PROFILES.txt.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Buttplug.Client;
using Buttplug.Core.Messages;

int udpPort = 45735;
string intifaceUri = "ws://127.0.0.1:12345";
bool verbose = false;
string profileName = "default";
bool listProfiles = false;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--udp-port" && i + 1 < args.Length) udpPort = int.Parse(args[++i]);
    else if (args[i] == "--intiface" && i + 1 < args.Length) intifaceUri = args[++i];
    else if (args[i] == "--verbose") verbose = true;
    else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[++i];
    else if (args[i] == "--list-profiles") listProfiles = true;
}

// ---- Tunables: adjust these to change how "thrust" feels on your toys ----
const double VibeDeltaScale = 7.0;       // how strongly thrust movement drives vibration intensity
const double VibeSmoothing = 0.35;       // 0..1, higher = snappier / more jitter, lower = smoother
const double OrgasmFloor = 0.45;         // minimum vibe intensity while the orgasm animation plays
const double OrgasmPulseAmplitude = 0.25; // added pulsing on top of the floor during orgasm
const double OrgasmPulseHz = 3.0;
const double MinStrokeDurationMs = 100;  // fastest a linear/stroker device will be told to move
const double MaxStrokeDurationMs = 1800; // slowest
const int TelemetryTimeoutMs = 500;      // if no packet arrives for this long, assume scene ended -> stop devices
const int ControlHz = 30;
// ----------------------------------------------------------------------------

string profilesPath = Path.Combine(AppContext.BaseDirectory, "profiles.json");
Dictionary<string, ProfileRange> profiles = ProfileStore.LoadOrCreate(profilesPath);

if (listProfiles)
{
    Console.WriteLine($"Profiles in {profilesPath}:");
    foreach (var kv in profiles)
    {
        Console.WriteLine($"  {kv.Key,-10} {kv.Value.Min:0.#}% - {kv.Value.Max:0.#}%");
    }
    return 0;
}

if (!profiles.TryGetValue(profileName, out ProfileRange? activeProfile))
{
    Console.WriteLine($"Unknown profile \"{profileName}\". Available: {string.Join(", ", profiles.Keys)}");
    Console.WriteLine("Falling back to \"default\".");
    activeProfile = profiles["default"];
    profileName = "default";
}

var telemetry = new TelemetryState();
using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, udpPort));

Console.WriteLine($"ButtplugBridge starting.");
Console.WriteLine($"  Listening for game telemetry on udp://127.0.0.1:{udpPort}");
Console.WriteLine($"  Connecting to Intiface Central at {intifaceUri}");
Console.WriteLine($"  Profile: {profileName} ({activeProfile.Min:0.#}% - {activeProfile.Max:0.#}%) - from {profilesPath}");
Console.WriteLine("  Press Ctrl+C to quit.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var udpListenTask = ListenForTelemetryAsync(udpClient, telemetry, cts.Token);
var buttplugTask = RunButtplugLoopAsync(intifaceUri, telemetry, cts.Token, verbose, activeProfile);

await Task.WhenAll(udpListenTask, buttplugTask);
Console.WriteLine("ButtplugBridge stopped.");
return 0;

static async Task ListenForTelemetryAsync(UdpClient udp, TelemetryState state, CancellationToken token)
{
    try
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            string text = System.Text.Encoding.ASCII.GetString(result.Buffer);
            string[] parts = text.Split(',');
            if (parts.Length < 6) continue;

            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double thrust) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double strength) &&
                int.TryParse(parts[4], out int insertFlag) &&
                int.TryParse(parts[5], out int orgasmFlag))
            {
                state.Update(thrust, speed, strength, insertFlag != 0, orgasmFlag != 0);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[telemetry] listener stopped: {ex.Message}");
    }
}

static async Task RunButtplugLoopAsync(string uri, TelemetryState state, CancellationToken token, bool verbose, ProfileRange profile)
{
    double profileMin = profile.Min / 100.0;
    double profileMax = profile.Max / 100.0;
    double Remap(double v01) => profileMin + Math.Clamp(v01, 0.0, 1.0) * (profileMax - profileMin);

    var client = new ButtplugClient("WifesBedroom-ButtplugBridge");
    var connectedDevices = new List<ButtplugClientDevice>();

    client.DeviceAdded += (_, e) =>
    {
        connectedDevices.Add(e.Device);
        Console.WriteLine($"[buttplug] device connected: {e.Device.DisplayName}"
            + $" (vibrate={e.Device.HasOutput(OutputType.Vibrate)}, oscillate={e.Device.HasOutput(OutputType.Oscillate)}, "
            + $"linear={e.Device.HasOutput(OutputType.HwPositionWithDuration) || e.Device.HasOutput(OutputType.Position)})");
    };
    client.DeviceRemoved += (_, e) =>
    {
        connectedDevices.RemoveAll(d => d.Index == e.Device.Index);
        Console.WriteLine($"[buttplug] device disconnected: {e.Device.DisplayName}");
    };
    client.ServerDisconnect += (_, _) => Console.WriteLine("[buttplug] server disconnected.");
    client.ErrorReceived += (_, e) => Console.WriteLine($"[buttplug] error: {e.Exception.Message}");

    bool wasIdle = true;
    double smoothedVibe = 0;
    double lastSampledThrust = 0;
    bool haveLastSample = false;
    double pulsePhase = 0;

    while (!token.IsCancellationRequested)
    {
        if (!client.Connected)
        {
            try
            {
                await client.ConnectAsync(new ButtplugWebsocketConnector(new Uri(uri)), token);
                Console.WriteLine("[buttplug] connected to Intiface Central.");
                await client.StartScanningAsync(token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[buttplug] not connected ({ex.Message}). Retrying in 5s... " +
                                   "(is Intiface Central running with the server started?)");
                try { await Task.Delay(5000, token); } catch (OperationCanceledException) { break; }
                continue;
            }
        }

        var snapshot = state.Snapshot();
        bool isFresh = (DateTime.UtcNow - snapshot.ReceivedAtUtc).TotalMilliseconds < TelemetryTimeoutMs;

        if (!isFresh)
        {
            if (!wasIdle)
            {
                wasIdle = true;
                haveLastSample = false;
                smoothedVibe = 0;
                await StopAllAsync(connectedDevices, token);
                Console.WriteLine("[bridge] no telemetry - toys stopped, waiting for scene...");
            }
        }
        else
        {
            wasIdle = false;

            double delta = haveLastSample ? Math.Abs(snapshot.Thrust - lastSampledThrust) : 0;
            lastSampledThrust = snapshot.Thrust;
            haveLastSample = true;

            double targetVibe = Math.Clamp(delta * VibeDeltaScale, 0.0, 1.0);

            if (snapshot.Orgasm)
            {
                pulsePhase += (2 * Math.PI * OrgasmPulseHz) / ControlHz;
                double pulse = OrgasmPulseAmplitude * (0.5 + 0.5 * Math.Sin(pulsePhase));
                targetVibe = Math.Max(targetVibe, OrgasmFloor + pulse);
            }
            else
            {
                pulsePhase = 0;
            }

            targetVibe = Math.Clamp(targetVibe, 0.0, 1.0);
            smoothedVibe += (targetVibe - smoothedVibe) * VibeSmoothing;

            double strokePos = Math.Clamp(snapshot.Thrust, 0.0, 1.0);
            double cycleMs = (360.0 / Math.Max(snapshot.Speed, 0.5)) * (1000.0 / 60.0);
            uint strokeDurationMs = (uint)Math.Clamp(cycleMs / 2.0, MinStrokeDurationMs, MaxStrokeDurationMs);

            // smoothedVibe/strokePos stay in raw 0..1 space for the EMA/state logic above;
            // only the values actually sent to devices get squeezed into the active profile's range.
            double outputVibe = Remap(smoothedVibe);
            double outputStroke = Remap(strokePos);

            if (snapshot.Insert)
            {
                await SendToDevicesAsync(connectedDevices, outputVibe, outputStroke, strokeDurationMs, token);
            }
            else if (smoothedVibe > 0.01)
            {
                smoothedVibe = 0;
                await StopAllAsync(connectedDevices, token);
            }

            if (verbose)
            {
                Console.WriteLine($"[bridge] thrust={snapshot.Thrust:F3} delta={delta:F3} vibe={smoothedVibe:F3}->{outputVibe:F3} " +
                                   $"stroke={strokePos:F3}->{outputStroke:F3}@{strokeDurationMs}ms insert={snapshot.Insert} orgasm={snapshot.Orgasm}");
            }
        }

        try { await Task.Delay(1000 / ControlHz, token); } catch (OperationCanceledException) { break; }
    }

    try
    {
        await StopAllAsync(connectedDevices, CancellationToken.None);
        if (client.Connected) await client.DisconnectAsync();
    }
    catch { /* best-effort shutdown */ }
}

static async Task SendToDevicesAsync(List<ButtplugClientDevice> devices, double vibeIntensity, double strokePos, uint strokeDurationMs, CancellationToken token)
{
    foreach (var device in devices)
    {
        try
        {
            if (device.HasOutput(OutputType.Vibrate))
            {
                await device.RunOutputAsync(DeviceOutput.Vibrate.Percent(vibeIntensity), token);
            }
            else if (device.HasOutput(OutputType.Oscillate))
            {
                await device.RunOutputAsync(DeviceOutput.Oscillate.Percent(vibeIntensity), token);
            }

            if (device.HasOutput(OutputType.HwPositionWithDuration))
            {
                await device.RunOutputAsync(DeviceOutput.PositionWithDuration.Percent(strokePos, strokeDurationMs), token);
            }
            else if (device.HasOutput(OutputType.Position))
            {
                await device.RunOutputAsync(DeviceOutput.Position.Percent(strokePos), token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[buttplug] failed to send to {device.DisplayName}: {ex.Message}");
        }
    }
}

static async Task StopAllAsync(List<ButtplugClientDevice> devices, CancellationToken token)
{
    foreach (var device in devices)
    {
        try { await device.StopAsync(token); }
        catch { /* device may have disconnected */ }
    }
}

class TelemetryState
{
    private readonly object _lock = new();
    private double _thrust, _speed, _strength;
    private bool _insert, _orgasm;
    private DateTime _receivedAtUtc = DateTime.MinValue;

    public void Update(double thrust, double speed, double strength, bool insert, bool orgasm)
    {
        lock (_lock)
        {
            _thrust = thrust;
            _speed = speed;
            _strength = strength;
            _insert = insert;
            _orgasm = orgasm;
            _receivedAtUtc = DateTime.UtcNow;
        }
    }

    public (double Thrust, double Speed, double Strength, bool Insert, bool Orgasm, DateTime ReceivedAtUtc) Snapshot()
    {
        lock (_lock)
        {
            return (_thrust, _speed, _strength, _insert, _orgasm, _receivedAtUtc);
        }
    }
}

// A profile squeezes the final vibe/stroke output into a percentage band (0-100), so e.g.
// "easy" never sends above 30%, "hard" never sends below 60%. See profiles.json / PROFILES.txt.
class ProfileRange
{
    public double Min { get; set; }
    public double Max { get; set; }
}

class ProfilesFile
{
    public Dictionary<string, ProfileRange> Profiles { get; set; } = new();
}

static class ProfileStore
{
    public static Dictionary<string, ProfileRange> LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, DefaultProfilesJson);
            Console.WriteLine($"[profiles] Created {path} with default profiles.");
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        try
        {
            string text = File.ReadAllText(path);
            ProfilesFile? file = JsonSerializer.Deserialize<ProfilesFile>(text, options);
            if (file?.Profiles is { Count: > 0 })
            {
                var result = new Dictionary<string, ProfileRange>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, ProfileRange> kv in file.Profiles)
                {
                    result[kv.Key] = kv.Value;
                }
                if (!result.ContainsKey("default"))
                {
                    result["default"] = new ProfileRange { Min = 0, Max = 100 };
                }
                return result;
            }
            Console.WriteLine($"[profiles] {path} has no profiles defined - using built-in defaults instead.");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[profiles] Couldn't parse {path}: {ex.Message}");
            Console.WriteLine("[profiles] Using built-in defaults instead. Fix the file, or delete it to regenerate.");
        }

        return BuiltInDefaults();
    }

    private static Dictionary<string, ProfileRange> BuiltInDefaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = new ProfileRange { Min = 0, Max = 100 },
        ["easy"] = new ProfileRange { Min = 0, Max = 30 },
        ["mid"] = new ProfileRange { Min = 30, Max = 60 },
        ["hard"] = new ProfileRange { Min = 60, Max = 100 },
        ["mideasy"] = new ProfileRange { Min = 0, Max = 50 },
        ["midhard"] = new ProfileRange { Min = 50, Max = 100 },
        ["custom"] = new ProfileRange { Min = 0, Max = 100 },
    };

    private const string DefaultProfilesJson = """
    {
      // ButtplugBridge intensity profiles.
      //
      // Each profile squeezes the toy's output into a percentage range (0-100):
      // "min" = weakest moment ever sent, "max" = strongest moment ever sent.
      // Everything in between (how fast/hard the game's thrust is moving, the
      // orgasm pulse, etc.) still happens exactly as tuned - a profile just
      // rescales the final result into this band.
      //
      // Pick one at launch with:  ButtplugBridge.exe --profile easy
      // See what's available with: ButtplugBridge.exe --list-profiles
      //
      // "custom" is yours to edit freely - change its min/max to whatever you
      // like (0-100, min must be less than max), or add entirely new named
      // profiles below by copying the pattern.

      "profiles": {
        "default":  { "min": 0,  "max": 100 },
        "easy":     { "min": 0,  "max": 30  },
        "mid":      { "min": 30, "max": 60  },
        "hard":     { "min": 60, "max": 100 },
        "mideasy":  { "min": 0,  "max": 50  },
        "midhard":  { "min": 50, "max": 100 },
        "custom":   { "min": 0,  "max": 100 }
      }
    }
    """;
}
