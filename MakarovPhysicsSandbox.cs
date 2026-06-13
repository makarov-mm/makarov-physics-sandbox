namespace MakarovPhysicsSandbox
{
    public partial class MakarovPhysicsSandbox : Form
    {
        private GlPanel _gl = null!;
        private ObjectPropertiesPanel _properties = null!;
        private TriggerPropertiesPanel _triggerProperties = null!;
        private TabControl _propertyTabs = null!;
        private ToolStripStatusLabel _status = null!;
        private ToolStripButton? _waterButton;
        private ToolStripButton? _gravityButton;
        private ToolStripButton? _soundButton;
        private ToolStripButton? _pauseButton;
        private ToolStripButton? _slowMoButton;
        private ToolStripButton? _attractorButton;
        private ToolStripButton? _repellerButton;
        private ToolStripButton? _windButton;
        private ToolStripButton? _connectButton;
        private ToolStripButton? _springButton;
        private ToolStripButton? _disconnectButton;
        private ToolStripButton? _selectToolButton;
        private ToolStripButton? _moveToolButton;
        private ToolStripButton? _rotateToolButton;
        private ToolStripButton? _scaleToolButton;
        private ToolStripButton? _propertiesButton;
        private bool _isFullscreen;
        private FormBorderStyle _previousBorderStyle;
        private Rectangle _previousBounds;
        private FormWindowState _previousWindowState;

        public MakarovPhysicsSandbox()
        {
            InitializeComponent();
            BuildUi();

            // Render continuously while the message queue is empty. vsync (set in the GL
            // panel) throttles this to the monitor refresh, so it doesn't spin the CPU.
            Application.Idle += OnIdle;
        }

        private void BuildUi()
        {
            Text = "Makarov Physics Sandbox";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1180, 760);
            WindowState = FormWindowState.Maximized;
            Font = new Font("Segoe UI", 9F);

            _gl = new GlPanel { Dock = DockStyle.Fill };
            _gl.StatusUpdated += OnStatus;
            _gl.StateChanged += UpdateToolbarState;
            _gl.HelpRequested += ShowHelp;

            _properties = new ObjectPropertiesPanel { Dock = DockStyle.Fill };
            _triggerProperties = new TriggerPropertiesPanel { Dock = DockStyle.Fill };
            _propertyTabs = new TabControl { Dock = DockStyle.Right, Width = 315, Padding = new Point(10, 5) };
            var objectTab = new TabPage("Object");
            objectTab.Controls.Add(_properties);
            var triggerTab = new TabPage("Trigger");
            triggerTab.Controls.Add(_triggerProperties);
            _propertyTabs.TabPages.Add(objectTab);
            _propertyTabs.TabPages.Add(triggerTab);

            _gl.SelectionChanged += snapshot => BeginInvoke(() =>
            {
                _properties.Bind(snapshot);
                if (snapshot != null) _propertyTabs.SelectedTab = objectTab;
            });
            _gl.TriggerSelectionChanged += snapshot => BeginInvoke(() =>
            {
                _triggerProperties.Bind(snapshot);
                if (snapshot != null) _propertyTabs.SelectedTab = triggerTab;
            });
            _properties.ApplyRequested += props => _gl.ApplySelectedBodyProperties(props);
            _properties.ScaleRequested += factor => _gl.ScaleSelectedBody(factor);
            _properties.DeleteRequested += () => _gl.DeleteSelectedBody();
            _properties.DuplicateRequested += () => _gl.DuplicateSelectedBody();
            _triggerProperties.ApplyRequested += props => _gl.ApplySelectedTriggerProperties(props);
            _triggerProperties.DeleteRequested += () => _gl.DeleteSelectedTrigger();
            _triggerProperties.DuplicateRequested += () => _gl.DuplicateSelectedTrigger();

            var menu = BuildMenu();
            var tools = BuildToolbar();

            var statusStrip = new StatusStrip();
            _status = new ToolStripStatusLabel("Ready — Q select · M move · O rotate · S scale · 1–9 spawn · E explosion · H help")
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            statusStrip.Items.Add(_status);

            // Add the fill control first, then the docked bars; WinForms resolves docking
            // by reverse z-order, so this leaves the GL panel filling the space between
            // the toolbar on top and the status bar on the bottom.
            Controls.Add(_gl);
            Controls.Add(_propertyTabs);
            Controls.Add(tools);
            Controls.Add(statusStrip);
            Controls.Add(menu);
            MainMenuStrip = menu;
            ApplyPolishedTheme(menu, tools, statusStrip);

            Shown += (_, _) => _gl.Focus();
            UpdateToolbarState();
        }


        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                SaveSceneAs();
                return true;
            }
            if (keyData == (Keys.Control | Keys.O))
            {
                LoadSceneFromFile();
                return true;
            }
            if (keyData == Keys.F4)
            {
                TogglePropertiesPanel();
                return true;
            }
            if (keyData == Keys.F11)
            {
                ToggleFullscreen();
                return true;
            }
            if (keyData == (Keys.Control | Keys.D))
            {
                _gl.DuplicateSelectedBody();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnStatus(string text)
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(() => _status.Text = text);
        }

        private ToolStrip BuildToolbar()
        {
            var ts = new ToolStrip
            {
                ImageScalingSize = new Size(32, 32),
                GripStyle = ToolStripGripStyle.Hidden,
            };

            _selectToolButton = AddToolbarButton(ts, "Select tool", "select.png", "Q", () => _gl.SetEditorTool(EditorToolMode.Select), checkable: true);
            _moveToolButton = AddToolbarButton(ts, "Move tool", "move.png", "M", () => _gl.SetEditorTool(EditorToolMode.Move), checkable: true);
            _rotateToolButton = AddToolbarButton(ts, "Rotate tool", "rotate.png", "O", () => _gl.SetEditorTool(EditorToolMode.Rotate), checkable: true);
            _scaleToolButton = AddToolbarButton(ts, "Scale tool", "scale.png", "S", () => _gl.SetEditorTool(EditorToolMode.Scale), checkable: true);

            ts.Items.Add(new ToolStripSeparator());

            AddToolbarButton(ts, "Sphere",       "sphere.png",   "1",        () => _gl.Spawn(1), placeOnScene: true);
            AddToolbarButton(ts, "Box",          "box.png",      "2",        () => _gl.Spawn(2), placeOnScene: true);
            AddToolbarButton(ts, "Capsule",      "capsule.png",  "3",        () => _gl.Spawn(3), placeOnScene: true);
            AddToolbarButton(ts, "Plank",        "plank.png",    "4",        () => _gl.Spawn(4), placeOnScene: true);
            AddToolbarButton(ts, "Pillar",       "pillar.png",   "5",        () => _gl.Spawn(5), placeOnScene: true);
            AddToolbarButton(ts, "Dumbbell",     "dumbbell.png", "6",        () => _gl.Spawn(6), placeOnScene: true);
            AddToolbarButton(ts, "Hammer",       "hammer.png",   "7",        () => _gl.Spawn(7), placeOnScene: true);
            AddToolbarButton(ts, "Table",        "table.png",    "8",        () => _gl.Spawn(8), placeOnScene: true);
            AddToolbarButton(ts, "Bowling pins", "pins.png",     "9",        () => _gl.SpawnPins(), placeOnScene: true);
            AddToolbarButton(ts, "Chain",        "chain.png",    "L",        () => _gl.SpawnChain(), placeOnScene: true);

            ts.Items.Add(new ToolStripSeparator());

            AddToolbarButton(ts, "Shoot ball",   "shoot.png",     "Space / F", () => _gl.Shoot());
            AddToolbarButton(ts, "Explosion",    "explosion.png", "E",         () => _gl.Detonate(), placeOnScene: true);
            _waterButton = AddToolbarButton(ts, "Water",          "water.png",     "V", () => _gl.Water(), checkable: true);
            _attractorButton = AddToolbarButton(ts, "Attractor",  "attractor.png", "Z", () => _gl.Attractor(), checkable: true, placeOnScene: true);
            _repellerButton = AddToolbarButton(ts, "Repeller",    "repeller.png",  "X", () => _gl.Repeller(), checkable: true, placeOnScene: true);
            _windButton = AddToolbarButton(ts, "Wind",            "wind.png",      "U", () => _gl.Wind(), checkable: true, placeOnScene: true);
            _gravityButton = AddToolbarButton(ts, "Gravity on/off", "gravity.png", "G", () => _gl.Gravity(), checkable: true);
            _soundButton = AddToolbarButton(ts, "Sound on/off", "sound.png", "", () => _gl.ToggleSound(), checkable: true);

            ts.Items.Add(new ToolStripSeparator());

            _connectButton = AddToolbarButton(ts, "Connect objects", "connect.png", "J", () => _gl.Connect(), checkable: true, placeOnScene: true);
            _springButton = AddToolbarButton(ts, "Spring link", "spring.png", "K", () => _gl.Spring(), checkable: true, placeOnScene: true);
            _disconnectButton = AddToolbarButton(ts, "Disconnect object", "disconnect.png", "Delete", () => _gl.Disconnect(), checkable: true, placeOnScene: true);

            ts.Items.Add(new ToolStripSeparator());

            AddToolbarButton(ts, "Save scene", "save.png", "Ctrl+S", SaveSceneAs);
            AddToolbarButton(ts, "Load scene", "load.png", "Ctrl+O", LoadSceneFromFile);
            _propertiesButton = AddToolbarButton(ts, "Show/hide properties", "properties.png", "F4", TogglePropertiesPanel, checkable: true);

            var presets = new ToolStripDropDownButton("Presets")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Load a ready-made scene",
            };
            AddPresetItem(presets, "Domino Run");
            AddPresetItem(presets, "Tower Collapse");
            AddPresetItem(presets, "Bridge Test");
            AddPresetItem(presets, "Catapult");
            AddPresetItem(presets, "Newton Cradle");
            AddPresetItem(presets, "Zero-G Chaos");
            AddPresetItem(presets, "Water Playground");
            AddPresetItem(presets, "Trigger Playground");
            ts.Items.Add(presets);

            var challenges = new ToolStripDropDownButton("Challenges")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Load a scene with a simple goal",
            };
            AddChallengeItem(challenges, "Hit the Target");
            AddChallengeItem(challenges, "Destroy the Tower");
            AddChallengeItem(challenges, "Bridge Endurance");
            AddChallengeItem(challenges, "Bowling Challenge");
            AddChallengeItem(challenges, "Float or Sink");
            ts.Items.Add(challenges);

            ts.Items.Add(new ToolStripSeparator());

            _pauseButton = AddToolbarButton(ts, "Pause", "pause.png", "P", () => _gl.TogglePause(), checkable: true);
            _slowMoButton = AddToolbarButton(ts, "Slow motion", "slowmo.png", "T", () => _gl.ToggleSlowMo(), checkable: true);
            AddToolbarButton(ts, "Single step", "step.png", "B", () => _gl.StepOnce());
            AddToolbarButton(ts, "Clear dynamic objects", "clear.png", "C", () => _gl.Clear());
            AddToolbarButton(ts, "Reset scene", "reset.png", "R", () => _gl.Reset());
            AddToolbarButton(ts, "Keyboard help", "help.png", "H", ShowHelp);

            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(new ToolStripLabel("Keys: Q select · M move · O rotate · S scale · J connect · K spring · Delete disconnect · Esc cancel · H help")
            {
                Margin = new Padding(8, 1, 0, 2),
            });

            return ts;
        }

        private ToolStripButton AddToolbarButton(ToolStrip ts, string label, string icon, string shortcut, Action act, bool checkable = false, bool placeOnScene = false)
        {
            var b = new ToolStripButton(label)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ToolTipText = placeOnScene
                    ? $"{label} ({shortcut}) — click this button, then click inside the scene to place. Esc cancels."
                    : $"{label} ({shortcut})",
                AutoSize = false,
                Size = new Size(44, 44),
                CheckOnClick = false,
            };

            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "icons", icon);
                if (File.Exists(path))
                {
                    using var img = Image.FromFile(path);
                    b.Image = new Bitmap(img);
                }
                else
                {
                    b.DisplayStyle = ToolStripItemDisplayStyle.Text;
                    b.Text = shortcut;
                }
            }
            catch
            {
                b.DisplayStyle = ToolStripItemDisplayStyle.Text;
                b.Text = shortcut;
            }

            b.Click += (_, _) =>
            {
                act();
                UpdateToolbarState();
            };

            if (checkable) b.CheckOnClick = false;
            ts.Items.Add(b);
            return b;
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();

            var file = new ToolStripMenuItem("File");
            file.DropDownItems.Add(new ToolStripMenuItem("Save scene...", null, (_, _) => SaveSceneAs())
            {
                ShortcutKeyDisplayString = "Ctrl+S",
            });
            file.DropDownItems.Add(new ToolStripMenuItem("Load scene...", null, (_, _) => LoadSceneFromFile())
            {
                ShortcutKeyDisplayString = "Ctrl+O",
            });
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (_, _) => Close()));

            var editor = new ToolStripMenuItem("Editor");
            AddItem(editor, "Select tool", "Q", () => _gl.SetEditorTool(EditorToolMode.Select));
            AddItem(editor, "Move tool", "M", () => _gl.SetEditorTool(EditorToolMode.Move));
            AddItem(editor, "Rotate tool", "O", () => _gl.SetEditorTool(EditorToolMode.Rotate));
            AddItem(editor, "Scale tool", "S", () => _gl.SetEditorTool(EditorToolMode.Scale));

            var actions = new ToolStripMenuItem("Actions");
            AddItem(actions, "Shoot ball",      "Middle mouse / Space / F", () => _gl.Shoot());
            AddItem(actions, "Explosion",       "E", () => _gl.Detonate());
            AddItem(actions, "Water",           "V", () => _gl.Water());
            AddItem(actions, "Attractor",       "Z", () => _gl.Attractor());
            AddItem(actions, "Repeller",        "X", () => _gl.Repeller());
            AddItem(actions, "Wind",            "U", () => _gl.Wind());
            AddItem(actions, "Gravity on/off",  "G", () => _gl.Gravity());
            AddItem(actions, "Sound on/off",     "", () => _gl.ToggleSound());
            actions.DropDownItems.Add(new ToolStripSeparator());
            AddItem(actions, "Connect objects", "J", () => _gl.Connect());
            AddItem(actions, "Spring link", "K", () => _gl.Spring());
            AddItem(actions, "Disconnect object", "Delete", () => _gl.Disconnect());

            var view = new ToolStripMenuItem("View");
            AddItem(view, "Show / hide properties", "F4", TogglePropertiesPanel);
            AddItem(view, "Fullscreen", "F11", ToggleFullscreen);

            var simulation = new ToolStripMenuItem("Simulation");
            AddItem(simulation, "Pause",        "P", () => _gl.TogglePause());
            AddItem(simulation, "Slow motion",  "T", () => _gl.ToggleSlowMo());
            AddItem(simulation, "Single step",  "B", () => _gl.StepOnce());

            var scene = new ToolStripMenuItem("Scene");
            AddItem(scene, "Clear dynamic objects", "C", () => _gl.Clear());
            AddItem(scene, "Reset scene",           "R", () => _gl.Reset());

            var presets = new ToolStripMenuItem("Presets");
            AddPresetItem(presets, "Domino Run");
            AddPresetItem(presets, "Tower Collapse");
            AddPresetItem(presets, "Bridge Test");
            AddPresetItem(presets, "Catapult");
            AddPresetItem(presets, "Newton Cradle");
            AddPresetItem(presets, "Zero-G Chaos");
            AddPresetItem(presets, "Water Playground");
            AddPresetItem(presets, "Trigger Playground");

            var challenges = new ToolStripMenuItem("Challenges");
            AddChallengeItem(challenges, "Hit the Target");
            AddChallengeItem(challenges, "Destroy the Tower");
            AddChallengeItem(challenges, "Bridge Endurance");
            AddChallengeItem(challenges, "Bowling Challenge");
            AddChallengeItem(challenges, "Float or Sink");

            var campaign = new ToolStripMenuItem("Campaign");
            // rebuilt every time it opens, so stars and unlocks stay current
            campaign.DropDownOpening += (_, _) => RebuildCampaignMenu(campaign);
            RebuildCampaignMenu(campaign);

            var help = new ToolStripMenuItem("Help");
            help.DropDownItems.Add(new ToolStripMenuItem("Keyboard controls", null, (_, _) => ShowHelp())
            {
                ShortcutKeyDisplayString = "H",
            });

            menu.Items.AddRange(new ToolStripItem[] { file, editor, actions, view, simulation, scene, presets, challenges, campaign, help });
            return menu;
        }

        private void RebuildCampaignMenu(ToolStripMenuItem campaign)
        {
            campaign.DropDownItems.Clear();

            int count = _gl.CampaignLevelCount;
            for (int i = 0; i < count; i++)
            {
                int index = i; // capture for the closure
                bool unlocked = _gl.IsLevelUnlocked(i);
                int stars = _gl.LevelStars(i);
                string st = new string('★', stars) + new string('☆', 3 - stars);
                string label = unlocked
                    ? $"{i + 1}. {_gl.LevelTitle(i)}   {st}"
                    : $"{i + 1}. {_gl.LevelTitle(i)}   (locked)";

                campaign.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) =>
                {
                    _gl.StartCampaignLevel(index);
                    _status.Text = $"Level {index + 1}: {_gl.LevelTitle(index)} — {_gl.LevelGoal(index)}";
                    UpdateToolbarState();
                })
                { Enabled = unlocked, ToolTipText = _gl.LevelStarHint(i) });
            }

            campaign.DropDownItems.Add(new ToolStripSeparator());
            campaign.DropDownItems.Add(new ToolStripMenuItem("Next level", null, (_, _) => { _gl.NextLevel(); UpdateToolbarState(); })
            { ShortcutKeyDisplayString = "N" });
            campaign.DropDownItems.Add(new ToolStripMenuItem("Retry level", null, (_, _) => { _gl.RetryLevel(); UpdateToolbarState(); })
            { ShortcutKeyDisplayString = "Y" });
            campaign.DropDownItems.Add(new ToolStripSeparator());
            campaign.DropDownItems.Add(new ToolStripMenuItem($"Total: {_gl.CampaignTotalStars} / {count * 3}\u2605") { Enabled = false });
        }

        private void AddPresetItem(ToolStripDropDownItem parent, string name)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                _gl.LoadPreset(name);
                _status.Text = $"Preset loaded: {name}";
                UpdateToolbarState();
            }));
        }

        private void AddChallengeItem(ToolStripDropDownItem parent, string name)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                _gl.LoadChallenge(name);
                _status.Text = $"Challenge loaded: {name}";
                UpdateToolbarState();
            }));
        }


        private void SaveSceneAs()
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save scene",
                Filter = "Makarov Physics Sandbox scene (*.mpscene)|*.mpscene|JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = "mpscene",
                AddExtension = true,
                FileName = "sandbox-scene.mpscene",
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                _gl.SaveScene(dialog.FileName);
                _status.Text = $"Scene saved: {Path.GetFileName(dialog.FileName)}";
                _gl.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not save scene", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadSceneFromFile()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Load scene",
                Filter = "Makarov Physics Sandbox scene (*.mpscene)|*.mpscene|JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = "mpscene",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                _gl.LoadScene(dialog.FileName);
                _status.Text = $"Scene loaded: {Path.GetFileName(dialog.FileName)}";
                UpdateToolbarState();
                _gl.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not load scene", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Menu items show the hotkey as a hint only. The actual key handling lives in the
        // GL panel (so it works while the mouse is over the scene); wiring real ShortcutKeys
        // here too would fire the action twice.
        private void AddItem(ToolStripMenuItem parent, string text, string hotkeyHint, Action act)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) =>
            {
                act();
                UpdateToolbarState();
            })
            {
                ShortcutKeyDisplayString = hotkeyHint,
            });
        }

        private void UpdateToolbarState()
        {
            if (_gl == null) return;

            var tool = _gl.ActiveEditorTool;
            if (_selectToolButton != null) _selectToolButton.Checked = tool == EditorToolMode.Select;
            if (_moveToolButton != null) _moveToolButton.Checked = tool == EditorToolMode.Move;
            if (_rotateToolButton != null) _rotateToolButton.Checked = tool == EditorToolMode.Rotate;
            if (_scaleToolButton != null) _scaleToolButton.Checked = tool == EditorToolMode.Scale;

            if (_waterButton != null) _waterButton.Checked = _gl.IsWaterOn;
            if (_gravityButton != null)
            {
                _gravityButton.Checked = _gl.IsZeroGravity;
                _gravityButton.ToolTipText = _gl.IsZeroGravity ? "Gravity is off (G)" : "Gravity is on (G)";
            }
            if (_soundButton != null)
            {
                _soundButton.Checked = _gl.IsSoundOn;
                _soundButton.ToolTipText = _gl.IsSoundOn ? "Sound is on" : "Sound is off";
            }
            if (_pauseButton != null) _pauseButton.Checked = _gl.IsPaused;
            if (_slowMoButton != null) _slowMoButton.Checked = _gl.IsSlowMo;
            if (_propertiesButton != null) _propertiesButton.Checked = _propertyTabs?.Visible == true;

            var field = _gl.ActiveForceField;
            var pendingField = _gl.PendingForceField;
            if (_attractorButton != null) _attractorButton.Checked = field == ActiveForceFieldKind.Attractor || pendingField == ActiveForceFieldKind.Attractor;
            if (_repellerButton != null) _repellerButton.Checked = field == ActiveForceFieldKind.Repeller || pendingField == ActiveForceFieldKind.Repeller;
            if (_windButton != null) _windButton.Checked = field == ActiveForceFieldKind.Wind || pendingField == ActiveForceFieldKind.Wind;

            var pending = _gl.PendingSceneAction;
            if (_connectButton != null) _connectButton.Checked = pending == PendingSceneActionKind.Connect;
            if (_springButton != null) _springButton.Checked = pending == PendingSceneActionKind.Spring;
            if (_disconnectButton != null) _disconnectButton.Checked = pending == PendingSceneActionKind.Disconnect;
        }

        private void TogglePropertiesPanel()
        {
            if (_propertyTabs == null) return;
            _propertyTabs.Visible = !_propertyTabs.Visible;
            _status.Text = _propertyTabs.Visible ? "Properties panel shown." : "Properties panel hidden. Press F4 to show it again.";
            UpdateToolbarState();
            _gl.Focus();
        }

        private void ToggleFullscreen()
        {
            if (!_isFullscreen)
            {
                _previousBorderStyle = FormBorderStyle;
                _previousBounds = Bounds;
                _previousWindowState = WindowState;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
                _isFullscreen = true;
                _status.Text = "Fullscreen enabled. Press F11 to return.";
            }
            else
            {
                FormBorderStyle = _previousBorderStyle;
                Bounds = _previousBounds;
                WindowState = _previousWindowState;
                _isFullscreen = false;
                _status.Text = "Fullscreen disabled.";
            }
            _gl.Focus();
        }

        private void ApplyPolishedTheme(MenuStrip menu, ToolStrip tools, StatusStrip statusStrip)
        {
            BackColor = Color.FromArgb(28, 31, 36);
            menu.RenderMode = ToolStripRenderMode.Professional;
            tools.RenderMode = ToolStripRenderMode.Professional;
            tools.BackColor = Color.FromArgb(35, 39, 45);
            tools.ForeColor = Color.Gainsboro;
            tools.Padding = new Padding(6, 3, 6, 3);
            menu.BackColor = Color.FromArgb(31, 34, 40);
            menu.ForeColor = Color.Gainsboro;
            statusStrip.BackColor = Color.FromArgb(31, 34, 40);
            statusStrip.ForeColor = Color.Gainsboro;
            _propertyTabs.BackColor = Color.FromArgb(31, 34, 40);
            _properties.ApplyPolishedTheme();
            _triggerProperties.ApplyPolishedTheme();
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "Mouse:\n" +
                "  Left mouse — select/grab an object, or use the active editor tool\n" +
                "  Right mouse + move — rotate the camera\n" +
                "  Mouse wheel — zoom in / out; in Scale mode it scales the selected object\n" +
                "  Middle mouse — shoot a ball\n\n" +
                "Toolbar / menu:\n" +
                "  Q/M/O/S switch editor tools: select, move, rotate and scale.\n" +
                "  Object, explosion, attractor, repeller and wind buttons arm a placement tool.\n" +
                "  Presets include Trigger Playground with pressure plates and automated actions.\n" +
                "  Click a trigger plate to edit action, target, radius, strength and cooldown.\n" +
                "  Then left-click inside the scene to place it. Esc cancels placement.\n\n" +
                "Keyboard:\n" +
                "  1–8 — objects, 9 — bowling pins, L — chain\n" +
                "  Space/F — shoot, E — explosion\n" +
                "  Q — select, M — move, O — rotate, S — scale selected object\n" +
                "  Z/X/U — attractor / repeller / wind, V — water, G — gravity on/off\n" +
                "  P — pause, T — slow motion, B — single physics step\n" +
                "  Ctrl+S/Ctrl+O — save/load scene, F4 — side panel, F11 — fullscreen\n" +
                "  C — clear dynamic objects, R — reset scene, H — this help, Esc — cancel placement\n\n" +
                "Keyboard actions use the current aim point under the cursor marker.",
                "Keyboard controls", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _gl.Focus();
        }

        private void OnIdle(object? sender, EventArgs e)
        {
            while (AppStillIdle)
                _gl.RenderFrame();
        }

        // true while there is no pending Windows message (PM_NOREMOVE = 0)
        private static bool AppStillIdle => !Win32.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
    }
}
