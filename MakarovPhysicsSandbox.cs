using MakarovPhysicsSandbox.Core;

namespace MakarovPhysicsSandbox
{
    public partial class MakarovPhysicsSandbox : Form
    {
        private GlPanel _gl = null!;
        private ObjectPropertiesPanel _properties = null!;
        private TriggerPropertiesPanel _triggerProperties = null!;
        private TabControl _propertyTabs = null!;
        private ToolStripStatusLabel _status = null!;
        private MenuStrip _menu = null!;
        private ToolStrip _tools = null!;
        private StatusStrip _statusStrip = null!;
        private Panel _hudTop = null!;
        private Panel _hudBottom = null!;
        private Label _hudTitle = null!;
        private Label _hudStatus = null!;
        private Label _hudMode = null!;
        private Label _hudObjective = null!;
        private Label _hudHotbar = null!;
        private Label _hudHints = null!;
        private Panel _playerControls = null!;
        private Panel _playerTopBar = null!;
        private FlowLayoutPanel _playerTopBarList = null!;
        private Button? _topPauseButton;
        private Button? _topSlowMoButton;
        private Panel _playMenu = null!;
        private FlowLayoutPanel _playerControlList = null!;
        private Label _playerControlsTitle = null!;
        private bool _propertyTabsVisibleBeforeFullscreen = true;
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
        private ToolStripButton? _wiringButton;
        private ToolStripDropDownButton? _catalogButton;
        private ContextMenuStrip _catalogContext = null!;
        private ContextMenuStrip _presetContext = null!;
        private Panel _startOverlay = null!;
        private Label _startSubtitle = null!;
        private Panel _resultOverlay = null!;
        private Label _resultTitle = null!;
        private Label _resultDetail = null!;
        private Label _resultStars = null!;
        private ToolStripButton? _startTestButton;
        private readonly LaunchOptions _launchOptions;
        private bool _isFullscreen;
        private FormBorderStyle _previousBorderStyle;
        private Rectangle _previousBounds;
        private FormWindowState _previousWindowState;

        public MakarovPhysicsSandbox() : this(LaunchOptions.Default)
        {
        }

        internal MakarovPhysicsSandbox(LaunchOptions launchOptions)
        {
            _launchOptions = launchOptions;
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
            _gl.StateChanged += () => { UpdateToolbarState(); UpdateResultOverlay(); };
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
            _triggerProperties.SnapTargetRequested += () => _gl.SnapSelectedTriggerTargetToNearestMechanism();
            _triggerProperties.RemoveOutputRequested += index => _gl.RemoveSelectedTriggerOutput(index);
            _triggerProperties.TestOutputRequested += index => _gl.TestSelectedTriggerOutput(index);
            _triggerProperties.ClearOutputsRequested += () => _gl.ClearSelectedTriggerOutputs();
            _triggerProperties.UpdateOutputRequested += (index, action, delay, radius, strength, enabled) =>
                _gl.UpdateSelectedTriggerOutput(index, action, delay, radius, strength, enabled);

            _menu = BuildMenu();
            _tools = BuildToolbar();

            _statusStrip = new StatusStrip();
            _status = new ToolStripStatusLabel("Ready — Q select · M move · O rotate · S scale · 1–9 spawn · E explosion · H help")
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _statusStrip.Items.Add(_status);

            BuildFullscreenHud();
            BuildPlayerControls();
            BuildStartScreen();
            BuildPlayMenu();
            BuildResultOverlay();
            _catalogContext = BuildSpawnCatalogContext();
            _presetContext = BuildPresetContext();

            // Add the fill control first, then the docked bars; WinForms resolves docking
            // by reverse z-order, so this leaves the GL panel filling the space between
            // the toolbar on top and the status bar on the bottom.
            Controls.Add(_gl);
            Controls.Add(_propertyTabs);
            Controls.Add(_tools);
            Controls.Add(_statusStrip);
            Controls.Add(_menu);
            Controls.Add(_hudBottom);
            Controls.Add(_hudTop);
            Controls.Add(_playerControls);
            Controls.Add(_playerTopBar);
            Controls.Add(_startOverlay);
            Controls.Add(_playMenu);
            Controls.Add(_resultOverlay);
            _hudTop.BringToFront();
            _hudBottom.BringToFront();
            _playerControls.BringToFront();
            _playerTopBar.BringToFront();
            _startOverlay.BringToFront();
            _playMenu.BringToFront();
            _resultOverlay.BringToFront();
            MainMenuStrip = _menu;
            ApplyPolishedTheme(_menu, _tools, _statusStrip);
            SetFullscreenHudVisible(false);
            SetPlayerControlsVisible(false);
            SetStartOverlayVisible(false);
            SetPlayMenuVisible(false);
            SetResultOverlayVisible(false);

            Shown += (_, _) => BeginInitialLaunchMode();
            Resize += (_, _) => { LayoutFullscreenHud(); LayoutPlayerTopBar(); LayoutPlayerControls(); LayoutStartScreen(); LayoutPlayMenu(); LayoutResultOverlay(); };
            LayoutFullscreenHud();
            LayoutPlayerControls();
            LayoutStartScreen();
            LayoutResultOverlay();
            UpdateToolbarState();
        }


        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_isFullscreen && keyData == Keys.Escape)
            {
                if (_playMenu?.Visible == true)
                {
                    if (MessageBox.Show(this, "Exit Makarov Physics Sandbox?", "Exit",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        Close();
                }
                else if (MessageBox.Show(this, "Return to the main menu? The current sandbox will be reset.",
                            "Return to menu", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    ShowPlayMenu();
                }
                else
                {
                    _gl.Focus();
                }
                return true;
            }
            if (_startOverlay?.Visible == true && keyData == Keys.Escape)
            {
                SetStartOverlayVisible(false);
                _gl.Focus();
                return true;
            }
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
            if (keyData == Keys.W)
            {
                _gl.ToggleTriggerWiring();
                UpdateToolbarState();
                return true;
            }
            if (keyData == Keys.F6)
            {
                ShowSpawnCatalog();
                return true;
            }
            if (keyData == Keys.F7)
            {
                _gl.SnapSelectedTriggerTargetToNearestMechanism();
                UpdateToolbarState();
                return true;
            }
            if (keyData == Keys.F5)
            {
                ShowStartScreen();
                return true;
            }
            if (keyData == Keys.F8)
            {
                StartVerticalSliceTest();
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
            BeginInvoke(() =>
            {
                _status.Text = text;
                if (_hudStatus != null) _hudStatus.Text = text;
            });
        }


        private void BeginInitialLaunchMode()
        {
            if (!string.IsNullOrWhiteSpace(_launchOptions.Preset))
            {
                _gl.LoadPreset(_launchOptions.Preset);
                _status.Text = $"Preset loaded: {_launchOptions.Preset}";
            }

            if (_launchOptions.PlayMode && !_isFullscreen)
                ToggleFullscreen();

            if (_launchOptions.ShowStartScreen)
                ShowStartScreen();
            else
                _gl.Focus();
        }

        private void BuildStartScreen()
        {
            _startOverlay = new Panel
            {
                Visible = false,
                BackColor = Color.FromArgb(235, 18, 21, 27),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(28),
            };

            var title = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 44,
                Text = "MAKAROV PHYSICS SANDBOX",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold),
                ForeColor = Color.WhiteSmoke,
            };
            _startSubtitle = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 64,
                Text = "Physics sandbox prototype · synthetic dummies · vehicles · mechanisms · chain reactions",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.Gainsboro,
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(90, 16, 90, 16),
            };
            AddStartButton(buttons, "Continue sandbox", () => { SetStartOverlayVisible(false); _gl.Focus(); });
            AddStartButton(buttons, "Start vertical slice: Android Crash Test", () => { SetStartOverlayVisible(false); _gl.LoadVerticalSlice(); StartVerticalSliceTest(); });
            AddStartButton(buttons, "Load vertical slice room", () => { SetStartOverlayVisible(false); _gl.LoadVerticalSlice(); UpdateToolbarState(); _gl.Focus(); });
            AddStartButton(buttons, "Android Stress Chamber", () => LoadStartPreset("Android Stress Chamber"));
            AddStartButton(buttons, "Piston Crusher Lab", () => LoadStartPreset("Piston Crusher Lab"));
            AddStartButton(buttons, "Conveyor Chain Lab", () => LoadStartPreset("Conveyor Chain Lab"));
            AddStartButton(buttons, "Bridge Jump", () => LoadStartPreset("Bridge Jump"));
            AddStartButton(buttons, "Catapult Bridge Siege", () => LoadStartPreset("Catapult Bridge Siege"));
            AddStartButton(buttons, "Drone Target Range", () => LoadStartPreset("Drone Target Range"));
            AddStartButton(buttons, "Open spawn catalog", () => { SetStartOverlayVisible(false); ShowSpawnCatalog(); });
            AddStartButton(buttons, "Return to editor view", () =>
            {
                SetStartOverlayVisible(false);
                if (_isFullscreen) ToggleFullscreen();
                _gl.Focus();
            });

            var footer = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = 32,
                Text = "F5 title screen · F8 start test · F6 catalog · F11 editor/play view · Esc close overlay",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(170, 180, 190),
            };

            _startOverlay.Controls.Add(buttons);
            _startOverlay.Controls.Add(_startSubtitle);
            _startOverlay.Controls.Add(title);
            _startOverlay.Controls.Add(footer);
        }

        // Player-facing start screen for play/fullscreen mode (the default Steam front-end):
        // Enter sandbox / Choose preset / Exit. Distinct from the editor title overlay above.
        private void BuildPlayMenu()
        {
            _playMenu = new Panel
            {
                Visible = false,
                BackColor = Color.FromArgb(248, 12, 14, 20),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(40, 30, 40, 22),
            };

            var title = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 66,
                Text = "MAKAROV PHYSICS SANDBOX",   // swap for the final game name once decided
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 246, 248),
                BackColor = Color.Transparent,
            };
            var accent = new Panel
            {
                Dock = DockStyle.Top,
                Height = 3,
                BackColor = Color.FromArgb(226, 96, 52),
            };
            var subtitle = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 44,
                Text = "Build it. Wreck it. Repeat.",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 11.5F),
                ForeColor = Color.FromArgb(176, 184, 196),
                BackColor = Color.Transparent,
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(48, 24, 48, 8),
                BackColor = Color.FromArgb(248, 12, 14, 20),
            };
            AddPlayMenuButton(buttons, "Enter sandbox", primary: true, () => EnterSandboxFromMenu(loadSandbox: true));
            AddPlayMenuButton(buttons, "Choose a preset", primary: false, () => { EnterSandboxFromMenu(loadSandbox: false); ShowPresets(); });
            AddPlayMenuButton(buttons, "Exit", primary: false, Close);

            var footer = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = 28,
                Text = "Alpha build  ·  Esc opens this menu during play",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(120, 130, 142),
                BackColor = Color.Transparent,
            };

            _playMenu.Controls.Add(buttons);
            _playMenu.Controls.Add(footer);
            _playMenu.Controls.Add(subtitle);
            _playMenu.Controls.Add(accent);
            _playMenu.Controls.Add(title);
        }

        private void AddPlayMenuButton(FlowLayoutPanel parent, string text, bool primary, Action action)
        {
            var b = new Button
            {
                Text = text,
                Width = 384,
                Height = 54,
                Margin = new Padding(0, 7, 0, 7),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false,
                ForeColor = primary ? Color.Black : Color.WhiteSmoke,
                BackColor = primary ? Color.FromArgb(226, 96, 52) : Color.FromArgb(40, 46, 56),
            };
            b.FlatAppearance.BorderColor = primary ? Color.FromArgb(240, 132, 92) : Color.FromArgb(78, 88, 104);
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(238, 120, 78) : Color.FromArgb(54, 62, 76);
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(198, 78, 40) : Color.FromArgb(32, 38, 48);
            b.Click += (_, _) => action();
            parent.Controls.Add(b);
        }

        private void LayoutPlayMenu()
        {
            if (_playMenu == null) return;
            int w = Math.Min(560, ClientSize.Width - 80);
            int h = Math.Min(470, ClientSize.Height - 80);
            _playMenu.Bounds = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }

        private void SetPlayMenuVisible(bool visible)
        {
            if (_playMenu == null) return;
            _playMenu.Visible = visible;
            if (visible)
            {
                LayoutPlayMenu();
                _playMenu.BringToFront();
            }
        }

        // Show the play-mode menu and hide the in-play HUD/controls behind it.
        private void ShowPlayMenu()
        {
            SetFullscreenHudVisible(false);
            SetPlayerControlsVisible(false);
            SetPlayMenuVisible(true);
        }

        // Leave the menu and reveal the in-play HUD/controls. Optionally (re)load the sandbox scene.
        private void EnterSandboxFromMenu(bool loadSandbox)
        {
            SetPlayMenuVisible(false);
            if (loadSandbox) _gl.Reset();
            SetFullscreenHudVisible(true);
            SetPlayerControlsVisible(true);
            UpdateToolbarState();
            _gl.Focus();
        }

        private void AddStartButton(FlowLayoutPanel parent, string text, Action action)
        {
            var b = new Button
            {
                Text = text,
                Width = 360,
                Height = 42,
                Margin = new Padding(0, 6, 0, 6),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(42, 48, 58),
                ForeColor = Color.WhiteSmoke,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(78, 88, 104);
            b.Click += (_, _) => action();
            parent.Controls.Add(b);
        }

        private void LoadStartPreset(string name)
        {
            _gl.LoadPreset(name);
            _status.Text = $"Preset loaded: {name}";
            SetStartOverlayVisible(false);
            UpdateToolbarState();
            _gl.Focus();
        }

        private void LayoutStartScreen()
        {
            if (_startOverlay == null) return;
            int w = Math.Min(620, Math.Max(420, ClientSize.Width - 80));
            int h = Math.Min(520, Math.Max(420, ClientSize.Height - 80));
            _startOverlay.Bounds = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }

        private void SetStartOverlayVisible(bool visible)
        {
            if (_startOverlay == null) return;
            _startOverlay.Visible = visible;
            if (visible)
            {
                LayoutStartScreen();
                _startOverlay.BringToFront();
            }
        }

        private void ShowStartScreen()
        {
            if (_startSubtitle != null)
                _startSubtitle.Text = _isFullscreen
                    ? "Play-mode shell · choose a preset, open the catalog, or continue the current scene"
                    : "Editor shell · use F11 for play view, or choose a preset to test the sandbox loop";
            SetStartOverlayVisible(true);
        }

        private void BuildResultOverlay()
        {
            _resultOverlay = new Panel
            {
                Visible = false,
                BackColor = Color.FromArgb(238, 16, 18, 24),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(24),
            };

            _resultTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold),
                ForeColor = Color.WhiteSmoke,
            };
            _resultStars = new Label
            {
                Dock = DockStyle.Top,
                Height = 38,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 218, 92),
            };
            _resultDetail = new Label
            {
                Dock = DockStyle.Top,
                Height = 86,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.Gainsboro,
            };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(70, 8, 70, 8),
            };
            AddStartButton(buttons, "Retry test", () => { SetResultOverlayVisible(false); _gl.RetryVerticalSlice(); });
            AddStartButton(buttons, "Back to title", () => { SetResultOverlayVisible(false); ShowStartScreen(); });
            AddStartButton(buttons, "Continue sandbox", () => { SetResultOverlayVisible(false); _gl.DismissVerticalSliceResult(); _gl.Focus(); });

            _resultOverlay.Controls.Add(buttons);
            _resultOverlay.Controls.Add(_resultDetail);
            _resultOverlay.Controls.Add(_resultStars);
            _resultOverlay.Controls.Add(_resultTitle);
        }

        private void LayoutResultOverlay()
        {
            if (_resultOverlay == null) return;
            int w = Math.Min(520, Math.Max(380, ClientSize.Width - 100));
            int h = Math.Min(360, Math.Max(300, ClientSize.Height - 120));
            _resultOverlay.Bounds = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }

        private void SetResultOverlayVisible(bool visible)
        {
            if (_resultOverlay == null) return;
            _resultOverlay.Visible = visible;
            if (visible)
            {
                LayoutResultOverlay();
                _resultOverlay.BringToFront();
            }
        }

        private void UpdateResultOverlay()
        {
            if (_resultOverlay == null || !_gl.IsVerticalSliceFinished) return;
            _resultTitle.Text = _gl.VerticalSliceResultTitle;
            _resultDetail.Text = _gl.VerticalSliceResultDetail;
            _resultStars.Text = new string('★', _gl.VerticalSliceStars) + new string('☆', Math.Max(0, 3 - _gl.VerticalSliceStars));
            SetResultOverlayVisible(true);
        }

        private void StartVerticalSliceTest()
        {
            SetStartOverlayVisible(false);
            SetResultOverlayVisible(false);
            _gl.StartVerticalSliceTest();
            UpdateToolbarState();
        }

        private void BuildFullscreenHud()
        {
            _hudTop = new Panel
            {
                Visible = false,
                BackColor = Color.FromArgb(24, 28, 34),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(12, 10, 12, 10),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Height = 92,
            };

            _hudTitle = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 22,
                Text = "MAKAROV PHYSICS SANDBOX — PLAY MODE",
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                ForeColor = Color.WhiteSmoke,
            };
            _hudObjective = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = 20,
                Font = new Font("Segoe UI", 9F, FontStyle.Italic),
                ForeColor = Color.FromArgb(255, 230, 164),
                Text = "Free sandbox — load a preset or challenge to see a goal here.",
            };
            _hudStatus = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = _status?.Text ?? "Ready",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.Gainsboro,
            };
            _hudMode = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Right,
                Width = 320,
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(124, 214, 255),
            };
            _hudTop.Controls.Add(_hudStatus);
            _hudTop.Controls.Add(_hudMode);
            _hudTop.Controls.Add(_hudObjective);
            _hudTop.Controls.Add(_hudTitle);

            _hudBottom = new Panel
            {
                Visible = false,
                BackColor = Color.FromArgb(24, 28, 34),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(12, 8, 12, 8),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Height = 64,
            };
            _hudHotbar = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 22,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                ForeColor = Color.WhiteSmoke,
                Text = "[F8] Start Test  [F6] Catalog  [0] Android  [N] Vehicle  [E] Boom  [I] Fire  [D] Shock",
            };
            _hudHints = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.Gainsboro,
                Text = "F11 editor view · H help · R reset · Space/F shoot · P pause · T slow-mo · G gravity",
            };
            _hudBottom.Controls.Add(_hudHints);
            _hudBottom.Controls.Add(_hudHotbar);
        }

        private void LayoutFullscreenHud()
        {
            if (_hudTop == null || _hudBottom == null) return;
            int margin = 16;
            _hudTop.Left = margin;
            _hudTop.Top = _menu.Visible ? _menu.Bottom + 10 : margin;
            _hudTop.Width = ClientSize.Width - margin * 2;

            _hudBottom.Left = margin;
            _hudBottom.Top = ClientSize.Height - _hudBottom.Height - margin;
            _hudBottom.Width = ClientSize.Width - margin * 2;
        }

        private void SetFullscreenHudVisible(bool visible)
        {
            if (_hudTop == null || _hudBottom == null) return;
            _hudTop.Visible = visible;
            _hudBottom.Visible = visible;
            if (visible)
            {
                LayoutFullscreenHud();
                _hudTop.BringToFront();
                _hudBottom.BringToFront();
                UpdateHudState();
            }
        }

        private void BuildPlayerControls()
        {
            _playerControls = new Panel
            {
                Visible = false,
                BackColor = Color.FromArgb(230, 18, 22, 30),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(8),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom,
                Width = 292,
            };

            _playerControlsTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 34,
                Text = "PLAYER TOOLS",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                ForeColor = Color.WhiteSmoke,
            };

            _playerControlList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = true,
                AutoScroll = true,
                Padding = new Padding(2),
            };

            AddPlayerButton("Object", "catalog.png", "F6", ShowSpawnCatalog);
            AddPlayerButton("Presets", "presets.png", "", ShowPresets);
            AddPlayerButton("Explosion", "explosion.png", "E", () => _gl.Detonate());
            AddPlayerButton("Shoot", "shoot.png", "F", () => _gl.Shoot());
            AddPlayerButton("Barrel", "barrel.png", "", () => _gl.SpawnExplosiveBarrel());
            AddPlayerButton("Cylinder", "cylinder.png", "", () => _gl.SpawnCylinder());
            AddPlayerButton("Gas", "gascylinder.png", "", () => _gl.SpawnGasCylinder());
            AddPlayerButton("Beach", "beachball.png", "", () => _gl.SpawnBeachBall());
            AddPlayerButton("Metal", "metalcube.png", "", () => _gl.SpawnMetalCube());
            AddPlayerButton("Cart", "cart.png", "", () => _gl.SpawnWoodenCart());
            AddPlayerButton("Glass", "glass.png", "", () => _gl.SpawnGlassBlock());
            AddPlayerButton("Drone", "drone.png", "", () => _gl.SpawnDroneTarget());
            AddPlayerButton("Target", "sentinel.png", "", () => _gl.SpawnSentinelBot());
            AddPlayerButton("Vehicle", "vehicle.png", "N", () => _gl.SpawnVehicle());
            AddPlayerButton("Police", "police.png", "", () => _gl.SpawnPoliceVehicle());
            AddPlayerButton("Ambulance", "ambulance.png", "", () => _gl.SpawnAmbulance());
            AddPlayerButton("Android", "android.png", "0", () => _gl.SpawnAndroid());
            AddPlayerButton("Bridge", "bridge.png", "", () => _gl.SpawnBridgeSpan());
            AddPlayerButton("Catapult", "catapult.png", "", () => _gl.SpawnCatapultLauncher());
            AddPlayerButton("Wrecking", "wreckingball.png", "", () => _gl.SpawnWreckingBallTarget());
            AddPlayerButton("Fire", "torch.png", "I", () => _gl.Ignite());
            AddPlayerButton("Shock", "electricity.png", "D", () => _gl.Electrify());
            AddPlayerButton("Water", "water.png", "V", () => _gl.Water());
            AddPlayerButton("Gravity", "gravity.png", "G", () => _gl.Gravity());
            AddPlayerButton("Attract", "attractor.png", "", () => _gl.Attractor());
            AddPlayerButton("Repel", "repeller.png", "", () => _gl.Repeller());
            AddPlayerButton("Wind", "wind.png", "", () => _gl.Wind());
            AddPlayerButton("Connect", "connect.png", "J", () => _gl.Connect());
            AddPlayerButton("Spring", "spring.png", "K", () => _gl.Spring());
            AddPlayerButton("Disconnect", "disconnect.png", "Del", () => _gl.Disconnect());
            AddPlayerButton("Conveyor", "conveyor.png", "", () => _gl.SpawnConveyor());
            AddPlayerButton("Piston", "piston.png", "", () => _gl.SpawnPiston());
            AddPlayerButton("Door", "door.png", "", () => _gl.SpawnSlidingDoor());

            BuildPlayerTopBar();

            _playerControls.Controls.Add(_playerControlList);
            _playerControls.Controls.Add(_playerControlsTitle);
        }

        // The top bar holds session/program controls (menu, pause, reset...) kept apart from the
        // object/tool buttons in the left panel, so players don't hunt for Reset among the spawners.
        private void BuildPlayerTopBar()
        {
            _playerTopBar = new Panel
            {
                Visible = false,
                BackColor = Color.FromArgb(22, 26, 32),
                Height = 64,
            };
            // thin accent rule along the bottom edge for a finished look
            _playerTopBar.Controls.Add(new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 2,
                BackColor = Color.FromArgb(226, 96, 52),
            });
            _playerTopBarList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(8, 6, 8, 6),
                BackColor = Color.FromArgb(22, 26, 32),
            };
            _playerTopBar.Controls.Add(_playerTopBarList);

            AddTopBarButton("Menu", "select.png", "Esc", ShowPlayMenu);
            _topPauseButton = AddTopBarButton("Pause", "pause.png", "P", () => _gl.TogglePause());
            _topSlowMoButton = AddTopBarButton("Slow-mo", "slowmo.png", "T", () => _gl.ToggleSlowMo());
            AddTopBarButton("Reset", "reset.png", "R", () => _gl.Reset());
            AddTopBarButton("Editor", "select.png", "F11", ToggleFullscreen);
        }

        private Button AddTopBarButton(string text, string icon, string shortcut, Action action)
        {
            var b = new Button
            {
                Text = string.IsNullOrWhiteSpace(shortcut) ? text : $"{text}  [{shortcut}]",
                Width = 134,
                Height = 48,
                Margin = new Padding(4),
                TextAlign = ContentAlignment.MiddleCenter,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(44, 52, 64),
                ForeColor = Color.WhiteSmoke,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                UseVisualStyleBackColor = false,
            };
            var img = LoadIcon(icon);
            if (img != null) b.Image = new Bitmap(img, new Size(22, 22));
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 96, 116);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 68, 84);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(34, 40, 50);
            b.Click += (_, _) =>
            {
                action();
                UpdateToolbarState();
                _gl.Focus();
            };
            _playerTopBarList.Controls.Add(b);
            return b;
        }

        // Reflect Pause / Slow-mo state on the top-bar buttons (called from UpdateToolbarState, which
        // fires on every state change incl. the P/T keyboard shortcuts).
        private void UpdatePlayerControlState()
        {
            if (_gl == null) return;
            var accent = Color.FromArgb(212, 150, 40);
            var idle = Color.FromArgb(44, 52, 64);
            if (_topPauseButton != null)
            {
                bool on = _gl.IsPaused;
                _topPauseButton.BackColor = on ? accent : idle;
                _topPauseButton.ForeColor = on ? Color.Black : Color.WhiteSmoke;
                _topPauseButton.Text = on ? "Resume  [P]" : "Pause  [P]";
            }
            if (_topSlowMoButton != null)
            {
                bool on = _gl.IsSlowMo;
                _topSlowMoButton.BackColor = on ? accent : idle;
                _topSlowMoButton.ForeColor = on ? Color.Black : Color.WhiteSmoke;
            }
        }

        private void AddPlayerHeader(string text)
        {
            // Fullscreen player mode uses compact icon buttons instead of text sections.
        }

        private void AddPlayerButton(string text, string icon, string shortcut, Action action)
        {
            var b = new Button
            {
                Text = string.IsNullOrWhiteSpace(shortcut) ? text : $"{text}\n[{shortcut}]",
                Width = 82,
                Height = 72,
                Margin = new Padding(4),
                TextAlign = ContentAlignment.BottomCenter,
                ImageAlign = ContentAlignment.TopCenter,
                TextImageRelation = TextImageRelation.ImageAboveText,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(38, 45, 56),
                ForeColor = Color.WhiteSmoke,
                Font = new Font("Segoe UI", 7.7F, FontStyle.Bold),
            };
            var img = LoadIcon(icon);
            if (img != null) b.Image = new Bitmap(img, new Size(28, 28));
            b.FlatAppearance.BorderColor = Color.FromArgb(72, 86, 104);
            b.Click += (_, _) =>
            {
                action();
                UpdateToolbarState();
                _gl.Focus();
            };
            _playerControlList.Controls.Add(b);
        }

        private void LayoutPlayerTopBar()
        {
            if (_playerTopBar == null) return;
            int margin = 16;
            int top = (_hudTop != null && _hudTop.Visible) ? _hudTop.Bottom + 8 : margin;
            _playerTopBar.Left = margin;
            _playerTopBar.Top = top;
            _playerTopBar.Width = Math.Max(320, ClientSize.Width - margin * 2);
            _playerTopBar.Height = 64;
        }

        private void LayoutPlayerControls()
        {
            if (_playerControls == null) return;
            int margin = 16;
            int top = (_playerTopBar != null && _playerTopBar.Visible) ? _playerTopBar.Bottom + 12
                    : (_hudTop != null && _hudTop.Visible) ? _hudTop.Bottom + 12 : margin;
            int bottom = (_hudBottom != null && _hudBottom.Visible) ? ClientSize.Height - _hudBottom.Height - margin * 2 : ClientSize.Height - margin;
            _playerControls.Left = margin;
            _playerControls.Top = top;
            _playerControls.Height = Math.Max(260, bottom - top);
        }

        private void SetPlayerControlsVisible(bool visible)
        {
            if (_playerControls == null) return;
            _playerControls.Visible = visible;
            if (_playerTopBar != null) _playerTopBar.Visible = visible;
            if (visible)
            {
                LayoutPlayerTopBar();
                LayoutPlayerControls();
                _playerTopBar?.BringToFront();
                _playerControls.BringToFront();
            }
        }

        private void UpdateHudState()
        {
            if (_hudMode == null || _hudHints == null) return;
            if (_hudStatus != null) _hudStatus.Text = _status?.Text ?? "Ready";

            string mode = _gl.ActiveEditorTool switch
            {
                EditorToolMode.Select => "TOOL: SELECT",
                EditorToolMode.Move => "TOOL: MOVE",
                EditorToolMode.Rotate => "TOOL: ROTATE",
                EditorToolMode.Scale => "TOOL: SCALE",
                _ => "TOOL: SELECT",
            };

            var tags = new List<string>();
            if (_gl.IsPaused) tags.Add("PAUSED");
            if (_gl.IsSlowMo) tags.Add("SLOW-MO");
            if (_gl.IsZeroGravity) tags.Add("ZERO-G");
            if (_gl.IsWaterOn) tags.Add("WATER ON");
            if (_gl.ActiveForceField == ActiveForceFieldKind.Attractor) tags.Add("ATTRACTOR");
            if (_gl.ActiveForceField == ActiveForceFieldKind.Repeller) tags.Add("REPELLER");
            if (_gl.ActiveForceField == ActiveForceFieldKind.Wind) tags.Add("WIND");
            if (_gl.PendingSceneAction != PendingSceneActionKind.None) tags.Add("ARMED: " + _gl.PendingSceneAction.ToString().ToUpperInvariant());
            _hudMode.Text = tags.Count > 0 ? mode + Environment.NewLine + string.Join(" · ", tags) : mode + Environment.NewLine + "SANDBOX READY";

            string title = _gl.CurrentScenarioTitle;
            string goal = _gl.CurrentScenarioGoal;
            if (_hudObjective != null)
                _hudObjective.Text = string.IsNullOrWhiteSpace(title)
                    ? "Free sandbox — load a preset or challenge to see a goal here."
                    : $"Objective — {title}: {goal}";

            _hudHotbar.Text = _isFullscreen
                ? "Player GUI: Start Test · Catalog · Spawn · Effects · Machines · Playback"
                : "[Q] Select  [M] Move  [O] Rotate  [S] Scale  [J] Link  [K] Spring  [Del] Disconnect";
            _hudHints.Text = _isFullscreen
                ? "F11 editor view · F5 title · F6 catalog · F8 start test · R retry/reset · Space/F shoot"
                : "Q/M/O/S tools · 1-0/N objects · J/K/Del links · F4 properties · F11 play view";
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

            _catalogButton = BuildCatalogButton();
            ts.Items.Add(_catalogButton);
            _startTestButton = AddToolbarButton(ts, "Start vertical slice test", "start.png", "F8", StartVerticalSliceTest);
            ts.Items.Add(new ToolStripSeparator());

            AddToolbarButton(ts, "Sphere",       "sphere.png",   "1",        () => _gl.Spawn(1), placeOnScene: true);
            AddToolbarButton(ts, "Box",          "box.png",      "2",        () => _gl.Spawn(2), placeOnScene: true);
            AddToolbarButton(ts, "Plank",        "plank.png",    "4",        () => _gl.Spawn(4), placeOnScene: true);
            AddToolbarButton(ts, "Pillar",       "pillar.png",   "5",        () => _gl.Spawn(5), placeOnScene: true);
            AddToolbarButton(ts, "Dumbbell",     "dumbbell.png", "6",        () => _gl.Spawn(6), placeOnScene: true);
            AddToolbarButton(ts, "Hammer",       "hammer.png",   "7",        () => _gl.Spawn(7), placeOnScene: true);
            AddToolbarButton(ts, "Table",        "table.png",    "8",        () => _gl.Spawn(8), placeOnScene: true);
            AddToolbarButton(ts, "Bowling pins", "pins.png",     "9",        () => _gl.SpawnPins(), placeOnScene: true);
            AddToolbarButton(ts, "Chain",        "chain.png",    "L",        () => _gl.SpawnChain(), placeOnScene: true);
            AddToolbarButton(ts, "Android dummy", "android.png", "0", () => _gl.SpawnAndroid(), placeOnScene: true);
            AddToolbarButton(ts, "Drone target",   "drone.png",   "",  () => _gl.SpawnDroneTarget(), placeOnScene: true);
            AddToolbarButton(ts, "Target dummy", "sentinel.png", "", () => _gl.SpawnSentinelBot(), placeOnScene: true);
            AddToolbarButton(ts, "Vehicle",       "vehicle.png", "N", () => _gl.SpawnVehicle(), placeOnScene: true);
            AddToolbarButton(ts, "Police car", "police.png", "", () => _gl.SpawnPoliceVehicle(), placeOnScene: true);
            AddToolbarButton(ts, "Ambulance", "ambulance.png", "", () => _gl.SpawnAmbulance(), placeOnScene: true);
            AddToolbarButton(ts, "Bridge span",   "bridge.png",  "",  () => _gl.SpawnBridgeSpan(), placeOnScene: true);
            AddToolbarButton(ts, "Catapult launcher", "catapult.png", "", () => _gl.SpawnCatapultLauncher(), placeOnScene: true);
            AddToolbarButton(ts, "Wooden cart", "cart.png", "", () => _gl.SpawnWoodenCart(), placeOnScene: true);
            AddToolbarButton(ts, "Glass block", "glass.png", "", () => _gl.SpawnGlassBlock(), placeOnScene: true);
            AddToolbarButton(ts, "Wrecking ball target", "wreckingball.png", "", () => _gl.SpawnWreckingBallTarget(), placeOnScene: true);
            AddToolbarButton(ts, "Explosive barrel", "barrel.png", "", () => _gl.SpawnExplosiveBarrel(), placeOnScene: true);
            AddToolbarButton(ts, "Cylinder", "cylinder.png", "", () => _gl.SpawnCylinder(), placeOnScene: true);
            AddToolbarButton(ts, "Beach ball", "beachball.png", "", () => _gl.SpawnBeachBall(), placeOnScene: true);
            AddToolbarButton(ts, "Metal cube", "metalcube.png", "", () => _gl.SpawnMetalCube(), placeOnScene: true);
            AddToolbarButton(ts, "Gas cylinder", "gascylinder.png", "", () => _gl.SpawnGasCylinder(), placeOnScene: true);
            AddToolbarButton(ts, "Motor hinge",   "motor.png",   "",  () => _gl.SpawnMotor(), placeOnScene: true);
            AddToolbarButton(ts, "Gate",          "gate.png",    "",  () => _gl.SpawnGate(), placeOnScene: true);
            AddToolbarButton(ts, "Timer",         "timer.png",   "",  () => _gl.SpawnTimer(), placeOnScene: true);
            AddToolbarButton(ts, "Conveyor belt", "conveyor.png", "", () => _gl.SpawnConveyor(), placeOnScene: true);
            AddToolbarButton(ts, "Piston actuator", "piston.png", "", () => _gl.SpawnPiston(), placeOnScene: true);
            AddToolbarButton(ts, "Sliding door", "door.png", "", () => _gl.SpawnSlidingDoor(), placeOnScene: true);

            ts.Items.Add(new ToolStripSeparator());

            AddToolbarButton(ts, "Shoot ball",   "shoot.png",     "Space / F", () => _gl.Shoot());
            AddToolbarButton(ts, "Explosion",    "explosion.png", "E",         () => _gl.Detonate(), placeOnScene: true);
            AddToolbarButton(ts, "Ignite",       "torch.png",     "I",         () => _gl.Ignite(), placeOnScene: true);
            AddToolbarButton(ts, "Electrify",    "electricity.png", "D",       () => _gl.Electrify(), placeOnScene: true);
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
            _wiringButton = AddToolbarButton(ts, "Show/hide trigger wiring", "wiring.png", "W", () => _gl.ToggleTriggerWiring(), checkable: true);
            AddToolbarButton(ts, "Snap selected trigger target", "target.png", "F7", () => _gl.SnapSelectedTriggerTargetToNearestMechanism());

            var presets = new ToolStripDropDownButton("Presets")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Load a ready-made scene",
            };
            AddPresetItem(presets, "Domino Run");
            AddPresetItem(presets, "Tower Collapse");
            AddPresetItem(presets, "Bridge Test");
            AddPresetItem(presets, "Catapult");
            AddPresetItem(presets, "Bridge Jump");
            AddPresetItem(presets, "Catapult Bridge Siege");
            AddPresetItem(presets, "Drone Target Range");
            AddPresetItem(presets, "Newton Cradle");
            AddPresetItem(presets, "Zero-G Chaos");
            AddPresetItem(presets, "Water Playground");
            AddPresetItem(presets, "Trigger Playground");
            AddPresetItem(presets, "Android Fire Lab");
            AddPresetItem(presets, "Electrical Chain Lab");
            AddPresetItem(presets, "Vehicle Crash Test");
            AddPresetItem(presets, "Mechanism Chain Reaction");
            AddPresetItem(presets, "Android Stress Chamber");
            AddPresetItem(presets, "Android Crash Test Chamber");
            AddPresetItem(presets, "Motor Gate Timer Lab");
            AddPresetItem(presets, "Conveyor Chain Lab");
            AddPresetItem(presets, "Piston Crusher Lab");
            AddPresetItem(presets, "Explosive Domino");
            AddPresetItem(presets, "Barrel Pyramid");
            AddPresetItem(presets, "Electric Floor Trap");
            AddPresetItem(presets, "Burning Barricade");
            AddPresetItem(presets, "Wrecking Ball");
            AddPresetItem(presets, "Ragdoll Bowling");
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


        private ToolStripDropDownButton BuildCatalogButton()
        {
            var button = new ToolStripDropDownButton("Catalog")
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Spawn catalog (F6): grouped objects, dummies, mechanisms and hazards",
                AutoSize = true,
            };
            var icon = LoadIcon("catalog.png");
            if (icon != null) button.Image = icon;
            PopulateSpawnCatalog(button.DropDownItems);
            return button;
        }

        private ContextMenuStrip BuildSpawnCatalogContext()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(31, 34, 40),
                ForeColor = Color.Gainsboro,
            };
            PopulateSpawnCatalog(menu.Items);
            return menu;
        }

        private ToolStripMenuItem BuildSpawnCatalogMenu()
        {
            var menu = new ToolStripMenuItem("Catalog");
            PopulateSpawnCatalog(menu.DropDownItems);
            return menu;
        }

        private void PopulateSpawnCatalog(ToolStripItemCollection items)
        {
            items.Clear();

            var basic = CatalogCategory("Basic objects");
            AddCatalogAction(basic.DropDownItems, "Sphere", "sphere.png", "1", () => _gl.Spawn(1), "Round dynamic body");
            AddCatalogAction(basic.DropDownItems, "Box", "box.png", "2", () => _gl.Spawn(2), "General-purpose block");
            AddCatalogAction(basic.DropDownItems, "Plank", "plank.png", "4", () => _gl.Spawn(4), "Long wooden piece");
            AddCatalogAction(basic.DropDownItems, "Pillar", "pillar.png", "5", () => _gl.Spawn(5), "Tall block / support");
            AddCatalogAction(basic.DropDownItems, "Dumbbell", "dumbbell.png", "6", () => _gl.Spawn(6), "Compound test object");
            AddCatalogAction(basic.DropDownItems, "Hammer", "hammer.png", "7", () => _gl.Spawn(7), "Impact-oriented prop");
            AddCatalogAction(basic.DropDownItems, "Table", "table.png", "8", () => _gl.Spawn(8), "Compound furniture prop");
            AddCatalogAction(basic.DropDownItems, "Bowling pins", "pins.png", "9", () => _gl.SpawnPins(), "Ready-made pin group");
            AddCatalogAction(basic.DropDownItems, "Chain", "chain.png", "L", () => _gl.SpawnChain(), "Linked physics chain");
            AddCatalogAction(basic.DropDownItems, "Beach ball", "beachball.png", "", () => _gl.SpawnBeachBall(), "Light bouncy toy ball");
            AddCatalogAction(basic.DropDownItems, "Metal cube", "metalcube.png", "", () => _gl.SpawnMetalCube(), "Heavy conductive block");
            AddCatalogAction(basic.DropDownItems, "Cylinder", "cylinder.png", "", () => _gl.SpawnCylinder(), "Generic metal cylinder");

            var dummies = CatalogCategory("Dummies & vehicles");
            AddCatalogAction(dummies.DropDownItems, "Android dummy", "android.png", "0", () => _gl.SpawnAndroid(), "Synthetic crash-test dummy");
            AddCatalogAction(dummies.DropDownItems, "Drone target", "drone.png", "", () => _gl.SpawnDroneTarget(), "Small synthetic aerial target");
            AddCatalogAction(dummies.DropDownItems, "Target dummy", "sentinel.png", "", () => _gl.SpawnSentinelBot(), "Bottom-heavy wobble dummy: knock it over and it rights itself, or smash it");
            AddCatalogAction(dummies.DropDownItems, "Vehicle", "vehicle.png", "N", () => _gl.SpawnVehicle(), "Simple crash-test vehicle rig");
            AddCatalogAction(dummies.DropDownItems, "Police car", "police.png", "", () => _gl.SpawnPoliceVehicle(), "Vehicle variant for crashes and bridge scenes");
            AddCatalogAction(dummies.DropDownItems, "Ambulance", "ambulance.png", "", () => _gl.SpawnAmbulance(), "Larger emergency vehicle variant");
            AddCatalogAction(dummies.DropDownItems, "Wrecking ball target", "wreckingball.png", "", () => _gl.SpawnWreckingBallTarget(), "Heavy suspended impact target");

            var structures = CatalogCategory("Structures & launchers");
            AddCatalogAction(structures.DropDownItems, "Bridge span", "bridge.png", "", () => _gl.SpawnBridgeSpan(), "Jointed wooden bridge module");
            AddCatalogAction(structures.DropDownItems, "Catapult launcher", "catapult.png", "", () => _gl.SpawnCatapultLauncher(), "One-shot launcher / siege toy");
            AddCatalogAction(structures.DropDownItems, "Wooden cart", "cart.png", "", () => _gl.SpawnWoodenCart(), "Breakable wooden cart sized for barrels and cargo");
            AddCatalogAction(structures.DropDownItems, "Glass block", "glass.png", "", () => _gl.SpawnGlassBlock(), "Fragile glass object that shatters into shards");

            var hazards = CatalogCategory("Hazards & fields");
            AddCatalogAction(hazards.DropDownItems, "Explosive barrel", "barrel.png", "", () => _gl.SpawnExplosiveBarrel(), "Detonates from fire, shock or impact");
            AddCatalogAction(hazards.DropDownItems, "Gas cylinder", "gascylinder.png", "", () => _gl.SpawnGasCylinder(), "Smaller explosive pressure vessel");
            AddCatalogAction(hazards.DropDownItems, "Explosion", "explosion.png", "E", () => _gl.Detonate(), "Place an explosion at the aim point");
            AddCatalogAction(hazards.DropDownItems, "Ignite", "torch.png", "I", () => _gl.Ignite(), "Set a body or place area on fire");
            AddCatalogAction(hazards.DropDownItems, "Electrify", "electricity.png", "D", () => _gl.Electrify(), "Shock a body or conductive chain");
            AddCatalogAction(hazards.DropDownItems, "Water", "water.png", "V", () => _gl.Water(), "Toggle water volume");
            AddCatalogAction(hazards.DropDownItems, "Attractor", "attractor.png", "Z", () => _gl.Attractor(), "Place an attraction field");
            AddCatalogAction(hazards.DropDownItems, "Repeller", "repeller.png", "X", () => _gl.Repeller(), "Place a repulsion field");
            AddCatalogAction(hazards.DropDownItems, "Wind", "wind.png", "U", () => _gl.Wind(), "Place a directional wind field");

            var mechanisms = CatalogCategory("Mechanisms");
            AddCatalogAction(mechanisms.DropDownItems, "Motor hinge", "motor.png", "", () => _gl.SpawnMotor(), "Rotating actuator");
            AddCatalogAction(mechanisms.DropDownItems, "Gate", "gate.png", "", () => _gl.SpawnGate(), "Simple vertical gate");
            AddCatalogAction(mechanisms.DropDownItems, "Timer", "timer.png", "", () => _gl.SpawnTimer(), "Delayed chain-reaction trigger");
            AddCatalogAction(mechanisms.DropDownItems, "Conveyor belt", "conveyor.png", "", () => _gl.SpawnConveyor(), "Moves dynamic bodies along a lane");
            AddCatalogAction(mechanisms.DropDownItems, "Piston actuator", "piston.png", "", () => _gl.SpawnPiston(), "Pushes bodies along a short stroke");
            AddCatalogAction(mechanisms.DropDownItems, "Sliding door", "door.png", "", () => _gl.SpawnSlidingDoor(), "Toggleable sliding blocker");

            var wiring = CatalogCategory("Links & editing");
            AddCatalogAction(wiring.DropDownItems, "Connect objects", "connect.png", "J", () => _gl.Connect(), "Rigid point link between two bodies");
            AddCatalogAction(wiring.DropDownItems, "Spring link", "spring.png", "K", () => _gl.Spring(), "Spring connection between bodies");
            AddCatalogAction(wiring.DropDownItems, "Disconnect object", "disconnect.png", "Delete", () => _gl.Disconnect(), "Remove links from selected object");

            items.Add(basic);
            items.Add(dummies);
            items.Add(structures);
            items.Add(hazards);
            items.Add(mechanisms);
            items.Add(wiring);
        }

        private ToolStripMenuItem CatalogCategory(string name)
        {
            return new ToolStripMenuItem(name)
            {
                ForeColor = Color.Gainsboro,
            };
        }

        private void AddCatalogAction(ToolStripItemCollection items, string text, string icon, string shortcut, Action action, string description)
        {
            var item = new ToolStripMenuItem(text)
            {
                ShortcutKeyDisplayString = shortcut,
                ToolTipText = description,
            };
            var img = LoadIcon(icon);
            if (img != null) item.Image = img;
            item.Click += (_, _) =>
            {
                action();
                _status.Text = string.IsNullOrWhiteSpace(shortcut)
                    ? $"Catalog: {text}. Click inside the scene to place/use it."
                    : $"Catalog: {text} ({shortcut}). Click inside the scene to place/use it.";
                UpdateToolbarState();
                _gl.Focus();
            };
            items.Add(item);
        }

        private Image? LoadIcon(string icon)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "icons", icon);
                if (!File.Exists(path)) return null;
                using var img = Image.FromFile(path);
                return new Bitmap(img);
            }
            catch
            {
                return null;
            }
        }

        private void ShowSpawnCatalog()
        {
            if (_catalogContext == null) return;
            _catalogContext.Show(_gl, new Point(Math.Max(8, _gl.Width / 2 - 140), Math.Max(8, _gl.Height / 2 - 120)));
            _status.Text = "Spawn catalog opened. Choose an item, then click inside the scene to place/use it.";
            UpdateToolbarState();
        }

        private ContextMenuStrip BuildPresetContext()
        {
            var cms = new ContextMenuStrip();
            void Add(string name) => cms.Items.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                _gl.LoadPreset(name);
                _status.Text = $"Preset loaded: {name}";
                UpdateToolbarState();
            }));
            foreach (var name in new[]
            {
                "Domino Run", "Tower Collapse", "Bridge Jump", "Catapult Bridge Siege", "Drone Target Range",
                "Newton Cradle", "Zero-G Chaos", "Water Playground", "Android Fire Lab", "Electrical Chain Lab",
                "Vehicle Crash Test", "Mechanism Chain Reaction", "Conveyor Chain Lab", "Piston Crusher Lab",
                "Explosive Domino", "Barrel Pyramid",
            }) Add(name);
            return cms;
        }

        private void ShowPresets()
        {
            if (_presetContext == null) return;
            _presetContext.Show(_gl, new Point(Math.Max(8, _gl.Width / 2 - 120), Math.Max(8, _gl.Height / 2 - 140)));
            _status.Text = "Presets — pick a ready-made scene.";
            UpdateToolbarState();
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

            var loadedIcon = LoadIcon(icon);
            if (loadedIcon != null)
            {
                b.Image = loadedIcon;
            }
            else
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
            AddItem(actions, "Start vertical slice test", "F8", StartVerticalSliceTest);
            AddItem(actions, "Load vertical slice room", "", () => _gl.LoadVerticalSlice());
            actions.DropDownItems.Add(new ToolStripSeparator());
            AddItem(actions, "Shoot ball",      "Middle mouse / Space / F", () => _gl.Shoot());
            AddItem(actions, "Explosion",       "E", () => _gl.Detonate());
            AddItem(actions, "Ignite",          "I", () => _gl.Ignite());
            AddItem(actions, "Electrify",       "D", () => _gl.Electrify());
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
            AddItem(view, "Show title screen", "F5", ShowStartScreen);
            AddItem(view, "Show / hide trigger wiring", "W", () => _gl.ToggleTriggerWiring());
            AddItem(view, "Snap selected trigger target", "F7", () => _gl.SnapSelectedTriggerTargetToNearestMechanism());
            AddItem(view, "Fullscreen", "F11", ToggleFullscreen);

            var simulation = new ToolStripMenuItem("Simulation");
            AddItem(simulation, "Pause",        "P", () => _gl.TogglePause());
            AddItem(simulation, "Slow motion",  "T", () => _gl.ToggleSlowMo());
            AddItem(simulation, "Single step",  "B", () => _gl.StepOnce());

            var scene = new ToolStripMenuItem("Scene");
            AddItem(scene, "Open spawn catalog", "F6", ShowSpawnCatalog);
            AddItem(scene, "Clear dynamic objects", "C", () => _gl.Clear());
            AddItem(scene, "Reset scene",           "R", () => _gl.Reset());
            scene.DropDownItems.Add(new ToolStripSeparator());
            AddItem(scene, "Place drone target", "", () => _gl.SpawnDroneTarget());
            AddItem(scene, "Place bridge span", "", () => _gl.SpawnBridgeSpan());
            AddItem(scene, "Place catapult launcher", "", () => _gl.SpawnCatapultLauncher());
            AddItem(scene, "Place wooden cart", "", () => _gl.SpawnWoodenCart());
            AddItem(scene, "Place glass block", "", () => _gl.SpawnGlassBlock());
            AddItem(scene, "Place wrecking ball target", "", () => _gl.SpawnWreckingBallTarget());
            AddItem(scene, "Place explosive barrel", "", () => _gl.SpawnExplosiveBarrel());
            AddItem(scene, "Place motor hinge", "", () => _gl.SpawnMotor());
            AddItem(scene, "Place gate", "", () => _gl.SpawnGate());
            AddItem(scene, "Place timer", "", () => _gl.SpawnTimer());
            AddItem(scene, "Place conveyor belt", "", () => _gl.SpawnConveyor());
            AddItem(scene, "Place piston actuator", "", () => _gl.SpawnPiston());
            AddItem(scene, "Place sliding door", "", () => _gl.SpawnSlidingDoor());

            var presets = new ToolStripMenuItem("Presets");
            AddPresetItem(presets, "Domino Run");
            AddPresetItem(presets, "Tower Collapse");
            AddPresetItem(presets, "Bridge Test");
            AddPresetItem(presets, "Catapult");
            AddPresetItem(presets, "Bridge Jump");
            AddPresetItem(presets, "Catapult Bridge Siege");
            AddPresetItem(presets, "Drone Target Range");
            AddPresetItem(presets, "Newton Cradle");
            AddPresetItem(presets, "Zero-G Chaos");
            AddPresetItem(presets, "Water Playground");
            AddPresetItem(presets, "Trigger Playground");
            AddPresetItem(presets, "Android Fire Lab");
            AddPresetItem(presets, "Electrical Chain Lab");
            AddPresetItem(presets, "Vehicle Crash Test");
            AddPresetItem(presets, "Mechanism Chain Reaction");
            AddPresetItem(presets, "Android Stress Chamber");
            AddPresetItem(presets, "Android Crash Test Chamber");
            AddPresetItem(presets, "Motor Gate Timer Lab");
            AddPresetItem(presets, "Conveyor Chain Lab");
            AddPresetItem(presets, "Piston Crusher Lab");
            AddPresetItem(presets, "Explosive Domino");
            AddPresetItem(presets, "Barrel Pyramid");
            AddPresetItem(presets, "Electric Floor Trap");
            AddPresetItem(presets, "Burning Barricade");
            AddPresetItem(presets, "Wrecking Ball");
            AddPresetItem(presets, "Ragdoll Bowling");

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

            var catalog = BuildSpawnCatalogMenu();

            menu.Items.AddRange(new ToolStripItem[] { file, editor, actions, view, simulation, scene, catalog, presets, challenges, campaign, help });
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
            UpdatePlayerControlState();
            if (_propertiesButton != null) _propertiesButton.Checked = _propertyTabs?.Visible == true;
            if (_wiringButton != null) _wiringButton.Checked = _gl.ShowTriggerWiring;

            var field = _gl.ActiveForceField;
            var pendingField = _gl.PendingForceField;
            if (_attractorButton != null) _attractorButton.Checked = field == ActiveForceFieldKind.Attractor || pendingField == ActiveForceFieldKind.Attractor;
            if (_repellerButton != null) _repellerButton.Checked = field == ActiveForceFieldKind.Repeller || pendingField == ActiveForceFieldKind.Repeller;
            if (_windButton != null) _windButton.Checked = field == ActiveForceFieldKind.Wind || pendingField == ActiveForceFieldKind.Wind;

            var pending = _gl.PendingSceneAction;
            if (_connectButton != null) _connectButton.Checked = pending == PendingSceneActionKind.Connect;
            if (_springButton != null) _springButton.Checked = pending == PendingSceneActionKind.Spring;
            if (_disconnectButton != null) _disconnectButton.Checked = pending == PendingSceneActionKind.Disconnect;
            UpdateHudState();
        }

        private void TogglePropertiesPanel()
        {
            if (_propertyTabs == null) return;
            _propertyTabs.Visible = !_propertyTabs.Visible;
            if (!_isFullscreen) _propertyTabsVisibleBeforeFullscreen = _propertyTabs.Visible;
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
                _propertyTabsVisibleBeforeFullscreen = _propertyTabs.Visible;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
                _menu.Visible = false;
                _tools.Visible = false;
                _statusStrip.Visible = false;
                _propertyTabs.Visible = false;
                _isFullscreen = true;
                ShowPlayMenu();   // land on the player start screen, not straight into the scene
                _status.Text = "Fullscreen play view enabled. Player GUI is active; press F11 to return to the editor layout.";
            }
            else
            {
                FormBorderStyle = _previousBorderStyle;
                Bounds = _previousBounds;
                WindowState = _previousWindowState;
                _menu.Visible = true;
                _tools.Visible = true;
                _statusStrip.Visible = true;
                _propertyTabs.Visible = _propertyTabsVisibleBeforeFullscreen;
                _isFullscreen = false;
                SetPlayMenuVisible(false);
                SetFullscreenHudVisible(false);
                SetPlayerControlsVisible(false);
                _status.Text = "Fullscreen disabled.";
            }
            LayoutFullscreenHud();
            LayoutPlayerTopBar();
            LayoutPlayerControls();
            LayoutStartScreen();
            LayoutPlayMenu();
            if (_startOverlay?.Visible == true) ShowStartScreen();
            UpdateToolbarState();
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
            _propertyTabs.ForeColor = Color.Gainsboro;
            _propertyTabs.Appearance = TabAppearance.Normal;
            foreach (TabPage page in _propertyTabs.TabPages)
            {
                page.BackColor = Color.FromArgb(37, 41, 48);
                page.ForeColor = Color.Gainsboro;
            }
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
                "Mechanism workflow:\n" +
                "  Trigger plate — fires one or more outputs when pressed.\n" +
                "  Timer — delayed relay for staged chain reactions.\n" +
                "  Gate / Sliding Door — blocking panels opened by trigger outputs.\n" +
                "  Conveyor — pushes bodies along the belt direction after StartConveyor.\n" +
                "  Piston — actuator/pusher fired by StartPiston.\n" +
                "  Motor Hinge — powered rotating arm started by StartMotor.\n" +
                "  W — show wiring. F7 — snap selected trigger target to nearest compatible mechanism.\n\n" +
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
            // While a menu/result overlay is up, stop the busy GL render loop: it otherwise pegs the
            // message pump and the GL surface overdraws the overlay, so button clicks were getting
            // eaten (the "first click does nothing" issue). Let the UI own input; resume after.
            if (_startOverlay?.Visible == true || _resultOverlay?.Visible == true || _playMenu?.Visible == true)
            {
                _gl.RenderFrame();   // one frame so the dimmed scene stays drawn behind the panel
                return;
            }
            while (AppStillIdle)
                _gl.RenderFrame();
        }

        // true while there is no pending Windows message (PM_NOREMOVE = 0)
        private static bool AppStillIdle => !Win32.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
    }
}
