using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed class ToolView
{
    public string Id, Category, Title;
    public Control Content;
    public Button[] Actions;
}
internal sealed class ToolShell : UserControl
{
    readonly Panel side = new Panel(), body = new Panel();
    readonly FlowLayoutPanel pages = new FlowLayoutPanel(), actions = new FlowLayoutPanel();
    readonly Button network = new Button(), serial = new Button(), debug = new Button(), adb = new Button();
    readonly Label actionTitle = new Label();
    readonly Dictionary<string, ToolView> views = new Dictionary<string, ToolView>();
    readonly Dictionary<string, string> last = new Dictionary<string, string>();
    readonly Dictionary<string, Button> pageButtons = new Dictionary<string, Button>();
    string category, current;
    static readonly Color Nav = Color.FromArgb(24, 35, 55), Blue = Color.FromArgb(49, 102, 215);
    internal ToolShell()
    {
        Size = new Size(1026, 800);
        Dock = DockStyle.Fill; Font = new Font("Microsoft YaHei UI", 10);
        side.Dock = DockStyle.Left; side.Width = 210; side.BackColor = Nav;
        body.Dock = DockStyle.Fill; body.BackColor = Color.FromArgb(245, 247, 251);
        Controls.Add(body); Controls.Add(side);
        side.Controls.Add(new Label { Text = "工具箱", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), ForeColor = Color.White, Location = new Point(20, 22), Size = new Size(170, 40) });
        side.Controls.Add(new Label { Text = "DESKTOP UTILITIES      3.3.2", Font = new Font("Segoe UI", 8), ForeColor = Color.FromArgb(148, 164, 189), Location = new Point(22, 67), Size = new Size(182, 20) });
        MainButton(network, "网络服务", 106); MainButton(serial, "串口助手", 144); MainButton(debug, "网络调试", 182); MainButton(adb, "ADB 工具", 220);
        network.Click += delegate { ShowCategory("network"); }; serial.Click += delegate { ShowCategory("serial"); };
        debug.Click += delegate { ShowCategory("debug"); };
        adb.Click += delegate { ShowCategory("adb"); };
        side.Controls.Add(new Label { Text = "功能页面", ForeColor = Color.FromArgb(148, 164, 189), Location = new Point(22, 276), Size = new Size(166, 24) });
        pages.SetBounds(16, 304, 182, 150); pages.FlowDirection = FlowDirection.TopDown; pages.WrapContents = false; side.Controls.Add(pages);
        actionTitle.Text = "当前操作"; actionTitle.ForeColor = Color.FromArgb(148, 164, 189); actionTitle.SetBounds(22, 414, 166, 24); side.Controls.Add(actionTitle);
        actions.SetBounds(16, 444, 184, 296);
        actions.FlowDirection = FlowDirection.TopDown; actions.WrapContents = false; actions.AutoScroll = true; side.Controls.Add(actions);
        side.SizeChanged += delegate { LayoutActions(); };
    }
    void MainButton(Button button, string text, int y)
    {
        button.Text = text; button.SetBounds(16, y, 178, 36); button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
        button.Font = new Font(Font.FontFamily, 12, FontStyle.Bold); button.TextAlign = ContentAlignment.MiddleLeft; button.Padding = new Padding(12, 0, 0, 0);
        button.BackColor = Nav; button.ForeColor = Color.White; side.Controls.Add(button);
    }
    internal void Add(string id, string group, string title, Control content, params Button[] buttons)
    {
        var view = new ToolView { Id = id, Category = group, Title = title, Content = content, Actions = buttons };
        views.Add(id, view);
        content.Dock = DockStyle.Fill;
        content.Visible = false;
        body.Controls.Add(content);
        foreach (var button in buttons)
        {
            if (button.Parent != null) button.Parent.Controls.Remove(button);
            button.EnabledChanged -= ActionEnabledChanged;
            button.EnabledChanged += ActionEnabledChanged;
        }
        if (!last.ContainsKey(group)) last[group] = id;
    }
    void ShowCategory(string name)
    {
        if (last.ContainsKey(name)) ShowView(last[name]);
    }

    internal void ShowView(string id)
    {
        if (!views.ContainsKey(id)) return;
        var view = views[id];
        if (category != view.Category)
        {
            category = view.Category;
            foreach (Control item in pages.Controls.Cast<Control>().ToArray())
            {
                pages.Controls.Remove(item);
                item.Dispose();
            }
            pageButtons.Clear();
            foreach (var item in views.Values.Where(v => v.Category == category))
            {
                string key = item.Id;
                var button = new Button { Text = "  " + item.Title, Width = 176, Height = 39, Margin = new Padding(0, 0, 0, 5), FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.FromArgb(211, 221, 238), BackColor = Nav, TextAlign = ContentAlignment.MiddleLeft };
                button.FlatAppearance.BorderSize = 0;
                button.Click += delegate { ShowView(key); };
                pages.Controls.Add(button);
                pageButtons[key] = button;
            }
        }
        current = id;
        last[category] = id;
        network.BackColor = category == "network" ? Blue : Nav;
        serial.BackColor = category == "serial" ? Blue : Nav;
        debug.BackColor = category == "debug" ? Blue : Nav;
        adb.BackColor = category == "adb" ? Blue : Nav;
        foreach (var item in pageButtons)
            item.Value.BackColor = item.Key == id ? Color.FromArgb(44, 61, 87) : Nav;
        foreach (var item in views.Values) item.Content.Visible = item.Id == id;
        view.Content.BringToFront();
        actions.Controls.Clear();
        foreach (var button in view.Actions)
        {
            button.Width = 174; button.Height = 35; button.Margin = new Padding(0, 0, 0, 6); button.Padding = Padding.Empty;
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0; button.ForeColor = Color.White;
            actions.Controls.Add(button);
            StyleAction(button);
        }
        actionTitle.Text = view.Title + " · 操作";
        LayoutActions();
    }

    void LayoutActions()
    {
        pages.Height = pageButtons.Count * 44;
        actionTitle.Top = pages.Bottom + 16;
        actions.Top = actionTitle.Bottom + 8;
        actions.Height = Math.Max(0, side.ClientSize.Height - actions.Top - 24);
    }

    void StyleAction(Button button)
    {
        if (button.Parent != actions) return;
        button.BackColor = !button.Enabled
            ? Color.FromArgb(174, 187, 205)
            : actions.Controls.GetChildIndex(button) == 0 ? Blue : Color.FromArgb(44, 61, 87);
    }

    void ActionEnabledChanged(object sender, EventArgs e) { StyleAction((Button)sender); }
    internal string CurrentView { get { return current; } }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var button in views.Values.SelectMany(v => v.Actions).Distinct()) button.Dispose();
        }
        base.Dispose(disposing);
    }
}
