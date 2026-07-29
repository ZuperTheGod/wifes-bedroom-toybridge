// ApkPatcher - patches the Android build of a compatible game to add Buttplug.io toy telemetry,
// entirely automated: finds the game's data file inside the APK, patches it (same logic as
// GamePatcher/the ToyLauncher GUI/AddButtplugTelemetry.csx), repackages the APK, and re-signs it
// (installing a modified APK always requires a fresh signature - the original developer's
// signature can't carry over). Never touches or redistributes anyone else's APK - you point this
// at YOUR OWN copy, and it produces a new file next to it.
//
// --replace-data lets you swap in a DIFFERENT data file (e.g. a PC mod's data.win, like ModRoom)
// before patching, instead of patching the APK's own data file in place. This is how you get a PC
// mod's content running on Android: you supply both your own copy of the official APK and your
// own copy of the mod's data.win, and this produces an APK with that mod's content plus toy
// telemetry. Still never bundles or redistributes anything - both inputs are files you already
// have. Whenever the mod updates, just re-run this against the new data.win to get a fresh APK.
//
// --include-mods lets you bundle a PC mod's external content folders (custom_wives, custom_futas,
// custom_bedrooms, dialogue_packs, texture_packs) into the APK too. This is required for those to
// work at all on Android - confirmed (by patching in a one-line diagnostic that logs
// working_directory) that GameMaker's working_directory on Android resolves to the APK's own
// bundled assets/ folder, a read-only location baked in at build time - there is no writable
// "next to the exe" folder the way there is on PC, so anything the game would normally read from
// those folders has to be packaged into the APK itself. Point --include-mods at whatever folder
// directly contains those subfolders (e.g. your ModRoom install folder) and each is copied in
// under assets/<foldername>/... whichever of the five exist. Every caller supplies their own mod
// content this way - nothing about anyone's specific mods ships with this tool.
//
// Requires a JDK on this machine (for `java`/`keytool` - see https://adoptium.net if you don't
// have one). Does NOT require the Android SDK - apksigner.jar (bundled alongside this tool) is
// the only Android-specific piece needed, and it runs under a plain JDK.
//
// Usage: ApkPatcher <path-to-game.apk> [--replace-data <path-to-data.win>] [--include-mods <path>] [--hmv | --touch-controls] [--out <output.apk>] [--yes]
//
// --hmv and --touch-controls each apply a DIFFERENT patch instead of the default toy telemetry
// one - they're mutually exclusive with each other and with the default in a single run. To
// combine more than one (e.g. toy telemetry + touch controls), run this tool once per patch,
// feeding each run's output back in as the next run's --replace-data - the patches are additive
// and idempotent, so this composes safely (confirmed directly: applying --hmv on top of an
// already toy-telemetry-patched file works correctly).

using System.IO.Compression;
using System.Diagnostics;
using ApkPatcher;

string? apkPath = null;
string? outPath = null;
string? replaceDataPath = null;
string? includeModsPath = null;
bool skipConfirm = false;
bool hmvMode = false;
bool touchControlsMode = false;
bool customAltsMode = false;

foreach (string arg in args)
{
    if (arg is "-h" or "--help") { PrintUsage(); return 0; }
    else if (arg == "--yes") skipConfirm = true;
    else if (arg == "--hmv") hmvMode = true;
    else if (arg == "--touch-controls") touchControlsMode = true;
    else if (arg == "--custom-alts") customAltsMode = true;
    else if (arg == "--out") { /* consumed below */ }
    else if (arg == "--replace-data") { /* consumed below */ }
    else if (arg == "--include-mods") { /* consumed below */ }
    else if (apkPath is null) apkPath = arg;
}
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length) outPath = args[i + 1];
    if (args[i] == "--replace-data" && i + 1 < args.Length) replaceDataPath = args[i + 1];
    if (args[i] == "--include-mods" && i + 1 < args.Length) includeModsPath = args[i + 1];
}

if (replaceDataPath is not null && !File.Exists(replaceDataPath))
{
    Console.WriteLine($"File not found: {replaceDataPath}");
    return 1;
}

if (includeModsPath is not null && !Directory.Exists(includeModsPath))
{
    Console.WriteLine($"Directory not found: {includeModsPath}");
    return 1;
}

string[] modFolderNames = ["custom_wives", "custom_futas", "custom_bedrooms", "dialogue_packs", "texture_packs"];

if (apkPath is null)
{
    PrintUsage();
    return 1;
}

if (!File.Exists(apkPath))
{
    Console.WriteLine($"File not found: {apkPath}");
    return 1;
}

outPath ??= Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(apkPath)) ?? ".",
    Path.GetFileNameWithoutExtension(apkPath) + "-toybridge.apk");

string toolDir = AppContext.BaseDirectory;
string apksignerJar = Path.Combine(toolDir, "apksigner.jar");
if (!File.Exists(apksignerJar))
{
    Console.WriteLine($"apksigner.jar not found next to this tool ({apksignerJar}) - reinstall/redownload ApkPatcher.");
    return 1;
}

string? java = FindJava();
if (java is null)
{
    Console.WriteLine("Couldn't find a Java installation (`java`/`keytool`). This tool needs a JDK to sign the");
    Console.WriteLine("patched APK - get one for free from https://adoptium.net (any recent version works),");
    Console.WriteLine("then make sure it's on your PATH, and try again.");
    return 1;
}
string javaBinDir = Path.GetDirectoryName(java)!;
string keytool = Path.Combine(javaBinDir, OperatingSystem.IsWindows() ? "keytool.exe" : "keytool");

Console.WriteLine($"APK: {apkPath}");
Console.WriteLine($"Output: {outPath}");

// ---- 1. Find and patch the game's data file inside the APK ----
string tempDataFile = Path.GetTempFileName();
string dataEntryName;
try
{
    using (var archive = ZipFile.OpenRead(apkPath))
    {
        var dataEntry = FindGameDataEntry(archive);
        if (dataEntry is null)
        {
            Console.WriteLine("Couldn't find a GameMaker data file (data.win/game.droid/game.unx/game.ios) inside this APK.");
            return 1;
        }
        dataEntryName = dataEntry.FullName;
        Console.WriteLine($"Found game data file inside APK: {dataEntryName}");
        if (replaceDataPath is not null)
        {
            Console.WriteLine($"Replacing it with: {replaceDataPath}");
            File.Copy(replaceDataPath, tempDataFile, overwrite: true);
        }
        else
        {
            dataEntry.ExtractToFile(tempDataFile, overwrite: true);
        }
    }

    var status = hmvMode ? GamePatcher.CheckHmvStatus(tempDataFile)
        : touchControlsMode ? GamePatcher.CheckTouchControlsStatus(tempDataFile)
        : customAltsMode ? GamePatcher.CheckCustomAltsStatus(tempDataFile)
        : GamePatcher.CheckStatus(tempDataFile);
    Console.WriteLine($"Compatible: {status.Compatible}   Already patched: {status.AlreadyPatched}   ({status.Detail})");
    if (!status.Compatible)
    {
        return 1;
    }

    if (!status.AlreadyPatched)
    {
        if (!skipConfirm)
        {
            string what = hmvMode ? "HMV mode" : touchControlsMode ? "touch controls" : customAltsMode ? "custom alts" : "toy telemetry";
            string action = replaceDataPath is not null
                ? $"This will build a new APK using the data file you gave it, with {what} added, re-signed, saved at:\n  {outPath}"
                : $"This will create a patched ({what}), re-signed copy at:\n  {outPath}";
            Console.Write($"{action}\nYour original APK is never modified. Continue? [y/N] ");
            string? answer = Console.ReadLine();
            if (string.IsNullOrEmpty(answer) || !answer.Trim().StartsWith('y'))
            {
                Console.WriteLine("Cancelled - nothing changed.");
                return 1;
            }
        }

        var outcome = hmvMode ? GamePatcher.PatchHmv(tempDataFile)
            : touchControlsMode ? GamePatcher.PatchTouchControls(tempDataFile)
            : customAltsMode ? GamePatcher.PatchCustomAlts(tempDataFile)
            : GamePatcher.Patch(tempDataFile);
        Console.WriteLine($"{outcome.Result}: {outcome.Message}");
        if (outcome.Result != GamePatcher.PatchResult.Patched)
        {
            return 1;
        }
    }
    else
    {
        Console.WriteLine("Already patched - will still repackage/re-sign in case you want a fresh copy.");
    }

    // ---- 2. Copy the APK and swap in the patched data file + strip old signature ----
    File.Copy(apkPath, outPath, overwrite: true);

    using (var archive = ZipFile.Open(outPath, ZipArchiveMode.Update))
    {
        var oldDataEntry = archive.GetEntry(dataEntryName);
        oldDataEntry?.Delete();
        var newEntry = archive.CreateEntry(dataEntryName, CompressionLevel.Optimal);
        using (var entryStream = newEntry.Open())
        using (var patchedFileStream = File.OpenRead(tempDataFile))
        {
            patchedFileStream.CopyTo(entryStream);
        }

        foreach (var entry in archive.Entries.ToList())
        {
            if (!entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
            string upper = entry.FullName.ToUpperInvariant();
            if (upper.EndsWith(".SF") || upper.EndsWith(".RSA") || upper.EndsWith(".DSA") || upper.EndsWith(".EC")
                || upper.EndsWith("MANIFEST.MF"))
            {
                entry.Delete();
            }
        }

        if (includeModsPath is not null)
        {
            // Folders whose mods live one-per-subfolder (custom_wives/custom_futas/texture_packs)
            // need a manifest listing those subfolder names - GameMaker's directory-ENUMERATION
            // functions (file_find_first) don't work against files bundled inside an APK on
            // Android (confirmed directly by reading ModRoom's actual scanning code and tracing
            // why custom characters never showed as available there), only opening a file by an
            // already-known name does. --touch-controls patches the game to read this manifest
            // instead of enumerating, when present - see GamePatcher.cs.
            string[] manifestFolderNames = ["custom_wives", "custom_futas", "texture_packs"];
            static string NormalizeAssetPath(string s) => s.ToLowerInvariant().Replace(' ', '_');

            int filesAdded = 0;
            foreach (string folderName in modFolderNames)
            {
                string sourceDir = Path.Combine(includeModsPath, folderName);
                if (!Directory.Exists(sourceDir)) continue;

                bool normalizeThisFolder = manifestFolderNames.Contains(folderName);
                foreach (string filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(sourceDir, filePath).Replace('\\', '/');
                    // GameMaker's Android asset-access layer normalizes paths before opening them,
                    // in ways file_exists() alone doesn't reflect if queried with the raw on-disk
                    // name - confirmed by diagnostic probe/logcat: (1) sprite_add() lowercases the
                    // path internally, so a mixed-case folder like "Phoebe" crashed with "Unable to
                    // get asset for file assets/custom_futas/phoebe/..."; (2) ANY space in the path
                    // gets replaced with an underscore before lookup (confirmed by every
                    // space-containing pack, e.g. "Momo Yaoyorozu", consistently failing
                    // file_exists() until the space was removed). Bundling these folders fully
                    // lowercase with spaces replaced by underscores sidesteps both mismatches.
                    if (normalizeThisFolder) relative = NormalizeAssetPath(relative);
                    string entryName = $"assets/{folderName}/{relative}";

                    archive.GetEntry(entryName)?.Delete();
                    var modEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var modEntryStream = modEntry.Open();
                    using var modFileStream = File.OpenRead(filePath);
                    modFileStream.CopyTo(modEntryStream);
                    filesAdded++;
                }

                // NOTE: custom_wives packs using the newer "custom_data.spouse" naming convention
                // (Meru, Momo Yaoyorozu, yoruichi, etc.) are NOT aliased to the old custom_data.wife
                // naming here, on purpose - a prior version of this tool did that, and it "worked" in
                // the sense that the packs became selectable, but rendered badly broken in practice.
                // Confirmed by reading ModRoom's own draw code: custom_wife_* sprites aren't simple
                // animations, they're fixed-layout layered rigs (frame 0 = separate boob-jiggle
                // overlay, frame 1/2 = ass overlay variants, frame 4 = tail overlay, a "cumflation"
                // belly-bulge frame, plus a full facial-expression atlas for the portrait - specific
                // hardcoded frame indices like "14 + blush_id" and "7 + eye_id" for eye/blush/mouth
                // variants). These newer-convention packs are simple single-image packs that were
                // never authored with this layered-rig/expression-atlas system in mind at all, and
                // have nowhere near enough frames for it - there is no file-renaming or frame-count
                // trick that fixes this, since the actual overlay artwork these packs would need
                // simply doesn't exist in them. Only custom_wife_template-style packs (already using
                // the old naming AND actually authored as a full compatible rig) work correctly via
                // the CUSTOM button - that's a genuine content limitation of these specific mod packs,
                // not a bug in this tool or in ModRoom's Android support.

                if (manifestFolderNames.Contains(folderName))
                {
                    string[] subfolderNames = Directory.GetDirectories(sourceDir)
                        .Select(Path.GetFileName)
                        .Where(n => n is not null)
                        .Select(n => NormalizeAssetPath(n!))
                        .ToArray();
                    if (subfolderNames.Length > 0)
                    {
                        string manifestEntryName = $"assets/{folderName}/_manifest.txt";
                        archive.GetEntry(manifestEntryName)?.Delete();
                        var manifestEntry = archive.CreateEntry(manifestEntryName, CompressionLevel.Optimal);
                        using (var manifestStream = manifestEntry.Open())
                        using (var writer = new StreamWriter(manifestStream))
                        {
                            foreach (string name in subfolderNames) writer.Write(name + "\n");
                        }
                        Console.WriteLine($"  ...and wrote a manifest listing its {subfolderNames.Length} subfolder(s) (needed for these to be selectable at all on Android).");
                    }
                }

                Console.WriteLine($"Bundled {folderName}/ into the APK.");
            }
            if (filesAdded == 0)
            {
                Console.WriteLine($"Warning: none of {string.Join(", ", modFolderNames)} were found under {includeModsPath} - nothing to bundle.");
            }
        }
    }
    Console.WriteLine("Repackaged APK with patched data file, stripped old signature.");

    // ---- 3. Get (or create) a signing key ----
    string keystorePath = Path.Combine(toolDir, "apkpatcher.keystore");
    string passwordPath = Path.Combine(toolDir, "apkpatcher.keystore.pass");
    string password = GetOrCreateKeystore(keytool, keystorePath, passwordPath);

    // ---- 4. Sign ----
    Console.WriteLine("Signing...");
    var signResult = RunProcess(java,
        $"-jar \"{apksignerJar}\" sign --ks \"{keystorePath}\" --ks-key-alias apkpatcherkey " +
        $"--ks-pass pass:{password} --key-pass pass:{password} \"{outPath}\"");
    if (signResult.ExitCode != 0)
    {
        Console.WriteLine("Signing failed:");
        Console.WriteLine(signResult.Output);
        return 1;
    }
    Console.WriteLine("Signed successfully.");

    Console.WriteLine();
    Console.WriteLine($"Done: {outPath}");
    Console.WriteLine();
    Console.WriteLine("To install: copy this APK to your phone and open it (you'll need \"install from");
    Console.WriteLine("unknown sources\" enabled for whatever app you use to open it). Since this has a");
    Console.WriteLine("different signature than the original, if the original app is already installed");
    Console.WriteLine("you'll likely need to uninstall it first before this one will install.");
    return 0;
}
finally
{
    try { File.Delete(tempDataFile); } catch { /* best effort cleanup */ }
    try { File.Delete(tempDataFile + ".bak"); } catch { /* best effort cleanup */ }
}

static void PrintUsage()
{
    Console.WriteLine("Usage: ApkPatcher <path-to-game.apk> [--replace-data <path-to-data.win>] [--include-mods <path>] [--hmv | --touch-controls | --custom-alts] [--out <output.apk>] [--yes]");
    Console.WriteLine();
    Console.WriteLine("Patches the Android build of a compatible game (Wife's Bedroom / ModRoom / compatible");
    Console.WriteLine("mods) to add Buttplug.io toy telemetry, and produces a new, re-signed APK next to the");
    Console.WriteLine("original (which is never modified). Requires a JDK on this machine - see");
    Console.WriteLine("https://adoptium.net if you don't have one.");
    Console.WriteLine();
    Console.WriteLine("--replace-data <path-to-data.win>");
    Console.WriteLine("    Swap in a different data file (e.g. a PC mod's data.win, like ModRoom) before");
    Console.WriteLine("    patching, instead of patching the APK's own data file. Use this to get a PC mod's");
    Console.WriteLine("    content running on Android - point it at your own copy of the official APK plus");
    Console.WriteLine("    your own copy of the mod's data.win. Re-run with a newer data.win any time the mod");
    Console.WriteLine("    updates to get a fresh APK.");
    Console.WriteLine();
    Console.WriteLine("--include-mods <path>");
    Console.WriteLine("    Bundle a mod's external content folders into the APK too - point this at whatever");
    Console.WriteLine("    folder directly contains custom_wives/custom_futas/custom_bedrooms/dialogue_packs/");
    Console.WriteLine("    texture_packs (e.g. your ModRoom install folder). Required for those to work at all");
    Console.WriteLine("    on Android - unlike PC, there's no writable folder next to the game these can be");
    Console.WriteLine("    dropped into later, so they have to be packaged in up front. Whichever of the five");
    Console.WriteLine("    folders exist are copied in; nothing about your specific mods is bundled with this");
    Console.WriteLine("    tool itself - you supply your own each time you run it.");
    Console.WriteLine();
    Console.WriteLine("--hmv");
    Console.WriteLine("    Apply the HMV mode patch (lets an external tool drive thrust rhythm and background");
    Console.WriteLine("    color in real time, e.g. to sync to a song's beat) instead of the toy telemetry");
    Console.WriteLine("    patch.");
    Console.WriteLine();
    Console.WriteLine("--touch-controls");
    Console.WriteLine("    Apply the touch-controls patch instead of the toy telemetry patch - adds long-press");
    Console.WriteLine("    (as a right-click equivalent) and drag-to-scroll (as a mouse-wheel equivalent) for");
    Console.WriteLine("    mods (confirmed in ModRoom) whose settings only work with a mouse. To combine with");
    Console.WriteLine("    toy telemetry or another patch, run this tool once per patch, feeding each output");
    Console.WriteLine("    back in as the next run's --replace-data.");
    Console.WriteLine();
    Console.WriteLine("--custom-alts");
    Console.WriteLine("    Apply the custom-character-alts + click-scroll patch instead of the toy telemetry");
    Console.WriteLine("    patch - adds numbered alt looks for custom characters (custom_data_1.futa/.spouse");
    Console.WriteLine("    etc, right-click the portrait-swap button to cycle) and click-scroll arrows for the");
    Console.WriteLine("    custom character list. Specific to this vanilla install's own func_load_custom -");
    Console.WriteLine("    not ModRoom-style builds.");
}

static ZipArchiveEntry? FindGameDataEntry(ZipArchive archive)
{
    byte[] header = new byte[4];
    foreach (var entry in archive.Entries)
    {
        if (!entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) continue;
        if (entry.Length < 4) continue;
        using var stream = entry.Open();
        int read = 0;
        while (read < 4)
        {
            int n = stream.Read(header.AsSpan(read));
            if (n <= 0) break;
            read += n;
        }
        if (read == 4 && header[0] == (byte)'F' && header[1] == (byte)'O' && header[2] == (byte)'R' && header[3] == (byte)'M')
        {
            return entry;
        }
    }
    return null;
}

static string GetOrCreateKeystore(string keytool, string keystorePath, string passwordPath)
{
    if (File.Exists(keystorePath) && File.Exists(passwordPath))
    {
        return File.ReadAllText(passwordPath).Trim();
    }

    Console.WriteLine("No signing key found yet - generating one (one-time, reused for future patches)...");
    string password = Convert.ToHexString(RandomBytes());
    var result = RunProcess(keytool,
        $"-genkeypair -keystore \"{keystorePath}\" -alias apkpatcherkey -keyalg RSA -keysize 2048 " +
        $"-validity 10000 -storepass {password} -keypass {password} " +
        "-dname \"CN=ApkPatcher, OU=ToyBridge, O=ToyBridge, L=Unknown, S=Unknown, C=US\"");
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException("Failed to generate signing key:\n" + result.Output);
    }
    File.WriteAllText(passwordPath, password);
    Console.WriteLine($"Signing key created: {keystorePath}");
    return password;
}

static byte[] RandomBytes()
{
    var bytes = new byte[16];
    System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
    return bytes;
}

static string? FindJava()
{
    string exeName = OperatingSystem.IsWindows() ? "java.exe" : "java";

    string? fromPath = Environment.GetEnvironmentVariable("PATH")?
        .Split(Path.PathSeparator)
        .Select(dir => { try { return Path.Combine(dir, exeName); } catch { return null; } })
        .FirstOrDefault(p => p is not null && File.Exists(p));
    if (fromPath is not null) return fromPath;

    string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
    if (!string.IsNullOrEmpty(javaHome))
    {
        string candidate = Path.Combine(javaHome, "bin", exeName);
        if (File.Exists(candidate)) return candidate;
    }

    if (OperatingSystem.IsWindows())
    {
        foreach (string root in new[] { @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\Java" })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string candidate = Path.Combine(dir, "bin", exeName);
                if (File.Exists(candidate)) return candidate;
            }
        }
    }

    return null;
}

static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var process = Process.Start(psi)!;
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout + stderr);
}
