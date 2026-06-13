namespace MakarovPhysicsSandbox
{
    public partial class MakarovPhysicsSandbox : Form
    {
        private GlPanel _gl = null!;
        private ToolStripStatusLabel _status = null!;
        private ToolStripButton? _waterButton;
        private ToolStripButton? _gravityButton;
        private ToolStripButton? _pauseButton;
        private ToolStripButton? _slowMoButton;
        private ToolStripButton? _attractorButton;
        private ToolStripButton? _repellerButton;
        private ToolStripButton? _windButton;

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
            _gl = new GlPanel { Dock = DockStyle.Fill };
            _gl.StatusUpdated += OnStatus;
            _gl.StateChanged += UpdateToolbarState;
            _gl.HelpRequested += ShowHelp;

            var menu = BuildMenu();
            var tools = BuildToolbar();

            var statusStrip = new StatusStrip();
            _status = new ToolStripStatusLabel("Ready — keys: 1–9/L objects | Space/F shoot | E explosion | G gravity | P pause | R reset | H help; toolbar placement tools: click icon, then click scene")
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            statusStrip.Items.Add(_status);

            // Add the fill control first, then the docked bars; WinForms resolves docking
            // by reverse z-order, so this leaves the GL panel filling the space between
            // the toolbar on top and the status bar on the bottom.
            Controls.Add(_gl);
            Controls.Add(tools);
            Controls.Add(statusStrip);
            Controls.Add(menu);
            MainMenuStrip = menu;

            Shown += (_, _) => _gl.Focus();
            UpdateToolbarState();
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
            _windButton = AddToolbarButton(ts, "Wind",            "wind.png",      "U", () => _gl.Wind(), checkable: true);
            _gravityButton = AddToolbarButton(ts, "Gravity on/off", "gravity.png", "G", () => _gl.Gravity(), checkable: true);

            ts.Items.Add(new ToolStripSeparator());

            _pauseButton = AddToolbarButton(ts, "Pause", "pause.png", "P", () => _gl.TogglePause(), checkable: true);
            _slowMoButton = AddToolbarButton(ts, "Slow motion", "slowmo.png", "T", () => _gl.ToggleSlowMo(), checkable: true);
            AddToolbarButton(ts, "Single step", "step.png", "B", () => _gl.StepOnce());
            AddToolbarButton(ts, "Clear dynamic objects", "clear.png", "C", () => _gl.Clear());
            AddToolbarButton(ts, "Reset scene", "reset.png", "R", () => _gl.Reset());
            AddToolbarButton(ts, "Keyboard help", "help.png", "H", ShowHelp);

            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(new ToolStripLabel("Keys: 1–9/L objects · Space/F shoot · E explosion · G gravity · Esc cancel placement · H help")
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
            file.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (_, _) => Close()));

            var actions = new ToolStripMenuItem("Actions");
            AddItem(actions, "Shoot ball",      "Middle mouse / Space / F", () => _gl.Shoot());
            AddItem(actions, "Explosion",       "E", () => _gl.Detonate());
            AddItem(actions, "Water",           "V", () => _gl.Water());
            AddItem(actions, "Attractor",       "Z", () => _gl.Attractor());
            AddItem(actions, "Repeller",        "X", () => _gl.Repeller());
            AddItem(actions, "Wind",            "U", () => _gl.Wind());
            AddItem(actions, "Gravity on/off",  "G", () => _gl.Gravity());

            var simulation = new ToolStripMenuItem("Simulation");
            AddItem(simulation, "Pause",        "P", () => _gl.TogglePause());
            AddItem(simulation, "Slow motion",  "T", () => _gl.ToggleSlowMo());
            AddItem(simulation, "Single step",  "B", () => _gl.StepOnce());

            var scene = new ToolStripMenuItem("Scene");
            AddItem(scene, "Clear dynamic objects", "C", () => _gl.Clear());
            AddItem(scene, "Reset scene",           "R", () => _gl.Reset());

            var help = new ToolStripMenuItem("Help");
            help.DropDownItems.Add(new ToolStripMenuItem("Keyboard controls", null, (_, _) => ShowHelp())
            {
                ShortcutKeyDisplayString = "H",
            });

            menu.Items.AddRange(new ToolStripItem[] { file, actions, simulation, scene, help });
            return menu;
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

            if (_waterButton != null) _waterButton.Checked = _gl.IsWaterOn;
            if (_gravityButton != null)
            {
                _gravityButton.Checked = _gl.IsZeroGravity;
                _gravityButton.ToolTipText = _gl.IsZeroGravity ? "Gravity is off (G)" : "Gravity is on (G)";
            }
            if (_pauseButton != null) _pauseButton.Checked = _gl.IsPaused;
            if (_slowMoButton != null) _slowMoButton.Checked = _gl.IsSlowMo;

            var field = _gl.ActiveForceField;
            var pendingField = _gl.PendingForceField;
            if (_attractorButton != null) _attractorButton.Checked = field == ActiveForceFieldKind.Attractor || pendingField == ActiveForceFieldKind.Attractor;
            if (_repellerButton != null) _repellerButton.Checked = field == ActiveForceFieldKind.Repeller || pendingField == ActiveForceFieldKind.Repeller;
            if (_windButton != null) _windButton.Checked = field == ActiveForceFieldKind.Wind;
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "Mouse:\n" +
                "  Left mouse — grab and drag an object\n" +
                "  Right mouse + move — rotate the camera\n" +
                "  Mouse wheel — zoom in / out\n" +
                "  Middle mouse — shoot a ball\n\n" +
                "Toolbar / menu:\n" +
                "  Object, explosion, attractor and repeller buttons arm a placement tool.\n" +
                "  Then left-click inside the scene to place it. Esc cancels placement.\n\n" +
                "Keyboard:\n" +
                "  1–8 — objects, 9 — bowling pins, L — chain\n" +
                "  Space/F — shoot, E — explosion\n" +
                "  Z/X/U — attractor / repeller / wind, V — water, G — gravity on/off\n" +
                "  P — pause, T — slow motion, B — single physics step\n" +
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
