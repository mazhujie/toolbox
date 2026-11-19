using System;
using System.Drawing;
using System.Windows.Forms;

internal static class UiTheme
{
    internal static readonly Color Background = Color.FromArgb(245, 247, 251);
    internal static readonly Color Ink = Color.FromArgb(30, 45, 67);
    internal static readonly Color Muted = Color.FromArgb(82, 99, 123);
    internal static readonly Color Border = Color.FromArgb(207, 218, 233);
    internal static readonly Color Blue = Color.FromArgb(32, 91, 199);
    internal static readonly Font Body = new Font("Microsoft YaHei UI", 10);
    internal static readonly Font Title = new Font(Body.FontFamily, 19, FontStyle.Bold);
    internal static readonly Font Section = new Font(Body.FontFamily, 12, FontStyle.Bold);
    internal static readonly Font Metric = new Font("Segoe UI", 32, FontStyle.Bold);
    internal static readonly Font Address = new Font("Segoe UI", 17, FontStyle.Bold);
    internal static readonly Font Mono = new Font("Consolas", 10);
    internal static readonly Font Small = new Font("Segoe UI", 9);
    internal static readonly Font Dot = new Font("Segoe UI", 8);

    internal static TableLayoutPanel Page(Control parent)
    {
        parent.Font = Body;
        parent.BackColor = Background;
        parent.ForeColor = Ink;
        var table = Stack();
        table.Padding = new Padding(20, 12, 20, 12);
        parent.Controls.Add(table);
        return table;
    }

    internal static TableLayoutPanel Stack(bool autoSize = false)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty, AutoSize = autoSize, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    internal static void Add(TableLayoutPanel table, Control control, bool fill = false)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(fill ? SizeType.Percent : SizeType.AutoSize, fill ? 100 : 0));
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 0, row);
    }

    internal static TableLayoutPanel Row(Control stretch, params Control[] controls)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = controls.Length + (stretch == null ? 1 : 0),
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        foreach (Control control in controls)
        {
            bool fills = control == stretch;
            row.ColumnStyles.Add(new ColumnStyle(fills ? SizeType.Percent : SizeType.AutoSize, fills ? 100 : 0));
            control.Anchor = fills ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
            row.Controls.Add(control);
        }
        if (stretch == null) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return row;
    }

    internal static TableLayoutPanel Columns(bool autoSize, params Control[] controls)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = controls.Length,
            AutoSize = autoSize, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        foreach (Control control in controls)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / controls.Length));
            control.Dock = DockStyle.Fill;
            row.Controls.Add(control);
        }
        return row;
    }

    internal static TableLayoutPanel Card(bool autoSize = false)
    {
        var table = Stack(autoSize);
        table.BackColor = Color.White;
        table.Padding = new Padding(11, 7, 11, 7);
        table.Margin = new Padding(3, 4, 3, 4);
        return table;
    }

    internal static Label Label(string text)
    {
        return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 4, 9, 4), TextAlign = ContentAlignment.MiddleLeft };
    }

    internal static void Status(Label label)
    {
        label.Height = 25;
        label.AutoEllipsis = true;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.ForeColor = Muted;
    }

    internal static void Heading(TableLayoutPanel table, string title, params Button[] buttons)
    {
        var label = Label(title);
        label.Font = Title;
        label.Margin = new Padding(0, 3, 10, 7);
        var controls = new Control[buttons.Length + 1];
        controls[0] = label;
        Array.Copy(buttons, 0, controls, 1, buttons.Length);
        Add(table, Row(label, controls));
    }

    internal static Button Button(Button button, string text, bool primary = false, bool warning = false)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(56, 30);
        button.Padding = new Padding(8, 1, 8, 1);
        button.Margin = new Padding(3, 3, 3, 3);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(225, 235, 252);
        if (primary || warning)
        {
            button.EnabledChanged += delegate { ColorButton(button, primary, warning); };
            ColorButton(button, primary, warning);
        }
        else
        {
            button.ForeColor = Ink;
            button.BackColor = Color.White;
            button.FlatAppearance.BorderColor = Border;
        }
        return button;
    }

    static void ColorButton(Button button, bool primary, bool warning)
    {
        Color color = button.Enabled ? (primary ? Blue : Color.FromArgb(167, 52, 59)) : Color.FromArgb(231, 237, 247);
        button.BackColor = color;
        button.ForeColor = button.Enabled ? Color.White : Muted;
        button.FlatAppearance.BorderColor = color;
        button.FlatAppearance.MouseOverBackColor = button.Enabled
            ? (warning ? Color.FromArgb(138, 42, 47) : Color.FromArgb(24, 75, 172)) : color;
    }

    internal static SplitContainer Split(Control top, Control bottom, int bottomHeight)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2,
            Size = new Size(600, 500), SplitterWidth = 8, Panel1MinSize = 80, Panel2MinSize = 100, SplitterDistance = 492 - bottomHeight };
        top.Dock = bottom.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(bottom);
        return split;
    }

    internal static void Check(CheckBox box, string text, bool value)
    {
        box.Text = text;
        box.AutoSize = true;
        box.Checked = value;
    }

    internal static void Number(NumericUpDown box, int width, int min, int max, int value)
    {
        box.Width = width;
        box.Minimum = min;
        box.Maximum = max;
        box.Value = value;
    }

    internal static void Combo(ComboBox box, int width, string[] values, int index)
    {
        box.Width = width;
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.Items.AddRange(values);
        if (index >= 0) box.SelectedIndex = index;
    }
}
