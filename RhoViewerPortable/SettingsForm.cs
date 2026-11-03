using System.Diagnostics;

namespace RhoViewerPortable;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _current;
    private readonly TextBox _rhoDirectory = new();
    private readonly TextBox _logDirectory = new();
    private readonly ComboBox _language = new();

    public SettingsForm(AppSettings current)
    {
        _current = current;
        Text = "Rho Viewer Portable · 設定";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(680, 326);
        Font = new Font("Microsoft JhengHei UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(18),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
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
        root.Controls.Add(BuildLanguageRow(current.UiLanguage), 0, 3);
        root.Controls.Add(new Label
        {
            Text = "語言包是 languages 資料夾中的獨立 XML 檔；可用文字編輯器維護，變更後重新啟動程式載入。",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(100, 110, 124),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 4);

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
        root.Controls.Add(buttons, 0, 5);

        AcceptButton = save;
        CancelButton = cancel;
        LanguageManager.Apply(this);
    }

    public AppSettings GetSettings()
    {
        var settings = new AppSettings
        {
            RhoDirectory = _rhoDirectory.Text.Trim(),
            GameDirectory = _current.GameDirectory,
            GameRegion = _current.GameRegion,
            UiLanguage = (_language.SelectedItem as LanguagePackageInfo)?.FileName ?? _current.UiLanguage,
            LogDirectory = _logDirectory.Text.Trim(),
            WindowWidth = _current.WindowWidth,
            WindowHeight = _current.WindowHeight,
            SplitterRatio = _current.SplitterRatio
        };
        settings.Normalize();
        return settings;
    }

    private Control BuildLanguageRow(string selectedFileName)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        row.Controls.Add(new Label
        {
            Text = "介面語言",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 65, 81)
        }, 0, 0);

        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Dock = DockStyle.Fill;
        _language.Margin = new Padding(0, 7, 10, 7);
        foreach (var package in LanguageManager.GetPackages())
            _language.Items.Add(package);
        for (var index = 0; index < _language.Items.Count; index++)
        {
            if (_language.Items[index] is LanguagePackageInfo package &&
                string.Equals(package.FileName, selectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                _language.SelectedIndex = index;
                break;
            }
        }
        if (_language.SelectedIndex < 0 && _language.Items.Count > 0)
            _language.SelectedIndex = 0;
        row.Controls.Add(_language, 1, 0);

        var openLanguageFolder = new Button
        {
            Text = "開啟語言包資料夾",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 7, 0, 7),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(235, 240, 246),
            ForeColor = Color.FromArgb(45, 55, 72)
        };
        openLanguageFolder.FlatAppearance.BorderColor = Color.FromArgb(205, 214, 226);
        openLanguageFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(LanguageManager.LanguageDirectory);
            Process.Start(new ProcessStartInfo { FileName = LanguageManager.LanguageDirectory, UseShellExecute = true });
        };
        row.Controls.Add(openLanguageFolder, 2, 0);
        return row;
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
