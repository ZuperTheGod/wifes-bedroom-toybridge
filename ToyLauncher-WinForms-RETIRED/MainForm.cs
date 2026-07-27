using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ButtplugLauncher;

public class MainForm : Form
{
    private static readonly (string Display, string Key)[] ProfileChoices =
    [
        ("Default (0-100%, full range)", "default"),
        ("Easy (0-30%)", "easy"),
        ("Mid (30-60%)", "mid"),
        ("Hard (60-100%)", "hard"),
        ("Mid-Easy (0-50%)", "mideasy"),
        ("Mid-Hard (50-100%)", "midhard"),
        ("Custom", "custom"),
    ];

    private readonly string _baseDir = AppContext.BaseDirectory;
    private string BridgeExePath => Path.Combine(_baseDir, "ButtplugBridge.exe");
    private string ProfilesJsonPath => Path.Combine(_baseDir, "profiles.json");
    private string SettingsPath => Path.Combine(_baseDir, "launcher_settings.json");

    private readonly Label _gamePathLabel = new();
    private readonly Button _browseGameButton = new();
    private readonly Button _patchGameButton = new();
    private readonly ComboBox _profileCombo = new();
    private readonly Panel _customPanel = new();
    private readonly NumericUpDown _customMinUpDown = new();
    private readonly NumericUpDown _customMaxUpDown = new();
    private readonly CheckBox _launchGameCheck = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _openIntifaceButton = new();
    private readonly TextBox _statusBox = new();

    private LauncherSettings _settings = new();
    private Process? _bridgeProcess;

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = SystemFonts.MessageBoxFont ?? Font;

        Text = "Toy Bridge Launcher";
        ClientSize = new Size(560, 520);
        MinimumSize = new Size(440, 400);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(14);

        BuildLayout();
        LoadSettings();
        ApplySettingsToUi();

        Load += async (_, _) => await ProbeIntifaceAndReportAsync();
    }

    private void BuildLayout()
    {
        // A vertically-stacked TableLayoutPanel: every row auto-sizes to fit its content
        // (based on the actual rendered font/DPI, not hardcoded pixels), except the status
        // log at the bottom, which is told to absorb all remaining space.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // status box row

        var gameLabel = new Label { Text = "Game:", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        root.Controls.Add(gameLabel, 0, 0);

        var gameRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 14),
        };
        gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _gamePathLabel.AutoEllipsis = true;
        _gamePathLabel.Dock = DockStyle.Fill;
        _gamePathLabel.Text = "(no game selected)";
        _gamePathLabel.Margin = new Padding(0, 7, 10, 0);

        var gameButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _browseGameButton.Text = "Browse...";
        _browseGameButton.AutoSize = true;
        _browseGameButton.Padding = new Padding(10, 4, 10, 4);
        _browseGameButton.Margin = new Padding(0, 0, 6, 0);
        _browseGameButton.Click += (_, _) => BrowseForGame();

        _patchGameButton.Text = "Patch Game...";
        _patchGameButton.AutoSize = true;
        _patchGameButton.Padding = new Padding(10, 4, 10, 4);
        _patchGameButton.Click += async (_, _) => await PatchGameClickedAsync();

        gameButtons.Controls.Add(_browseGameButton);
        gameButtons.Controls.Add(_patchGameButton);

        gameRow.Controls.Add(_gamePathLabel, 0, 0);
        gameRow.Controls.Add(gameButtons, 1, 0);
        root.Controls.Add(gameRow, 0, 1);

        var profileLabel = new Label { Text = "Intensity profile:", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        root.Controls.Add(profileLabel, 0, 2);

        _profileCombo.Dock = DockStyle.Fill;
        _profileCombo.Margin = new Padding(0, 0, 0, 12);
        _profileCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var choice in ProfileChoices) _profileCombo.Items.Add(choice.Display);
        _profileCombo.SelectedIndexChanged += (_, _) => UpdateCustomPanelVisibility();
        root.Controls.Add(_profileCombo, 0, 3);

        _customPanel.AutoSize = true;
        _customPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _customPanel.Dock = DockStyle.Fill;
        _customPanel.Margin = new Padding(0, 0, 0, 12);
        _customPanel.Padding = new Padding(10);
        _customPanel.BorderStyle = BorderStyle.FixedSingle;

        var customFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        var minLabel = new Label { Text = "Min %:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        _customMinUpDown.Width = 65;
        _customMinUpDown.Margin = new Padding(0, 3, 24, 0);
        _customMinUpDown.Minimum = 0;
        _customMinUpDown.Maximum = 100;
        _customMinUpDown.Value = 0;
        var maxLabel = new Label { Text = "Max %:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        _customMaxUpDown.Width = 65;
        _customMaxUpDown.Margin = new Padding(0, 3, 0, 0);
        _customMaxUpDown.Minimum = 0;
        _customMaxUpDown.Maximum = 100;
        _customMaxUpDown.Value = 100;
        customFlow.Controls.Add(minLabel);
        customFlow.Controls.Add(_customMinUpDown);
        customFlow.Controls.Add(maxLabel);
        customFlow.Controls.Add(_customMaxUpDown);
        _customPanel.Controls.Add(customFlow);
        root.Controls.Add(_customPanel, 0, 4);

        _launchGameCheck.Text = "Also launch the game";
        _launchGameCheck.AutoSize = true;
        _launchGameCheck.Margin = new Padding(0, 0, 0, 14);
        _launchGameCheck.Checked = true;
        root.Controls.Add(_launchGameCheck, 0, 5);

        var buttonRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 16),
        };
        _startButton.Text = "Start";
        _startButton.AutoSize = true;
        _startButton.Padding = new Padding(14, 6, 14, 6);
        _startButton.Margin = new Padding(0, 0, 10, 0);
        _startButton.Click += async (_, _) => await StartClickedAsync();

        _stopButton.Text = "Stop";
        _stopButton.AutoSize = true;
        _stopButton.Padding = new Padding(14, 6, 14, 6);
        _stopButton.Margin = new Padding(0, 0, 10, 0);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopBridge();

        _openIntifaceButton.Text = "Open Intiface Central";
        _openIntifaceButton.AutoSize = true;
        _openIntifaceButton.Padding = new Padding(14, 6, 14, 6);
        _openIntifaceButton.Click += (_, _) => OpenIntifaceCentral();

        buttonRow.Controls.Add(_startButton);
        buttonRow.Controls.Add(_stopButton);
        buttonRow.Controls.Add(_openIntifaceButton);
        root.Controls.Add(buttonRow, 0, 6);

        var statusLabel = new Label { Text = "Status:", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        root.Controls.Add(statusLabel, 0, 7);

        _statusBox.Dock = DockStyle.Fill;
        _statusBox.Multiline = true;
        _statusBox.ScrollBars = ScrollBars.Vertical;
        _statusBox.ReadOnly = true;
        _statusBox.Font = new Font(FontFamily.GenericMonospace, 9f);
        root.Controls.Add(_statusBox, 0, 8);

        Controls.Add(root);

        UpdateCustomPanelVisibility();
    }

    private void UpdateCustomPanelVisibility()
    {
        int index = _profileCombo.SelectedIndex;
        bool isCustom = index >= 0 && ProfileChoices[index].Key == "custom";
        _customPanel.Visible = isCustom;
    }

    private void Log(string message) => _statusBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\r\n");

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string text = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize<LauncherSettings>(text) ?? new LauncherSettings();
            }
        }
        catch
        {
            _settings = new LauncherSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, options));
        }
        catch (Exception ex)
        {
            Log($"Couldn't save settings: {ex.Message}");
        }
    }

    private void ApplySettingsToUi()
    {
        int index = Array.FindIndex(ProfileChoices, c => c.Key == _settings.Profile);
        _profileCombo.SelectedIndex = index >= 0 ? index : 0;
        _customMinUpDown.Value = (decimal)Math.Clamp(_settings.CustomMin, 0, 100);
        _customMaxUpDown.Value = (decimal)Math.Clamp(_settings.CustomMax, 0, 100);
        _launchGameCheck.Checked = _settings.LaunchGame;
        UpdateCustomPanelVisibility();

        // Try a silent auto-detect (no prompt) in case the game exe happens to already sit
        // next to this launcher, same as older installs where the two were bundled together.
        ResolveGameExePath(allowPrompt: false);
        UpdateGamePathLabel();
    }

    private void SaveSettingsFromUi(string profileKey)
    {
        _settings.Profile = profileKey;
        _settings.CustomMin = (double)_customMinUpDown.Value;
        _settings.CustomMax = (double)_customMaxUpDown.Value;
        _settings.LaunchGame = _launchGameCheck.Checked;
        SaveSettings();
    }

    private async Task StartClickedAsync()
    {
        if (!File.Exists(BridgeExePath))
        {
            MessageBox.Show(this, $"Can't find ButtplugBridge.exe next to this launcher:\n{BridgeExePath}",
                "Missing file", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        int index = _profileCombo.SelectedIndex;
        if (index < 0) index = 0;
        string profileKey = ProfileChoices[index].Key;

        if (profileKey == "custom")
        {
            if (_customMinUpDown.Value >= _customMaxUpDown.Value)
            {
                MessageBox.Show(this, "Custom Min % must be less than Max %.", "Check the custom range",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool updated = TryUpdateCustomProfile((double)_customMinUpDown.Value, (double)_customMaxUpDown.Value, out string error);
            if (!updated)
            {
                Log($"Couldn't update the custom profile in profiles.json: {error}");
                MessageBox.Show(this, $"Couldn't update profiles.json:\n{error}", "Custom profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Log($"Custom profile set to {_customMinUpDown.Value:0}% - {_customMaxUpDown.Value:0}%.");
        }

        SaveSettingsFromUi(profileKey);

        Log("Checking for Intiface Central...");
        bool intifaceUp = await ProbeIntifaceAsync();
        if (!intifaceUp)
        {
            var choice = MessageBox.Show(this,
                "Intiface Central doesn't seem to be reachable at ws://127.0.0.1:12345.\n\n" +
                "Make sure Intiface Central is running AND its server is started (there's a " +
                "\"Start Server\" toggle inside it).\n\n" +
                "Click Yes to try launching Intiface Central now, or No to continue anyway.",
                "Intiface Central not detected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (choice == DialogResult.Yes)
            {
                LaunchIntifaceCentral();
            }
        }
        else
        {
            Log("Intiface Central is reachable.");
        }

        try
        {
            _bridgeProcess = Process.Start(new ProcessStartInfo
            {
                FileName = BridgeExePath,
                Arguments = $"--profile {profileKey}",
                WorkingDirectory = _baseDir,
                UseShellExecute = false,
            });
            Log($"Started ButtplugBridge.exe --profile {profileKey}");
        }
        catch (Exception ex)
        {
            Log($"Failed to start ButtplugBridge.exe: {ex.Message}");
            MessageBox.Show(this, $"Failed to start ButtplugBridge.exe:\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_launchGameCheck.Checked)
        {
            string? gamePath = ResolveGameExePath(allowPrompt: false);
            if (gamePath is not null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = gamePath,
                        WorkingDirectory = Path.GetDirectoryName(gamePath),
                        UseShellExecute = true,
                    });
                    Log($"Launched the game ({gamePath}).");
                }
                catch (Exception ex)
                {
                    Log($"Couldn't launch the game: {ex.Message}");
                }
            }
            else
            {
                Log("No game is set yet - click Browse next to \"Game:\" to pick one. Skipped launching the game.");
            }
        }

        _startButton.Enabled = false;
        _stopButton.Enabled = true;
    }

    /// <summary>
    /// Returns the currently configured game exe path if it's still valid. If not set (or the
    /// file has moved/vanished), tries a one-time auto-detect of any .exe sitting next to this
    /// launcher that isn't one of our own tools; if that's ambiguous (zero or multiple candidates)
    /// and prompting is allowed, asks the user to browse for it and remembers the answer.
    /// </summary>
    private string? ResolveGameExePath(bool allowPrompt)
    {
        if (!string.IsNullOrEmpty(_settings.GamePath) && File.Exists(_settings.GamePath))
        {
            return _settings.GamePath;
        }

        string[] ownExeNames = ["ButtplugBridge.exe", "ToyLauncher.exe", Path.GetFileName(Application.ExecutablePath)];
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(_baseDir, "*.exe")
                .Where(p => !ownExeNames.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
        catch
        {
            candidates = [];
        }

        if (candidates.Length == 1)
        {
            _settings.GamePath = candidates[0];
            SaveSettings();
            return candidates[0];
        }

        if (!allowPrompt) return null;

        BrowseForGame();
        return (!string.IsNullOrEmpty(_settings.GamePath) && File.Exists(_settings.GamePath)) ? _settings.GamePath : null;
    }

    private void BrowseForGame()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Locate the game's .exe",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (!string.IsNullOrEmpty(_settings.GamePath) && Directory.Exists(Path.GetDirectoryName(_settings.GamePath)))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_settings.GamePath);
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _settings.GamePath = dialog.FileName;
            SaveSettings();
            Log($"Game set to: {dialog.FileName}");
        }

        UpdateGamePathLabel();
    }

    private void UpdateGamePathLabel()
    {
        _gamePathLabel.Text = !string.IsNullOrEmpty(_settings.GamePath) && File.Exists(_settings.GamePath)
            ? _settings.GamePath
            : "(no game selected - click Browse)";
    }

    private async Task PatchGameClickedAsync()
    {
        string? gamePath = ResolveGameExePath(allowPrompt: true);
        if (gamePath is null) return;

        string? gameDir = Path.GetDirectoryName(gamePath);
        string dataWinPath = Path.Combine(gameDir ?? _baseDir, "data.win");
        if (!File.Exists(dataWinPath))
        {
            MessageBox.Show(this, $"Couldn't find data.win next to the game's exe:\n{dataWinPath}",
                "Can't patch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"This will back up and patch:\n{dataWinPath}\n\n" +
            "A backup (data.win.bak) is made automatically first if one doesn't already exist. " +
            "This only adds a small amount of code to broadcast toy telemetry - nothing else about " +
            "the game is changed. Continue?",
            "Patch game for toy support", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        Log($"Patching {dataWinPath}...");
        _patchGameButton.Enabled = false;
        Cursor = Cursors.WaitCursor;
        try
        {
            var outcome = await Task.Run(() => GamePatcher.Patch(dataWinPath));
            Log($"[patch] {outcome.Result}: {outcome.Message}");
            var icon = outcome.Result is GamePatcher.PatchResult.Error or GamePatcher.PatchResult.NotSupported
                ? MessageBoxIcon.Error
                : MessageBoxIcon.Information;
            MessageBox.Show(this, outcome.Message, "Patch game for toy support", MessageBoxButtons.OK, icon);
        }
        finally
        {
            _patchGameButton.Enabled = true;
            Cursor = Cursors.Default;
        }
    }

    private bool TryUpdateCustomProfile(double min, double max, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(ProfilesJsonPath))
            {
                error = $"{ProfilesJsonPath} doesn't exist yet - run ButtplugBridge.exe once first.";
                return false;
            }

            string text = File.ReadAllText(ProfilesJsonPath);
            string pattern = "\"custom\"\\s*:\\s*\\{[^}]*\\}";
            string replacement = $"\"custom\":   {{ \"min\": {min:0.##}, \"max\": {max:0.##} }}";

            if (!Regex.IsMatch(text, pattern))
            {
                error = "Couldn't find a \"custom\" entry in profiles.json to update.";
                return false;
            }

            text = Regex.Replace(text, pattern, replacement);
            File.WriteAllText(ProfilesJsonPath, text);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void StopBridge()
    {
        if (_bridgeProcess is null)
        {
            _stopButton.Enabled = false;
            _startButton.Enabled = true;
            return;
        }

        try
        {
            if (!_bridgeProcess.HasExited)
            {
                _bridgeProcess.CloseMainWindow();
                if (!_bridgeProcess.WaitForExit(2000))
                {
                    _bridgeProcess.Kill();
                }
            }
            Log("Stopped ButtplugBridge.");
        }
        catch (Exception ex)
        {
            Log($"Couldn't stop ButtplugBridge cleanly: {ex.Message}");
        }
        finally
        {
            _bridgeProcess = null;
            _stopButton.Enabled = false;
            _startButton.Enabled = true;
        }
    }

    private void OpenIntifaceCentral() => LaunchIntifaceCentral();

    private void LaunchIntifaceCentral()
    {
        string? path = _settings.IntifacePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = FindIntifaceCentralGuess();
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Locate intiface_central.exe",
                Filter = "Intiface Central (intiface_central.exe)|intiface_central.exe|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                Log("Intiface Central location not set - skipped.");
                return;
            }
            path = dialog.FileName;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            _settings.IntifacePath = path;
            SaveSettings();
            Log($"Launched Intiface Central ({path}). Remember to click \"Start Server\" inside it.");
        }
        catch (Exception ex)
        {
            Log($"Couldn't launch Intiface Central: {ex.Message}");
        }
    }

    private static string? FindIntifaceCentralGuess()
    {
        string[] candidates =
        [
            @"D:\NSFW Porn Programs\IntifaceCentral\intiface_central.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "IntifaceCentral", "intiface_central.exe"),
            @"C:\Program Files\IntifaceCentral\intiface_central.exe",
        ];
        return Array.Find(candidates, File.Exists);
    }

    private async Task<bool> ProbeIntifaceAsync()
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", 12345);
            var timeoutTask = Task.Delay(800);
            var completed = await Task.WhenAny(connectTask, timeoutTask);
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task ProbeIntifaceAndReportAsync()
    {
        Log("Checking for Intiface Central...");
        bool up = await ProbeIntifaceAsync();
        Log(up
            ? "Intiface Central is reachable at ws://127.0.0.1:12345."
            : "Intiface Central not detected yet - that's fine, you can start it before pressing Start.");
    }
}

class LauncherSettings
{
    public string Profile { get; set; } = "default";
    public double CustomMin { get; set; } = 0;
    public double CustomMax { get; set; } = 100;
    public bool LaunchGame { get; set; } = true;
    public string? IntifacePath { get; set; }
    public string? GamePath { get; set; }
}
