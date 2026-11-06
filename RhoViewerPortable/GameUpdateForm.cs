namespace RhoViewerPortable;

internal sealed class GameUpdateForm : Form
{
    private readonly AppSettings _currentSettings;
    private readonly TextBox _gameDirectory = new();
    private readonly ComboBox _regionCombo = new();
    private readonly CheckBox _allowInsecureHttp = new();
    private readonly Label _regionMappingLabel = new();
    private readonly Label _clientVersion = new();
    private readonly Label _serverVersion = new();
    private readonly Label _sourceLabel = new();
    private readonly Label _summaryLabel = new();
    private readonly Label _statusLabel = new();
    private readonly DataGridView _fileGrid = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _checkButton = new();
    private readonly Button _applyButton = new();
    private readonly Button _cancelButton = new();
    private CancellationTokenSource? _operationCts;
    private KartPatchPlan? _plan;
    private bool _running;

    public GameUpdateForm(AppSettings currentSettings)
    {
        _currentSettings = currentSettings;
        Text = "遊戲檔案更新";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(860, 560);
        ClientSize = new Size(980, 660);
        Font = new Font("Microsoft JhengHei UI", 9F);
        BackColor = Color.FromArgb(242, 245, 249);

        BuildLayout();
        LanguageManager.Apply(this);
        UpdateControls();
    }

    public AppSettings GetSettings()
    {
        var settings = new AppSettings
        {
            RhoDirectory = _currentSettings.RhoDirectory,
            LogDirectory = _currentSettings.LogDirectory,
            UiLanguage = _currentSettings.UiLanguage,
            GameDirectory = _gameDirectory.Text.Trim(),
            GameRegion = (_regionCombo.SelectedItem as KartRegionProfile)?.Code ?? _currentSettings.GameRegion,
            WindowWidth = _currentSettings.WindowWidth,
            WindowHeight = _currentSettings.WindowHeight,
            SplitterRatio = _currentSettings.SplitterRatio
        };
        settings.Normalize();
        return settings;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(16),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        var title = new Label
        {
            Text = "更新自己的遊戲檔案",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(35, 44, 58),
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(title, 0, 0);

        root.Controls.Add(BuildDirectoryRow(), 0, 1);

        var note = new Label
        {
            Text = "請選擇含有 KartRider.pin 的遊戲目錄。更新端點與版本會自動從遊戲檔案及更新服務取得。",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(80, 91, 107),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        root.Controls.Add(note, 0, 2);

        root.Controls.Add(BuildRegionRow(), 0, 3);
        _allowInsecureHttp.Text = "允許 HTTP 更新端點（不安全；每次檢查需明確勾選）";
        _allowInsecureHttp.Dock = DockStyle.Fill;
        _allowInsecureHttp.ForeColor = Color.FromArgb(145, 74, 28);
        _allowInsecureHttp.CheckedChanged += (_, _) =>
        {
            _plan = null;
            _summaryLabel.Text = "HTTP 設定已變更，請重新檢查更新。";
            UpdateControls();
        };
        root.Controls.Add(_allowInsecureHttp, 0, 4);
        root.Controls.Add(BuildVersionPanel(), 0, 5);

        ConfigureGrid();
        root.Controls.Add(_fileGrid, 0, 6);

        var summaryPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(10, 6, 10, 6),
            ColumnCount = 1,
            RowCount = 3
        };
        summaryPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        summaryPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        summaryPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.ForeColor = Color.FromArgb(45, 55, 72);
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.AutoEllipsis = true;
        summaryPanel.Controls.Add(_summaryLabel, 0, 0);
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = Color.FromArgb(90, 99, 112);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        summaryPanel.Controls.Add(_statusLabel, 0, 1);
        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        summaryPanel.Controls.Add(_progressBar, 0, 2);
        root.Controls.Add(summaryPanel, 0, 7);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        var close = new Button { Text = "關閉", Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
        ConfigureButton(close, primary: false);
        _cancelButton.Text = "取消作業";
        _cancelButton.Width = 92;
        _cancelButton.Height = 30;
        _cancelButton.Click += (_, _) => _operationCts?.Cancel();
        ConfigureButton(_cancelButton, primary: false);
        _checkButton.Text = "檢查更新";
        _checkButton.Width = 104;
        _checkButton.Height = 30;
        _checkButton.Click += async (_, _) => await CheckAsync();
        ConfigureButton(_checkButton, primary: false);
        _applyButton.Text = "開始更新";
        _applyButton.Width = 104;
        _applyButton.Height = 30;
        _applyButton.Click += async (_, _) => await ApplyAsync();
        ConfigureButton(_applyButton, primary: true);
        buttons.Controls.Add(close);
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_checkButton);
        buttons.Controls.Add(_applyButton);
        root.Controls.Add(buttons, 0, 8);

        AcceptButton = _checkButton;
        CancelButton = close;
        FormClosing += OnFormClosing;
    }

    private Control BuildDirectoryRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));

        var label = new Label
        {
            Text = "遊戲目錄",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 65, 81)
        };
        row.Controls.Add(label, 0, 0);

        _gameDirectory.Text = string.IsNullOrWhiteSpace(_currentSettings.GameDirectory)
            ? _currentSettings.RhoDirectory
            : _currentSettings.GameDirectory;
        _gameDirectory.Dock = DockStyle.Fill;
        _gameDirectory.Margin = new Padding(0, 5, 10, 5);
        _gameDirectory.BorderStyle = BorderStyle.FixedSingle;
        _gameDirectory.TextChanged += (_, _) =>
        {
            _plan = null;
            _summaryLabel.Text = "路徑已變更，請重新檢查更新。";
            UpdateControls();
        };
        row.Controls.Add(_gameDirectory, 1, 0);

        var browse = new Button
        {
            Text = "選擇資料夾",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 5, 0, 5),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(235, 240, 246),
            ForeColor = Color.FromArgb(45, 55, 72)
        };
        browse.FlatAppearance.BorderColor = Color.FromArgb(205, 214, 226);
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "選擇含有 KartRider.pin 的遊戲目錄",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_gameDirectory.Text) ? _gameDirectory.Text : string.Empty,
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _gameDirectory.Text = dialog.SelectedPath;
        };
        row.Controls.Add(browse, 2, 0);
        return row;
    }

    private Control BuildRegionRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var label = new Label
        {
            Text = "區域",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 65, 81)
        };
        row.Controls.Add(label, 0, 0);

        _regionCombo.Dock = DockStyle.Fill;
        _regionCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _regionCombo.Margin = new Padding(0, 4, 10, 4);
        _regionCombo.DisplayMember = nameof(KartRegionProfile.DisplayName);
        foreach (var profile in KartRegionCatalog.Profiles)
            _regionCombo.Items.Add(profile);
        var selectedProfile = KartRegionCatalog.FromCode(_currentSettings.GameRegion);
        _regionCombo.SelectedItem = selectedProfile;
        _regionCombo.SelectedIndexChanged += (_, _) =>
        {
            _plan = null;
            UpdateRegionMapping();
            _summaryLabel.Text = "Region 已變更，請重新檢查更新。";
            UpdateControls();
        };
        row.Controls.Add(_regionCombo, 1, 0);

        _regionMappingLabel.Dock = DockStyle.Fill;
        _regionMappingLabel.TextAlign = ContentAlignment.MiddleLeft;
        _regionMappingLabel.ForeColor = Color.FromArgb(80, 91, 107);
        _regionMappingLabel.AutoEllipsis = true;
        row.Controls.Add(_regionMappingLabel, 2, 0);
        UpdateRegionMapping();
        return row;
    }

    private void UpdateRegionMapping()
    {
        if (_regionCombo.SelectedItem is not KartRegionProfile profile)
            return;
        _regionMappingLabel.Text = profile.Region == KartUpdateRegion.Auto
            ? "對應伺服器：使用 KartRider.pin 內的位址與連接埠"
            : $"對應伺服器：{profile.Host}:{profile.Port} — {profile.Note}";
    }

    private Control BuildVersionPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            BackColor = Color.White,
            Padding = new Padding(10, 3, 10, 3)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddValue(panel, "本機版本", _clientVersion, 0, 1);
        AddValue(panel, "伺服器版本", _serverVersion, 2, 3);
        AddValue(panel, "端點", _sourceLabel, 4, 5);
        return panel;
    }

    private static void AddValue(TableLayoutPanel panel, string labelText, Label value, int labelColumn, int valueColumn)
    {
        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(90, 99, 112)
        };
        panel.Controls.Add(label, labelColumn, 0);
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.ForeColor = Color.FromArgb(35, 44, 58);
        value.AutoEllipsis = true;
        panel.Controls.Add(value, valueColumn, 0);
    }

    private void ConfigureGrid()
    {
        _fileGrid.Dock = DockStyle.Fill;
        _fileGrid.AllowUserToAddRows = false;
        _fileGrid.AllowUserToDeleteRows = false;
        _fileGrid.AllowUserToResizeRows = false;
        _fileGrid.AutoGenerateColumns = false;
        _fileGrid.ReadOnly = true;
        _fileGrid.MultiSelect = false;
        _fileGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _fileGrid.BackgroundColor = Color.White;
        _fileGrid.BorderStyle = BorderStyle.FixedSingle;
        _fileGrid.RowHeadersVisible = false;
        _fileGrid.RowTemplate.Height = 28;
        _fileGrid.GridColor = Color.FromArgb(229, 234, 241);
        _fileGrid.EnableHeadersVisualStyles = false;
        _fileGrid.ColumnHeadersHeight = 30;
        _fileGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(238, 242, 247),
            ForeColor = Color.FromArgb(35, 44, 58),
            Font = new Font(Font, FontStyle.Bold),
            SelectionBackColor = Color.FromArgb(238, 242, 247),
            SelectionForeColor = Color.FromArgb(35, 44, 58)
        };
        _fileGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Color.FromArgb(35, 44, 58),
            SelectionBackColor = Color.FromArgb(214, 232, 252),
            SelectionForeColor = Color.FromArgb(25, 45, 72),
            Padding = new Padding(5, 0, 5, 0)
        };
        _fileGrid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(249, 251, 253) };
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "狀態", Name = "State", Width = 92 });
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "資源路徑", Name = "Path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 300 });
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "本機大小", Name = "LocalSize", Width = 112 });
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "更新大小", Name = "RemoteSize", Width = 112 });
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "CRC", Name = "Crc", Width = 108 });
    }

    private async Task CheckAsync()
    {
        if (_running)
            return;

        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        _running = true;
        _plan = null;
        _fileGrid.Rows.Clear();
        _summaryLabel.Text = "正在讀取 KartRider.pin 並檢查更新服務……";
        _statusLabel.Text = "檢查中";
        UpdateControls();
        AppLogger.Info("GameUpdateCheck", $"directory={_gameDirectory.Text.Trim()}");
        try
        {
            var service = new KartPatchService();
            var region = (_regionCombo.SelectedItem as KartRegionProfile)?.Region ?? KartUpdateRegion.Auto;
            _plan = await service.BuildPlanAsync(_gameDirectory.Text, region, cts.Token, _allowInsecureHttp.Checked);
            RenderPlan(_plan);
            AppLogger.Info("GameUpdateCheck", $"client={_plan.ClientVersion}; server={_plan.ServerVersion}; total={_plan.Items.Count}; changes={_plan.Changes.Count}");
        }
        catch (OperationCanceledException)
        {
            _summaryLabel.Text = "檢查已取消。";
            AppLogger.Info("GameUpdateCheck", "cancelled");
        }
        catch (Exception ex)
        {
            _summaryLabel.Text = "檢查失敗：" + ex.Message;
            AppLogger.Error("GameUpdateCheck", $"directory={_gameDirectory.Text.Trim()}", ex);
            MessageBox.Show(this, ex.Message, "無法檢查遊戲更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts))
                _operationCts = null;
            _running = false;
            _statusLabel.Text = string.Empty;
            UpdateControls();
        }
    }

    private async Task ApplyAsync()
    {
        if (_plan is null || _plan.Changes.Count == 0 || _running)
            return;

        var confirm = MessageBox.Show(
            this,
            $"即將更新 {_plan.Changes.Count:N0} 個遊戲檔案，並修改：\n{_plan.GameDirectory}\n\n" +
            $"更新來源：{FormatEndpointForDisplay(_plan.PatchBaseUrl)}\nDiscovery：{_plan.ServerHost}:{_plan.ServerPort}\n" +
            "Adler-32/CRC 校驗只用於偵測資料損壞，不能驗證發布者；請確認更新來源可信。\n\n" +
            "請先關閉 KartRider。原檔會先備份到 .RhoViewerUpdate\\backups。\n" +
            "目前只套用完整檔案，不執行 delta/Patcher.exe。確定要繼續嗎？",
            "確認更新遊戲檔案",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
            return;

        _running = true;
        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        UpdateControls();
        AppLogger.Info("GameUpdateApply", $"directory={_plan.GameDirectory}; changes={_plan.Changes.Count}; serverVersion={_plan.ServerVersion}");
        try
        {
            var service = new KartPatchService();
            var progress = new Progress<KartPatchProgress>(UpdateProgress);
            var result = await service.ApplyAsync(_plan, progress, cts.Token);
            AppLogger.Info("GameUpdateApply", $"completed={result.UpdatedFiles}; backup={result.BackupDirectory}");
            MessageBox.Show(this,
                $"更新完成，共套用 {result.UpdatedFiles:N0} 個檔案。\n\n備份位置：\n{result.BackupDirectory}",
                "遊戲更新完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _summaryLabel.Text = $"更新完成：{result.UpdatedFiles:N0} 個檔案；備份：{result.BackupDirectory}";
            _plan = null;
            _fileGrid.Rows.Clear();
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("GameUpdateApply", "cancelled");
            _summaryLabel.Text = "更新已取消；尚未完成的檔案不會套用。已套用部分會嘗試自動復原。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("GameUpdateApply", $"directory={_gameDirectory.Text.Trim()}", ex);
            _summaryLabel.Text = "更新失敗：" + ex.Message;
            MessageBox.Show(this, ex.Message, "遊戲更新失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts))
                _operationCts = null;
            _running = false;
            _statusLabel.Text = string.Empty;
            UpdateControls();
        }
    }

    private void RenderPlan(KartPatchPlan plan)
    {
        _clientVersion.Text = plan.ClientVersion.ToString();
        _serverVersion.Text = plan.ServerVersion.ToString();
        _sourceLabel.Text = $"{plan.ServerHost}:{plan.ServerPort} -> {plan.PatchBaseUrl}";
        _fileGrid.Rows.Clear();
        foreach (var item in plan.Items)
        {
            var state = item.State switch
            {
                KartPatchFileState.Unchanged => "已是最新",
                KartPatchFileState.Missing => "缺少",
                _ => "需更新"
            };
            var row = _fileGrid.Rows.Add(state, item.File.RelativePath, FormatSize(item.LocalSize), FormatSize(item.File.Size), $"0x{item.File.Crc:X8}");
            if (item.NeedsUpdate)
                _fileGrid.Rows[row].DefaultCellStyle.ForeColor = Color.FromArgb(156, 88, 0);
        }

        var changed = plan.Changes.Count;
        var regionName = KartRegionCatalog.Get(plan.Region).DisplayName;
        _summaryLabel.Text = changed == 0
            ? $"{regionName}：本機已是伺服器版本 {plan.ServerVersion}，共檢查 {plan.Items.Count:N0} 個檔案。"
            : $"{regionName}：本機版本 {plan.ClientVersion} → 伺服器版本 {plan.ServerVersion}；{changed:N0} 個檔案需要更新，預估下載 {FormatSize(plan.DownloadBytes)}。更新前會先預覽並確認。";
    }

    private void UpdateProgress(KartPatchProgress value)
    {
        _statusLabel.Text = $"{value.Stage}：{value.RelativePath ?? string.Empty} ({value.CompletedFiles}/{value.TotalFiles})";
        var percentage = value.TotalBytes <= 0 ? 0 : (int)Math.Clamp(value.CompletedBytes * 100L / value.TotalBytes, 0, 100);
        _progressBar.Value = percentage;
    }

    internal static string FormatEndpointForDisplay(string value)
    {
        var endpoint = new Uri(value, UriKind.Absolute);
        return $"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}";
    }

    private void UpdateControls()
    {
        _gameDirectory.Enabled = !_running;
        _regionCombo.Enabled = !_running;
        _allowInsecureHttp.Enabled = !_running;
        _checkButton.Enabled = !_running;
        _applyButton.Enabled = !_running && _plan is not null && _plan.Changes.Count > 0;
        _cancelButton.Visible = _operationCts is not null;
        _cancelButton.Enabled = _operationCts is not null;
        _progressBar.Visible = _running;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_operationCts is null)
            return;
        var result = MessageBox.Show(this, "目前仍有更新作業進行中，要取消並關閉視窗嗎？", "更新進行中", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result == DialogResult.Yes)
            _operationCts.Cancel();
        e.Cancel = true;
    }

    private static void ConfigureButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = primary ? Color.FromArgb(45, 105, 175) : Color.FromArgb(235, 240, 246);
        button.ForeColor = primary ? Color.White : Color.FromArgb(45, 55, 72);
        button.Cursor = Cursors.Hand;
    }

    private static string FormatSize(long value)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{size:N1} {units[unit]}";
    }
}
