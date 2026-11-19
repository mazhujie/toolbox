using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed class ToolView
{
    internal string Id, Category, Title;
    internal Control Content;
}

internal sealed class ToolShell : UserControl
{
    readonly Panel side = new Panel(), body = new Panel(), content = new Panel();
    readonly FlowLayoutPanel pages = new FlowLayoutPanel();
    readonly Button network = new Button(), serial = new Button(), debug = new Button(), adb = new Button();
    readonly TableLayoutPanel mutexBanner;
    readonly Label mutexMessage = UiTheme.Label("");
    readonly Dictionary<string, ToolView> views = new Dictionary<string, ToolView>();
    readonly List<ToolView> orderedViews = new List<ToolView>();
    readonly Dictionary<string, string> last = new Dictionary<string, string>();
    readonly Dictionary<string, Button> pageButtons = new Dictionary<string, Button>();
    string category, current, busyView;
    static readonly Color Nav = Color.FromArgb(24, 35, 55);
    static readonly Color BlueHighlight = Color.FromArgb(224, 235, 255);

    internal ToolShell()
    {
        Dock = DockStyle.Fill;
        Font = UiTheme.Body;
        side.Dock = DockStyle.Left;
        side.TabIndex = 0;
        body.TabIndex = 1;
        side.Width = 196;
        side.BackColor = Nav;
        Controls.Add(body);
        Controls.Add(side);

        var brand = new Label { Text = "工具箱", Font = UiTheme.Title, ForeColor = Color.White, Location = new Point(18, 19), AutoSize = true };
        var version = new Label { Text = "v" + Program.Version, Font = UiTheme.Small, ForeColor = Color.FromArgb(170, 186, 211), Location = new Point(20, 62), AutoSize = true };
        side.Controls.Add(brand);
        side.Controls.Add(version);
        MainButton(network, "网络服务", "network", 100);
        MainButton(serial, "串口助手", "serial", 146);
        MainButton(debug, "网络调试", "debug", 192);
        MainButton(adb, "ADB 工具", "adb", 238);

        var workspace = UiTheme.Stack();
        workspace.BackColor = UiTheme.Background;
        body.Dock = DockStyle.Fill;
        body.Controls.Add(workspace);
        pages.Height = 46;
        pages.Margin = Padding.Empty;
        pages.Padding = new Padding(16, 5, 16, 5);
        pages.BackColor = Color.White;
        pages.WrapContents = false;
        pages.TabStop = false;
        UiTheme.Add(workspace, pages);
        UiTheme.Add(workspace, new Panel { Height = 1, BackColor = UiTheme.Border, Margin = Padding.Empty });

        var jump = UiTheme.Button(new Button(), "前往查看");
        jump.Click += delegate { if (busyView != null) ShowView(busyView); };
        mutexMessage.ForeColor = Color.FromArgb(106, 66, 16);
        mutexBanner = UiTheme.Row(mutexMessage, mutexMessage, jump);
        mutexBanner.BackColor = Color.FromArgb(255, 242, 212);
        mutexBanner.Padding = new Padding(16, 1, 16, 1);
        UiTheme.Add(workspace, mutexBanner);
        mutexBanner.Visible = false;

        content.Margin = Padding.Empty;
        UiTheme.Add(workspace, content, true);
    }

    void MainButton(Button button, string title, string group, int top)
    {
        button.Text = title;
        button.SetBounds(12, top, 172, 40);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 60, 87);
        button.Font = UiTheme.Section;
        button.ForeColor = Color.White;
        button.BackColor = Nav;
        button.Padding = new Padding(10, 0, 0, 0);
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Click += delegate { if (last.ContainsKey(group)) ShowView(last[group]); };
        side.Controls.Add(button);
    }

    internal void Add(string id, string group, string title, Control page)
    {
        var view = new ToolView { Id = id, Category = group, Title = title, Content = page };
        views.Add(id, view);
        orderedViews.Add(view);
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        content.Controls.Add(page);
        if (!last.ContainsKey(group)) last[group] = id;
    }

    internal void ShowView(string id)
    {
        ToolView view;
        if (id == null || !views.TryGetValue(id, out view)) return;
        if (category != view.Category)
        {
            category = view.Category;
            pages.SuspendLayout();
            foreach (Control old in pages.Controls.Cast<Control>().ToArray()) old.Dispose();
            pageButtons.Clear();
            foreach (var item in orderedViews.Where(item => item.Category == category))
            {
                string key = item.Id;
                var button = UiTheme.Button(new Button(), item.Title);
                button.Click += delegate { ShowView(key); };
                pages.Controls.Add(button);
                pageButtons.Add(key, button);
            }
            pages.ResumeLayout();
        }
        current = id;
        last[category] = id;
        network.BackColor = category == "network" ? UiTheme.Blue : Nav;
        serial.BackColor = category == "serial" ? UiTheme.Blue : Nav;
        debug.BackColor = category == "debug" ? UiTheme.Blue : Nav;
        adb.BackColor = category == "adb" ? UiTheme.Blue : Nav;
        foreach (var item in pageButtons)
        {
            bool selected = item.Key == id;
            item.Value.BackColor = selected ? BlueHighlight : Color.White;
            item.Value.ForeColor = selected ? UiTheme.Blue : UiTheme.Ink;
            item.Value.FlatAppearance.BorderColor = selected ? BlueHighlight : Color.White;
        }
        foreach (var item in orderedViews) item.Content.Visible = item.Id == id;
        view.Content.BringToFront();
        UpdateBanner();
    }

    internal void ShowNetworkBusy(string owner)
    {
        busyView = owner;
        UpdateBanner();
    }

    void UpdateBanner()
    {
        bool show = busyView != null && current != busyView && (current == "speed" || current == "ip" || current == "lan");
        if (show) mutexMessage.Text = views[busyView].Title + "正在运行，本页暂时无法启动。";
        mutexBanner.Visible = show;
    }

    internal string CurrentView { get { return current; } }
}
