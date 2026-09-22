using Xt1.App.Controls;
using Xt1.App.Rendering;
using Xt1.App.Services;
using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Interop;
using Xt1.Core.Nvtt;
using Xt1.Core.Settings;
using Xt1.Core.Util;
using Xt1.Core.Xt1;

namespace Xt1.App.Forms;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private ResolvedTools _tools;
    private NvttNative? _nvttNative;
    private NvttEncoder? _nvttEncoder;
    private CliEncoder? _cliEncoder;
    private readonly DecodeCache _cache = new();
    private readonly EditHistory _history = new();
    private EncoderPicker _encoders = null!;
    private BatchRecompressor _recompressor = null!;
    private CancellationTokenSource? _volumeCts;
    private CancellationTokenSource? _exportCts;
    private bool _syncingSlices;

    /// <summary>A volume decode is in flight, so slice requests must not start their own.</summary>
    private bool _volumePending;

    /// <summary>
    /// Slices of the volume the GL tab decoded, reused for the 2D tab and the validation verdict.
    /// Decoding one slice of an interleaved volume untiles the whole volume, so repeating it per
    /// slice (or per trackbar step) would repeat all of that work.
    /// </summary>
    private EditableTexture? _volumeSource;
    private int _volumeSourceMip = -1;
    private IReadOnlyList<RgbaImage>? _volumeSourceSlices;

    /// <summary>Upper bound on slices assembled for one preview, before the texel budget is applied.</summary>
    private const int MaxPreviewSlices = 512;

    /// <summary>Extensions the loader accepts, i.e. the file-browser roots shown in the tree.</summary>
    private static readonly string[] OpenableExtensions = { ".dat", ".wta", ".wtb", ".xt1" };

    private BankDocument? _document;
    private BankSection? _section;
    private EditableTexture? _texture;

    /// <summary>Preview/edit wrapper for a raw `.xt1` document (which has no WTB bank).</summary>
    private EditableTexture? _rawTexture;

    private bool _suppressTreeEvents;

    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };

    /// <summary>The file browser: openable files in the active document's folder, in columns.</summary>
    private readonly ListView _fileList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        ShowItemToolTips = true,
        Sorting = SortOrder.None,
    };

    private readonly SplitContainer _browserSplit = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        Panel1MinSize = 60,
        Panel2MinSize = 120,
    };

    private string _fileSortColumn = "name";
    private bool _fileSortAscending = true;
    private bool _suppressFileSelection;
    private Font? _activeFileFont;
    private Font ActiveFileFont => _activeFileFont ??= new Font(_fileList.Font, FontStyle.Bold);
    private readonly Image2DViewer _imageViewer = new() { Dock = DockStyle.Fill };
    private readonly GlPreviewControl _glPreview = new() { Dock = DockStyle.Fill };
    private readonly TabControl _viewTabs = new() { Dock = DockStyle.Fill };
    private readonly TextureInspectorPanel _inspector = new() { Dock = DockStyle.Fill };
    private readonly HexEditorControl _hexPanel = new() { Dock = DockStyle.Fill };
    private Form? _hexDialog;
    private readonly ContextMenuStrip _treeMenu = new();
    private ToolStripMenuItem _menuReplace = null!;
    private ToolStripMenuItem _menuReplaceXt1 = null!;
    private ToolStripMenuItem _menuExportGroup = null!;
    private ToolStripMenuItem _menuExportPng = null!;
    private ToolStripMenuItem _menuExportDds = null!;
    private ToolStripMenuItem _menuExportXt1 = null!;
    private ToolStripMenuItem _menuAddTexture = null!;
    private ToolStripMenuItem _menuDeleteTexture = null!;
    private ToolStripMenuItem _menuCopyHash = null!;
    private ToolStripMenuItem _menuExportBank = null!;
    private readonly ToolStripStatusLabel _statusLabel = new("Ready");
    private readonly ToolStripStatusLabel _backendLabel = new(string.Empty);
    private readonly ToolStripComboBox _backendCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly ToolStripComboBox _mipCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ToolStripComboBox _channelCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly ToolStripButton _undoButton = new("Undo");
    private readonly ToolStripButton _redoButton = new("Redo");
    private readonly ComboBox _sliceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Label _sliceLabel = new() { Text = "Slice", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
    private readonly TrackBar _sliceTrack = new() { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, Width = 120, Visible = false };
    private readonly CheckBox _showSlices = new() { Text = "Slice planes", AutoSize = true, Checked = false, Visible = false };
    private readonly Label _volumeLabel = new() { Text = "Render", AutoSize = true, Visible = false };
    private readonly ComboBox _volumeMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Visible = false };
    private readonly Label _gammaLabel = new() { Text = "Gamma", AutoSize = true };
    private readonly NumericUpDown _gammaBox = new()
    {
        DecimalPlaces = 2, Minimum = 0.10m, Maximum = 8.00m, Increment = 0.05m, Value = 1.00m, Width = 60,
    };
    private readonly Label _exposureLabel = new() { Text = "Exposure", AutoSize = true };
    private readonly NumericUpDown _exposureBox = new()
    {
        DecimalPlaces = 2, Minimum = 0.05m, Maximum = 20.00m, Increment = 0.05m, Value = 1.00m, Width = 60,
    };
    private ToolStripButton? _opaqueButton;
    private bool _syncingVolumeMode;

    /// <summary>Index into <see cref="_volumeMode"/>. MIP ignores alpha, so it has no opaque counterpart.</summary>
    private const int VolumeModeRgb = 0;
    private const int VolumeModeAlpha = 1;
    private const int VolumeModeMip = 2;

    /// <summary>Total volume texels a preview will assemble; beyond this the extra slices are dropped.</summary>
    private const long MaxPreviewVolumeTexels = 64L * 1024 * 1024;
    private readonly string? _initialFile;
    private SplitContainer _outerSplit = null!;
    private SplitContainer _innerSplit = null!;

    public MainForm(string? initialFile = null)
    {
        _settings = AppSettings.Load();
        _tools = _settings.Resolve();
        _initialFile = initialFile;

        Text = "XT1 Studio";
        Icon = AppIcon.Shared;
        ClientSize = new Size(1400, 900);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                LoadFile(files[0]);
        };

        BuildMenu();
        BuildLayout();
        BuildToolbar();

        _history.Changed += () => UpdateHistoryButtons();
        _backendCombo.SelectedIndexChanged += (_, _) => OnBackendChanged();
        _mipCombo.SelectedIndexChanged += (_, _) => UpdatePreview();
        _sliceCombo.SelectedIndexChanged += (_, _) => SyncSliceFromCombo();
        _channelCombo.SelectedIndexChanged += (_, _) =>
        {
            var channel = (ImageChannelMode)Math.Max(0, _channelCombo.SelectedIndex);
            _imageViewer.Channel = channel;
            _glPreview.Channel = channel;
            _glPreview.Invalidate();
        };
        _gammaBox.ValueChanged += (_, _) =>
        {
            _glPreview.Gamma = (float)_gammaBox.Value;
            _glPreview.Invalidate();
        };
        _exposureBox.ValueChanged += (_, _) =>
        {
            _glPreview.Exposure = (float)_exposureBox.Value;
            _glPreview.Invalidate();
        };
        _sliceTrack.ValueChanged += (_, _) => SyncSliceFromTrack();
        _showSlices.CheckedChanged += (_, _) => { _glPreview.ShowSlices = _showSlices.Checked; _glPreview.Invalidate(); };
        _volumeMode.SelectedIndexChanged += (_, _) => { ApplyVolumeMode(); SyncOpaqueButtonFromVolumeMode(); };

        _inspector.CommandProduced += command => { _history.Push(command); UpdateInfo(); };
        _inspector.RequestHex += (bytes, caption) => ShowHexDialog(bytes, caption);
        _hexPanel.BytesApplied += (after, before) =>
        {
            var target = _hexPanel.Buffer;
            if (target is null) return;
            _history.Push(new DelegateCommand("Raw byte edit",
                () => after.CopyTo(target, 0),
                () => before.CopyTo(target, 0)));
            UpdateInfo();
        };

        InitializeNativeTools();
    }

    // --- construction -------------------------------------------------------------------

    private void BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Open...", null, (_, _) => OpenFile());
        file.DropDownItems.Add("&New empty bank...", null, (_, _) => NewBank());
        file.DropDownItems.Add("&Reload", null, (_, _) => { if (_document?.FilePath is { } p) LoadFile(p); });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("&Save", null, (_, _) => SaveDocument(false));
        file.DropDownItems.Add("Save &As...", null, (_, _) => SaveDocument(true));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        // Per-texture editing/exporting lives in the tree's right-click menu, so this only keeps the
        // document-wide actions.
        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add("&Undo", null, (_, _) => { _history.Undo(); RefreshAll(); });
        edit.DropDownItems.Add("&Redo", null, (_, _) => { _history.Redo(); RefreshAll(); });
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add("&Export every texture in this container (PNG folder)...", null,
            (_, _) => ExportContainerTextures());

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Settings...", null, (_, _) => ShowSettings());
        tools.DropDownItems.Add("&Batch recompress (ASTC to BC)...", null, (_, _) => ShowBatchRecompress());

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&About", null, (_, _) => MessageBox.Show(this,
            "XT1 Studio\nViewer/editor for PlatinumGames WTB/XT1 texture banks.\n\nFormat reference: XT1_FORMAT.md",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(tools);
        menu.Items.Add(help);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    /// <summary>
    /// Right-click menu for the container tree. The per-texture and per-bank actions live here rather
    /// than in the menu bar: the tree is where a texture is selected, so it is where acting on one is
    /// discoverable. Items are enabled per node kind when the menu opens.
    /// </summary>
    private void BuildTreeMenu()
    {
        _menuReplace = NewTreeMenuItem("Replace...", (_, _) => ImportTexture());
        _menuReplaceXt1 = NewTreeMenuItem("Replace with .xt1...", (_, _) => ReplaceRawPayload());

        _menuExportPng = NewTreeMenuItem("Export .png...", (_, _) => ExportPng());
        _menuExportDds = NewTreeMenuItem("Export .dds...", (_, _) => ExportDds());
        _menuExportXt1 = NewTreeMenuItem("Export .xt1...", (_, _) => ExportRawXt1());
        _menuExportGroup = new ToolStripMenuItem("Export");
        _menuExportGroup.DropDownItems.Add(_menuExportPng);
        _menuExportGroup.DropDownItems.Add(_menuExportDds);
        _menuExportGroup.DropDownItems.Add(_menuExportXt1);

        _menuAddTexture = NewTreeMenuItem("Add texture from image...", (_, _) => AddTexture());
        _menuDeleteTexture = NewTreeMenuItem("Delete texture", (_, _) => DeleteTexture());
        _menuCopyHash = NewTreeMenuItem("Copy hash", (_, _) => CopySelectedHash());
        _menuExportBank = NewTreeMenuItem("Export every texture in this bank (PNG folder)...",
            (_, _) => ExportBankTextures());

        _treeMenu.Items.Add(_menuReplace);
        _treeMenu.Items.Add(_menuReplaceXt1);
        _treeMenu.Items.Add(new ToolStripSeparator());
        _treeMenu.Items.Add(_menuExportGroup);
        _treeMenu.Items.Add(new ToolStripSeparator());
        _treeMenu.Items.Add(_menuAddTexture);
        _treeMenu.Items.Add(_menuDeleteTexture);
        _treeMenu.Items.Add(new ToolStripSeparator());
        _treeMenu.Items.Add(_menuCopyHash);
        _treeMenu.Items.Add(new ToolStripSeparator());
        _treeMenu.Items.Add(_menuExportBank);

        _treeMenu.Opening += (_, _) => UpdateTreeMenuState();

        // Right-clicking should act on the node under the cursor, not on whatever was selected before.
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            _tree.SelectedNode = e.Node;
        };
        _tree.ContextMenuStrip = _treeMenu;
    }

    private static ToolStripMenuItem NewTreeMenuItem(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += onClick;
        return item;
    }

    /// <summary>Tag of the currently selected node, if it is a texture.</summary>
    private EditableTexture? SelectedTexture => _tree.SelectedNode?.Tag as EditableTexture ?? _texture;

    private void UpdateTreeMenuState()
    {
        var selected = SelectedTexture;
        bool hasTexture = selected is not null;
        var info = selected?.FormatInfo;
        bool canDecode = TextureCodec.CanDecode(info);
        bool canExportDds = info is not null && DdsIo.CanExport(info);
        bool inBank = _section is not null;
        bool bankHasTextures = inBank && _section!.Bank.Textures.Count > 0;

        _menuReplace.Enabled = hasTexture;
        _menuReplaceXt1.Enabled = hasTexture;
        _menuExportGroup.Enabled = hasTexture;
        _menuExportPng.Enabled = hasTexture && canDecode;
        _menuExportDds.Enabled = hasTexture && canExportDds;
        _menuExportXt1.Enabled = hasTexture;
        // Adding only needs a bank, not an existing texture, so an empty bank can be filled.
        _menuAddTexture.Enabled = inBank;
        // Deleting removes the selected texture from its bank, so it needs both.
        _menuDeleteTexture.Enabled = hasTexture && inBank;
        _menuCopyHash.Enabled = hasTexture;
        _menuExportBank.Enabled = bankHasTextures;
    }

    private void BuildToolbar()
    {
        var strip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };


        _backendCombo.Items.AddRange(new object[] { EncoderBackend.Nvtt, EncoderBackend.Managed, EncoderBackend.Cli });
        _backendCombo.SelectedItem = _settings.PreferredBackend;
        strip.Items.Add(new ToolStripLabel("Backend"));
        strip.Items.Add(_backendCombo);
        strip.Items.Add(new ToolStripSeparator());

        strip.Items.Add(new ToolStripLabel("Mip"));
        strip.Items.Add(_mipCombo);
        strip.Items.Add(new ToolStripSeparator());

        _channelCombo.Items.AddRange(new object[] { "RGBA", "R", "G", "B" });
        if (_settings.ShowAlphaChannel) _channelCombo.Items.Add("A");
        _channelCombo.SelectedIndex = 0;
        strip.Items.Add(new ToolStripLabel("Channel"));
        strip.Items.Add(_channelCombo);
        strip.Items.Add(new ToolStripSeparator());

        var gamma = new ToolStripButton("Gamma Correct") { CheckOnClick = true, Checked = _settings.ApplySrgbPreview };
        gamma.CheckedChanged += (_, _) => _imageViewer.ApplyGamma = gamma.Checked;
        strip.Items.Add(gamma);
        _imageViewer.ApplyGamma = gamma.Checked;

        var opaque = new ToolStripButton("Opaque Preview")
        {
            CheckOnClick = true,
            Checked = _settings.OpaqueAlphaByDefault,
            ToolTipText = "Ignore the alpha channel: 2D shows the RGB as-is, 3D derives the volume opacity from the RGB",
        };
        opaque.CheckedChanged += (_, _) =>
        {
            _imageViewer.OpaqueAlpha = opaque.Checked;
            _glPreview.OpaqueAlpha = opaque.Checked;
            _glPreview.Invalidate();
            SyncVolumeModeFromOpaqueButton();
        };
        strip.Items.Add(opaque);
        _opaqueButton = opaque;
        _imageViewer.OpaqueAlpha = opaque.Checked;
        _glPreview.OpaqueAlpha = opaque.Checked;

        // The 3D render mode follows the same opaque setting; MIP ignores alpha entirely.
        _volumeMode.SelectedIndex = opaque.Checked ? VolumeModeRgb : VolumeModeAlpha;
        ApplyVolumeMode();

        strip.Items.Add(new ToolStripButton("Fit to view", null, (_, _) => _imageViewer.FitToWindow()));
        strip.Items.Add(new ToolStripSeparator());

        strip.Items.Add(_undoButton);
        strip.Items.Add(_redoButton);
        _undoButton.Click += (_, _) => { _history.Undo(); RefreshAll(); };
        _redoButton.Click += (_, _) => { _history.Redo(); RefreshAll(); };

        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(new ToolStripButton("GL reset", null, (_, _) => _glPreview.ResetCamera()));

        Controls.Add(strip);
    }

    /// <summary>Slice/render controls live under the GL viewport (they are plain WinForms controls).</summary>
    private Panel BuildSliceBar()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 32 };

        _sliceLabel.Location = new Point(8, 8);
        _sliceCombo.Location = new Point(48, 4);
        _sliceCombo.Width = 100;
        _sliceTrack.Location = new Point(154, 2);
        _sliceTrack.Value = 50;
        _volumeLabel.Location = new Point(284, 7);
        _volumeMode.Location = new Point(336, 4);
        _showSlices.Location = new Point(414, 7);

        // Gamma/exposure are pinned to the right edge in their own flow panel so a narrower viewport
        // can never push the GL surface over them.
        var adjustments = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
        };
        _gammaLabel.Margin = new Padding(0, 6, 4, 0);
        _gammaBox.Margin = new Padding(0, 3, 14, 0);
        _exposureLabel.Margin = new Padding(0, 6, 4, 0);
        _exposureBox.Margin = new Padding(0, 3, 0, 0);
        adjustments.Controls.Add(_gammaLabel);
        adjustments.Controls.Add(_gammaBox);
        adjustments.Controls.Add(_exposureLabel);
        adjustments.Controls.Add(_exposureBox);

        panel.Controls.Add(_sliceLabel);
        panel.Controls.Add(_sliceCombo);
        panel.Controls.Add(_sliceTrack);
        panel.Controls.Add(_volumeLabel);
        panel.Controls.Add(_volumeMode);
        panel.Controls.Add(_showSlices);
        panel.Controls.Add(adjustments);
        adjustments.BringToFront();

        void AnchorAdjustments()
        {
            adjustments.Location = new Point(Math.Max(0, panel.ClientSize.Width - adjustments.Width - 8), 4);
        }
        panel.Resize += (_, _) => AnchorAdjustments();
        AnchorAdjustments();

        _glPreview.ShowSlices = _showSlices.Checked;
        _glPreview.Gamma = (float)_gammaBox.Value;
        _glPreview.Exposure = (float)_exposureBox.Value;
        return panel;
    }

    private void BuildLayout()
    {
        var outer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
        };
        _outerSplit = outer;

        _tree.NodeMouseDoubleClick += (_, e) => e.Node.Expand();
        _tree.AfterSelect += (_, _) => OnTreeSelectionChanged();
        BuildTreeMenu();

        // The left pane is a file browser (sortable, resizeable columns) over the openable files in
        // the folder, with the selected file's bank/texture tree underneath it.
        _fileList.Columns.Add("Name", 240, HorizontalAlignment.Left);
        _fileList.Columns.Add("Modified", 140, HorizontalAlignment.Left);
        _fileList.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _fileList.ColumnClick += OnFileColumnClick;
        _fileList.SelectedIndexChanged += (_, _) => OnFileSelectionChanged();
        EnableDoubleBuffering(_fileList);
        EnableDoubleBuffering(_tree);

        _browserSplit.Panel1.Controls.Add(_fileList);
        _browserSplit.Panel2.Controls.Add(_tree);
        outer.Panel1.Controls.Add(_browserSplit);

        var inner = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        _innerSplit = inner;

        var imageTab = new TabPage("2D");
        imageTab.Controls.Add(_imageViewer);
        var glTab = new TabPage("Cube / 3D");
        glTab.Controls.Add(_glPreview);
        glTab.Controls.Add(BuildSliceBar());
        _volumeMode.Items.AddRange(new object[] { "RGB", "Alpha", "MIP" });
        _viewTabs.TabPages.Add(imageTab);
        _viewTabs.TabPages.Add(glTab);
        inner.Panel1.Controls.Add(_viewTabs);

        inner.Panel2.Controls.Add(_inspector);

        outer.Panel2.Controls.Add(inner);

        var status = new StatusStrip();
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        status.Items.Add(_statusLabel);
        status.Items.Add(_backendLabel);

        Controls.Add(outer);
        Controls.Add(status);
        outer.BringToFront();
        inner.BringToFront();
    }

    private void InitializeNativeTools()
    {
        _tools = _settings.Resolve();

        if (_tools.NvttDll is not null)
        {
            _nvttNative = NvttNative.TryCreate(out _, _tools.NvttDll);
            if (_nvttNative is not null) _nvttEncoder = new NvttEncoder(_nvttNative);
        }
        if (_tools.NvttCli is not null)
            _cliEncoder = CliEncoder.TryCreate(out _, _tools.NvttCli);

        _encoders = new EncoderPicker(_nvttEncoder, _cliEncoder);
        _recompressor = new BatchRecompressor(_encoders);
        UpdateBackendLabel();
    }

    private void UpdateBackendLabel()
    {
        var parts = new List<string> { $"NVTT.dll: {(_nvttEncoder is null ? "no" : "yes")}" };
        parts.Add($"CLI: {(_cliEncoder is null ? "no" : "yes")}");
        _backendLabel.Text = string.Join("  |  ", parts);
    }

    // --- document lifecycle -------------------------------------------------------------

    private void OpenFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Texture banks (*.wta;*.dat;*.dtt;*.wtb;*.xt1)|*.wta;*.dat;*.dtt;*.wtb;*.xt1|All files (*.*)|*.*",
            InitialDirectory = _settings.LastDirectory ?? TestDefaultDirectory(),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        LoadFile(dialog.FileName);
    }

    private static string TestDefaultDirectory()
    {
        const string romfs = @"D:\Bayonetta3\romfs";
        return Directory.Exists(romfs) ? romfs : Environment.CurrentDirectory;
    }

    private bool LoadFile(string path)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            AdoptDocument(BankDocument.Load(path));
            _settings.LastDirectory = Path.GetDirectoryName(path);
            _settings.Save();

            SetStatus(_document!.Warnings.Count == 0
                ? $"Loaded {_document.DisplayName}"
                : $"Loaded {_document.DisplayName} - {string.Join("; ", _document.Warnings)}");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to open", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>
    /// Openable files sitting in the active document's folder (the file-browser roots). The active
    /// file is always included even when its extension is unexpected, and parsing is deferred until
    /// the user picks one, so a folder of large banks does not all load at once.
    /// </summary>
    private IReadOnlyList<string> SiblingFiles()
    {
        var active = _document?.FilePath;
        if (string.IsNullOrEmpty(active)) return Array.Empty<string>();

        var folder = Path.GetDirectoryName(active);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return new[] { active };

        var files = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var extension = Path.GetExtension(path);
                if (OpenableExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
                    files.Add(path);
            }
        }
        catch
        {
            return new[] { active };
        }

        if (!files.Any(p => SamePath(p, active))) files.Add(active);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fill or update the file browser. Switching between files in the same folder leaves the row set
    /// identical, so that path only moves the selection/bold marker instead of clearing and re-adding
    /// every row (which flashed and re-entered the selection handler).
    /// </summary>
    private void RefreshFileList()
    {
        var active = _document?.FilePath;
        var entries = SiblingFiles().Select(FileEntry.Read).ToList();
        entries.Sort(CompareFileEntries);
        var paths = entries.Select(e => e.Path).ToList();
        var current = _fileList.Items.Cast<ListViewItem>()
            .Select(item => item.Tag as string ?? string.Empty).ToList();

        if (current.Count == paths.Count && current.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
        {
            UpdateFileActiveRow(active);
            UpdateFileColumnHeaders();
            return;
        }

        _suppressFileSelection = true;
        _fileList.BeginUpdate();
        try
        {
            _fileList.Items.Clear();
            foreach (var entry in entries)
            {
                var item = new ListViewItem(entry.Name) { Tag = entry.Path, ToolTipText = entry.Path };
                item.SubItems.Add(entry.ModifiedDisplay);
                item.SubItems.Add(entry.SizeDisplay);
                _fileList.Items.Add(item);
            }
        }
        finally
        {
            _fileList.EndUpdate();
            _suppressFileSelection = false;
        }

        UpdateFileActiveRow(active);
        UpdateFileColumnHeaders();
    }

    /// <summary>Bold and select the active file's row without touching the other rows.</summary>
    private void UpdateFileActiveRow(string? active)
    {
        _suppressFileSelection = true;
        try
        {
            foreach (ListViewItem item in _fileList.Items)
            {
                bool isActive = active is not null && item.Tag is string path && SamePath(path, active);
                item.Font = isActive ? ActiveFileFont : _fileList.Font;
                if (!isActive) continue;
                item.Selected = true;
                item.EnsureVisible();
            }
        }
        finally
        {
            _suppressFileSelection = false;
        }
    }

    private int CompareFileEntries(FileEntry a, FileEntry b)
    {
        int result = _fileSortColumn switch
        {
            "modified" => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
            "size" => a.Size.CompareTo(b.Size),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        if (result == 0) result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return _fileSortAscending ? result : -result;
    }

    private void OnFileSelectionChanged()
    {
        if (_suppressFileSelection || _fileList.SelectedItems.Count == 0) return;
        if (_fileList.SelectedItems[0].Tag is not string path) return;
        if (_document?.FilePath is not null && SamePath(path, _document.FilePath)) return;

        // Deferred so the load/rebuild never runs inside the ListView's own selection change.
        void Open()
        {
            if (!LoadFile(path)) RefreshFileList(); // a failed load keeps the previous selection
        }
        if (IsHandleCreated) BeginInvoke(Open);
        else Open();
    }

    private void OnFileColumnClick(object? sender, ColumnClickEventArgs e)
    {
        var column = e.Column switch { 1 => "modified", 2 => "size", _ => "name" };
        if (string.Equals(_fileSortColumn, column, StringComparison.Ordinal))
        {
            _fileSortAscending = !_fileSortAscending;
        }
        else
        {
            _fileSortColumn = column;
            _fileSortAscending = true;
        }
        RefreshFileList();
    }

    private void UpdateFileColumnHeaders()
    {
        var indicator = _fileSortAscending ? " ^" : " v";
        _fileList.Columns[0].Text = _fileSortColumn == "name" ? "Name" + indicator : "Name";
        _fileList.Columns[1].Text = _fileSortColumn == "modified" ? "Modified" + indicator : "Modified";
        _fileList.Columns[2].Text = _fileSortColumn == "size" ? "Size" + indicator : "Size";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes / 1024.0;
        if (value < 1024) return $"{value:0.#} KB";
        value /= 1024.0;
        if (value < 1024) return $"{value:0.#} MB";
        return $"{value / 1024.0:0.##} GB";
    }

    /// <summary>
    /// WinForms TreeView/ListView flicker on Clear() + rebuild; enabling their protected double
    /// buffer is the standard fix and not reachable through the public API.
    /// </summary>
    private static void EnableDoubleBuffering(Control control)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance |
                                            System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(control, true, null);
    }

    /// <summary>One row of the file browser.</summary>
    private readonly record struct FileEntry(string Path, string Name, DateTime ModifiedUtc, long Size)
    {
        public static FileEntry Read(string path)
        {
            var name = System.IO.Path.GetFileName(path);
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) return new FileEntry(path, name, info.LastWriteTimeUtc, info.Length);
            }
            catch
            {
                // Unreadable metadata: fall through to an "unknown" row so the file is still listed.
            }
            return new FileEntry(path, name, DateTime.MinValue, -1);
        }

        public string ModifiedDisplay => ModifiedUtc == DateTime.MinValue
            ? "--"
            : ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public string SizeDisplay => Size < 0 ? "--" : MainForm.FormatSize(Size);
    }

    /// <summary>Make a freshly loaded or created document the active one and rebuild the UI around it.</summary>
    private void AdoptDocument(BankDocument document)
    {
        _document = document;
        _history.Clear();
        _cache.Clear();
        _rawTexture = _document.RawXt1 is { } raw ? EditableTexture.FromRaw(raw) : null;
        _section = _document.Sections.FirstOrDefault();
        _texture = _section?.Bank.Textures.FirstOrDefault() ?? _rawTexture;
        RebuildTree();
        SelectTexture(_texture);
    }

    /// <summary>Author a new empty `.wta`+`.wtp` or standalone `.wtb` bank, then edit it normally.</summary>
    private void NewBank()
    {
        using var dialog = new NewBankDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        using var save = new SaveFileDialog
        {
            Title = "Choose the new bank file",
            Filter = dialog.Standalone
                ? "Standalone WTB (*.wtb)|*.wtb"
                : "Split bank (*.wta)|*.wta",
            FileName = dialog.Standalone ? "new.wtb" : "new.wta",
            InitialDirectory = _settings.LastDirectory ?? Environment.CurrentDirectory,
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var kind = dialog.Standalone ? BankKind.StandaloneWtb : BankKind.EffectWta;
            AdoptDocument(BankDocument.CreateEmpty(kind, save.FileName));
            _settings.LastDirectory = Path.GetDirectoryName(save.FileName);
            _settings.Save();
            SetStatus($"New empty {(dialog.Standalone ? "standalone WTB" : "split WTA")} bank - right-click the " +
                      "bank in the tree and choose \"Add texture from image...\", then Save.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not create the bank", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Renders <see cref="BankOutline"/> into the tree (the grouping rules live in Core).</summary>
    private void RebuildTree()
    {
        _suppressTreeEvents = true;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        if (_document is not null)
        {
            var outline = BankOutline.Build(_document, _rawTexture);
            var root = new TreeNode(outline.Label) { Tag = _document };
            AddOutlineChildren(root, outline);
            _tree.Nodes.Add(root);
            root.ExpandAll();
        }

        _tree.EndUpdate();
        _suppressTreeEvents = false;

        RefreshFileList();
    }

    private void AddOutlineChildren(TreeNode parent, BankOutlineNode node)
    {
        foreach (var child in node.Children)
        {
            var treeNode = new TreeNode(child.Label)
            {
                Tag = child.Kind switch
                {
                    OutlineKind.Texture when child.Texture is not null => (object)child.Texture,
                    OutlineKind.Texture when child.Section is not null => child.Section,
                    OutlineKind.SubFile when child.Section is not null => child.Section,
                    _ => _document!,
                },
            };
            parent.Nodes.Add(treeNode);
            AddOutlineChildren(treeNode, child);
        }
    }

    private void OnTreeSelectionChanged()
    {
        if (_suppressTreeEvents) return;
        switch (_tree.SelectedNode?.Tag)
        {
            case EditableTexture texture:
                _section = _document?.Sections.FirstOrDefault(s => s.Bank.Textures.Contains(texture));
                SelectTexture(texture);
                break;
            case BankSection section:
                _section = section;
                SelectTexture(section.Bank.Textures.FirstOrDefault());
                break;
            case BankDocument document:
                _section = document.Sections.FirstOrDefault();
                SelectTexture(_section?.Bank.Textures.FirstOrDefault() ?? _rawTexture);
                break;
        }
    }

    private void SelectTexture(EditableTexture? texture)
    {
        _texture = texture;
        _inspector.Bind(_document, _section, texture);
        _cache.Clear();

        RebuildMipCombo();
        UpdateInfo();
        UpdatePreview();
        UpdateInspectorValidation();
        UpdateSliceControls();
        UpdateHistoryButtons();
    }

    /// <summary>
    /// Tell the inspector whether this texture is trustworthy: the layout/size warnings plus whether
    /// it actually decodes. Decoding reuses the preview cache, so it is normally free; a 3D volume
    /// defers to the asynchronous volume decode instead of untiling the whole volume here.
    /// </summary>
    private void UpdateInspectorValidation()
    {
        if (_texture is null)
        {
            _inspector.SetValidation(Array.Empty<string>(), null);
            return;
        }

        var warnings = _texture.Validate();
        string? decodeError = null;

        if (_texture.Header.Type.Is3D())
        {
            // Decoding a single slice of an interleaved volume untiles the whole volume, and the GL tab
            // is already doing that off the UI thread. Report the layout warnings now and let the volume
            // decode supply the verdict (ApplyVolumePreview) instead of blocking the selection.
            _inspector.SetValidation(warnings, null, pending: true);
            return;
        }

        var info = _texture.FormatInfo;
        if (!TextureCodec.CanDecode(info))
        {
            decodeError = $"no decoder for {info?.Name ?? "this format"}";
        }
        else
        {
            int mip = Math.Max(0, _mipCombo.SelectedIndex);
            var image = DecodeSubresource(mip, 0, 0, out var error);
            if (image is null) decodeError = error ?? "the decoder returned no image";
        }

        _inspector.SetValidation(warnings, decodeError);
    }

    /// <summary>Raw `.xt1` edits live on the preview wrapper; push them back before serializing.</summary>
    private void SyncRawDocument()
    {
        if (_document?.RawXt1 is { } raw && _rawTexture is not null)
            raw.Payload = _rawTexture.Payload;
    }

    private void RebuildMipCombo()
    {
        _mipCombo.Items.Clear();
        int mips = _texture?.Header.MipCount ?? 1;
        for (int m = 0; m < Math.Max(1, mips); m++)
        {
            int w = Math.Max(1, (int)(_texture?.Header.Width ?? 1) >> m);
            int h = Math.Max(1, (int)(_texture?.Header.Height ?? 1) >> m);
            _mipCombo.Items.Add($"mip {m} ({w}x{h})");
        }
        if (_mipCombo.Items.Count > 0) _mipCombo.SelectedIndex = 0;
    }

    private void UpdateSliceControls()
    {
        bool is3D = _texture?.Header.Type.Is3D() == true;
        _sliceLabel.Visible = is3D;
        _sliceCombo.Visible = is3D;
        _sliceTrack.Visible = is3D;
        _showSlices.Visible = is3D;
        _volumeLabel.Visible = is3D;
        _volumeMode.Visible = is3D;
        _sliceCombo.Items.Clear();
        if (!is3D) return;

        var layout = _texture!.Layout;
        int mip = Math.Max(0, _mipCombo.SelectedIndex);
        int slices = layout.Applicable && mip < layout.Mips.Count ? layout.Mips[mip].SliceCount : 1;
        for (int s = 0; s < slices; s++) _sliceCombo.Items.Add($"slice {s}");
        // Start in the middle so the cross-section plane and the 2D slice are both meaningful.
        if (_sliceCombo.Items.Count > 0) _sliceCombo.SelectedIndex = _sliceCombo.Items.Count / 2;
    }

    /// <summary>Push the render-mode combo into the viewport.</summary>
    private void ApplyVolumeMode()
    {
        _glPreview.RenderMode = _volumeMode.SelectedIndex switch
        {
            VolumeModeAlpha => VolumeRenderMode.Alpha,
            VolumeModeMip => VolumeRenderMode.Mip,
            _ => VolumeRenderMode.Rgb,
        };
        _glPreview.Invalidate();
    }

    /// <summary>
    /// The global "Opaque a" toggle drives the 3D mode too: ignoring alpha in a volume means deriving
    /// its opacity from the RGB instead. MIP is left alone because it never looks at alpha.
    /// </summary>
    private void SyncVolumeModeFromOpaqueButton()
    {
        if (_syncingVolumeMode || _opaqueButton is null) return;
        if (_volumeMode.SelectedIndex == VolumeModeMip) return;

        _syncingVolumeMode = true;
        try
        {
            _volumeMode.SelectedIndex = _opaqueButton.Checked ? VolumeModeRgb : VolumeModeAlpha;
        }
        finally
        {
            _syncingVolumeMode = false;
        }
    }

    /// <summary>Keep the toolbar toggle in step when the render mode is picked from the combo.</summary>
    private void SyncOpaqueButtonFromVolumeMode()
    {
        if (_syncingVolumeMode || _opaqueButton is null) return;
        if (_volumeMode.SelectedIndex == VolumeModeMip) return;

        _syncingVolumeMode = true;
        try
        {
            _opaqueButton.Checked = _volumeMode.SelectedIndex == VolumeModeRgb;
        }
        finally
        {
            _syncingVolumeMode = false;
        }
    }

    private void UpdateHistoryButtons()
    {
        _undoButton.Enabled = _history.CanUndo;
        _redoButton.Enabled = _history.CanRedo;
        _undoButton.ToolTipText = _history.UndoName ?? "Nothing to undo";
        _redoButton.ToolTipText = _history.RedoName ?? "Nothing to redo";
    }

    private void RefreshAll()
    {
        // Any of the callers may have changed payload bytes in place (import, undo, redo, a field
        // edit), and the cache key cannot see content, so drop the decoded images rather than risk
        // showing a preview of the previous bytes.
        _cache.Clear();
        UpdateInfo();
        _inspector.RefreshValues();
        UpdatePreview();
        UpdateInspectorValidation();
    }

    // --- info ---------------------------------------------------------------------------

    private void UpdateInfo()
    {
        var builder = new System.Text.StringBuilder();
        if (_texture is null)
        {
            builder.AppendLine("No texture selected.");
            _inspector.SetTechnicalSummary(builder.ToString());
            return;
        }

        var h = _texture.Header;
        var info = _texture.FormatInfo;
        var layout = _texture.Layout;

        builder.AppendLine($"Texture #{_texture.Index}  {info?.Name ?? "unknown format"}");
        builder.AppendLine($"  {h.Width} x {h.Height} x {h.Depth}   type {h.Type} ({h.Type.Describe()})   mips {h.MipCount}");
        builder.AppendLine($"  imageSize   {h.ImageSize} ({Mem.Hex(h.ImageSize)})");
        builder.AppendLine($"  format      0x{h.FormatValue:X2}   codecTag 0x{h.CodecTag:X4}   textureFlag 0x{h.TextureFlag:X}");
        builder.AppendLine($"  layout      {Mem.Hex(h.Layout)}  blockHeightLog2 {h.BlockHeightLog2}   layout2 {Mem.Hex(h.Layout2)}");
        builder.AppendLine();
        builder.AppendLine("table entry");
        builder.AppendLine($"  table0 offset  {_texture.PayloadOffset} (0x{_texture.PayloadOffset:X})");
        builder.AppendLine($"  table1 size    {_texture.PayloadSize}");
        builder.AppendLine($"  payload bytes  {_texture.Payload.Length}");
        builder.AppendLine($"  table2         {Mem.HexBytes(_texture.Table2Bytes)}  (BE 0x{_texture.Table2WordBE:X8})" +
                           $"  class={(_texture.IsModelBankClass ? "model" : "effect")}" +
                           (info is { IsSrgb: true } || _texture.IsSrgbFlag ? " sRGB" : string.Empty) +
                           (_texture.IsCompressedFlag ? " af-format (b3 0x08, uncompressed)" : string.Empty) +
                           (_texture.IsCubeUsageFlag ? " cube-usage" : string.Empty));
        builder.AppendLine($"  table3 hash    0x{_texture.Hash:X8}  mode {_texture.HashMode}" +
                           (_texture.Name is null ? string.Empty : $"  name \"{_texture.Name}\""));
        builder.AppendLine();
        builder.AppendLine("layout");
        if (!layout.Applicable)
        {
            builder.AppendLine($"  not computed: {layout.Note}");
        }
        else
        {
            builder.AppendLine($"  computed imageSize {layout.ComputedImageSize}   " +
                               (layout.SizeMatches ? "MATCH" : "MISMATCH"));
            if (!layout.SizeMatches && layout.Note is not null) builder.AppendLine($"  note: {layout.Note}");
            builder.AppendLine($"  faces {layout.FaceCount}   face size {layout.FaceSize}   face align {layout.FaceAlignment}");
            for (int m = 0; m < layout.Mips.Count && m < 12; m++)
            {
                var mip = layout.Mips[m];
                builder.AppendLine($"    mip {m}: {mip.Width}x{mip.Height}x{mip.SliceCount}  " +
                                   $"{mip.WidthBlocks}x{mip.HeightBlocks} blocks  pitch {mip.Pitch}  " +
                                   $"blockHeight {mip.BlockHeight}  offset {mip.Offset}  size {mip.TotalSize}");
            }
        }

        var warnings = _texture.Validate();
        builder.AppendLine();
        builder.AppendLine(warnings.Count == 0 ? "validation: OK" : "validation warnings:");
        foreach (var warning in warnings) builder.AppendLine("  - " + warning);

        if (_section is not null)
        {
            builder.AppendLine();
            var subFileName = _section.WtaSubFile?.Name;
            builder.AppendLine($"bank: {_section.Label}" +
                               (string.IsNullOrWhiteSpace(subFileName) ? string.Empty : $"  \"{subFileName}\"") +
                               $"  ({_section.Bank.Count} textures)");
            builder.AppendLine($"  table3 {(_section.Bank.IncludeTable3 ? "present" : "absent")}   " +
                               $"standalone {_section.Bank.IsStandalone}   payloadBase 0x{_section.Bank.PayloadBase:X}" +
                               (_document is not null && _document.Sections.Count > 1
                                   ? $"   bank {_document.Sections.IndexOf(_section) + 1} of {_document.Sections.Count}"
                                   : string.Empty));
        }

        _inspector.SetTechnicalSummary(builder.ToString());
    }

    // --- preview ------------------------------------------------------------------------

    private void UpdatePreview()
    {
        if (_texture is null)
        {
            _imageViewer.SetImage(null);
            return;
        }

        int mip = Math.Max(0, _mipCombo.SelectedIndex);
        var type = _texture.Header.Type;

        if (type.Is3D())
        {
            UpdateVolumePreview();
            return;
        }

        if (type.IsCube())
        {
            // HDR faces go to the GPU as floats so the exposure control works on the real values;
            // everything else uses the 8-bit path.
            var hdrFaces = TextureCodec.IsHdr(_texture.FormatInfo) ? DecodeCubeFacesHdr(mip) : null;
            var faces = hdrFaces is { Count: 6 } ? null : DecodeCubeFaces(mip);
            if (hdrFaces is { Count: 6 } || faces is { Count: 6 })
            {
                if (hdrFaces is { Count: 6 }) _glPreview.SetCubeFacesHdr(hdrFaces);
                else _glPreview.SetCubeFaces(faces!);
                _viewTabs.SelectedIndex = 1;
                SetStatus($"Cubemap mip {mip} ready - drag or arrow keys to look, wheel to zoom, Home/double-click to reset.");
            }
            else
            {
                _viewTabs.SelectedIndex = 0;
                SetStatus("Cubemap could not be decoded; showing the first face if available.");
                _imageViewer.SetImage(DecodeSubresource(mip, 0, 0, out var error));
                if (error is not null) SetStatus(error);
            }
            return;
        }

        var image = DecodeSubresource(mip, 0, 0, out var decodeError);
        _viewTabs.SelectedIndex = 0;
        _imageViewer.SetImage(image);
        if (image is null) SetStatus(decodeError ?? "Decode failed.");
    }

    /// <summary>
    /// Decode a 3D texture's slices off the UI thread. Selecting a different slice only moves the
    /// slice planes (see <see cref="SyncSliceFromCombo"/>); it never triggers a re-decode.
    /// </summary>
    private void UpdateVolumePreview()
    {
        if (_texture is null || !_texture.Header.Type.Is3D()) return;
        var texture = _texture;
        int mip = Math.Max(0, _mipCombo.SelectedIndex);

        _volumeCts?.Cancel();
        _volumeCts?.Dispose();
        _volumeCts = new CancellationTokenSource();
        var token = _volumeCts.Token;
        _volumePending = true;

        var layout = texture.Layout;
        int sliceCount = layout.Applicable && mip < layout.Mips.Count ? layout.Mips[mip].SliceCount : 1;
        int requested = PreviewSliceCount(texture, mip, sliceCount);
        SetStatus($"Decoding volume mip {mip} ({requested} slice(s))...");

        var context = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() =>
        {
            var slices = TextureCodec.DecodeVolumeSlices(texture, mip, requested, out var error);
            return (slices, error);
        }, token).ContinueWith(task =>
        {
            if (IsDisposed || token.IsCancellationRequested) return;
            if (task.IsFaulted)
            {
                _volumePending = false;
                SetStatus("Volume decode failed: " + task.Exception?.GetBaseException().Message);
                return;
            }
            ApplyVolumePreview(texture, mip, task.Result.slices, task.Result.error);
        }, CancellationToken.None, TaskContinuationOptions.None, context);
    }

    /// <summary>
    /// How many slices of a volume preview to assemble: the whole mip unless it is huge, in which case
    /// the axes are capped so a preview cannot exhaust memory.
    /// </summary>
    private static int PreviewSliceCount(EditableTexture texture, int mip, int sliceCount)
    {
        var layout = texture.Layout;
        int width = layout.Applicable && mip < layout.Mips.Count ? layout.Mips[mip].Width : (int)texture.Header.Width;
        int height = layout.Applicable && mip < layout.Mips.Count ? layout.Mips[mip].Height : (int)texture.Header.Height;
        long perSlice = Math.Max(1L, (long)width * height);
        long byTexels = Math.Max(1L, MaxPreviewVolumeTexels / perSlice);
        return (int)Math.Max(1, Math.Min(sliceCount, Math.Min(MaxPreviewSlices, byTexels)));
    }

    private void ApplyVolumePreview(EditableTexture texture, int mip, IReadOnlyList<RgbaImage> slices, string? error)
    {
        if (!ReferenceEquals(_texture, texture)) return; // selection changed while decoding
        _volumePending = false;
        if (slices.Count == 0)
        {
            _viewTabs.SelectedIndex = 0;
            _inspector.SetValidation(texture.Validate(), error ?? "the volume decoder returned no slice");
            SetStatus(error ?? "Volume decode failed.");
            return;
        }

        // The GL tab has just untiled the whole volume: keep the slices so the 2D tab and the
        // validation verdict can reuse them instead of untiling the volume again per slice.
        _volumeSource = texture;
        _volumeSourceMip = mip;
        _volumeSourceSlices = slices;
        _inspector.SetValidation(texture.Validate(), null);

        _glPreview.VolumeResolutionCap = _settings.VolumeResolutionCap;
        RgbaImage volume;
        if (slices.Count == 1 && slices[0].Depth > 1)
        {
            volume = slices[0];
        }
        else
        {
            // Compose the slices into a single volume image for the GPU.
            volume = new RgbaImage(slices[0].Width, slices[0].Height, slices.Count) { IsSrgb = slices[0].IsSrgb };
            for (int z = 0; z < slices.Count; z++) volume.SetSlice(z, slices[z]);
        }
        _glPreview.SetVolume(volume);
        UpdateSliceImage();

        // A uniform alpha channel carries no density, so the alpha-blended view would be blank or a
        // solid block. Default to the RGB-derived opacity so the assembled volume is visible at all.
        // The render-mode combo and the toolbar "Opaque a" toggle are two views of the same "ignore
        // alpha" state and stay in sync, so the button shows what the volume is actually drawing with.
        bool alphaUniform = volume.IsAlphaUniform();
        if (alphaUniform && _volumeMode.SelectedIndex != VolumeModeMip)
            _volumeMode.SelectedIndex = VolumeModeRgb;

        int sliceCount = texture.Layout.Applicable && mip < texture.Layout.Mips.Count
            ? texture.Layout.Mips[mip].SliceCount
            : slices.Count;
        string truncation = slices.Count < sliceCount ? $", first {slices.Count} of {sliceCount}" : string.Empty;
        string alphaNote = alphaUniform ? ", alpha is uniform - showing RGB" : string.Empty;

        _viewTabs.SelectedIndex = 1;
        SetStatus($"Volume mip {mip}: {slices.Count} slice(s) assembled{truncation}{alphaNote} - " +
                  "drag to orbit, wheel to zoom, double-click to reset.");
    }

    /// <summary>
    /// Show the selected volume slice in the 2D tab. The slice control therefore both moves the
    /// cross-section plane and lets a single slice be inspected with channel/gamma/zoom.
    /// </summary>
    private void UpdateSliceImage()
    {
        if (_texture is null || !_texture.Header.Type.Is3D()) return;

        // While the volume is decoding, wait for it: a per-slice decode would untile the whole volume
        // on the UI thread, which is the work the async preview is already doing.
        if (_volumePending) return;

        int mip = Math.Max(0, _mipCombo.SelectedIndex);
        int slice = Math.Max(0, _sliceCombo.SelectedIndex);

        var cached = _volumeSourceSlices;
        if (cached is not null && ReferenceEquals(_volumeSource, _texture) && _volumeSourceMip == mip &&
            slice < cached.Count)
        {
            _imageViewer.SetImage(cached[slice], preserveView: true);
            return;
        }

        var image = DecodeSubresource(mip, 0, slice, out _);
        if (image is not null) _imageViewer.SetImage(image, preserveView: true);
    }

    /// <summary>Slice selector moved: reposition the slice plane without re-decoding the volume.</summary>
    private void SyncSliceFromCombo()
    {
        if (_syncingSlices) return;
        int count = _sliceCombo.Items.Count;
        if (count <= 1) return;

        _syncingSlices = true;
        try
        {
            _sliceTrack.Value = Math.Clamp(_sliceCombo.SelectedIndex * 100 / (count - 1), 0, 100);
            _glPreview.SliceZ = _sliceTrack.Value / 100f;
            UpdateSliceImage();
            _glPreview.Invalidate();
        }
        finally
        {
            _syncingSlices = false;
        }
    }

    /// <summary>Slice bar moved: move the slice plane and mirror the selection into the combo.</summary>
    private void SyncSliceFromTrack()
    {
        if (_syncingSlices) return;

        _syncingSlices = true;
        try
        {
            _glPreview.SliceZ = _sliceTrack.Value / 100f;
            int count = _sliceCombo.Items.Count;
            if (count > 1)
            {
                int index = Math.Clamp((int)Math.Round(_sliceTrack.Value / 100.0 * (count - 1)), 0, count - 1);
                _sliceCombo.SelectedIndex = index;
            }
            UpdateSliceImage();
            _glPreview.Invalidate();
        }
        finally
        {
            _syncingSlices = false;
        }
    }

    private IReadOnlyList<RgbaImage>? DecodeCubeFaces(int mip)
    {
        if (_texture is null) return null;
        var faces = new List<RgbaImage>(6);
        for (int face = 0; face < 6; face++)
        {
            var image = DecodeSubresource(mip, face, 0, out _);
            if (image is null) return null;
            faces.Add(image);
        }
        return faces;
    }

    private IReadOnlyList<FloatImage>? DecodeCubeFacesHdr(int mip)
    {
        if (_texture is null) return null;
        var faces = TextureCodec.DecodeCubeFacesHdr(_texture, mip, out _);
        return faces.Count == 6 ? faces : null;
    }

    private RgbaImage? DecodeSubresource(int mip, int face, int slice, out string? error)
    {
        error = null;
        if (_texture is null) return null;
        var key = DecodeCache.Key(_texture, mip, face, slice);
        if (_cache.TryGet(key, out var cached) && cached is not null) return cached;

        var image = TextureCodec.Decode(_texture, mip, face, slice, out error);
        if (image is not null) _cache.Put(key, image);
        return image;
    }

    // --- save / export ------------------------------------------------------------------

    private void SaveDocument(bool saveAs)
    {
        if (_document is null)
        {
            SetStatus("Nothing to save.");
            return;
        }

        string? outputDirectory = null;
        if (saveAs)
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose the output folder" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            outputDirectory = dialog.SelectedPath;
        }

        try
        {
            SyncRawDocument();
            var options = new WriteOptions
            {
                OutputDirectory = outputDirectory,
            };
            var result = BankWriter.Write(_document, options);
            result.SaveAll();

            SetStatus(result.Warnings.Count == 0
                ? $"Saved {result.Files.Count} file(s)"
                : $"Saved {result.Files.Count} file(s): {string.Join("; ", result.Warnings)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportPng()
    {
        if (_texture is null) return;
        int mip = Math.Max(0, _mipCombo.SelectedIndex);
        var image = DecodeSubresource(mip, 0, 0, out var error);
        if (image is null)
        {
            SetStatus(error ?? "Decode failed.");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = $"{Path.GetFileNameWithoutExtension(_document?.FilePath ?? "texture")}_{_texture.Index}_mip{mip}.png",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImageIo.Save(image, dialog.FileName);
        SetStatus("Exported " + dialog.FileName);
    }

    /// <summary>
    /// Apply the import dialog's optional passes to a freshly decoded image: alpha bleeding first
    /// (so the colour fill still sees the cutout), then the opaque conversion.
    /// </summary>
    private static RgbaImage PreprocessImport(RgbaImage image, TextureImportDialog options) =>
        Solidify.Process(image, options.Solidify, options.MakeOpaque);

    private void ExportDds()
    {
        if (_texture is null) return;
        var info = _texture.FormatInfo;
        if (info is null || !DdsExport.CanExport(_texture))
        {
            SetStatus($"DDS export is not supported for {info?.Name ?? "this format"} (use raw XT1 or PNG).");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "DDS texture (*.dds)|*.dds",
            FileName = $"{Path.GetFileNameWithoutExtension(_document?.FilePath ?? "texture")}_{_texture.Index}.dds",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, DdsExport.Build(_texture));
            SetStatus("Exported " + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DDS export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportRawXt1()
    {
        if (_texture is null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "Raw XT1 (*.xt1)|*.xt1",
            FileName = $"{Path.GetFileNameWithoutExtension(_document?.FilePath ?? "texture")}_{_texture.Index}.xt1",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var raw = new RawXt1(_texture.Header.Clone(), _texture.Payload);
        File.WriteAllBytes(dialog.FileName, raw.ToBytes());
        SetStatus("Exported " + dialog.FileName);
    }

    // --- editing ------------------------------------------------------------------------

    private void ImportTexture()
    {
        if (_texture is null || _section is null)
        {
            SetStatus("Select a texture first.");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Filter = "Images (*.png;*.bmp;*.jpg;*.jpeg;*.tga;*.dds)|*.png;*.bmp;*.jpg;*.jpeg;*.tga;*.dds|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // A DDS is already compressed: import it losslessly (surfaces copied, mips preserved)
            // instead of decoding and re-encoding through the import dialog.
            if (dialog.FileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                DdsImage dds;
                try
                {
                    dds = DdsIo.Read(File.ReadAllBytes(dialog.FileName));
                }
                catch (Exception ex)
                {
                    SetStatus("Could not read the DDS: " + ex.Message);
                    return;
                }

                var replaced = TextureOperations.ReplaceWithDds(_texture, dds, _settings.SyncFlagsFromFormat);
                if (!replaced.Success)
                {
                    RefreshAll();
                    SetStatus("Import failed: " + replaced.Error);
                    return;
                }

                var ddsCommand = TextureOperations.TakePendingCommand();
                if (ddsCommand is not null) _history.Push(ddsCommand);
                RefreshAll();
                SetStatus($"Imported {Path.GetFileName(dialog.FileName)} losslessly " +
                          $"({dds.MipCount} mip(s), {dds.Format?.Name}).");
                return;
            }

            var image = ImageIo.Load(dialog.FileName);
            if (image is null) return;

            using var options = new TextureImportDialog(Path.GetFileName(dialog.FileName), image,
                _texture.FormatInfo ?? FormatTable.Get(Xt1Format.Bc7Unorm)!, CurrentBackend,
                EncodeQuality.Normal,
                generateMips: true);
            if (options.ShowDialog(this) != DialogResult.OK) return;

            image = PreprocessImport(image, options);

            // Captured before the format is switched: the undo snapshot inside the operation must
            // record the header as it was, otherwise undoing leaves the new format over the old
            // payload. The same snapshot restores the header if the encode fails.
            var originalHeader = _texture.Header.Raw.ToArray();
            _texture.Header.Format = options.SelectedFormat.Format;
            var encoder = _encoders.Pick(options.Backend, options.SelectedFormat);

            var result = TextureOperations.Replace2D(_texture, image, encoder, options.GenerateMips,
                options.Quality, originalHeader, _settings.SyncFlagsFromFormat);

            if (!result.Success)
            {
                originalHeader.CopyTo(_texture.Header.Raw, 0);
                RefreshAll();
                SetStatus("Import failed: " + result.Error);
                return;
            }

            var command = TextureOperations.TakePendingCommand();
            if (command is not null) _history.Push(command);
            RefreshAll();
            SetStatus("Imported " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ReplaceRawPayload()
    {
        if (_texture is null) return;
        using var dialog = new OpenFileDialog
        {
            Filter = "Raw XT1 (*.xt1)|*.xt1|Raw payload (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var bytes = File.ReadAllBytes(dialog.FileName);
        if (RawXt1.HasMagic(bytes))
        {
            var replaced = TextureOperations.ReplaceWithXt1(_texture, bytes);
            if (!replaced.Success)
            {
                SetStatus(replaced.Error ?? "Replace failed.");
                return;
            }
            var xt1Command = TextureOperations.TakePendingCommand();
            if (xt1Command is not null) _history.Push(xt1Command);
            RefreshAll();
            SetStatus("Replaced from " + Path.GetFileName(dialog.FileName));
            return;
        }

        var result = TextureOperations.ReplaceRawPayload(_texture, bytes, requireExactSize: true);
        if (!result.Success)
        {
            var confirm = MessageBox.Show(this, result.Error + "\n\nApply anyway?", "Size mismatch",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            result = TextureOperations.ReplaceRawPayload(_texture, bytes, requireExactSize: false);
        }

        if (!result.Success)
        {
            SetStatus(result.Error ?? "Failed.");
            return;
        }

        var command = TextureOperations.TakePendingCommand();
        if (command is not null) _history.Push(command);
        RefreshAll();
        SetStatus(result.Warning is null ? "Payload replaced." : "Payload replaced: " + result.Warning);
    }

    private void AddTexture()
    {
        if (_section is null)
        {
            SetStatus("Load a bank first.");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Filter = "Images (*.png;*.bmp;*.jpg;*.jpeg;*.tga;*.dds)|*.png;*.bmp;*.jpg;*.jpeg;*.tga;*.dds|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var name = Path.GetFileNameWithoutExtension(dialog.FileName);
            EditableTexture? texture;
            if (dialog.FileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var dds = DdsIo.Read(File.ReadAllBytes(dialog.FileName));
                texture = TextureOperations.BuildFromDds(dds, name, out var ddsError);
                if (texture is null)
                {
                    SetStatus("Add failed: " + (ddsError ?? "could not import the DDS"));
                    return;
                }
            }
            else
            {
                var image = ImageIo.Load(dialog.FileName);
                if (image is null) return;
                using var options = new TextureImportDialog(Path.GetFileName(dialog.FileName), image,
                    FormatTable.Get(_settings.DefaultImportFormat) ?? FormatTable.Get(Xt1Format.Bc7Unorm)!,
                    CurrentBackend, EncodeQuality.Normal, generateMips: true);
                if (options.ShowDialog(this) != DialogResult.OK) return;

                image = PreprocessImport(image, options);
                texture = EditableTexture.CreateNew(options.SelectedFormat.Format, (uint)image.Width, (uint)image.Height);
                var encoder = _encoders.Pick(options.Backend, options.SelectedFormat);
                var result = TextureOperations.Replace2D(texture, image, encoder, options.GenerateMips, options.Quality);
                if (!result.Success)
                {
                    SetStatus("Add failed: " + result.Error);
                    return;
                }
            }

            // New textures default to the model/UI bank class and, per the flag-sync preference, get
            // their sRGB/cubemap bits from the format they were encoded with.
            texture.Name = name;
            texture.HashMode = HashMode.AutoFromName;
            texture.Hash = Crc32NameHash.ComputeMasked(name);
            texture.SetModelBankClass(true);
            if (_settings.SyncFlagsFromFormat) TextureOperations.SyncFlagsFromFormat(texture);

            _history.Execute(TextureOperations.CreateAddTextureCommand(_section, texture));
            RebuildTree();
            SelectTexture(texture);
            SetStatus($"Added \"{name}\" (hash 0x{texture.Hash:X8}).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Add failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteTexture()
    {
        if (_texture is null || _section is null) return;
        var confirm = MessageBox.Show(this, $"Delete texture #{_texture.Index}?", "Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        var section = _section;
        var texture = _texture;
        _history.Execute(TextureOperations.CreateDeleteTextureCommand(section, texture));
        RebuildTree();
        SelectTexture(section.Bank.Textures.FirstOrDefault());
        SetStatus("Texture deleted.");
    }

    /// <summary>Recompute the header imageSize (and table1) from the layout, as one undoable step.</summary>
    private void RecomputeSelectedImageSize()
    {
        var texture = SelectedTexture;
        if (texture is null) return;

        ulong beforeImageSize = texture.Header.ImageSize;
        uint beforeTable1 = texture.PayloadSize;
        TextureOperations.RecomputeImageSize(texture);
        ulong afterImageSize = texture.Header.ImageSize;
        uint afterTable1 = texture.PayloadSize;
        if (beforeImageSize == afterImageSize && beforeTable1 == afterTable1)
        {
            SetStatus("imageSize already matches the layout.");
            return;
        }

        _history.Push(new DelegateCommand("Recompute image size",
            () => { texture.Header.ImageSize = afterImageSize; texture.PayloadSize = afterTable1; },
            () => { texture.Header.ImageSize = beforeImageSize; texture.PayloadSize = beforeTable1; }));
        RefreshAll();
        SetStatus($"imageSize {beforeImageSize} -> {afterImageSize}.");
    }

    private void CopySelectedHash()
    {
        var texture = SelectedTexture;
        if (texture is null) return;
        var text = "0x" + texture.EffectiveHash().ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Clipboard.SetText(text);
            SetStatus($"Copied {text}.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not copy the hash: " + ex.Message);
        }
    }

    private void ValidateSelectedTexture()
    {
        var texture = SelectedTexture;
        if (texture is null) return;
        var lines = new List<string>(texture.Validate());
        if (TextureCodec.CanDecode(texture.FormatInfo))
        {
            var image = DecodeSubresource(Math.Max(0, _mipCombo.SelectedIndex), 0, 0, out var error);
            lines.Insert(0, image is null
                ? "decode: FAILED - " + (error ?? "no image")
                : $"decode: ok ({image.Width}x{image.Height})");
        }
        else
        {
            lines.Insert(0, $"decode: no decoder for {texture.FormatInfo?.Name ?? "this format"}");
        }

        MessageBox.Show(this, string.Join(Environment.NewLine, lines),
            $"Texture #{texture.Index} validation", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Export every texture of the selected bank (mip 0) as PNG into one folder.</summary>
    private async void ExportBankTextures()
    {
        if (_section is null) return;
        using var dialog = new FolderBrowserDialog { Description = $"Choose an output folder for {_section.Label}" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var textures = _section.Bank.Textures.ToList();
        await ExportTexturesAsync(textures, dialog.SelectedPath, _section.Label,
            texture => $"tex{texture.Index}_0x{texture.EffectiveHash():X8}.png");
    }

    /// <summary>Export mip 0 of every texture in every bank of the container into one folder.</summary>
    private async void ExportContainerTextures()
    {
        if (_document is null) return;
        using var dialog = new FolderBrowserDialog { Description = "Choose an output folder for this container" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var textures = _document.AllTextures.ToList();
        if (textures.Count == 0)
        {
            SetStatus("This container has no textures.");
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(_document.FilePath ?? "container");
        await ExportTexturesAsync(textures, dialog.SelectedPath, "container",
            texture => $"{stem}_tex{texture.Index}_0x{texture.EffectiveHash():X8}.png");
    }

    /// <summary>
    /// Decode and PNG-encode a set of textures off the UI thread with progress reports. A container can
    /// hold thousands of textures, so running this inline froze the window for the whole export.
    /// </summary>
    private async Task ExportTexturesAsync(IReadOnlyList<EditableTexture> textures, string folder,
        string what, Func<EditableTexture, string> fileName)
    {
        _exportCts?.Cancel();
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;

        IProgress<int> progress = new Progress<int>(done =>
        {
            if (!IsDisposed) SetStatus($"Exporting {what}: {done}/{textures.Count}...");
        });

        try
        {
            var (exported, skipped) = await Task.Run(() =>
            {
                int ok = 0, failed = 0, done = 0;
                foreach (var texture in textures)
                {
                    token.ThrowIfCancellationRequested();
                    var image = TextureCodec.Decode(texture, 0, 0, 0, out _);
                    if (image is null)
                    {
                        failed++;
                    }
                    else
                    {
                        ImageIo.Save(image, Path.Combine(folder, fileName(texture)));
                        ok++;
                    }
                    if (++done % 16 == 0) progress.Report(done);
                }
                progress.Report(done);
                return (ok, failed);
            }, token);

            SetStatus($"Exported {exported} texture(s) from {what}" +
                      (skipped == 0 ? "." : $", skipped {skipped} that could not be decoded."));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Export canceled.");
        }
        catch (Exception ex)
        {
            SetStatus("Export failed: " + ex.Message);
        }
    }

    /// <summary>Raw hex lives in its own dialog now that the right-hand side is a single panel.</summary>
    private void ShowHexDialog(byte[] bytes, string caption)
    {
        if (bytes.Length == 0) return;
        _hexPanel.Bind(bytes, caption);

        if (_hexDialog is null || _hexDialog.IsDisposed)
        {
            _hexDialog = new Form
            {
                Text = "Raw bytes",
                ClientSize = new Size(720, 560),
                StartPosition = FormStartPosition.CenterParent,
                ShowIcon = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
            };
            _hexPanel.Dock = DockStyle.Fill;
            _hexDialog.Controls.Add(_hexPanel);
            // Hide rather than close, so the control (and its undo wiring) survives.
            _hexDialog.FormClosing += (_, e) =>
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                _hexDialog.Hide();
            };
        }

        _hexDialog.Text = "Raw bytes - " + caption;
        _hexDialog.Show(this);
        _hexDialog.BringToFront();
    }

    private void BatchForceTable2()
    {
        if (_section is null) return;
        using var dialog = new TextPromptDialog("Force table2 byte", "Byte index (0-3):", "0");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!int.TryParse(dialog.Value, out int index) || index is < 0 or > 3) return;

        using var valueDialog = new TextPromptDialog("Force table2 byte", $"Value for b{index} (hex or decimal):", "20");
        if (valueDialog.ShowDialog(this) != DialogResult.OK) return;
        if (!TryParseByte(valueDialog.Value, out byte value)) return;

        _history.Execute(TextureOperations.CreateBatchTable2Command(_section, index, value));
        RefreshAll();
        SetStatus($"Forced table2[b{index}] = 0x{value:X2} across {_section.Bank.Count} textures.");
    }

    private void BatchForceTextureFlag()
    {
        if (_section is null) return;
        using var dialog = new TextPromptDialog("Force textureFlag", "Value (hex or decimal):", "0x20");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!TryParseUInt(dialog.Value, out uint value)) return;

        _history.Execute(TextureOperations.CreateBatchTextureFlagCommand(_section, value));
        RefreshAll();
        SetStatus($"Forced textureFlag = {Mem.Hex(value)} across {_section.Bank.Count} textures.");
    }

    private void ValidateDocument()
    {
        if (_document is null) return;
        var lines = new List<string>();
        foreach (var section in _document.Sections)
            lines.AddRange(TextureOperations.ValidateBank(section).Select(w => $"{section.Label}: {w}"));
        lines.AddRange(_document.Warnings.Select(w => "document: " + w));

        MessageBox.Show(this,
            lines.Count == 0 ? "No validation warnings." : string.Join(Environment.NewLine, lines.Take(200)),
            "Validation", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowSettings()
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ApplySettings();
        SetStatus("Settings saved.");
    }

    /// <summary>
    /// The post-dialog half of <see cref="ShowSettings"/>: the native tools are dropped and loaded
    /// again because the dialog may have pointed them somewhere else.
    /// </summary>
    private void ApplySettings()
    {
        _settings.Save();

        // NvttEncoder.Dispose() unloads the library it was built on, so the native is released
        // through its owner: disposing both unloaded the same handle twice and threw (FreeLibrary on
        // a freed handle gives "Operation is not valid due to the current state of the object").
        if (_nvttEncoder is not null) _nvttEncoder.Dispose();
        else _nvttNative?.Dispose();
        _nvttNative = null;
        _nvttEncoder = null;
        _cliEncoder = null;

        InitializeNativeTools();
        UpdatePreview();
    }

    /// <summary>Test hook: the settings-apply path, without opening the dialog.</summary>
    internal void UiTestApplySettings() => ApplySettings();

    private void ShowBatchRecompress()
    {
        using var dialog = new BatchRecompressDialog(RunBatchRecompressAsync, CurrentBackend,
            _document is not null);
        dialog.ShowDialog(this);
        if (_document is not null)
        {
            // Rebind from scratch: a recompress replaced payloads (and possibly a raw .xt1's), so the
            // preview wrappers and tree labels have to be rebuilt rather than refreshed in place.
            AdoptDocument(_document);
        }
        else
        {
            RefreshAll();
        }
    }

    private Task RunBatchRecompressAsync(RecompressRequest request, IProgress<string> progress,
        CancellationToken token)
    {
        return Task.Run(() =>
        {
            if (request.CurrentDocument)
            {
                if (_document is not null)
                    _recompressor.RunOnDocument(_document, request.Options, progress, token);
                return;
            }
            _recompressor.RunOnFiles(request.Files, request.Options, progress, token);
        }, token);
    }

    private EncoderBackend CurrentBackend =>
        (EncoderBackend)(_backendCombo.SelectedItem ?? EncoderBackend.Nvtt);

    private void OnBackendChanged()
    {
        SetStatus($"Encoder backend: {CurrentBackend}");
        UpdateBackendLabel();
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private static bool TryParseUInt(string text, out uint value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return uint.TryParse(text, out value);
    }

    private static bool TryParseByte(string text, out byte value)
    {
        if (TryParseUInt(text, out uint parsed) && parsed <= 255)
        {
            value = (byte)parsed;
            return true;
        }
        value = 0;
        return false;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Splitter distances must be applied after the form is sized, otherwise they can exceed
        // the control bounds and throw.
        TrySetSplitter(_outerSplit, 260);
        TrySetSplitter(_innerSplit, Math.Max(200, _innerSplit.Width - 440));
        TrySetSplitter(_browserSplit, 200);

        if (_initialFile is not null)
        {
            _settings.LastDirectory = Path.GetDirectoryName(_initialFile);
            LoadFile(_initialFile);
        }
    }

    private static void TrySetSplitter(SplitContainer container, int distance)
    {
        try
        {
            int min = container.Panel1MinSize;
            int max = container.Width - container.Panel2MinSize - container.SplitterWidth;
            if (max < min) return;
            container.SplitterDistance = Math.Clamp(distance, min, max);
        }
        catch (InvalidOperationException)
        {
            // Ignore: the control is not laid out yet.
        }
    }

    /// <summary>Test hook used by <c>--uitest</c> to drive the UI without a user.</summary>
    internal void UiTestSelectRightTab(int index) => _inspector.AdvancedExpanded = index != 0;

    /// <summary>Test hook used by <c>--uitest</c>.</summary>
    internal void UiTestSelectTexture(int sectionIndex, int textureIndex)
    {
        var section = _document?.Sections.ElementAtOrDefault(sectionIndex);
        if (section is null) return;
        _section = section;
        SelectTexture(section.Bank.Textures.ElementAtOrDefault(textureIndex));
    }

    /// <summary>Test hook used by <c>--uitest</c>.</summary>
    internal string UiTestStatus => _statusLabel.Text ?? string.Empty;

    /// <summary>Test hook used by <c>--uitest</c>: which native encoders are loaded.</summary>
    internal string UiTestBackendState => _backendLabel.Text ?? string.Empty;

    /// <summary>Test hook used by <c>--uitest</c>: select the first 3D texture in the document.</summary>
    internal string UiTestSelectFirstVolume()
    {
        if (_document is null) return "no document";
        for (int s = 0; s < _document.Sections.Count; s++)
        {
            var textures = _document.Sections[s].Bank.Textures;
            for (int t = 0; t < textures.Count; t++)
            {
                if (!textures[t].Header.Type.Is3D()) continue;
                UiTestSelectTexture(s, t);
                var header = textures[t].Header;
                return $"#{textures[t].Index} {header.Width}x{header.Height}x{header.Depth}";
            }
        }
        return "none";
    }

    /// <summary>Test hook used by <c>--uitest</c>: change the shared channel selector.</summary>
    internal void UiTestSelectChannel(int index) => _channelCombo.SelectedIndex = index;

    /// <summary>Test hook used by <c>--uitest</c>: toggle the shared "Opaque a" button.</summary>
    internal void UiTestToggleOpaque()
    {
        if (_opaqueButton is not null) _opaqueButton.Checked = !_opaqueButton.Checked;
    }

    /// <summary>Test hook used by <c>--uitest</c>: pick a 3D render mode (0 RGB, 1 Alpha, 2 MIP).</summary>
    internal void UiTestSelectVolumeMode(int index) => _volumeMode.SelectedIndex = index;

    /// <summary>Test hook used by <c>--uitest</c>: what the 3D viewport would actually draw with.</summary>
    internal string UiTestVolumeState =>
        $"render={_glPreview.RenderMode} channel={_glPreview.Channel} planes={_glPreview.ShowSlices} " +
        $"sliceZ={_glPreview.SliceZ:F2} distance={_glPreview.Camera.Distance:F2} tab={_viewTabs.SelectedIndex}";

    /// <summary>Test hook used by <c>--uitest</c>: the right-click menu entries for the current selection.</summary>
    /// <summary>Test hook: adopt a new empty bank without going through the dialogs.</summary>
    internal Xt1.Core.Containers.BankDocument UiTestCreateEmptyBank(bool standalone)
    {
        var kind = standalone
            ? Xt1.Core.Containers.BankKind.StandaloneWtb
            : Xt1.Core.Containers.BankKind.EffectWta;
        var directory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "uitest");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, standalone ? "empty.wtb" : "empty.wta");
        var document = Xt1.Core.Containers.BankDocument.CreateEmpty(kind, path);
        AdoptDocument(document);
        return document;
    }

    internal IReadOnlyList<string> UiTestTreeMenuItems()
    {
        UpdateTreeMenuState();
        var items = new List<string>();
        foreach (ToolStripItem item in _treeMenu.Items)
        {
            if (item is not ToolStripMenuItem menuItem || !menuItem.Available) continue;
            items.Add($"{menuItem.Text}|{(menuItem.Enabled ? "enabled" : "disabled")}");
        }
        return items;
    }

    /// <summary>Test hook: the file browser's rows, in display order (Name column).</summary>
    internal IReadOnlyList<string> UiTestFileListItems() =>
        _fileList.Items.Cast<ListViewItem>().Select(item => item.Text ?? string.Empty).ToList();

    /// <summary>Test hook: the file browser's column headers.</summary>
    internal IReadOnlyList<string> UiTestFileColumnHeaders() =>
        _fileList.Columns.Cast<ColumnHeader>().Select(column => column.Text ?? string.Empty).ToList();

    /// <summary>Test hook: click a column header (0=Name, 1=Modified, 2=Size) and return the new order.</summary>
    internal IReadOnlyList<string> UiTestSortFileColumn(int column)
    {
        OnFileColumnClick(this, new ColumnClickEventArgs(column));
        return UiTestFileListItems();
    }

    /// <summary>Test hook: pick a file row by name fragment, the way a click does.</summary>
    internal string UiTestPickFile(string nameFragment)
    {
        foreach (ListViewItem item in _fileList.Items)
        {
            if (item.Text?.Contains(nameFragment, StringComparison.OrdinalIgnoreCase) != true) continue;
            item.Selected = true;
            item.EnsureVisible();
            return item.Text;
        }
        return "not found";
    }

    /// <summary>Test hook used by <c>--uitest</c>: select a node the way a right-click does.</summary>
    internal string UiTestSelectNode(string labelFragment)
    {
        foreach (TreeNode root in _tree.Nodes)
        {
            var found = FindNode(root, labelFragment);
            if (found is null) continue;
            _tree.SelectedNode = found;
            return found.Text;
        }
        return "not found";

        static TreeNode? FindNode(TreeNode node, string fragment)
        {
            if (node.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return node;
            foreach (TreeNode child in node.Nodes)
            {
                var match = FindNode(child, fragment);
                if (match is not null) return match;
            }
            return null;
        }
    }

    /// <summary>Test hook used by <c>--uitest</c>: what the inspector currently shows.</summary>
    internal string UiTestInspectorState =>
        $"advanced={_inspector.AdvancedExpanded} hash={_inspector.UiTestHashText} flags={_inspector.UiTestFlagState}";

    /// <summary>Test hook: the inspector validation verdict, for <c>--panelshot</c>.</summary>
    internal string UiTestInspectorValidation => _inspector.UiTestValidationText;

    /// <summary>Test hook: the inspector control, so <c>--panelshot</c> can capture it.</summary>
    internal Control UiTestInspectorControl => _inspector;

    /// <summary>Test hook used by <c>--uitest</c>: paste into the hash field.</summary>
    internal void UiTestPasteHash(string text) => _inspector.UiTestPasteHash(text);

    /// <summary>Test hook used by <c>--uitest</c>: run the CRC32 computation with a known name.</summary>
    internal void UiTestComputeHashFor(string name) => _inspector.UiTestComputeHashFor(name);

    /// <summary>
    /// Test hook used by <c>--uitest</c>: open the raw-bytes dialog the way the button does, and
    /// report what it bound. The dialog is modeless, so this does not block.
    /// </summary>
    internal string UiTestOpenHex()
    {
        if (_texture is null) return "no texture";
        ShowHexDialog(_texture.Header.Raw, "XT1 header");
        Application.DoEvents();
        var state = _hexDialog is null
            ? "no dialog"
            : $"visible={_hexDialog.Visible} bytes={_hexPanel.Buffer?.Length ?? -1}";
        _hexDialog?.Hide();
        return state;
    }

    /// <summary>Test hook: the tree, so <c>--panelshot</c> can capture it next to the inspector.</summary>
    internal Control UiTestTreeControl => _tree;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)    {
        if (keyData == (Keys.Control | Keys.Z)) { _history.Undo(); RefreshAll(); return true; }
        if (keyData == (Keys.Control | Keys.Y)) { _history.Redo(); RefreshAll(); return true; }
        if (keyData == (Keys.Control | Keys.O)) { OpenFile(); return true; }
        if (keyData == (Keys.Control | Keys.S)) { SaveDocument(false); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}