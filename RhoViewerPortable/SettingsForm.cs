namespace RhoViewerPortable;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _current;
    private readonly TextBox _rhoDirectory = new();
    private readonly TextBox _logDirectory = new();

    public SettingsForm(AppSettings current)
    {
        _current = current;
        Text = "Rho Viewer Portable · 設定";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(680, 240);
        Font = new Font("Microsoft JhengHei UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "常用目錄",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(35, 44, 58),
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(BuildDirectoryRow("預設 Rho 目錄", _rhoDirectory, current.RhoDirectory, "選擇 Rho 目錄"), 0, 1);
        root.Controls.Add(BuildDirectoryRow("Log 目錄", _logDirectory, current.LogDirectory, "選擇 Log 目錄"), 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88, Height = 30 };
        var save = new Button
        {
            Text = "儲存設定",
            Width = 100,
            Height = 30,
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(45, 105, 175),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) =>
        {
            if (!ValidateDirectories())
                DialogResult = DialogResult.None;
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        root.Controls.Add(buttons, 0, 3);

        AcceptButton = save;
        CancelButton = cancel;
    }

    public AppSettings GetSettings()
    {
        var settings = new AppSettings
        {
            RhoDirectory = _rhoDirectory.Text.Trim(),
            GameDirectory = _current.GameDirectory,
            GameRegion = _current.GameRegion,
            LogDirectory = _logDirectory.Text.Trim(),
            WindowWidth = _current.WindowWidth,
            WindowHeight = _current.WindowHeight,
            SplitterRatio = _current.SplitterRatio
        };
        settings.Normalize();
        return settings;
    }

    private Control BuildDirectoryRow(string labelText, TextBox textBox, string value, string description)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));

        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 65, 81)
        };
        row.Controls.Add(label, 0, 0);
        textBox.Text = value;
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 10, 10, 10);
        textBox.BorderStyle = BorderStyle.FixedSingle;
        row.Controls.Add(textBox, 1, 0);

        var browse = new Button
        {
            Text = "瀏覽…",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(235, 240, 246),
            ForeColor = Color.FromArgb(45, 55, 72),
            AccessibleDescription = description
        };
        browse.FlatAppearance.BorderColor = Color.FromArgb(205, 214, 226);
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(textBox.Text) ? textBox.Text : string.Empty,
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                textBox.Text = dialog.SelectedPath;
        };
        row.Controls.Add(browse, 2, 0);
        return row;
    }

    private bool ValidateDirectories()
    {
        if (!string.IsNullOrWhiteSpace(_rhoDirectory.Text) && !Directory.Exists(_rhoDirectory.Text))
        {
            var result = MessageBox.Show(this, "預設 Rho 目錄目前不存在，仍要保存這個路徑嗎？", "確認目錄", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes)
                return false;
        }
        if (string.IsNullOrWhiteSpace(_logDirectory.Text))
        {
            MessageBox.Show(this, "請指定 Log 目錄，或按瀏覽建立一個目錄。", "缺少 Log 目錄", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        return true;
    }
}
