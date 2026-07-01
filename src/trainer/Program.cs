using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Forms;

namespace TaskbarHeroTrainer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrainerForm());
    }
}

internal sealed class TrainerForm : Form
{
    private const string TrainerVersion = "1.6";
    private const string GameProcessName = "TaskBarHero";
    private const string PipeName = "TaskbarHeroTrainerPipe";
    private const int PipeTimeoutMs = 700;
    private const int TrainerLaunchArmSeconds = 90;
    private const int ButtonHeight = 30;
    private static readonly Color WindowBackColor = Color.FromArgb(13, 15, 19);
    private static readonly Color HeaderBackColor = Color.FromArgb(21, 24, 30);
    private static readonly Color SectionBackColor = Color.FromArgb(24, 28, 35);
    private static readonly Color SurfaceColor = Color.FromArgb(19, 23, 29);
    private static readonly Color ControlBackColor = Color.FromArgb(34, 40, 50);
    private static readonly Color ControlBorderColor = Color.FromArgb(75, 87, 105);
    private static readonly Color ControlHoverColor = Color.FromArgb(45, 54, 68);
    private static readonly Color ControlDownColor = Color.FromArgb(58, 72, 92);
    private static readonly Color AccentColor = Color.FromArgb(78, 161, 255);
    private static readonly Color AccentDarkColor = Color.FromArgb(39, 83, 136);
    private static readonly Color TextColor = Color.FromArgb(234, 238, 245);
    private static readonly Color MutedTextColor = Color.FromArgb(165, 174, 188);
    private static readonly string GameDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        @"Steam\steamapps\common\TaskbarHero");
    private static readonly string DoorstopConfigPath = Path.Combine(GameDirectory, "doorstop_config.ini");
    private static readonly string BepInExConfigPath = Path.Combine(GameDirectory, "BepInEx", "config", "BepInEx.cfg");
    private readonly Label _processLabel;
    private readonly Label _bridgeLabel;
    private readonly TextBox _statusBox;
    private readonly Button _refreshButton;
    private readonly Button _currencyButton;
    private readonly Button _heroesButton;
    private readonly Button _unlockSlotsButton;
    private readonly Button _listItemKeysButton;
    private readonly Button _petsButton;
    private readonly Button _skillPointsZeroButton;
    private readonly Button _skillPointsMaxButton;
    private readonly Button _prototypeHeroButton;
    private readonly Button _removePrototypeHeroButton;
    private readonly Button _bestKnightGearButton;
    private readonly Button _bestRangerGearButton;
    private readonly Button _bestSorcererGearButton;
    private readonly Button _bestPriestGearButton;
    private readonly Button _bestHunterGearButton;
    private readonly Button _bestSlayerGearButton;
    private readonly Button _knightGemsButton;
    private readonly Button _rangerGemsButton;
    private readonly Button _sorcererGemsButton;
    private readonly Button _priestGemsButton;
    private readonly Button _hunterGemsButton;
    private readonly Button _slayerGemsButton;
    private readonly CheckBox _oneHitToggle;
    private readonly CheckBox _godModeToggle;
    private readonly Button _repairEquippedDupesButton;
    private readonly Label _gameSpeedLabel;
    private readonly TrackBar _gameSpeedTrackBar;
    private readonly Button _gameSpeedResetButton;
    private readonly Button _addItemButton;
    private readonly TextBox _itemKeyBox;
    private readonly System.Windows.Forms.Timer _timer;

    private Process _gameProcess;
    private bool _bridgeReady;
    private bool _doorstopArmedForTrainerLaunch;
    private int _lastGameProcessId;
    private DateTime _doorstopArmExpiresAt;

    public TrainerForm()
    {
        Text = $"TaskbarHero Trainer {TrainerVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(740, 940);
        Size = new Size(760, 1050);
        Font = new Font("Segoe UI", 9F);
        BackColor = WindowBackColor;
        ForeColor = TextColor;
        Padding = new Padding(16);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WindowBackColor,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 188F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 114F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 258F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _processLabel = new Label
        {
            AutoSize = true,
            Text = "Proceso: buscando...",
            ForeColor = MutedTextColor
        };

        _bridgeLabel = new Label
        {
            AutoSize = true,
            Text = "Bridge: esperando...",
            ForeColor = MutedTextColor
        };

        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = HeaderBackColor,
            Padding = new Padding(18, 12, 18, 10),
            Margin = new Padding(0, 0, 0, 10)
        };
        var title = new Label
        {
            Text = $"TaskbarHero Trainer {TrainerVersion}",
            Font = new Font("Segoe UI Semibold", 18F),
            AutoSize = true,
            Location = new Point(0, 0),
            ForeColor = TextColor
        };
        _processLabel.Location = new Point(2, 42);
        _bridgeLabel.Location = new Point(250, 42);
        header.Controls.AddRange(new Control[] { title, _processLabel, _bridgeLabel });
        root.Controls.Add(header, 0, 0);

        _statusBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SurfaceColor,
            ForeColor = TextColor,
            Margin = new Padding(0, 7, 0, 0),
            Text = "Abre TaskbarHero y espera a estar en partida. Luego pulsa Refresh runtime."
        };

        var launchButton = CreateButton("Launch game", LaunchGame, accent: true);
        _refreshButton = CreateButton("Refresh runtime", () => SendCommand("REFRESH"));
        root.Controls.Add(BuildSection("Inicio", BuildTwoColumnRows((launchButton, _refreshButton))), 0, 1);

        _currencyButton = CreateButton("+999.999.999 currency", () => SendCommand("CURRENCIES"));
        _heroesButton = CreateButton("Unlock + level heroes", () => SendCommand("HEROES"));
        _unlockSlotsButton = CreateButton("Unlock inventory/stash", () => SendCommand("UNLOCK_SLOTS"));
        _petsButton = CreateButton("Unlock all pets", () => SendCommand("PETS"));
        _skillPointsZeroButton = CreateButton("Skill pts 0", () => SendCommand("SKILL_POINTS_0"));
        _skillPointsMaxButton = CreateButton("Skill pts 999", () => SendCommand("SKILL_POINTS_999"));
        _prototypeHeroButton = CreateButton("Prototype hero", () => SendCommand("PROTOTYPE_HERO"));
        _removePrototypeHeroButton = CreateButton("Remove prototype", () => SendCommand("REMOVE_PROTOTYPE_HERO"));
        root.Controls.Add(BuildSection(
            "Cuenta y progreso",
            BuildTwoColumnRows(
                (_currencyButton, _heroesButton),
                (_unlockSlotsButton, _petsButton),
                (_skillPointsZeroButton, _skillPointsMaxButton),
                (_prototypeHeroButton, _removePrototypeHeroButton))), 0, 2);

        _listItemKeysButton = CreateButton("List useful item keys", () => SendCommand("LIST_ITEM_KEYS"));

        _itemKeyBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SurfaceColor,
            ForeColor = TextColor,
            Margin = new Padding(8, 4, 8, 4)
        };
        _addItemButton = CreateButton("Add item");
        _addItemButton.Click += (_, _) => AddItemByKey();
        _repairEquippedDupesButton = CreateButton("Repair equip dupes", () => SendCommand("REPAIR_EQUIPPED_DUPES"));
        root.Controls.Add(BuildSection("Items y limpieza", BuildItemsPanel()), 0, 3);

        _bestKnightGearButton = CreateButton("Best Knight gear", () => SendCommand("BEST_GEAR:Knight"));
        _knightGemsButton = CreateButton("Knight gems + socket", () => SendCommand("SOCKET_EQUIPPED_GEMS:Knight"));
        _bestRangerGearButton = CreateButton("Best Ranger gear", () => SendCommand("BEST_GEAR:Ranger"));
        _rangerGemsButton = CreateButton("Ranger gems + socket", () => SendCommand("SOCKET_EQUIPPED_GEMS:Ranger"));
        _bestSorcererGearButton = CreateButton("Best Sorcerer gear", () => SendCommand("BEST_GEAR:Sorcerer"));
        _sorcererGemsButton = CreateButton("Sorcerer gems + socket", () => SendCommand("SOCKET_EQUIPPED_GEMS:Sorcerer"));
        _bestPriestGearButton = CreateButton("Best Priest gear", () => SendCommand("BEST_GEAR:Priest"));
        _priestGemsButton = CreateButton("Priest gems + socket", () => SendCommand("SOCKET_EQUIPPED_GEMS:Priest"));
        _bestHunterGearButton = CreateButton("Best Hunter gear", () => SendCommand("BEST_GEAR:Hunter"));
        _hunterGemsButton = CreateButton("Hunter gems + socket", () => SendCommand("SOCKET_EQUIPPED_GEMS:Hunter"));
        _bestSlayerGearButton = CreateButton("Best Assassin gear", () => SendCommand("BEST_GEAR:Slayer"));
        _slayerGemsButton = CreateButton("Assassin gems + socket", () => SendCommand("SOCKET_EQUIPPED_GEMS:Slayer"));
        root.Controls.Add(BuildSection(
            "Equipo por clase",
            BuildTwoColumnRows(
                (_bestKnightGearButton, _knightGemsButton),
                (_bestRangerGearButton, _rangerGemsButton),
                (_bestSorcererGearButton, _sorcererGemsButton),
                (_bestPriestGearButton, _priestGemsButton),
                (_bestHunterGearButton, _hunterGemsButton),
                (_bestSlayerGearButton, _slayerGemsButton))), 0, 4);

        _oneHitToggle = CreateToggle("One hit kill: OFF");
        _oneHitToggle.CheckedChanged += (_, _) => ToggleOneHitKill();
        _godModeToggle = CreateToggle("God mode: OFF");
        _godModeToggle.CheckedChanged += (_, _) => ToggleGodMode();

        _gameSpeedLabel = new Label
        {
            Text = "Game/combat speed: 1.0x",
            AutoSize = true,
            ForeColor = MutedTextColor,
            Anchor = AnchorStyles.Left
        };

        _gameSpeedTrackBar = new TrackBar
        {
            Dock = DockStyle.Fill,
            Minimum = 10,
            Maximum = 100,
            Value = 10,
            TickFrequency = 10,
            SmallChange = 5,
            LargeChange = 10,
            BackColor = WindowBackColor,
            ForeColor = TextColor
        };
        _gameSpeedTrackBar.ValueChanged += (_, _) => UpdateGameSpeedLabel();
        _gameSpeedTrackBar.MouseUp += (_, _) => ApplyGameSpeed();
        _gameSpeedTrackBar.KeyUp += (_, _) => ApplyGameSpeed();

        _gameSpeedResetButton = CreateButton("Reset 1x");
        _gameSpeedResetButton.Click += (_, _) => ResetGameSpeed();
        root.Controls.Add(BuildSection("Gameplay", BuildGameplayPanel()), 0, 5);
        root.Controls.Add(BuildSection("Registro", _statusBox), 0, 6);

        Controls.Add(root);

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => UpdateDetection();
        _timer.Start();
        FormClosed += (_, _) => DisarmTrainerLaunch();
        ConfigureNormalLaunchDefaults();
        UpdateDetection();
    }

    private static Panel BuildSection(string title, Control content)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SectionBackColor,
            Padding = new Padding(12, 6, 12, 8),
            Margin = new Padding(0, 0, 0, 8)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SectionBackColor,
            ColumnCount = 1,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var label = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 9F),
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft
        };

        content.Dock = DockStyle.Fill;
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(content, 0, 1);
        panel.Controls.Add(layout);
        return panel;
    }

    private static TableLayoutPanel BuildTwoColumnRows(params (Control Left, Control Right)[] rows)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SectionBackColor,
            ColumnCount = 2,
            RowCount = rows.Length
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / Math.Max(1, rows.Length)));
            AddGridControl(grid, rows[i].Left, 0, i);
            AddGridControl(grid, rows[i].Right, 1, i);
        }

        return grid;
    }

    private static TableLayoutPanel BuildFullWidthRows(params Control[] rows)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SectionBackColor,
            ColumnCount = 1,
            RowCount = rows.Length
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / Math.Max(1, rows.Length)));
            AddGridControl(grid, rows[i], 0, i);
        }

        return grid;
    }

    private Control BuildItemsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SectionBackColor,
            ColumnCount = 4,
            RowCount = 2
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33F));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

        var itemKeyLabel = new Label
        {
            Text = "ItemKey",
            Dock = DockStyle.Fill,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft
        };

        AddGridControl(panel, itemKeyLabel, 0, 0);
        AddGridControl(panel, _itemKeyBox, 1, 0);
        AddGridControl(panel, _addItemButton, 2, 0);
        AddGridControl(panel, _listItemKeysButton, 3, 0);
        AddGridControl(panel, _repairEquippedDupesButton, 0, 1, 4);
        return panel;
    }

    private Control BuildSpeedPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SectionBackColor,
            ColumnCount = 3,
            RowCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        AddGridControl(panel, _gameSpeedLabel, 0, 0);
        AddGridControl(panel, _gameSpeedTrackBar, 1, 0);
        AddGridControl(panel, _gameSpeedResetButton, 2, 0);
        return panel;
    }

    private Control BuildGameplayPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SectionBackColor,
            ColumnCount = 1,
            RowCount = 2
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, ButtonHeight + 8));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        AddGridControl(panel, BuildTwoColumnRows((_oneHitToggle, _godModeToggle)), 0, 0);
        AddGridControl(panel, BuildSpeedPanel(), 0, 1);
        return panel;
    }

    private static void AddGridControl(TableLayoutPanel grid, Control control, int column, int row, int columnSpan = 1)
    {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(6, 3, 6, 3);
        grid.Controls.Add(control, column, row);
        if (columnSpan > 1)
        {
            grid.SetColumnSpan(control, columnSpan);
        }
    }

    private Button CreateButton(string text, Action action = null, bool accent = false)
    {
        var button = new Button
        {
            Text = text,
            Height = ButtonHeight,
            TextAlign = ContentAlignment.MiddleCenter
        };
        StyleButton(button, accent);
        if (action != null)
        {
            button.Click += (_, _) => action();
        }

        return button;
    }

    private CheckBox CreateToggle(string text)
    {
        var toggle = new CheckBox
        {
            Appearance = Appearance.Button,
            Text = text,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = ButtonHeight
        };
        StyleButton(toggle);
        return toggle;
    }

    private static void StyleButton(ButtonBase button, bool accent = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = accent ? AccentDarkColor : ControlBackColor;
        button.ForeColor = TextColor;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderColor = accent ? AccentColor : ControlBorderColor;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(49, 101, 164) : ControlHoverColor;
        button.FlatAppearance.MouseDownBackColor = ControlDownColor;
    }

    private void UpdateDetection()
    {
        _gameProcess = FindGameProcess();

        if (_gameProcess == null)
        {
            _bridgeReady = false;
            _lastGameProcessId = 0;
            _processLabel.Text = "Proceso: TaskBarHero.exe no detectado";
            if (_doorstopArmedForTrainerLaunch && DateTime.Now <= _doorstopArmExpiresAt)
            {
                _bridgeLabel.Text = "Bridge: esperando a que Steam abra el juego";
            }
            else
            {
                _bridgeLabel.Text = "Bridge: inactivo";
                DisarmTrainerLaunch();
            }

            SetActionButtons(false);
            return;
        }

        _processLabel.Text = $"Proceso: detectado PID {_gameProcess.Id}";
        if (_lastGameProcessId != _gameProcess.Id)
        {
            _lastGameProcessId = _gameProcess.Id;
        }

        DisarmTrainerLaunch();
        string response = TrySendCommand("PING", 250);
        _bridgeReady = response.StartsWith("Bridge listo", StringComparison.OrdinalIgnoreCase);
        _bridgeLabel.Text = _bridgeReady ? "Bridge: conectado" : "Bridge: no activo; abre el juego con Steam o Launch game";

        SetActionButtons(_bridgeReady);
    }

    private static Process FindGameProcess()
    {
        Process selected = null;
        foreach (var process in Process.GetProcessesByName(GameProcessName))
        {
            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                    continue;
                }

                if (selected == null || process.StartTime > selected.StartTime)
                {
                    selected?.Dispose();
                    selected = process;
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return selected;
    }

    private void SetActionButtons(bool enabled)
    {
        _ = enabled;
        foreach (Control control in new Control[]
        {
            _refreshButton,
            _currencyButton,
            _heroesButton,
            _unlockSlotsButton,
            _listItemKeysButton,
            _petsButton,
            _skillPointsZeroButton,
            _skillPointsMaxButton,
            _prototypeHeroButton,
            _removePrototypeHeroButton,
            _bestKnightGearButton,
            _bestRangerGearButton,
            _bestSorcererGearButton,
            _bestPriestGearButton,
            _bestHunterGearButton,
            _bestSlayerGearButton,
            _knightGemsButton,
            _rangerGemsButton,
            _sorcererGemsButton,
            _priestGemsButton,
            _hunterGemsButton,
            _slayerGemsButton,
            _repairEquippedDupesButton,
            _oneHitToggle,
            _godModeToggle,
            _gameSpeedTrackBar,
            _gameSpeedResetButton,
            _addItemButton,
            _itemKeyBox
        })
        {
            control.Enabled = true;
        }
    }

    private void SendCommand(string command)
    {
        UpdateDetection();
        if (_gameProcess == null)
        {
            SetStatus("No veo TaskBarHero.exe. Abre el juego primero o pulsa Launch game.");
            return;
        }

        if (!_bridgeReady)
        {
            SetStatus("El juego esta abierto sin bridge. Cierra el juego y abrelo otra vez desde Steam o Launch game.");
            return;
        }

        HandleBridgeResponse(TrySendCommand(command, PipeTimeoutMs));
    }

    private void AddItemByKey()
    {
        if (!int.TryParse(_itemKeyBox.Text.Trim(), out int itemKey) || itemKey <= 0)
        {
            SetStatus("ItemKey invalido. Pulsa List item keys y escribe una clave numerica.");
            return;
        }

        SendCommand("ADD_ITEM:" + itemKey);
    }

    private void ToggleOneHitKill()
    {
        bool enabled = _oneHitToggle.Checked;
        _oneHitToggle.Text = enabled ? "One hit kill: ON" : "One hit kill: OFF";
        SendCommand(enabled ? "ONE_HIT:ON" : "ONE_HIT:OFF");
    }

    private void ToggleGodMode()
    {
        bool enabled = _godModeToggle.Checked;
        _godModeToggle.Text = enabled ? "God mode: ON" : "God mode: OFF";
        SendCommand(enabled ? "GOD_MODE:ON" : "GOD_MODE:OFF");
    }

    private void UpdateGameSpeedLabel()
    {
        _gameSpeedLabel.Text = $"Game/combat speed: {GetSelectedGameSpeed().ToString("0.0", CultureInfo.InvariantCulture)}x";
    }

    private float GetSelectedGameSpeed()
    {
        return _gameSpeedTrackBar.Value / 10f;
    }

    private void ApplyGameSpeed()
    {
        string value = GetSelectedGameSpeed().ToString("0.0", CultureInfo.InvariantCulture);
        SendCommand("GAME_SPEED:" + value);
    }

    private void ResetGameSpeed()
    {
        _gameSpeedTrackBar.Value = 10;
        ApplyGameSpeed();
    }

    private string TrySendCommand(string command, int timeoutMs)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            client.Connect(timeoutMs);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(command);
            return reader.ReadLine() ?? "Bridge sin respuesta.";
        }
        catch (Exception ex)
        {
            return $"Bridge no conectado: {ex.Message}";
        }
    }

    private void HandleBridgeResponse(string response)
    {
        const string itemListPrefix = "ITEM_LIST_FILE|";
        if (response.StartsWith(itemListPrefix, StringComparison.Ordinal))
        {
            string[] parts = response.Split('|', 3);
            if (parts.Length == 3)
            {
                SetStatus(parts[2]);
                OpenTextFile(parts[1]);
                return;
            }
        }

        SetStatus(response);
    }

    private void OpenTextFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            SetStatus("Lista generada, pero no pude abrir Notepad: " + ex.Message);
        }
    }

    private void LaunchGame()
    {
        try
        {
            if (FindGameProcess() != null)
            {
                SetStatus("TaskbarHero ya esta abierto. Cierralo y lanzalo desde este boton para activar el trainer.");
                return;
            }

            ConfigureNormalLaunchDefaults();
            if (!TrySetDoorstopEnabled(true, out string doorstopError))
            {
                SetStatus("No pude activar el loader del trainer: " + doorstopError);
                return;
            }

            _doorstopArmedForTrainerLaunch = true;
            _doorstopArmExpiresAt = DateTime.Now.AddSeconds(TrainerLaunchArmSeconds);
            Process.Start(new ProcessStartInfo
            {
                FileName = "steam://rungameid/3678970",
                UseShellExecute = true
            });
            SetStatus("Lanzando TaskbarHero desde Steam con trainer activado...");
        }
        catch (Exception ex)
        {
            _doorstopArmedForTrainerLaunch = false;
            SetStatus("No pude lanzar Steam: " + ex.Message);
        }
    }

    private void ConfigureNormalLaunchDefaults()
    {
        TrySetBepInExConsoleEnabled(false, out _);
        TrySetDoorstopEnabled(false, out _);
    }

    private void DisarmTrainerLaunch()
    {
        if (!_doorstopArmedForTrainerLaunch)
        {
            return;
        }

        _doorstopArmedForTrainerLaunch = false;
        TrySetDoorstopEnabled(false, out _);
    }

    private static bool TrySetDoorstopEnabled(bool enabled, out string error)
    {
        return TrySetIniValue(DoorstopConfigPath, "General", "enabled", enabled ? "true" : "false", out error);
    }

    private static bool TrySetBepInExConsoleEnabled(bool enabled, out string error)
    {
        return TrySetIniValue(BepInExConfigPath, "Logging.Console", "Enabled", enabled ? "true" : "false", out error);
    }

    private static bool TrySetIniValue(string path, string section, string key, string value, out string error)
    {
        error = string.Empty;
        try
        {
            if (!File.Exists(path))
            {
                error = "no existe " + path;
                return false;
            }

            string[] lines = File.ReadAllLines(path);
            bool inSection = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    inSection = string.Equals(trimmed.Trim('[', ']'), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                {
                    continue;
                }

                if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    int equalsIndex = trimmed.IndexOf('=');
                    if (equalsIndex < 0)
                    {
                        continue;
                    }

                    string currentKey = trimmed.Substring(0, equalsIndex).Trim();
                    if (!string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string newLine = key + " = " + value;
                    if (!string.Equals(lines[i].Trim(), newLine, StringComparison.Ordinal))
                    {
                        lines[i] = newLine;
                        File.WriteAllLines(path, lines, Encoding.UTF8);
                    }

                    return true;
                }
            }

            error = $"no encontre {section}.{key}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void SetStatus(string text)
    {
        _statusBox.Text = $"{DateTime.Now:HH:mm:ss}  {text}";
    }
}
