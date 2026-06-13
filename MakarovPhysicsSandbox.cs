using System.Diagnostics;

namespace MakarovPhysicsSandbox
{
    public partial class MakarovPhysicsSandbox : Form
    {
        private GlPanel _gl = null!;
        private ToolStripStatusLabel _status = null!;

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

            var menu = BuildMenu();
            var tools = BuildToolbar();

            var statusStrip = new StatusStrip();
            _status = new ToolStripStatusLabel("готово") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
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

            // object -> icon file -> action; icons live in the icons\ folder next to the exe
            (string label, string icon, Action act)[] items =
            {
                ("Шар",     "sphere.png",   () => _gl.Spawn(1)),
                ("Куб",     "box.png",      () => _gl.Spawn(2)),
                ("Капсула", "capsule.png",  () => _gl.Spawn(3)),
                ("Доска",   "plank.png",    () => _gl.Spawn(4)),
                ("Колонна", "pillar.png",   () => _gl.Spawn(5)),
                ("Гантель", "dumbbell.png", () => _gl.Spawn(6)),
                ("Молоток", "hammer.png",   () => _gl.Spawn(7)),
                ("Стол",    "table.png",    () => _gl.Spawn(8)),
                ("Кегли",   "pins.png",     () => _gl.SpawnPins()),
                ("Цепь",    "chain.png",    () => _gl.SpawnChain()),
            };

            string dir = Path.Combine(AppContext.BaseDirectory, "icons");
            foreach (var (label, icon, act) in items)
            {
                var b = new ToolStripButton(label)
                {
                    DisplayStyle = ToolStripItemDisplayStyle.Image,
                    ToolTipText = label,
                    AutoSize = false,
                    Size = new Size(44, 44),
                };
                try
                {
                    string path = Path.Combine(dir, icon);
                    if (File.Exists(path)) b.Image = Image.FromFile(path);
                    else b.DisplayStyle = ToolStripItemDisplayStyle.Text; // fall back to a text button
                }
                catch { b.DisplayStyle = ToolStripItemDisplayStyle.Text; }

                b.Click += (_, _) => act();
                ts.Items.Add(b);
            }
            return ts;
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();

            var file = new ToolStripMenuItem("Файл");
            file.DropDownItems.Add(new ToolStripMenuItem("Выход", null, (_, _) => Close()));

            var actions = new ToolStripMenuItem("Действия");
            AddItem(actions, "Выстрел шаром", "СКМ / Space / F", () => _gl.Shoot());
            AddItem(actions, "Взрыв",          "E", () => _gl.Detonate());
            AddItem(actions, "Вода",           "V", () => _gl.Water());
            AddItem(actions, "Аттрактор",      "Z", () => _gl.Attractor());
            AddItem(actions, "Репеллер",       "X", () => _gl.Repeller());
            AddItem(actions, "Ветер",          "U", () => _gl.Wind());
            AddItem(actions, "Невесомость",    "G", () => _gl.Gravity());

            var time = new ToolStripMenuItem("Время");
            AddItem(time, "Пауза",        "P", () => _gl.TogglePause());
            AddItem(time, "Замедление",   "T", () => _gl.ToggleSlowMo());
            AddItem(time, "Один шаг",     "B", () => _gl.StepOnce());

            var scene = new ToolStripMenuItem("Сцена");
            AddItem(scene, "Очистить",    "C", () => _gl.Clear());
            AddItem(scene, "Сбросить",    "R", () => _gl.Reset());

            var help = new ToolStripMenuItem("Справка");
            help.DropDownItems.Add(new ToolStripMenuItem("Управление", null, (_, _) => ShowHelp()));

            menu.Items.AddRange(new ToolStripItem[] { file, actions, time, scene, help });
            return menu;
        }

        // Menu items show the hotkey as a hint only. The actual key handling lives in the
        // GL panel (so it works while the mouse is over the scene); wiring real ShortcutKeys
        // here too would fire the action twice.
        private static void AddItem(ToolStripMenuItem parent, string text, string hotkeyHint, Action act)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => act())
            {
                ShortcutKeyDisplayString = hotkeyHint,
            });
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "Мышь:\n" +
                "  ЛКМ — схватить и тащить объект\n" +
                "  ПКМ + движение — вращать камеру\n" +
                "  Колесо — приблизить / отдалить\n" +
                "  СКМ — выстрелить шаром\n\n" +
                "Клавиши:\n" +
                "  1–8 — объекты, 9 — кегли, L — цепь\n" +
                "  Space/F — выстрел, E — взрыв\n" +
                "  Z/X/U — аттрактор/репеллер/ветер, V — вода, G — невесомость\n" +
                "  P — пауза, T — замедление, B — шаг\n" +
                "  C — очистить, R — сбросить\n\n" +
                "Объекты появляются там, куда наведён прицел (янтарный маркер).",
                "Управление", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
