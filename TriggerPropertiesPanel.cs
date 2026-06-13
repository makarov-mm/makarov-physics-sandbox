using System.Drawing;
using System.Numerics;
using System.Windows.Forms;

namespace MakarovPhysicsSandbox;

internal sealed class TriggerPropertiesPanel : Panel
{
    public event Action<SelectedTriggerProperties>? ApplyRequested;
    public event Action? DeleteRequested;
    public event Action? DuplicateRequested;

    private readonly Label _title = new() { Text = "Trigger properties", Dock = DockStyle.Top, Height = 28, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
    private readonly Label _info = new() { Text = "No trigger selected.\nClick a sensor plate in the scene.", AutoSize = false, Dock = DockStyle.Top, Height = 54 };
    private readonly TextBox _name = new() { Width = 150 };
    private readonly ComboBox _action = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly CheckBox _enabled = new() { Text = "Enabled", AutoSize = true };
    private readonly CheckBox _oneShot = new() { Text = "One shot", AutoSize = true };
    private readonly NumericUpDown _px = Num(-100m, 100m, 0m, 2);
    private readonly NumericUpDown _py = Num(-10m, 30m, 0m, 2);
    private readonly NumericUpDown _pz = Num(-100m, 100m, 0m, 2);
    private readonly NumericUpDown _sx = Num(0.15m, 8m, 0.9m, 2);
    private readonly NumericUpDown _sy = Num(0.02m, 2m, 0.08m, 2);
    private readonly NumericUpDown _sz = Num(0.15m, 8m, 0.9m, 2);
    private readonly NumericUpDown _radius = Num(0.5m, 40m, 5m, 2);
    private readonly NumericUpDown _strength = Num(0.1m, 80m, 10m, 2);
    private readonly NumericUpDown _cooldown = Num(0.05m, 20m, 1m, 2);
    private readonly NumericUpDown _tx = Num(-100m, 100m, 0m, 2);
    private readonly NumericUpDown _ty = Num(-10m, 30m, 0m, 2);
    private readonly NumericUpDown _tz = Num(-100m, 100m, 0m, 2);
    private bool _hasSelection;
    private bool _updating;

    public TriggerPropertiesPanel()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(10);
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

        _action.Items.AddRange(Enum.GetNames(typeof(TriggerActionKind)).Cast<object>().ToArray());
        _action.SelectedIndex = 0;

        AddRow(layout, "Name", _name);
        AddRow(layout, "Action", _action);
        AddControl(layout, _enabled);
        AddControl(layout, _oneShot);
        AddHeader(layout, "Plate position");
        AddRow(layout, "X", _px); AddRow(layout, "Y", _py); AddRow(layout, "Z", _pz);
        AddHeader(layout, "Plate half-size");
        AddRow(layout, "X", _sx); AddRow(layout, "Y", _sy); AddRow(layout, "Z", _sz);
        AddHeader(layout, "Effect");
        AddRow(layout, "Radius", _radius);
        AddRow(layout, "Strength", _strength);
        AddRow(layout, "Cooldown", _cooldown);
        AddHeader(layout, "Target position");
        AddRow(layout, "X", _tx); AddRow(layout, "Y", _ty); AddRow(layout, "Z", _tz);

        var apply = new Button { Text = "Apply trigger", Height = 30, Dock = DockStyle.Fill };
        apply.Click += (_, _) => RaiseApply();
        AddControl(layout, apply);

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
        SetEnabled(false);
    }

    public void Bind(SelectedTriggerSnapshot? s)
    {
        _updating = true;
        _hasSelection = s != null;
        if (s == null)
        {
            _info.Text = "No trigger selected.\nClick a sensor plate in the scene.";
            SetEnabled(false);
            _updating = false;
            return;
        }
        _info.Text = $"Selected: {s.Name} ({s.Action})";
        _name.Text = s.Name;
        _action.SelectedItem = s.Action.ToString();
        _enabled.Checked = s.Enabled;
        _oneShot.Checked = s.OneShot;
        Set(_px, s.Position.X); Set(_py, s.Position.Y); Set(_pz, s.Position.Z);
        Set(_sx, s.HalfExtents.X); Set(_sy, s.HalfExtents.Y); Set(_sz, s.HalfExtents.Z);
        Set(_radius, s.Radius); Set(_strength, s.Strength); Set(_cooldown, s.CooldownSeconds);
        Set(_tx, s.TargetPosition.X); Set(_ty, s.TargetPosition.Y); Set(_tz, s.TargetPosition.Z);
        SetEnabled(true);
        _updating = false;
    }

    private void RaiseApply()
    {
        if (!_hasSelection || _updating) return;
        Enum.TryParse<TriggerActionKind>(_action.SelectedItem?.ToString(), out var action);
        ApplyRequested?.Invoke(new SelectedTriggerProperties
        {
            Name = _name.Text,
            Action = action,
            Enabled = _enabled.Checked,
            OneShot = _oneShot.Checked,
            Position = new Vector3((float)_px.Value, (float)_py.Value, (float)_pz.Value),
            HalfExtents = new Vector3((float)_sx.Value, (float)_sy.Value, (float)_sz.Value),
            Radius = (float)_radius.Value,
            Strength = (float)_strength.Value,
            CooldownSeconds = (float)_cooldown.Value,
            TargetPosition = new Vector3((float)_tx.Value, (float)_ty.Value, (float)_tz.Value),
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
        var l = new Label { Text = text, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
        t.Controls.Add(l, 0, t.RowCount);
        t.SetColumnSpan(l, 2);
        t.RowCount++;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control control)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, t.RowCount);
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
