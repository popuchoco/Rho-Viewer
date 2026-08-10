using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text;

namespace RhoViewerPortable;

internal sealed class MainForm : Form
{
    private readonly List<RhoArchiveContext> _contexts = new();
    private readonly List<RhoItem> _allItems = new();
    private readonly ListBox _archiveList = new();
    private readonly TreeView _resourceTree = new();
    private readonly DataGridView _fileGrid = new();
    private readonly TextBox _searchBox = new();
    private readonly ComboBox _extensionFilter = new();
    private readonly Label _breadcrumbLabel = new();
    private readonly PictureBox _previewImage = new();
    private readonly TextBox _previewText = new();
    private readonly Label _previewTitle = new();
    private readonly Label _summaryLabel = new();
    private readonly Label _statusLabel = new();
    private readonly SplitContainer _mainSplit = new();
    private readonly ToolStripProgressBar _progress = new();
    private readonly Button _exportSelectedButton = new();
    private readonly Button _exportAllButton = new();
    private readonly ToolStripButton _playAudioButton = new();
    private readonly ToolStripButton _stopAudioButton = new();
    private readonly ToolStripButton _selectAllButton = new();
    private readonly ToolStripButton _invertSelectionButton = new();
    private readonly ToolStripButton _cancelExportButton = new();
    private readonly ToolStripButton _importConfiguredButton = new();
    private readonly ToolStripButton _settingsButton = new();
    private readonly ToolStrip _toolStrip = new();
    private readonly HashSet<string> _playbackTempDirectories = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _exportCts;
    private Process? _playbackProcess;
    private string? _activePlaybackTempDirectory;
    private string _selectedFolderPath = string.Empty;
    private RhoArchiveContext? _treeContextFilter;
    private Bitmap? _currentBitmap;
    private int _previewGeneration;
    private bool _busy;
    private bool _updatingFilters;
    private bool _rebuildingTree;
    private bool _selectionUpdating;
    private bool _resizingLayout;
    private bool _layoutInitialized;
    private AppSettings _settings = AppSettings.CreateDefault();

    public MainForm()
    {
        Text = "Rho Viewer Portable";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(_settings.WindowWidth, _settings.WindowHeight);
        Font = new Font("Microsoft JhengHei UI", 9F);
        BackColor = Color.FromArgb(242, 245, 249);
        KeyPreview = true;
        AllowDrop = true;
        _settings = AppSettings.Load();
        AppLogger.Initialize(_settings.LogDirectory);
        AppLogger.Info("Startup", $"config={AppSettings.ConfigPath}; rhoDirectory={_settings.RhoDirectory}; logDirectory={AppLogger.LogDirectory}");

        BuildToolbar();
        BuildLayout();
        WireEvents();
        UpdateButtons();
    }

    private void BuildToolbar()
    {
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.BackColor = Color.White;
        _toolStrip.Padding = new Padding(8, 5, 8, 5);
        _toolStrip.Renderer = new ToolStripProfessionalRenderer(new RhoToolStripColorTable());
        _toolStrip.ImageScalingSize = new Size(18, 18);

        _toolStrip.Items.Add(CreateToolButton("📁 選擇資料夾", async (_, _) => await ChooseFolderAsync()));
        _importConfiguredButton.Text = "📌 匯入設定目錄";
        _importConfiguredButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _importConfiguredButton.AutoSize = true;
        _importConfiguredButton.Padding = new Padding(7, 4, 7, 4);
        _importConfiguredButton.ToolTipText = "直接載入設定中的預設 Rho 目錄";
        _importConfiguredButton.Click += async (_, _) => await ImportConfiguredFolderAsync();
        _toolStrip.Items.Add(_importConfiguredButton);
        _toolStrip.Items.Add(CreateToolButton("＋ 選擇 Rho 檔案", async (_, _) => await ChooseFilesAsync()));
        _toolStrip.Items.Add(new ToolStripSeparator());
        _exportSelectedButton.Text = "匯出選取";
        _exportSelectedButton.AutoSize = true;
        _exportSelectedButton.Padding = new Padding(8, 3, 8, 3);
        ConfigureActionButton(_exportSelectedButton, primary: true);
        _exportSelectedButton.Click += async (_, _) => await ExportSelectedAsync();
        _toolStrip.Items.Add(new ToolStripControlHost(_exportSelectedButton) { Margin = new Padding(2, 0, 2, 0) });
        _exportAllButton.Text = "匯出全部";
        _exportAllButton.AutoSize = true;
        _exportAllButton.Padding = new Padding(8, 3, 8, 3);
        ConfigureActionButton(_exportAllButton, primary: false);
        _exportAllButton.Click += async (_, _) => await ExportAllAsync();
        _toolStrip.Items.Add(new ToolStripControlHost(_exportAllButton) { Margin = new Padding(2, 0, 2, 0) });
        _selectAllButton.Text = "全選";
        _selectAllButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _selectAllButton.AutoSize = true;
        _selectAllButton.Padding = new Padding(7, 4, 7, 4);
        _selectAllButton.ToolTipText = "選取目前篩選結果（Ctrl+A）";
        _selectAllButton.Click += (_, _) => SelectAllVisibleRows();
        _toolStrip.Items.Add(_selectAllButton);
        _invertSelectionButton.Text = "反選";
        _invertSelectionButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _invertSelectionButton.AutoSize = true;
        _invertSelectionButton.Padding = new Padding(7, 4, 7, 4);
        _invertSelectionButton.ToolTipText = "反轉目前篩選結果的選取狀態（Ctrl+I）";
        _invertSelectionButton.Click += (_, _) => InvertVisibleRows();
        _toolStrip.Items.Add(_invertSelectionButton);
        _playAudioButton.Text = "▶ 播放音效";
        _playAudioButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _playAudioButton.AutoSize = true;
        _playAudioButton.Padding = new Padding(7, 4, 7, 4);
        _playAudioButton.ToolTipText = "先解出選取的 OGG／FLAC／WAV／MP3，再交給播放器播放";
        _playAudioButton.Click += async (_, _) => await PlaySelectedAudioAsync();
        _toolStrip.Items.Add(_playAudioButton);
        _stopAudioButton.Text = "■ 停止音效";
        _stopAudioButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _stopAudioButton.AutoSize = true;
        _stopAudioButton.Padding = new Padding(7, 4, 7, 4);
        _stopAudioButton.Click += (_, _) => StopAudio();
        _toolStrip.Items.Add(_stopAudioButton);
        _cancelExportButton.Text = "取消匯出";
        _cancelExportButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _cancelExportButton.AutoSize = true;
        _cancelExportButton.Padding = new Padding(7, 4, 7, 4);
        _cancelExportButton.Visible = false;
        _cancelExportButton.Click += (_, _) =>
        {
            AppLogger.Info("Export", "cancel requested");
            _exportCts?.Cancel();
        };
        _toolStrip.Items.Add(_cancelExportButton);
        _toolStrip.Items.Add(CreateToolButton("清除", (_, _) => ClearArchives()));
        _toolStrip.Items.Add(new ToolStripSeparator());
        _settingsButton.Text = "⚙ 設定";
        _settingsButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _settingsButton.AutoSize = true;
        _settingsButton.Padding = new Padding(7, 4, 7, 4);
        _settingsButton.ToolTipText = "設定預設 Rho 目錄與 Log 目錄";
        _settingsButton.Click += (_, _) => ShowSettings();
        _toolStrip.Items.Add(_settingsButton);
        _toolStrip.Items.Add(CreateToolButton("關於", (_, _) => ShowAbout()));

        _progress.Alignment = ToolStripItemAlignment.Right;
        _progress.Width = 140;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Visible = false;
        _toolStrip.Items.Add(_progress);
    }

    private static ToolStripButton CreateToolButton(string text, EventHandler handler)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true,
            Padding = new Padding(7, 4, 7, 4)
        };
        button.Click += handler;
        return button;
    }

    private static void ConfigureActionButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(54, 116, 190) : Color.FromArgb(226, 232, 240);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(39, 91, 157) : Color.FromArgb(210, 219, 231);
        button.BackColor = primary ? Color.FromArgb(45, 105, 175) : Color.FromArgb(235, 240, 246);
        button.ForeColor = primary ? Color.White : Color.FromArgb(45, 55, 72);
        button.Cursor = Cursors.Hand;
        button.Height = 28;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.FromArgb(242, 245, 249)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        Controls.Add(root);
        _toolStrip.Dock = DockStyle.Fill;
        root.Controls.Add(_toolStrip, 0, 0);

        _mainSplit.Dock = DockStyle.Fill;
        _mainSplit.Orientation = Orientation.Vertical;
        _mainSplit.SplitterWidth = 6;
        _mainSplit.BackColor = Color.FromArgb(220, 226, 235);
        _mainSplit.BorderStyle = BorderStyle.FixedSingle;
        root.Controls.Add(_mainSplit, 0, 1);

        BuildArchivePanel(_mainSplit.Panel1);
        BuildContentPanel(_mainSplit.Panel2);

        var statusPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _statusLabel.AutoSize = true;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = Color.FromArgb(90, 99, 112);
        statusPanel.Controls.Add(_statusLabel);
        root.Controls.Add(statusPanel, 0, 2);
    }

    private void BuildArchivePanel(Control host)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 5,
            ColumnCount = 1,
            BackColor = Color.FromArgb(248, 250, 252),
            Padding = new Padding(8)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        host.Controls.Add(panel);

        var title = new Label
        {
            Text = "已載入的 Rho 封包",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(35, 44, 58),
            TextAlign = ContentAlignment.MiddleLeft
        };
        panel.Controls.Add(title, 0, 0);

        _archiveList.Dock = DockStyle.Fill;
        _archiveList.BorderStyle = BorderStyle.FixedSingle;
        _archiveList.SelectionMode = SelectionMode.One;
        _archiveList.DisplayMember = nameof(ArchiveListEntry.DisplayText);
        _archiveList.Font = new Font("Microsoft JhengHei UI", 9F);
        _archiveList.DrawMode = DrawMode.OwnerDrawFixed;
        _archiveList.ItemHeight = 26;
        _archiveList.DrawItem += DrawArchiveListItem;
        panel.Controls.Add(_archiveList, 0, 1);

        var treeTitle = new Label
        {
            Text = "封包內資料夾",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(35, 44, 58),
            TextAlign = ContentAlignment.MiddleLeft
        };
        panel.Controls.Add(treeTitle, 0, 2);

        _resourceTree.Dock = DockStyle.Fill;
        _resourceTree.BorderStyle = BorderStyle.FixedSingle;
        _resourceTree.HideSelection = false;
        _resourceTree.FullRowSelect = true;
        _resourceTree.Font = new Font("Microsoft JhengHei UI", 9F);
        panel.Controls.Add(_resourceTree, 0, 3);

        var hint = new Label
        {
            Text = "可拖曳資料夾或 .rho 到視窗\n選取封包可快速篩選內容",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(110, 119, 132),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        panel.Controls.Add(hint, 0, 4);
    }

    private void BuildContentPanel(Control host)
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        host.Controls.Add(content);

        var filterPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        content.Controls.Add(filterPanel, 0, 0);

        var searchLabel = new Label { Text = "搜尋", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        filterPanel.Controls.Add(searchLabel, 0, 0);
        _searchBox.Dock = DockStyle.Fill;
        _searchBox.BorderStyle = BorderStyle.FixedSingle;
        _searchBox.PlaceholderText = "輸入檔名、路徑或副檔名…";
        _searchBox.Margin = new Padding(0, 5, 8, 5);
        filterPanel.Controls.Add(_searchBox, 1, 0);
        _extensionFilter.Dock = DockStyle.Fill;
        _extensionFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _extensionFilter.DrawMode = DrawMode.OwnerDrawFixed;
        _extensionFilter.DrawItem += DrawComboItem;
        _extensionFilter.Margin = new Padding(0, 5, 0, 5);
        _extensionFilter.Items.Add("全部類型");
        _extensionFilter.SelectedIndex = 0;
        filterPanel.Controls.Add(_extensionFilter, 2, 0);

        _breadcrumbLabel.Dock = DockStyle.Fill;
        _breadcrumbLabel.AutoEllipsis = true;
        _breadcrumbLabel.TextAlign = ContentAlignment.MiddleLeft;
        _breadcrumbLabel.ForeColor = Color.FromArgb(90, 99, 112);
        _breadcrumbLabel.Padding = new Padding(4, 0, 4, 0);
        content.Controls.Add(_breadcrumbLabel, 0, 1);

        ConfigureGrid();
        content.Controls.Add(_fileGrid, 0, 2);

        var previewGroup = new GroupBox
        {
            Text = "預覽與資源資訊",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ForeColor = Color.FromArgb(35, 44, 58)
        };
        content.Controls.Add(previewGroup, 0, 3);

        _previewTitle.Text = "尚未選取資源";
        _previewTitle.Dock = DockStyle.Top;
        _previewTitle.Height = 28;
        _previewTitle.Font = new Font(Font, FontStyle.Bold);
        _previewTitle.AutoEllipsis = true;
        previewGroup.Controls.Add(_previewTitle);

        _previewImage.Dock = DockStyle.Fill;
        _previewImage.SizeMode = PictureBoxSizeMode.Zoom;
        _previewImage.BackColor = Color.FromArgb(249, 250, 252);
        _previewImage.Visible = false;
        previewGroup.Controls.Add(_previewImage);

        _previewText.Dock = DockStyle.Fill;
        _previewText.ReadOnly = true;
        _previewText.Multiline = true;
        _previewText.ScrollBars = ScrollBars.Both;
        _previewText.WordWrap = false;
        _previewText.BackColor = Color.FromArgb(249, 250, 252);
        _previewText.Font = new Font("Consolas", 9F);
        previewGroup.Controls.Add(_previewText);
    }

    private void ConfigureGrid()
    {
        _fileGrid.Dock = DockStyle.Fill;
        _fileGrid.AllowUserToAddRows = false;
        _fileGrid.AllowUserToDeleteRows = false;
        _fileGrid.AllowUserToResizeRows = false;
        _fileGrid.AutoGenerateColumns = false;
        _fileGrid.BackgroundColor = Color.White;
        _fileGrid.BorderStyle = BorderStyle.FixedSingle;
        _fileGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _fileGrid.GridColor = Color.FromArgb(229, 234, 241);
        _fileGrid.RowTemplate.Height = 28;
        _fileGrid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(249, 251, 253)
        };
        _fileGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Color.FromArgb(35, 44, 58),
            SelectionBackColor = Color.FromArgb(214, 232, 252),
            SelectionForeColor = Color.FromArgb(25, 45, 72),
            Padding = new Padding(5, 0, 5, 0)
        };
        _fileGrid.RowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Color.FromArgb(35, 44, 58),
            SelectionBackColor = Color.FromArgb(214, 232, 252),
            SelectionForeColor = Color.FromArgb(25, 45, 72)
        };
        _fileGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(238, 242, 247),
            ForeColor = Color.FromArgb(35, 44, 58),
            Font = new Font(Font, FontStyle.Bold),
            SelectionBackColor = Color.FromArgb(238, 242, 247),
            SelectionForeColor = Color.FromArgb(35, 44, 58)
        };
        _fileGrid.EnableHeadersVisualStyles = false;
        _fileGrid.ColumnHeadersHeight = 30;
        _fileGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _fileGrid.RowHeadersVisible = false;
        _fileGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _fileGrid.MultiSelect = true;
        _fileGrid.ReadOnly = true;
        _fileGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "封包", Width = 150, Name = "Archive" });
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "資源路徑", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 240, Name = "Path" });
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "類型", Width = 80, Name = "Type" });
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "大小", Width = 92, Name = "Size" });
        _fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "保護方式", Width = 145, Name = "Property" });
    }

    private void WireEvents()
    {
        _archiveList.SelectedIndexChanged += async (_, _) =>
        {
            _selectedFolderPath = string.Empty;
            _treeContextFilter = null;
            RefreshResourceTree();
            UpdateBreadcrumb();
            ApplyFilter();
            await PreviewSelectedAsync();
        };
        _resourceTree.AfterSelect += (_, e) =>
        {
            if (_rebuildingTree)
                return;

            if (e.Node is not null && e.Node.Tag is ResourceTreeNodeTag tag)
            {
                _treeContextFilter = tag.Context;
                _selectedFolderPath = tag.RelativePath;
                UpdateBreadcrumb();
                ApplyFilter();
            }
        };
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        _extensionFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _fileGrid.SelectionChanged += async (_, _) =>
        {
            UpdateButtons();
            if (_selectionUpdating)
                return;
            await PreviewSelectedAsync();
        };
        _mainSplit.SplitterMoved += (_, _) => CaptureSplitterRatio();
        Resize += (_, _) => ApplyResponsiveLayout();
        Shown += (_, _) => InitializeResponsiveLayout();
        KeyDown += MainForm_KeyDown;
        DragEnter += MainForm_DragEnter;
        DragDrop += MainForm_DragDrop;
        FormClosing += (_, _) =>
        {
            SaveWindowLayout();
            StopAudio();
            DisposeArchives();
            CleanupPlaybackFiles();
        };
    }

    private void ApplyResponsiveLayout()
    {
        if (!_layoutInitialized || _resizingLayout || _mainSplit.ClientSize.Width <= 0)
            return;

        _resizingLayout = true;
        try
        {
            var availableWidth = _mainSplit.ClientSize.Width;
            var minimum = _mainSplit.Panel1MinSize;
            var maximum = Math.Max(minimum, availableWidth - _mainSplit.Panel2MinSize - _mainSplit.SplitterWidth);
            var target = (int)Math.Round(availableWidth * _settings.SplitterRatio);
            _mainSplit.SplitterDistance = Math.Clamp(target, minimum, maximum);
        }
        finally
        {
            _resizingLayout = false;
        }
    }

    private void CaptureSplitterRatio()
    {
        if (_layoutInitialized && _mainSplit.ClientSize.Width > 0)
            _settings.SplitterRatio = Math.Clamp((double)_mainSplit.SplitterDistance / _mainSplit.ClientSize.Width, 0.18, 0.42);
    }

    private void InitializeResponsiveLayout()
    {
        _mainSplit.Panel1MinSize = 180;
        _mainSplit.Panel2MinSize = 420;
        _layoutInitialized = true;
        ApplyResponsiveLayout();
    }

    private void SaveWindowLayout()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        CaptureSplitterRatio();
        if (!_settings.TrySave(out var error))
            AppLogger.Warning("SaveLayout", error);
        else
            AppLogger.Info("SaveLayout", $"size={_settings.WindowWidth}x{_settings.WindowHeight}; splitter={_settings.SplitterRatio:F3}");
    }

    private void DrawArchiveListItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _archiveList.Items.Count)
            return;

        var selected = (e.State & DrawItemState.Selected) != 0;
        var background = selected ? Color.FromArgb(218, 234, 251) : Color.White;
        var foreground = selected ? Color.FromArgb(25, 67, 112) : Color.FromArgb(45, 55, 72);
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, _archiveList.Items[e.Index]?.ToString() ?? string.Empty, _archiveList.Font,
            e.Bounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo || e.Index < 0 || e.Index >= combo.Items.Count)
            return;

        var background = Color.White;
        var foreground = Color.FromArgb(35, 44, 58);
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString() ?? string.Empty, combo.Font,
            e.Bounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private async void MainForm_DragDrop(object? sender, DragEventArgs e)
    {
        if (_busy || e.Data?.GetData(DataFormats.FileDrop) is not string[] dropped)
            return;

        AppLogger.Info("DragDrop", $"items={dropped.Length}");
        var files = await CollectRhoFilesAsync(dropped);
        if (files.Count > 0)
            await LoadPathsAsync(files);
        else
            MessageBox.Show(this, "拖曳內容中找不到 .rho 檔案。", "沒有可載入的檔案", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void MainForm_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = !_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            if (_exportCts is not null)
                _exportCts.Cancel();
            else if (_playbackProcess is not null || _activePlaybackTempDirectory is not null)
                StopAudio();
            e.Handled = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.F)
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.A)
        {
            SelectAllVisibleRows();
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.I)
        {
            InvertVisibleRows();
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.E)
        {
            await ExportSelectedAsync();
            e.SuppressKeyPress = true;
        }
    }

    private void SelectAllVisibleRows()
    {
        if (_busy || _fileGrid.Rows.Count == 0)
            return;

        _selectionUpdating = true;
        try
        {
            _fileGrid.SelectAll();
        }
        finally
        {
            _selectionUpdating = false;
        }
        UpdateButtons();
    }

    private void InvertVisibleRows()
    {
        if (_busy || _fileGrid.Rows.Count == 0)
            return;

        _selectionUpdating = true;
        try
        {
            foreach (DataGridViewRow row in _fileGrid.Rows)
                row.Selected = !row.Selected;
        }
        finally
        {
            _selectionUpdating = false;
        }
        UpdateButtons();
        _ = PreviewSelectedAsync();
    }

    private async Task ChooseFolderAsync()
    {
        if (_busy)
            return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "選擇要掃描 .rho 檔案的資料夾（包含子資料夾）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_settings.RhoDirectory) ? _settings.RhoDirectory : string.Empty
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        AppLogger.Info("ChooseFolder", $"selected={dialog.SelectedPath}");
        var files = await CollectRhoFilesAsync(new[] { dialog.SelectedPath });
        if (files.Count == 0)
        {
            MessageBox.Show(this, "此資料夾與子資料夾沒有找到 .rho 檔案。", "沒有可載入的檔案", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await LoadPathsAsync(files);
    }

    private async Task ImportConfiguredFolderAsync()
    {
        if (_busy)
            return;

        if (!Directory.Exists(_settings.RhoDirectory))
        {
            AppLogger.Warning("ImportConfiguredFolder", $"directory not found: {_settings.RhoDirectory}");
            MessageBox.Show(this, "設定中的預設 Rho 目錄不存在，請先到「設定」修正路徑。", "找不到預設目錄", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AppLogger.Info("ImportConfiguredFolder", $"directory={_settings.RhoDirectory}");
        var files = await CollectRhoFilesAsync(new[] { _settings.RhoDirectory });
        if (files.Count == 0)
        {
            AppLogger.Warning("ImportConfiguredFolder", "no rho files found");
            MessageBox.Show(this, "預設 Rho 目錄與子資料夾沒有找到 .rho 檔案。", "沒有可載入的檔案", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await LoadPathsAsync(files);
    }

    private void ShowSettings()
    {
        if (_busy)
            return;

        AppLogger.Info("OpenSettings", "opened");
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            AppLogger.Info("OpenSettings", "cancelled");
            return;
        }

        var updated = dialog.GetSettings();
        if (!updated.TrySave(out var error))
        {
            AppLogger.Error("SaveSettings", "failed", new IOException(error));
            MessageBox.Show(this, $"設定檔儲存失敗：{error}\n\n設定檔位置：{AppSettings.ConfigPath}", "設定未儲存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings = updated;
        AppLogger.Initialize(_settings.LogDirectory);
        AppLogger.Info("SaveSettings", $"rhoDirectory={_settings.RhoDirectory}; logDirectory={AppLogger.LogDirectory}");
        _statusLabel.Text = $"設定已儲存；Log：{AppLogger.LogDirectory}";
        UpdateButtons();
    }

    private async Task ChooseFilesAsync()
    {
        if (_busy)
            return;

        using var dialog = new OpenFileDialog
        {
            Title = "選擇 Rho 檔案",
            Filter = "Rho 封包 (*.rho)|*.rho|所有檔案 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AppLogger.Info("ChooseFiles", $"selected={dialog.FileNames.Length}");
            await LoadPathsAsync(dialog.FileNames);
        }
    }

    private static async Task<List<string>> CollectRhoFilesAsync(IEnumerable<string> roots)
    {
        return await Task.Run(() =>
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        foreach (var file in Directory.EnumerateFiles(root, "*.rho", SearchOption.AllDirectories))
                            result.Add(System.IO.Path.GetFullPath(file));
                    }
                    else if (File.Exists(root) && string.Equals(System.IO.Path.GetExtension(root), ".rho", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(System.IO.Path.GetFullPath(root));
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // 單一受保護子目錄不可阻止其他檔案繼續載入。
                }
                catch (IOException)
                {
                    // 暫時失效或無效路徑略過，最後只透過統計與 Log 回報。
                }
            }

            return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        });
    }

    private async Task LoadPathsAsync(IEnumerable<string> paths)
    {
        if (_busy)
            return;

        var uniquePaths = paths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !_contexts.Any(context => string.Equals(context.Path, path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (uniquePaths.Length == 0)
        {
            MessageBox.Show(this, "選取的檔案都已載入，或沒有可用的 .rho 檔案。", "沒有新增檔案", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, $"正在讀取 {uniquePaths.Length} 個 Rho 封包…");
        AppLogger.Info("LoadPaths", $"count={uniquePaths.Length}");
        var loaded = new List<RhoArchiveContext>();
        var failures = new List<string>();
        try
        {
            foreach (var path in uniquePaths)
            {
                try
                {
                    var context = await Task.Run(() => RhoReader.Open(path));
                    loaded.Add(context);
                    _statusLabel.Text = $"已讀取 {loaded.Count}/{uniquePaths.Length}：{Path.GetFileName(path)}";
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(path)}：{ex.Message}");
                    AppLogger.Error("LoadPaths", $"failed={path}", ex);
                }
            }

            _contexts.AddRange(loaded);
            _allItems.AddRange(loaded.SelectMany(context => context.Items));
            AppLogger.Info("LoadPaths", $"loaded={loaded.Count}; resources={loaded.Sum(context => context.Items.Count)}; failures={failures.Count}");
            RefreshArchiveList();
            ApplyFilter();
            if (failures.Count > 0)
            {
                var details = string.Join(Environment.NewLine, failures.Take(8));
                if (failures.Count > 8)
                    details += Environment.NewLine + $"…另有 {failures.Count - 8} 個檔案失敗。";
                MessageBox.Show(this, details, "部分 Rho 無法讀取", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            SetBusy(false, $"已載入 {_contexts.Count} 個封包、{_allItems.Count:N0} 個資源");
        }
    }

    internal Task LoadPathsForSmokeAsync(IEnumerable<string> paths) => LoadPathsAsync(paths);

    private void RefreshArchiveList()
    {
        var selectedPath = (_archiveList.SelectedItem as ArchiveListEntry)?.Context?.Path;
        _archiveList.BeginUpdate();
        try
        {
            _archiveList.Items.Clear();
            _archiveList.Items.Add(new ArchiveListEntry(null, $"全部封包（{_allItems.Count:N0} 個資源）"));
            foreach (var context in _contexts.OrderBy(context => context.DisplayName, StringComparer.OrdinalIgnoreCase))
                _archiveList.Items.Add(new ArchiveListEntry(context, BuildArchiveListText(context)));

            var index = 0;
            if (selectedPath is not null)
            {
                for (var i = 1; i < _archiveList.Items.Count; i++)
                {
                    if ((_archiveList.Items[i] as ArchiveListEntry)?.Context?.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        index = i;
                        break;
                    }
                }
            }
            _archiveList.SelectedIndex = index;
        }
        finally
        {
            _archiveList.EndUpdate();
        }
        RefreshResourceTree();
        UpdateBreadcrumb();
    }

    private void RefreshResourceTree()
    {
        _rebuildingTree = true;
        _resourceTree.BeginUpdate();
        try
        {
            _resourceTree.Nodes.Clear();
            var selectedArchive = (_archiveList.SelectedItem as ArchiveListEntry)?.Context;
            var root = new TreeNode(selectedArchive is null ? "全部封包" : selectedArchive.DisplayName)
            {
                Tag = new ResourceTreeNodeTag(selectedArchive, string.Empty)
            };
            _resourceTree.Nodes.Add(root);

            IEnumerable<RhoArchiveContext> contexts = selectedArchive is null
                ? _contexts.OrderBy(context => context.DisplayName, StringComparer.OrdinalIgnoreCase)
                : new[] { selectedArchive };
            foreach (var context in contexts)
            {
                var contextRoot = selectedArchive is null
                    ? new TreeNode(context.DisplayName) { Tag = new ResourceTreeNodeTag(context, string.Empty) }
                    : root;
                if (selectedArchive is null)
                    root.Nodes.Add(contextRoot);
                AddFolderNodes(context, contextRoot);
            }

            root.Expand();
            if (selectedArchive is null)
                foreach (TreeNode child in root.Nodes)
                    child.Expand();
            _resourceTree.SelectedNode = FindTreeNode(root, selectedArchive, _selectedFolderPath) ?? root;
        }
        finally
        {
            _resourceTree.EndUpdate();
            _rebuildingTree = false;
        }
    }

    private static void AddFolderNodes(RhoArchiveContext context, TreeNode contextRoot)
    {
        var nodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = contextRoot
        };
        foreach (var item in context.Items)
        {
            var parts = item.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
                continue;

            var path = string.Empty;
            var parent = contextRoot;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                path = string.IsNullOrEmpty(path) ? parts[i] : $"{path}/{parts[i]}";
                if (!nodes.TryGetValue(path, out var child))
                {
                    child = new TreeNode(parts[i])
                    {
                        Tag = new ResourceTreeNodeTag(context, path)
                    };
                    parent.Nodes.Add(child);
                    nodes[path] = child;
                }
                parent = child;
            }
        }
    }

    private static TreeNode? FindTreeNode(TreeNode root, RhoArchiveContext? context, string relativePath)
    {
        if (root.Tag is ResourceTreeNodeTag rootTag && ReferenceEquals(rootTag.Context, context) && string.Equals(rootTag.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            return root;
        foreach (TreeNode child in root.Nodes)
        {
            var found = FindTreeNode(child, context, relativePath);
            if (found is not null)
                return found;
        }
        return null;
    }

    private void UpdateBreadcrumb()
    {
        var selectedArchive = (_archiveList.SelectedItem as ArchiveListEntry)?.Context ?? _treeContextFilter;
        var archiveText = selectedArchive?.DisplayName ?? _treeContextFilter?.DisplayName ?? "全部封包";
        _breadcrumbLabel.Text = string.IsNullOrEmpty(_selectedFolderPath)
            ? $"位置：{archiveText}"
            : $"位置：{archiveText}  ›  {_selectedFolderPath.Replace("/", "  ›  ")}";
    }

    private void ApplyFilter()
    {
        if (_updatingFilters || _archiveList.Items.Count == 0)
            return;

        _updatingFilters = true;
        try
        {
            var selectedArchive = (_archiveList.SelectedItem as ArchiveListEntry)?.Context;
            var filterArchive = selectedArchive ?? _treeContextFilter;
            var query = _searchBox.Text.Trim();
            var extension = _extensionFilter.SelectedItem as string ?? "全部類型";
            var extensions = _allItems
                .Where(item => filterArchive is null || ReferenceEquals(item.Owner, filterArchive))
                .Select(item => item.Extension)
                .Where(extensionName => !string.IsNullOrEmpty(extensionName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(extensionName => extensionName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var current = extension;
            _extensionFilter.BeginUpdate();
            _extensionFilter.Items.Clear();
            _extensionFilter.Items.Add("全部類型");
            foreach (var extensionName in extensions)
                _extensionFilter.Items.Add(extensionName);
            _extensionFilter.SelectedItem = _extensionFilter.Items.Contains(current) ? current : "全部類型";
            _extensionFilter.EndUpdate();
            extension = _extensionFilter.SelectedItem as string ?? "全部類型";

            var filtered = _allItems.Where(item =>
                    (filterArchive is null || ReferenceEquals(item.Owner, filterArchive)) &&
                    (string.IsNullOrEmpty(_selectedFolderPath) || item.RelativePath.StartsWith(_selectedFolderPath + "/", StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(query) || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Owner.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
                    (extension == "全部類型" || string.Equals(item.Extension, extension, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            _fileGrid.Rows.Clear();
            foreach (var item in filtered)
            {
                var rowIndex = _fileGrid.Rows.Add(item.Owner.DisplayName, item.RelativePath, item.Extension, FormatSize(item.Size), item.Property);
                _fileGrid.Rows[rowIndex].Tag = item;
            }

            _summaryLabel.Text = $"顯示 {filtered.Count:N0} / {_allItems.Count:N0} 個資源";
            _statusLabel.Text = _contexts.Count == 0 ? "請選擇資料夾或 Rho 檔案開始。" : $"已載入 {_contexts.Count} 個封包、{_allItems.Count:N0} 個資源；目前顯示 {filtered.Count:N0} 個";
            if (!string.IsNullOrWhiteSpace(query) || extension != "全部類型" || !string.IsNullOrEmpty(_selectedFolderPath))
                AppLogger.Info("Filter", $"query={query}; extension={extension}; folder={_selectedFolderPath}; shown={filtered.Count}");
            UpdateButtons();
        }
        finally
        {
            _updatingFilters = false;
        }
    }

    private async Task PreviewSelectedAsync()
    {
        var item = _fileGrid.SelectedRows.Count > 0 ? _fileGrid.SelectedRows[0].Tag as RhoItem : null;
        var selectedArchive = (_archiveList.SelectedItem as ArchiveListEntry)?.Context ?? _treeContextFilter;
        var generation = ++_previewGeneration;
        DisposePreviewBitmap();
        _previewImage.Visible = false;
        _previewText.Visible = true;
        _previewText.Text = string.Empty;
        if (item is null)
        {
            if (selectedArchive is not null)
            {
                _previewTitle.Text = $"{selectedArchive.DisplayName} · 封包分析";
                _previewText.Text = BuildArchiveSummary(selectedArchive);
            }
            else if (_contexts.Count > 0)
            {
                _previewTitle.Text = "封包分析摘要";
                _previewText.Text = BuildAllArchivesSummary();
            }
            else
            {
                _previewTitle.Text = "尚未選取資源";
            }
            return;
        }

        _previewTitle.Text = $"{item.Owner.DisplayName} / {item.RelativePath}";
        _previewText.Text = "正在解碼資源…";
        try
        {
            var bytes = await Task.Run(item.ReadBytes);
            if (generation != _previewGeneration || IsDisposed)
                return;

            var info = BuildItemInfo(item, bytes.Length);
            if (item.Extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var source = Image.FromStream(stream);
                _currentBitmap = new Bitmap(source);
                _previewImage.Image = _currentBitmap;
                _previewImage.Visible = true;
                _previewText.Visible = false;
                _previewTitle.Text = $"{item.Name}  ·  {source.Width} × {source.Height}  ·  {FormatSize(bytes.Length)}";
            }
            else
            {
                _previewText.Text = info + Environment.NewLine + Environment.NewLine + BuildPayloadPreview(item, bytes);
            }
        }
        catch (Exception ex)
        {
            if (generation == _previewGeneration)
                _previewText.Text = BuildItemInfo(item, item.Size) + Environment.NewLine + Environment.NewLine + "解碼失敗：" + ex.Message;
        }
    }

    private static string BuildArchiveListText(RhoArchiveContext context)
    {
        var summary = context.Items.Count == 0 ? "空封包" : $"{context.Items.Count:N0} 個資源";
        return $"{context.DisplayName}  ·  {summary}";
    }

    private static string BuildArchiveSummary(RhoArchiveContext context)
    {
        var builder = new StringBuilder("Rho 封包分析\n");
        builder.AppendLine($"檔案：{context.DisplayName}");
        builder.AppendLine($"檔案大小：{FormatSize(new FileInfo(context.Path).Length)}");
        builder.AppendLine($"格式層：{context.LayerVersion}");
        builder.AppendLine($"子資料夾數：{context.FolderCount:N0}");
        builder.AppendLine($"資源數：{context.Items.Count:N0}");

        if (context.Items.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("結果：封包已成功解析，但沒有資源項目。");
            builder.AppendLine("這不是載入失敗；目前檔案本身是空的 Rho 封包，因此沒有可預覽、可分析或可匯出的內容。");
            return builder.ToString();
        }

        builder.AppendLine();
        builder.AppendLine("資源類型：");
        foreach (var group in context.Items
                     .GroupBy(item => string.IsNullOrEmpty(item.Extension) ? "（無副檔名）" : item.Extension, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"  {group.Key}：{group.Count():N0}");
        }

        builder.AppendLine();
        builder.AppendLine("請在上方清單選取資源，以查看圖片、文字、HEX 或 OGG／FLAC／KSV 分析。");
        return builder.ToString();
    }

    private string BuildAllArchivesSummary()
    {
        var builder = new StringBuilder("Rho 封包分析摘要\n");
        builder.AppendLine($"封包數：{_contexts.Count:N0}");
        builder.AppendLine($"資源總數：{_allItems.Count:N0}");
        builder.AppendLine($"空封包：{_contexts.Count(context => context.Items.Count == 0):N0}");
        builder.AppendLine();

        foreach (var context in _contexts.OrderBy(context => context.DisplayName, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"- {BuildArchiveListText(context)}");

        if (_allItems.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("目前載入的 Rho 都沒有資源項目；請改選有內容的 Rho，或確認遊戲資源是否使用其他封包格式。");
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("請從左側選取單一封包，或從中央清單選取資源查看詳細分析。");
        }

        return builder.ToString();
    }

    private static string BuildItemInfo(RhoItem item, int decodedSize)
    {
        var kind = item.Extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" => "圖片",
            ".ogg" or ".flac" or ".wav" or ".mp3" or ".ksv" => "音訊／音效（可匯出／播放）",
            ".bml" or ".xml" or ".json" => "結構／文字資料",
            _ => "其他二進位資料"
        };
        return $"封包：{item.Owner.DisplayName}{Environment.NewLine}路徑：{item.RelativePath}{Environment.NewLine}類型：{kind} ({item.Extension}){Environment.NewLine}解碼後大小：{FormatSize(decodedSize)}{Environment.NewLine}保護方式：{item.Property}{Environment.NewLine}{(kind.StartsWith("音訊", StringComparison.Ordinal) ? "播放方式：先解出暫存檔，再交給 Players\u002f 或 Windows 預設播放器。" : "")}";
    }

    private static string BuildPayloadPreview(RhoItem item, byte[] bytes)
    {
        var specialized = ResourceAnalyzers.Analyze(item, bytes);
        if (!string.IsNullOrEmpty(specialized))
            return specialized;

        if (item.Extension is ".bml" or ".xml" or ".json" or ".txt" or ".ini" or ".lua")
        {
            var text = DecodeText(bytes);
            if (text.Length > 12000)
                text = text[..12000] + Environment.NewLine + "…（預覽截斷，請匯出完整檔案）";
            return text;
        }

        var length = Math.Min(bytes.Length, 512);
        var builder = new StringBuilder($"HEX 預覽（前 {length:N0} bytes）：");
        for (var offset = 0; offset < length; offset += 16)
        {
            builder.AppendLine();
            builder.Append($"{offset:X8}  ");
            for (var i = 0; i < 16; i++)
                builder.Append(offset + i < length ? $"{bytes[offset + i]:X2} " : "   ");
            builder.Append(" ");
            for (var i = 0; i < 16 && offset + i < length; i++)
            {
                var value = bytes[offset + i];
                builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }
        }
        return builder.ToString();
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return Encoding.GetEncoding(950).GetString(bytes);
        }
    }

    private async Task ExportSelectedAsync()
    {
        var items = _fileGrid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag as RhoItem).Where(item => item is not null).Cast<RhoItem>().ToList();
        if (items.Count == 0)
        {
            MessageBox.Show(this, "請先在清單中選取至少一個資源。", "尚未選取資源", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await ExportAsync(items);
    }

    private async Task PlaySelectedAudioAsync()
    {
        var item = GetSelectedAudioItem();
        if (item is null)
        {
            MessageBox.Show(this, "請先選取一個 OGG、FLAC、WAV 或 MP3 資源。", "尚未選取音效", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        StopAudio();
        SetBusy(true, $"正在解出音效：{item.Name}…");
        AppLogger.Info("PlayAudio", $"started={item.Owner.DisplayName}/{item.RelativePath}");
        try
        {
            var bytes = await Task.Run(item.ReadBytes);
            var playbackDirectory = Path.Combine(Path.GetTempPath(), "RhoViewerPortable", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(playbackDirectory);
            var fileName = SanitizeFileName(item.Name);
            if (!fileName.EndsWith(item.Extension, StringComparison.OrdinalIgnoreCase))
                fileName += item.Extension;
            var tempFile = Path.Combine(playbackDirectory, fileName);
            File.WriteAllBytes(tempFile, bytes);
            _playbackTempDirectories.Add(playbackDirectory);

            Process? process;
            var launched = TryLaunchBundledPlayer(tempFile, out process) || TryLaunchDefaultPlayer(tempFile, out process);
            if (launched)
            {
                _activePlaybackTempDirectory = playbackDirectory;
                _playbackProcess = process;
                if (_playbackProcess is not null)
                {
                    _playbackProcess.EnableRaisingEvents = true;
                    _playbackProcess.Exited += (_, _) =>
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke((MethodInvoker)(() => FinishPlayback(playbackDirectory)));
                    };
                    _statusLabel.Text = $"正在播放：{item.Name}";
                }
                else
                {
                    _statusLabel.Text = $"已交給 Windows 預設播放器：{item.Name}";
                }
                AppLogger.Info("PlayAudio", $"launched={item.Name}; temp={tempFile}; process={(process is null ? "shell" : process.Id.ToString())}");
                UpdateButtons();
                return;
            }

            MessageBox.Show(this,
                $"已成功解出音效，但找不到可播放 {item.Extension} 的播放器。\n\n" +
                "請安裝 VLC、mpv 或 ffplay，或將播放器放到 portable\\Players\\ 資料夾後重試。\n" +
                $"暫存檔：{tempFile}",
                "找不到音效播放器", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Error("PlayAudio", $"failed={item.Name}", ex);
            MessageBox.Show(this, $"音效解出或播放失敗：{ex.Message}", "播放失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            var status = _playbackProcess is not null || _activePlaybackTempDirectory is not null
                ? "音效已啟動；按「停止音效」可結束並清理暫存檔。"
                : $"已載入 {_contexts.Count} 個封包、{_allItems.Count:N0} 個資源";
            SetBusy(false, status);
        }
    }

    private RhoItem? GetSelectedAudioItem()
    {
        return _fileGrid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.Tag as RhoItem)
            .FirstOrDefault(item => item is not null && IsAudioExtension(item.Extension));
    }

    private static bool IsAudioExtension(string extension) => extension is ".ogg" or ".flac" or ".wav" or ".mp3";

    private void FinishPlayback(string tempDirectory)
    {
        if (!string.Equals(_activePlaybackTempDirectory, tempDirectory, StringComparison.OrdinalIgnoreCase))
            return;

        _playbackProcess?.Dispose();
        _playbackProcess = null;
        _activePlaybackTempDirectory = null;
        CleanupPlaybackDirectory(tempDirectory);
        AppLogger.Info("PlayAudio", $"finished={tempDirectory}");
        _statusLabel.Text = "音效播放結束，暫存檔已清理。";
        UpdateButtons();
    }

    private void StopAudio()
    {
        var process = _playbackProcess;
        var directory = _activePlaybackTempDirectory;
        _playbackProcess = null;
        _activePlaybackTempDirectory = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 外部播放器可能在檢查與 Kill 之間自行結束。
            }
            catch (Win32Exception)
            {
                // 透過 Windows shell 啟動的播放器可能不允許程序控制。
            }
            finally
            {
                process.Dispose();
            }
        }

        if (directory is not null)
            CleanupPlaybackDirectory(directory);
        if (!_busy && (process is not null || directory is not null))
        {
            AppLogger.Info("StopAudio", $"process={(process is null ? "none" : process.Id.ToString())}; directory={directory}");
            _statusLabel.Text = "音效已停止，暫存檔已清理。";
        }
        UpdateButtons();
    }

    private static bool TryLaunchBundledPlayer(string tempFile, out Process? process)
    {
        process = null;
        var playerNames = new[] { "vlc.exe", "mpv.exe", "ffplay.exe" };
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Players"),
            AppContext.BaseDirectory
        };
        foreach (var root in roots)
        {
            foreach (var playerName in playerNames)
            {
                var playerPath = Path.Combine(root, playerName);
                if (!File.Exists(playerPath))
                    continue;

                var startInfo = new ProcessStartInfo(playerPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(playerPath) ?? AppContext.BaseDirectory
                };
                startInfo.ArgumentList.Add(tempFile);
                try
                {
                    process = Process.Start(startInfo);
                    return true;
                }
                catch (Win32Exception ex)
                {
                    AppLogger.Warning("PlayAudio", $"bundled player failed={playerPath}; {ex.Message}");
                }
            }
        }

        return false;
    }

    private static bool TryLaunchDefaultPlayer(string tempFile, out Process? process)
    {
        process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = tempFile,
                UseShellExecute = true
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private async Task ExportAllAsync()
    {
        if (_allItems.Count == 0)
            return;
        await ExportAsync(_allItems);
    }

    private async Task ExportAsync(IReadOnlyCollection<RhoItem> items)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "選擇匯出根目錄；工具會按 Rho 檔名建立子資料夾",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        using var exportCts = new CancellationTokenSource();
        _exportCts = exportCts;
        SetBusy(true, $"正在匯出 {items.Count:N0} 個資源…");
        AppLogger.Info("Export", $"started={items.Count}; output={dialog.SelectedPath}");
        _progress.Visible = true;
        _progress.Minimum = 0;
        _progress.Maximum = Math.Max(1, items.Count);
        var outputRoot = Path.GetFullPath(dialog.SelectedPath);
        var exported = 0;
        var processed = 0;
        var failures = new List<string>();
        var cancelled = false;
        try
        {
            var archiveNames = BuildArchiveOutputNames(items.Select(item => item.Owner));
            await Task.Run(() =>
            {
                foreach (var item in items)
                {
                    try
                    {
                        exportCts.Token.ThrowIfCancellationRequested();
                        var archiveDir = Path.Combine(outputRoot, archiveNames[item.Owner]);
                        var relative = SafeRelativePath(item.RelativePath);
                        var outputPath = Path.GetFullPath(Path.Combine(archiveDir, relative));
                        if (!outputPath.StartsWith(archiveDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("封包內路徑超出匯出目錄。");
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        File.WriteAllBytes(outputPath, item.ReadBytes());
                        Interlocked.Increment(ref exported);
                        Interlocked.Increment(ref processed);
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke(() =>
                        {
                                _progress.Value = Math.Min(_progress.Maximum, processed);
                                _statusLabel.Text = $"已匯出 {exported}/{items.Count}：{item.Name}";
                            });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lock (failures)
                            failures.Add($"{item.Owner.DisplayName}/{item.RelativePath}：{ex.Message}");
                        AppLogger.Error("Export", $"failed={item.Owner.DisplayName}/{item.RelativePath}", ex);
                        Interlocked.Increment(ref processed);
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke(() => _progress.Value = Math.Min(_progress.Maximum, processed));
                    }
                }
            }, exportCts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            _exportCts = null;
            _progress.Visible = false;
            AppLogger.Info("Export", $"completed={exported}; failures={failures.Count}; cancelled={cancelled}");
            SetBusy(false, cancelled
                ? $"匯出已取消：成功 {exported:N0}、失敗 {failures.Count:N0}"
                : $"匯出完成：成功 {exported:N0}、失敗 {failures.Count:N0}");
        }

        string? reportPath = null;
        if (failures.Count > 0)
        {
            reportPath = Path.Combine(outputRoot, $"RhoViewer_export_failures_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            try
            {
                File.WriteAllText(reportPath,
                    "Rho Viewer Portable 匯出失敗報告" + Environment.NewLine +
                    $"時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                    $"失敗數：{failures.Count:N0}" + Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, failures),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                reportPath = $"（報告寫入失敗：{ex.Message}）";
            }

            var details = string.Join(Environment.NewLine, failures.Take(8));
            if (failures.Count > 8)
                details += Environment.NewLine + $"…另有 {failures.Count - 8} 個檔案，請查看完整報告。";
            MessageBox.Show(this, details + Environment.NewLine + Environment.NewLine + $"完整報告：{reportPath}", "部分資源匯出失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (cancelled)
        {
            MessageBox.Show(this, $"匯出已取消。已完成 {exported:N0} 個資源。", "匯出已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this, $"已匯出 {exported:N0} 個資源。", "匯出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static Dictionary<RhoArchiveContext, string> BuildArchiveOutputNames(IEnumerable<RhoArchiveContext> contexts)
    {
        var result = new Dictionary<RhoArchiveContext, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var context in contexts.Distinct())
        {
            var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(context.Path));
            var name = baseName;
            var suffix = 2;
            while (!used.Add(name))
                name = $"{baseName} ({suffix++})";
            result[context] = name;
        }
        return result;
    }

    private static string SafeRelativePath(string relativePath)
    {
        var parts = relativePath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Path.DirectorySeparatorChar, parts.Select(part => part is "." or ".." ? "_" : SanitizeFileName(part)));
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) ? '_' : character);
        return string.IsNullOrWhiteSpace(builder.ToString()) ? "unnamed" : builder.ToString();
    }

    private void ClearArchives()
    {
        if (_busy)
            return;
        AppLogger.Info("Clear", $"archives={_contexts.Count}; resources={_allItems.Count}");
        StopAudio();
        DisposeArchives();
        CleanupPlaybackFiles();
        _contexts.Clear();
        _allItems.Clear();
        _archiveList.Items.Clear();
        _resourceTree.Nodes.Clear();
        _selectedFolderPath = string.Empty;
        _treeContextFilter = null;
        UpdateBreadcrumb();
        _fileGrid.Rows.Clear();
        _extensionFilter.Items.Clear();
        _extensionFilter.Items.Add("全部類型");
        _extensionFilter.SelectedIndex = 0;
        DisposePreviewBitmap();
        _previewImage.Visible = false;
        _previewText.Visible = true;
        _previewText.Text = string.Empty;
        _previewTitle.Text = "尚未選取資源";
        _statusLabel.Text = "已清除所有封包。";
        UpdateButtons();
    }

    private void DisposeArchives()
    {
        foreach (var context in _contexts)
            context.Dispose();
    }

    private void DisposePreviewBitmap()
    {
        _previewImage.Image = null;
        _currentBitmap?.Dispose();
        _currentBitmap = null;
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _statusLabel.Text = status;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _exportSelectedButton.Enabled = !_busy && _fileGrid.SelectedRows.Count > 0;
        _exportAllButton.Enabled = !_busy && _allItems.Count > 0;
        _playAudioButton.Enabled = !_busy && GetSelectedAudioItem() is not null;
        _selectAllButton.Enabled = !_busy && _fileGrid.Rows.Count > 0;
        _invertSelectionButton.Enabled = !_busy && _fileGrid.Rows.Count > 0;
        _stopAudioButton.Visible = _playbackProcess is not null || _activePlaybackTempDirectory is not null;
        _stopAudioButton.Enabled = !_busy && _stopAudioButton.Visible;
        _cancelExportButton.Visible = _exportCts is not null;
        _cancelExportButton.Enabled = _exportCts is not null;
        _importConfiguredButton.Enabled = !_busy && Directory.Exists(_settings.RhoDirectory);
    }

    private void CleanupPlaybackFiles()
    {
        StopAudio();
        foreach (var directory in _playbackTempDirectories.ToArray())
            CleanupPlaybackDirectory(directory);
        _playbackTempDirectories.Clear();
    }

    private void CleanupPlaybackDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 播放器可能仍持有暫存檔，交由作業系統稍後清理。
        }
        catch (UnauthorizedAccessException)
        {
            // 播放器可能仍持有暫存檔，交由作業系統稍後清理。
        }
        _playbackTempDirectories.Remove(directory);
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "Rho Viewer Portable 0.1.4\n\n" +
            "用於讀取、預覽與匯出 KartRider Rho 封包。\n" +
            "支援從資料夾遞迴掃描 .rho，也可直接多選檔案。\n\n" +
            "音效會先解出暫存檔，再交給 portable\\Players 或 Windows 預設播放器。\n" +
            "格式解碼參考 xpoi5010/Kartrider-File-Reader。\n" +
            "第三方來源與 GPL-3.0 授權見 THIRD_PARTY_NOTICES.txt。",
            "關於 Rho Viewer Portable", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    private sealed record ArchiveListEntry(RhoArchiveContext? Context, string DisplayText)
    {
        public override string ToString() => DisplayText;
    }

    private sealed record ResourceTreeNodeTag(RhoArchiveContext? Context, string RelativePath);
}

internal sealed class RhoToolStripColorTable : ProfessionalColorTable
{
    private static Color Surface => Color.White;
    private static Color Border => Color.FromArgb(220, 226, 235);
    private static Color Selected => Color.FromArgb(224, 237, 251);

    public override Color ToolStripBorder => Border;
    public override Color ToolStripGradientBegin => Surface;
    public override Color ToolStripGradientMiddle => Surface;
    public override Color ToolStripGradientEnd => Surface;
    public override Color ButtonSelectedBorder => Border;
    public override Color ButtonSelectedGradientBegin => Selected;
    public override Color ButtonSelectedGradientMiddle => Selected;
    public override Color ButtonSelectedGradientEnd => Selected;
    public override Color ButtonPressedBorder => Border;
    public override Color ButtonPressedGradientBegin => Selected;
    public override Color ButtonPressedGradientMiddle => Selected;
    public override Color ButtonPressedGradientEnd => Selected;
    public override Color MenuItemSelected => Selected;
    public override Color MenuItemBorder => Border;
}
