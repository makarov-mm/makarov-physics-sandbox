using System;
using System.Collections.Generic;
using MakarovPhysicsSandbox.Core;
using MakarovPhysicsSandbox.Physics;

namespace MakarovPhysicsSandbox;

// GL-drawn top menu bar that replaces the old WinForms editor toolbar. Categories open a
// drop-down of items; each item calls a GlPanel action directly. Save/Load run WinForms file
// dialogs that live on the form, so those are raised as events for the form to handle.
public sealed partial class GlPanel
{
    private sealed class TopMenuItem
    {
        public string Label = "";
        public Action Action = () => { };
        public Func<bool>? IsActive;
    }

    private sealed class TopMenuCategory
    {
        public string Label = "";
        public List<TopMenuItem> Items = new();
    }

    public event Action? SaveRequested;
    public event Action? LoadRequested;
    public event Action? MenuRequested;
    public event Action? FullscreenRequested;

    private struct UiRect
    {
        public float X, Y, W, H;
        public readonly bool Has(float px, float py) => px >= X && px <= X + W && py >= Y && py <= Y + H;
    }

    private List<TopMenuCategory>? _topMenu;
    private int _menuOpen = -1;
    private readonly List<UiRect> _menuTopRects = new();
    private readonly List<UiRect> _menuItemRects = new();

    private const float MenuBarH = 38f;
    private const float MenuScale = 0.8f;
    private const float MenuPadX = 14f;
    private const float MenuItemH = 30f;

    private void EnsureTopMenu()
    {
        if (_topMenu != null) return;
        _topMenu = new List<TopMenuCategory>
        {
            new() { Label = "Tools", Items =
            {
                new() { Label = "Select", Action = () => SetEditorTool(EditorToolMode.Select), IsActive = () => ActiveEditorTool == EditorToolMode.Select },
                new() { Label = "Move",   Action = () => SetEditorTool(EditorToolMode.Move),   IsActive = () => ActiveEditorTool == EditorToolMode.Move },
                new() { Label = "Rotate", Action = () => SetEditorTool(EditorToolMode.Rotate), IsActive = () => ActiveEditorTool == EditorToolMode.Rotate },
                new() { Label = "Scale",  Action = () => SetEditorTool(EditorToolMode.Scale),  IsActive = () => ActiveEditorTool == EditorToolMode.Scale },
            }},
            new() { Label = "Fields", Items =
            {
                new() { Label = "Water",     Action = () => { CancelPendingSceneAction(); ToggleWater(); }, IsActive = () => IsWaterOn },
                new() { Label = "Attractor", Action = () => { CancelPendingSceneAction(); AddField(ForceField.Kind.Attract); } },
                new() { Label = "Repeller",  Action = () => { CancelPendingSceneAction(); AddField(ForceField.Kind.Repel); } },
                new() { Label = "Wind",      Action = () => { CancelPendingSceneAction(); AddField(ForceField.Kind.Wind); } },
                new() { Label = "Gravity",   Action = () => { CancelPendingSceneAction(); ToggleGravity(); }, IsActive = () => IsZeroGravity },
            }},
            new() { Label = "Joints", Items =
            {
                new() { Label = "Connect",    Action = () => ArmSceneAction(PendingSceneActionKind.Connect) },
                new() { Label = "Spring",     Action = () => ArmSceneAction(PendingSceneActionKind.Spring) },
                new() { Label = "Disconnect", Action = () => ArmSceneAction(PendingSceneActionKind.Disconnect) },
            }},
            new() { Label = "Sim", Items =
            {
                new() { Label = "Pause",   Action = TogglePause,  IsActive = () => IsPaused },
                new() { Label = "Slow-mo", Action = ToggleSlowMo, IsActive = () => IsSlowMo },
                new() { Label = "Step",    Action = StepOnce },
                new() { Label = "Clear",   Action = () => { CancelPendingSceneAction(); ClearDynamic(); } },
                new() { Label = "Reset",   Action = () => { CancelPendingSceneAction(); Reset(); } },
                new() { Label = "Sound",   Action = ToggleSound,  IsActive = () => IsSoundOn },
            }},
            new() { Label = "Scene", Items =
            {
                new() { Label = "Save", Action = () => SaveRequested?.Invoke() },
                new() { Label = "Load", Action = () => LoadRequested?.Invoke() },
            }},
            new() { Label = "Help", Items =
            {
                new() { Label = "Controls", Action = () => HelpRequested?.Invoke() },
            }},
            new() { Label = "Game", Items =
            {
                new() { Label = "Main menu",  Action = () => MenuRequested?.Invoke() },
                new() { Label = "Fullscreen", Action = () => FullscreenRequested?.Invoke() },
            }},
        };
    }

    private void LayoutTopMenu()
    {
        EnsureTopMenu();
        _menuTopRects.Clear();
        float x = 10f;
        foreach (var cat in _topMenu!)
        {
            float w = _ui.MeasureText(cat.Label, MenuScale) + MenuPadX * 2f;
            _menuTopRects.Add(new UiRect { X = x, Y = 0f, W = w, H = MenuBarH });
            x += w + 2f;
        }

        _menuItemRects.Clear();
        if (_menuOpen >= 0 && _menuOpen < _topMenu.Count)
        {
            var cat = _topMenu[_menuOpen];
            var top = _menuTopRects[_menuOpen];
            float menuW = top.W;
            foreach (var it in cat.Items)
                menuW = Math.Max(menuW, _ui.MeasureText(it.Label, MenuScale) + MenuPadX * 2f);
            float iy = MenuBarH;
            foreach (var _ in cat.Items)
            {
                _menuItemRects.Add(new UiRect { X = top.X, Y = iy, W = menuW, H = MenuItemH });
                iy += MenuItemH;
            }
        }
    }

    private void DrawTopMenu()
    {
        LayoutTopMenu();
        float th = _ui.LineHeight * MenuScale;

        _ui.DrawRect(0f, 0f, _width, MenuBarH, 0.09f, 0.11f, 0.14f, 0.92f);
        for (int i = 0; i < _topMenu!.Count; i++)
        {
            var r = _menuTopRects[i];
            bool hot = i == _menuOpen || r.Has(_lastMouseX, _lastMouseY);
            if (hot) _ui.DrawRect(r.X, r.Y, r.W, r.H, 0.18f, 0.22f, 0.28f, 1f);
            _ui.DrawText(r.X + MenuPadX, r.Y + (MenuBarH - th) * 0.5f, _topMenu[i].Label, 0.92f, 0.94f, 0.98f, 1f, MenuScale);
        }

        if (_menuOpen >= 0 && _menuOpen < _topMenu.Count && _menuItemRects.Count > 0)
        {
            var cat = _topMenu[_menuOpen];
            var first = _menuItemRects[0];
            var last = _menuItemRects[_menuItemRects.Count - 1];
            _ui.DrawRect(first.X, first.Y, first.W, last.Y + last.H - first.Y, 0.11f, 0.13f, 0.17f, 0.97f);
            for (int j = 0; j < cat.Items.Count && j < _menuItemRects.Count; j++)
            {
                var it = cat.Items[j];
                var r = _menuItemRects[j];
                bool hover = r.Has(_lastMouseX, _lastMouseY);
                bool active = it.IsActive?.Invoke() == true;
                if (hover) _ui.DrawRect(r.X, r.Y, r.W, r.H, 0.20f, 0.30f, 0.40f, 1f);
                if (active)
                    _ui.DrawText(r.X + MenuPadX, r.Y + (r.H - th) * 0.5f, it.Label, 1f, 0.82f, 0.32f, 1f, MenuScale);
                else
                    _ui.DrawText(r.X + MenuPadX, r.Y + (r.H - th) * 0.5f, it.Label, 0.90f, 0.92f, 0.96f, 1f, MenuScale);
            }
        }
    }

    // Returns true if the click was on the menu bar or an open drop-down (and was consumed).
    private bool HandleMenuMouseDown(int mx, int my)
    {
        LayoutTopMenu();
        for (int i = 0; i < _menuTopRects.Count; i++)
        {
            if (_menuTopRects[i].Has(mx, my))
            {
                _menuOpen = _menuOpen == i ? -1 : i;
                return true;
            }
        }

        if (_menuOpen >= 0)
        {
            var cat = _topMenu![_menuOpen];
            for (int j = 0; j < _menuItemRects.Count && j < cat.Items.Count; j++)
            {
                if (_menuItemRects[j].Has(mx, my))
                {
                    cat.Items[j].Action();
                    _menuOpen = -1;
                    return true;
                }
            }
            // Click elsewhere closes the open menu and consumes the click.
            _menuOpen = -1;
            return true;
        }

        // Any click on the bar strip itself is consumed so it never reaches the scene.
        return my <= MenuBarH;
    }

    // ---- GL spawn catalog (left panel): grid of square icon tiles ----
    private List<PhysicObjectMenuItem>? _catalogItems;
    private float _catalogScroll;
    private const float CatX = 8f, CatY = 46f, CatW = 332f, CatTitleH = 30f;
    private const float CatPadX = 10f, CatTile = 100f, CatGap = 7f, CatRowStride = 108f;
    private const int CatCols = 3;

    private void EnsureCatalog()
    {
        _catalogItems ??= new List<PhysicObjectMenuItem>(PhysicObjectMenuGenerator.Generate(this));
    }

    private float CatHeight() => Math.Max(120f, _height - CatY - 8f);
    private float CatContentTop() => CatY + CatTitleH + 6f;
    private int CatRowCount() => (_catalogItems!.Count + CatCols - 1) / CatCols;

    private (float x, float y) CatTilePos(int i)
    {
        int col = i % CatCols, row = i / CatCols;
        float x = CatX + CatPadX + col * (CatTile + CatGap);
        float y = CatContentTop() - _catalogScroll + row * CatRowStride;
        return (x, y);
    }

    private void DrawCatalog()
    {
        EnsureCatalog();
        float h = CatHeight();
        float th = _ui.LineHeight * MenuScale;
        _ui.DrawRect(CatX, CatY, CatW, h, 0.08f, 0.10f, 0.13f, 0.88f);
        _ui.DrawRect(CatX, CatY, CatW, CatTitleH, 0.13f, 0.16f, 0.20f, 0.97f);
        _ui.DrawText(CatX + 12f, CatY + (CatTitleH - th) * 0.5f, "Objects", 0.96f, 0.86f, 0.36f, 1f, MenuScale);

        float contentTop = CatY + CatTitleH, contentBottom = CatY + h;
        const float lblScale = 0.64f;
        for (int i = 0; i < _catalogItems!.Count; i++)
        {
            var (tx, ty) = CatTilePos(i);
            if (ty + CatTile < contentTop || ty > contentBottom) continue;   // cull (scissor also clips)
            var it = _catalogItems[i];
            bool hover = _lastMouseX >= tx && _lastMouseX <= tx + CatTile
                       && _lastMouseY >= ty && _lastMouseY <= ty + CatTile
                       && _lastMouseY >= contentTop && _lastMouseY <= contentBottom;

            _ui.DrawRect(tx, ty, CatTile, CatTile, 0.32f, 0.37f, 0.45f, 1f);                 // border
            if (hover) _ui.DrawRect(tx + 1f, ty + 1f, CatTile - 2f, CatTile - 2f, 0.26f, 0.33f, 0.42f, 1f);
            else _ui.DrawRect(tx + 1f, ty + 1f, CatTile - 2f, CatTile - 2f, 0.15f, 0.17f, 0.21f, 1f);

            var lines = WrapLabel(it.Text, CatTile - 8f, lblScale, 2);
            float lh = _ui.LineHeight * lblScale;
            float ly = ty + CatTile - 7f - lines.Count * lh;
            for (int k = 0; k < lines.Count; k++)
            {
                float lw = _ui.MeasureText(lines[k], lblScale);
                _ui.DrawText(tx + (CatTile - lw) * 0.5f, ly + k * lh, lines[k], 0.90f, 0.92f, 0.96f, 1f, lblScale);
            }
            if (!string.IsNullOrEmpty(it.Shortcut))
                _ui.DrawText(tx + 5f, ty + 4f, it.Shortcut, 0.58f, 0.63f, 0.71f, 1f, 0.6f);
        }
    }

    // Greedy word wrap into at most maxLines lines that fit maxW; the final line is ellipsized.
    private List<string> WrapLabel(string text, float maxW, float scale, int maxLines)
    {
        var lines = new List<string>();
        var words = text.Split(' ');
        string cur = "";
        for (int i = 0; i < words.Length; i++)
        {
            string trial = cur.Length == 0 ? words[i] : cur + " " + words[i];
            if (cur.Length == 0 || _ui.MeasureText(trial, scale) <= maxW)
            {
                cur = trial;
            }
            else
            {
                lines.Add(cur);
                cur = words[i];
                if (lines.Count == maxLines - 1)
                {
                    for (i++; i < words.Length; i++) cur += " " + words[i];   // dump the rest onto the last line
                    break;
                }
            }
        }
        if (cur.Length > 0) lines.Add(cur);
        if (lines.Count > 0)
            lines[^1] = _ui.Ellipsize(lines[^1], maxW, scale);
        return lines;
    }

    // Drawn after the catalog text batch is flushed (icons are full-colour, self-flushing quads).
    private void DrawCatalogIcons()
    {
        if (_catalogItems == null) return;
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "icons");
        float h = CatHeight();
        float contentTop = CatY + CatTitleH, contentBottom = CatY + h;
        const float iconSz = 44f;
        for (int i = 0; i < _catalogItems.Count; i++)
        {
            var (tx, ty) = CatTilePos(i);
            if (ty + CatTile < contentTop || ty > contentBottom) continue;
            var it = _catalogItems[i];
            if (string.IsNullOrEmpty(it.Icon)) continue;
            _ui.DrawIcon(tx + (CatTile - iconSz) * 0.5f, ty + 10f, iconSz, iconSz, System.IO.Path.Combine(dir, it.Icon));
        }
    }

    // GL scissor rectangle for the catalog content area (GL coords: origin bottom-left).
    private (int x, int y, int w, int h) CatalogScissor()
    {
        float h = CatHeight();
        int gy = _height - (int)(CatY + h);
        return ((int)CatX, Math.Max(0, gy), (int)CatW, (int)h);
    }

    private bool HandleCatalogMouseDown(int mx, int my)
    {
        EnsureCatalog();
        float h = CatHeight();
        if (!(mx >= CatX && mx <= CatX + CatW && my >= CatY && my <= CatY + h)) return false;
        float contentTop = CatY + CatTitleH, contentBottom = CatY + h;
        if (my < contentTop) return true;   // title strip: consume, do nothing
        for (int i = 0; i < _catalogItems!.Count; i++)
        {
            var (tx, ty) = CatTilePos(i);
            if (mx >= tx && mx <= tx + CatTile && my >= ty && my <= ty + CatTile && my <= contentBottom)
            {
                _menuOpen = -1;
                _catalogItems[i].Callback();
                return true;
            }
        }
        return true;   // inside panel: consume so it never reaches the scene
    }

    private bool HandleCatalogWheel(int mx, int my, int delta)
    {
        EnsureCatalog();
        float h = CatHeight();
        if (!(mx >= CatX && mx <= CatX + CatW && my >= CatY && my <= CatY + h)) return false;
        float content = CatRowCount() * CatRowStride;
        float view = h - CatTitleH;
        float maxScroll = Math.Max(0f, content - view + 8f);
        _catalogScroll = Math.Clamp(_catalogScroll - delta * 0.5f, 0f, maxScroll);
        return true;
    }

    // ---- GL presets panel (right) ----
    private static readonly string[] _presetNames =
    {
        "Domino Run", "Tower Collapse", "Bridge Jump", "Catapult Bridge Siege", "Drone Target Range",
        "Newton Cradle", "Zero-G Chaos", "Water Playground", "Android Fire Lab", "Electrical Chain Lab",
        "Vehicle Crash Test", "Mechanism Chain Reaction", "Conveyor Chain Lab", "Piston Crusher Lab",
        "Explosive Domino", "Barrel Pyramid", "Wrecking Ball", "Ragdoll Bowling",
    };
    private float _presetScroll;
    private const float PreW = 240f, PreTitleH = 30f, PreRowH = 30f, PreY = 46f, PreMargin = 8f;
    private float PreX => _width - PreW - PreMargin;
    private float PreHeight() => Math.Max(120f, _height - PreY - PreMargin);

    private void DrawPresets()
    {
        float x = PreX, h = PreHeight();
        float th = _ui.LineHeight * MenuScale;
        _ui.DrawRect(x, PreY, PreW, h, 0.08f, 0.10f, 0.13f, 0.88f);
        _ui.DrawRect(x, PreY, PreW, PreTitleH, 0.13f, 0.16f, 0.20f, 0.97f);
        _ui.DrawText(x + 12f, PreY + (PreTitleH - th) * 0.5f, "Presets", 0.96f, 0.86f, 0.36f, 1f, MenuScale);

        float contentTop = PreY + PreTitleH, contentBottom = PreY + h;
        float ry = contentTop - _presetScroll;
        for (int i = 0; i < _presetNames.Length; i++)
        {
            float rowY = ry + i * PreRowH;
            if (rowY + PreRowH < contentTop || rowY > contentBottom) continue;
            bool hover = _lastMouseX >= x && _lastMouseX <= x + PreW
                       && _lastMouseY >= contentTop && _lastMouseY <= contentBottom
                       && _lastMouseY >= rowY && _lastMouseY < rowY + PreRowH;

            float bx = x + 5f, by = rowY + 2f, bw = PreW - 10f, bh = PreRowH - 4f;
            _ui.DrawRect(bx, by, bw, bh, 0.32f, 0.37f, 0.45f, 1f);
            if (hover) _ui.DrawRect(bx + 1f, by + 1f, bw - 2f, bh - 2f, 0.26f, 0.33f, 0.42f, 1f);
            else _ui.DrawRect(bx + 1f, by + 1f, bw - 2f, bh - 2f, 0.15f, 0.17f, 0.21f, 1f);

            string label = _ui.Ellipsize(_presetNames[i], PreW - 28f, MenuScale);
            _ui.DrawText(x + 14f, rowY + (PreRowH - th) * 0.5f, label, 0.91f, 0.93f, 0.97f, 1f, MenuScale);
        }
    }

    private (int x, int y, int w, int h) PresetScissor()
    {
        float h = PreHeight();
        int gy = _height - (int)(PreY + h);
        return ((int)PreX, Math.Max(0, gy), (int)PreW, (int)h);
    }

    private bool HandlePresetMouseDown(int mx, int my)
    {
        float x = PreX, h = PreHeight();
        if (!(mx >= x && mx <= x + PreW && my >= PreY && my <= PreY + h)) return false;
        float contentTop = PreY + PreTitleH, contentBottom = PreY + h;
        if (my < contentTop) return true;
        float ry = contentTop - _presetScroll;
        for (int i = 0; i < _presetNames.Length; i++)
        {
            float rowY = ry + i * PreRowH;
            if (my >= rowY && my < rowY + PreRowH && my <= contentBottom)
            {
                _menuOpen = -1;
                LoadPreset(_presetNames[i]);
                return true;
            }
        }
        return true;
    }

    private bool HandlePresetWheel(int mx, int my, int delta)
    {
        float x = PreX, h = PreHeight();
        if (!(mx >= x && mx <= x + PreW && my >= PreY && my <= PreY + h)) return false;
        float content = _presetNames.Length * PreRowH;
        float view = h - PreTitleH;
        float maxScroll = Math.Max(0f, content - view);
        _presetScroll = Math.Clamp(_presetScroll - delta * 0.4f, 0f, maxScroll);
        return true;
    }

    // ---- status line ----
    private string _statusText = "";
    private void DrawStatusLine()
    {
        if (string.IsNullOrEmpty(_statusText)) return;
        float th = _ui.LineHeight * 0.7f;
        _ui.DrawRect(0f, _height - th - 10f, _width, th + 10f, 0.06f, 0.07f, 0.09f, 0.78f);
        _ui.DrawText(12f, _height - th - 5f, _statusText, 0.78f, 0.82f, 0.88f, 1f, 0.7f);
    }

    // ---- GL modal overlays (start screen / play menu / result) ----
    private OverlayKind _overlay = OverlayKind.None;
    private string _ovTitle = "", _ovSubtitle = "";
    private int _ovStars = -1;
    private readonly List<(string label, bool primary, Action action)> _ovButtons = new();
    private const float OvBtnW = 440f, OvBtnH = 46f, OvBtnGap = 12f, OvPanelW = 560f, OvBodyScale = 0.8f;

    private string[] OverlayBodyLines()
        => _ovSubtitle.Length == 0 ? Array.Empty<string>() : _ovSubtitle.Split('\n');

    public bool OverlayVisible => _overlay != OverlayKind.None;
    public bool PlayMenuOpen => _overlay == OverlayKind.PlayMenu;
    public bool StartOpen => _overlay == OverlayKind.Start;

    public void ShowOverlay(OverlayKind kind, string title, string subtitle, int stars,
                            List<(string label, bool primary, Action action)> buttons)
    {
        _overlay = kind;
        _ovTitle = title ?? "";
        _ovSubtitle = subtitle ?? "";
        _ovStars = stars;
        _ovButtons.Clear();
        if (buttons != null) _ovButtons.AddRange(buttons);
        Invalidate();
    }

    public void HideOverlay()
    {
        _overlay = OverlayKind.None;
        _ovButtons.Clear();
        Invalidate();
    }

    private float OverlayHeaderH()
    {
        float h = 22f + _ui.LineHeight * 1.6f + 8f;
        var body = OverlayBodyLines();
        if (body.Length > 0) h += body.Length * (_ui.LineHeight * OvBodyScale) + 12f;
        if (_ovStars >= 0) h += _ui.LineHeight * 1.6f + 12f;
        return h + 10f;
    }

    private (float px, float py, float pw, float ph, float btnTop) OverlayLayout()
    {
        float header = OverlayHeaderH();
        float ph = header + _ovButtons.Count * (OvBtnH + OvBtnGap) + 26f;
        float pw = OvPanelW;
        foreach (var line in OverlayBodyLines())                       // grow to fit the widest body line
            pw = Math.Max(pw, _ui.MeasureText(line, OvBodyScale) + 56f);
        pw = Math.Min(pw, _width - 40f);
        float px = (_width - pw) * 0.5f;
        float py = (_height - ph) * 0.5f;
        return (px, py, pw, ph, py + header);
    }

    private (float x, float y, float w, float h) OverlayButtonRect(int i, float btnTop)
        => ((_width - OvBtnW) * 0.5f, btnTop + i * (OvBtnH + OvBtnGap), OvBtnW, OvBtnH);

    private void DrawOverlay()
    {
        _ui.DrawRect(0f, 0f, _width, _height, 0.04f, 0.05f, 0.07f, 0.82f);   // dim backdrop
        var L = OverlayLayout();
        _ui.DrawRect(L.px, L.py, L.pw, L.ph, 0.07f, 0.08f, 0.11f, 0.99f);
        _ui.DrawRect(L.px, L.py, L.pw, 4f, 0.89f, 0.38f, 0.20f, 1f);          // accent bar

        float ts = 1.6f;
        float tw = _ui.MeasureText(_ovTitle, ts);
        float yy = L.py + 22f;
        _ui.DrawText((_width - tw) * 0.5f, yy, _ovTitle, 0.96f, 0.97f, 0.98f, 1f, ts);
        yy += _ui.LineHeight * ts + 8f;

        var body = OverlayBodyLines();
        if (body.Length > 0)
        {
            float lh = _ui.LineHeight * OvBodyScale;
            bool center = body.Length == 1;
            foreach (var line in body)
            {
                float lx = center ? (_width - _ui.MeasureText(line, OvBodyScale)) * 0.5f : L.px + 26f;
                _ui.DrawText(lx, yy, line, 0.72f, 0.77f, 0.84f, 1f, OvBodyScale);
                yy += lh;
            }
            yy += 12f;
        }
        if (_ovStars >= 0)
        {
            int s = Math.Clamp(_ovStars, 0, 3);
            string stars = new string('*', s) + new string('.', 3 - s);
            float ss = 1.6f;
            float sw = _ui.MeasureText(stars, ss);
            _ui.DrawText((_width - sw) * 0.5f, yy, stars, 1f, 0.85f, 0.36f, 1f, ss);
        }

        for (int i = 0; i < _ovButtons.Count; i++)
        {
            var r = OverlayButtonRect(i, L.btnTop);
            bool hover = _lastMouseX >= r.x && _lastMouseX <= r.x + r.w && _lastMouseY >= r.y && _lastMouseY <= r.y + r.h;
            var b = _ovButtons[i];
            if (b.primary)
                _ui.DrawRect(r.x, r.y, r.w, r.h, hover ? 0.94f : 0.89f, hover ? 0.48f : 0.38f, hover ? 0.30f : 0.20f, 1f);
            else
                _ui.DrawRect(r.x, r.y, r.w, r.h, hover ? 0.22f : 0.16f, hover ? 0.25f : 0.18f, hover ? 0.31f : 0.22f, 1f);
            float bs = 0.95f;
            float bw = _ui.MeasureText(b.label, bs);
            float cr = b.primary ? 0.05f : 0.92f, cg = b.primary ? 0.05f : 0.94f, cb = b.primary ? 0.07f : 0.97f;
            _ui.DrawText((_width - bw) * 0.5f, r.y + (r.h - _ui.LineHeight * bs) * 0.5f, b.label, cr, cg, cb, 1f, bs);
        }
    }

    private void HandleOverlayMouseDown(int mx, int my)
    {
        var L = OverlayLayout();
        for (int i = 0; i < _ovButtons.Count; i++)
        {
            var r = OverlayButtonRect(i, L.btnTop);
            if (mx >= r.x && mx <= r.x + r.w && my >= r.y && my <= r.y + r.h)
            {
                _ovButtons[i].action?.Invoke();
                return;
            }
        }
    }
}

public enum OverlayKind { None, Start, PlayMenu, Result, Dialog }
