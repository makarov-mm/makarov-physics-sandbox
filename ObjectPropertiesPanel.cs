using System.Numerics;

namespace MakarovPhysicsSandbox;

internal sealed class ObjectPropertiesPanel : Panel
{
    public event Action<SelectedBodyProperties>? ApplyRequested;
    public event Action<float>? ScaleRequested;
    public event Action? DeleteRequested;
    public event Action? DuplicateRequested;

    private readonly Label _title = new() { Text = "Object properties", Dock = DockStyle.Top, Height = 28, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
    private readonly Label _info = new() { Text = "No object selected.\nClick a dynamic object in the scene.", AutoSize = false, Dock = DockStyle.Top, Height = 54 };
    private readonly ComboBox _material = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly CheckBox _static = new() { Text = "Static / frozen", AutoSize = true };
    private readonly CheckBox _breakable = new() { Text = "Breakable", AutoSize = true };
    private readonly NumericUpDown _breakThreshold = Num(1m, 50m, 7.5m, 1);
    private readonly NumericUpDown _flammability = Num(0m, 1.5m, 0.7m, 2);
    private readonly NumericUpDown _conductivity = Num(0m, 1.5m, 0.05m, 2);
    private readonly NumericUpDown _explosivePower = Num(0m, 5m, 0m, 2);
    private readonly NumericUpDown _density = Num(0.001m, 100m, 1m, 3);
    private readonly NumericUpDown _friction = Num(0m, 3m, 0.5m, 2);
    private readonly NumericUpDown _bounce = Num(0m, 2m, 0.3m, 2);
    private readonly NumericUpDown _px = Num(-100m, 100m, 0m, 2);
    private readonly NumericUpDown _py = Num(-100m, 100m, 0m, 2);
    private readonly NumericUpDown _pz = Num(-100m, 100m, 0m, 2);
    private readonly NumericUpDown _vx = Num(-100m, 100m, 0m, 2);
    private readonly NumericUpDown _vy = Num(-100m, 100m, 0m, 2);
    private readonly NumericUpDown _vz = Num(-100m, 100m, 0m, 2);
    private readonly NumericUpDown _cr = Num(0m, 1m, 0.8m, 2);
    private readonly NumericUpDown _cg = Num(0m, 1m, 0.8m, 2);
    private readonly NumericUpDown _cb = Num(0m, 1m, 0.8m, 2);
    private readonly NumericUpDown _scale = Num(0.1m, 5m, 1.2m, 2);
    private bool _hasSelection;
    private bool _updating;

    public ObjectPropertiesPanel()
    {
        Dock = DockStyle.Right;
        Width = 285;
        Padding = new Padding(10);
        // readable dark palette set here (not only in ApplyPolishedTheme) so labels never
        // end up light-on-light if the theme pass is skipped or runs before controls exist
        BackColor = Color.FromArgb(38, 42, 49);
        ForeColor = Color.Gainsboro;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            AutoScroll = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));

        _material.Items.AddRange(Materials.All.Select(m => m.DisplayName).Cast<object>().ToArray());
        _material.SelectedIndex = 0;
        _material.SelectedIndexChanged += (_, _) => ApplyMaterialPresetFromCombo();
        AddRow(layout, "Material", _material);

        AddRow(layout, "Density", _density);
        AddRow(layout, "Friction", _friction);
        AddRow(layout, "Bounciness", _bounce);
        AddHeader(layout, "Position");
        AddRow(layout, "X", _px); AddRow(layout, "Y", _py); AddRow(layout, "Z", _pz);
        AddHeader(layout, "Velocity");
        AddRow(layout, "X", _vx); AddRow(layout, "Y", _vy); AddRow(layout, "Z", _vz);
        AddHeader(layout, "Color RGB");
        AddRow(layout, "R", _cr); AddRow(layout, "G", _cg); AddRow(layout, "B", _cb);
        AddControl(layout, _static);
        AddControl(layout, _breakable);
        AddRow(layout, "Break force", _breakThreshold);
        AddRow(layout, "Flammable", _flammability);
        AddRow(layout, "Conductive", _conductivity);
        AddRow(layout, "Explosive", _explosivePower);

        var apply = new Button { Text = "Apply", Height = 30, Dock = DockStyle.Fill };
        apply.Click += (_, _) => RaiseApply();
        AddControl(layout, apply);

        var scalePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        scalePanel.Controls.Add(_scale);
        var scaleBtn = new Button { Text = "Scale", Width = 76 };
        scaleBtn.Click += (_, _) => { if (_hasSelection) ScaleRequested?.Invoke((float)_scale.Value); };
        scalePanel.Controls.Add(scaleBtn);
        AddLabeledControl(layout, "Size x", scalePanel);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var dup = new Button { Text = "Duplicate", Width = 86 };
        dup.Click += (_, _) => { if (_hasSelection) DuplicateRequested?.Invoke(); };
        var del = new Button { Text = "Delete", Width = 76 };
        del.Click += (_, _) => { if (_hasSelection) DeleteRequested?.Invoke(); };
        buttons.Controls.Add(dup);
        buttons.Controls.Add(del);
        AddControl(layout, buttons);

        Controls.Add(layout);
        Controls.Add(_info);
        Controls.Add(_title);
        ApplyControlTheme(this);
        SetEnabled(false);
    }

    private static void ApplyControlTheme(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label or CheckBox or GroupBox) c.ForeColor = Color.Gainsboro;
            if (c is Button b)
            {
                b.BackColor = Color.FromArgb(52, 60, 72);
                b.ForeColor = Color.WhiteSmoke;
                b.FlatStyle = FlatStyle.Flat;
            }
            ApplyControlTheme(c);
        }
    }

    public void Bind(SelectedBodySnapshot? s)
    {
        _updating = true;
        _hasSelection = s != null;
        if (s == null)
        {
            _info.Text = "No object selected.\nClick an object in the scene.";
            ApplyControlTheme(this);
        SetEnabled(false);
            _updating = false;
            return;
        }

        _info.Text = $"Selected: {s.ChildCount} shape(s), mass {s.Mass:0.###}";
        _material.SelectedIndex = Math.Clamp((int)s.MaterialId, 0, _material.Items.Count - 1);
        _static.Checked = s.IsStatic;
        _breakable.Checked = s.Breakable;
        Set(_breakThreshold, s.BreakThreshold);
        Set(_flammability, s.Flammability);
        Set(_conductivity, s.Conductivity);
        Set(_explosivePower, s.ExplosivePower);
        Set(_density, s.Density); Set(_friction, s.Friction); Set(_bounce, s.Restitution);
        Set(_px, s.Position.X); Set(_py, s.Position.Y); Set(_pz, s.Position.Z);
        Set(_vx, s.Velocity.X); Set(_vy, s.Velocity.Y); Set(_vz, s.Velocity.Z);
        Set(_cr, s.Color.X); Set(_cg, s.Color.Y); Set(_cb, s.Color.Z);
        SetEnabled(true);
        _updating = false;
    }

    private void ApplyMaterialPresetFromCombo()
    {
        if (_updating || !_hasSelection) return;
        int index = _material.SelectedIndex;
        if (index <= 0 || index >= Materials.All.Length) return; // Custom does not overwrite values.

        var m = Materials.All[index];
        Set(_density, m.Density);
        Set(_friction, m.Friction);
        Set(_bounce, m.Restitution);
        Set(_cr, m.Color.X); Set(_cg, m.Color.Y); Set(_cb, m.Color.Z);
        _breakable.Checked = m.Breakable;
        Set(_breakThreshold, m.BreakThreshold);
        Set(_flammability, m.Flammability);
        Set(_conductivity, m.Conductivity);
        Set(_explosivePower, m.ExplosivePower);
    }

    private void RaiseApply()
    {
        if (!_hasSelection || _updating) return;
        ApplyRequested?.Invoke(new SelectedBodyProperties
        {
            IsStatic = _static.Checked,
            MaterialId = _material.SelectedIndex >= 0 && _material.SelectedIndex < Materials.All.Length ? Materials.All[_material.SelectedIndex].Id : MaterialId.Custom,
            Density = (float)_density.Value,
            Friction = (float)_friction.Value,
            Restitution = (float)_bounce.Value,
            Position = new Vector3((float)_px.Value, (float)_py.Value, (float)_pz.Value),
            Velocity = new Vector3((float)_vx.Value, (float)_vy.Value, (float)_vz.Value),
            Color = new Vector3((float)_cr.Value, (float)_cg.Value, (float)_cb.Value),
            Breakable = _breakable.Checked,
            BreakThreshold = (float)_breakThreshold.Value,
            Flammability = (float)_flammability.Value,
            Conductivity = (float)_conductivity.Value,
            ExplosivePower = (float)_explosivePower.Value,
        });
    }


    public void ApplyPolishedTheme()
    {
        BackColor = Color.FromArgb(38, 42, 49);
        ForeColor = Color.Gainsboro;
        ApplyThemeRecursive(this);
        _title.ForeColor = Color.White;
        _info.ForeColor = Color.FromArgb(190, 198, 210);
    }

    private static void ApplyThemeRecursive(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = Color.FromArgb(56, 62, 72);
                    b.ForeColor = Color.White;
                    b.FlatAppearance.BorderColor = Color.FromArgb(82, 91, 105);
                    break;
                case TextBox t:
                    t.BackColor = Color.FromArgb(26, 29, 34);
                    t.ForeColor = Color.White;
                    t.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox cb:
                    cb.BackColor = Color.FromArgb(26, 29, 34);
                    cb.ForeColor = Color.White;
                    cb.FlatStyle = FlatStyle.Flat;
                    break;
                case NumericUpDown n:
                    n.BackColor = Color.FromArgb(26, 29, 34);
                    n.ForeColor = Color.White;
                    break;
                case CheckBox ch:
                    ch.ForeColor = Color.Gainsboro;
                    break;
                case Label l:
                    l.ForeColor = Color.Gainsboro;
                    break;
                case Panel panel:
                    panel.BackColor = Color.FromArgb(38, 42, 49);
                    panel.ForeColor = Color.Gainsboro;
                    break;
            }
            ApplyThemeRecursive(c);
        }
    }

    private void SetEnabled(bool enabled)
    {
        foreach (Control c in Controls) SetEnabledRecursive(c, enabled);
        _title.Enabled = true;
        _info.Enabled = true;
    }

    private static void SetEnabledRecursive(Control c, bool enabled)
    {
        c.Enabled = enabled;
        foreach (Control child in c.Controls) SetEnabledRecursive(child, enabled);
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        DecimalPlaces = decimals,
        Increment = decimals >= 2 ? 0.05m : 0.1m,
        Width = 90,
    };

    private static void Set(NumericUpDown n, float value)
    {
        decimal v = (decimal)Math.Clamp(value, (float)n.Minimum, (float)n.Maximum);
        n.Value = v;
    }

    private static void AddHeader(TableLayoutPanel t, string text)
    {
        var l = new Label
        {
            Text = text, 
            Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), 
            AutoSize = true, 
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 10, 0, 2)
        };

        t.Controls.Add(l, 0, t.RowCount);
        t.SetColumnSpan(l, 2);
        t.RowCount++;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control control) => AddLabeledControl(t, label, control);

    private static void AddLabeledControl(TableLayoutPanel t, string label, Control control)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.Gainsboro }, 0, t.RowCount);
        t.Controls.Add(control, 1, t.RowCount);
        t.RowCount++;
    }

    private static void AddControl(TableLayoutPanel t, Control control)
    {
        t.Controls.Add(control, 0, t.RowCount);
        t.SetColumnSpan(control, 2);
        t.RowCount++;
    }
}
