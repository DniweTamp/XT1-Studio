using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Xt1.App.Wpf.Dialogs;
using Xt1.App.Wpf.Inspector;
using Xt1.App.Wpf.Preview;
using Xt1.App.Wpf.Theming;
using Xt1.Rendering.WinForms.Rendering;
using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Nvtt;
using Xt1.Core.Settings;
using Xt1.Core.Util;
using Xt1.Core.Xt1;
using VolumeRenderMode = Xt1.Rendering.WinForms.Rendering.VolumeRenderMode;

namespace Xt1.App.Wpf;

/// <summary>
/// Pilot shell: the FModel-style frame (menu bar, segmented toolbar, asset browser over a resizable
/// file browser, 2D/Cube-3D viewport, inspector cards, status bar) wired to the real
/// <see cref="BankDocument"/> model.
/// </summary>
public partial class MainWindow : Window
{
    private sealed record TextureRef(EditableTexture Texture, BankSection? Section);

    /// <summary>
    /// One row of the file browser. Mutable <see cref="Weight"/> so switching between siblings can
    /// re-bold in place instead of rebuilding the list (which flashes and drops the selection).
    /// </summary>
    private sealed class FileRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required string Name { get; init; }
        public required string Modified { get; init; }
        public required string Size { get; init; }
        public required string Path { get; init; }
        public required DateTime ModifiedUtc { get; init; }
        public required long SizeBytes { get; init; }

        private FontWeight _weight;

        public FontWeight Weight
        {
            get => _weight;
            set
            {
                if (_weight == value) return;
                _weight = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Weight)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static readonly string[] OpenableExtensions = BankFileScan.Extensions;

    private const string RawReadOnlyTip =
        "Raw .xt1 payloads are read-only until the editing pipeline is ported.";
    private const string HashTip =
        "The CRC32 hash (table3), pasted as hex (0x prefix optional). Editing it fixes the value written on save";
    private const string HashCalcTip = "Ask for a name and compute crc32(name) & 0x7FFFFFFF";
    private const string NoHashTip =
        "This record has no CRC32 hash; enable 'Has CRC32 hash' to edit the value";
    private const string HashFlagTip =
        "table2 byte 0, bit 0x20 - whether the record carries a CRC32 hash " +
        "(set for model/UI/character banks, clear for effect banks)";
    private const string SrgbFlagTip = "table2 byte 3, bit 0x04";
    private const string CubeFlagTip = "table2 byte 3, bit 0x80";

    private readonly EditHistory _history = new();

    private BankDocument? _document;
    private EditableTexture? _selected;
    private bool _suppressFlags;
    private bool _suppressFileSelection;
    private bool _suppressPreview;
    private string _fileSortColumn = "name";
    private bool _fileSortAscending = true;
    private CancellationTokenSource? _exportCts;

    private int _mipIndex;
    private int _sliceIndex;
    private RgbaImage? _previewImage;
    private RgbaImage? _sliceBuffer;
    private string? _lastDecodeError;
    private EditableTexture? _rawTexture;
    private HexEditorDialog? _hexDialog;
    private List<FileRow>? _fileRows;
    private string? _fileListFolder;
    private EncoderPicker _encoders = null!;
    private BatchRecompressor _recompressor = null!;

    // Asset Browser zoom: the List tree or a grid of texture thumbnails. Thumbnails are decoded off
    // the UI thread from the cheapest mip, cached, and cancelled when the grid is rebuilt.
    private AssetViewLevel _assetLevel = AssetViewLevel.List;
    private bool _suppressZoom;
    private (EditableTexture? Texture, BankSection? Section) _gridSelection;
    private Border? _selectedTile;
    private CancellationTokenSource? _thumbCts;
    private readonly SemaphoreSlim _thumbGate = new(3, 3);
    private readonly List<PendingThumb> _pendingThumbs = new();
    private readonly Dictionary<ThumbKey, BitmapSource> _thumbCache = new();

    // A thumbnail only depends on the decode inputs (header/layout + payload) and the tile size, so the
    // key is those stamps rather than the live payload array — otherwise every edit would pin the old
    // payload and bitmap forever. The cache is capped to bound memory.
    private const int ThumbCacheLimit = 600;
    private readonly record struct PendingThumb(Border Tile, Image Image, UIElement Placeholder,
        EditableTexture Texture, int Size);
    private readonly record struct ThumbKey(EditableTexture Texture, int PayloadId, int PayloadStamp,
        int HeaderStamp, int Size);

    // Layer-animation playback for a multilayer (3D) texture, shown in the 2D tab.
    private readonly DispatcherTimer _animTimer = new();
    private bool _animPlaying;
    private bool _suppressAnim;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        ThemeButton.Click += OnThemeButtonClick;
        ThemeManager.Changed += OnThemeChanged;
        _history.Changed += UpdateUndoRedo;
        PreviewKeyDown += OnPreviewKeyDown;
        Preview2D.ViewChanged += (_, _) => UpdateZoomReadout();
        // The hosted GL viewport reports readiness (async, once its handle exists) and asks the
        // shell to move its render-mode combo when a volume's alpha carries no density.
        GlView.ReadyChanged += Update3DPlaceholder;
        GlView.RenderModeSuggested += OnGlRenderModeSuggested;
        // The field editor raises already-applied commands; the shell records them and refreshes
        // the surrounding cards. Raw byte edits go through the hidden hex dialog.
        FieldEditor.CommandProduced += command =>
        {
            _history.Push(command);
            RefreshInspector();
        };

        BuildPreviewContextMenu();
        _animTimer.Tick += OnAnimTick;
        // Dropping a bank anywhere on the window opens it, like the WinForms shell.
        AllowDrop = true;
        DragOver += OnFileDragOver;
        Drop += OnFileDropped;
        // The status zoom readout tracks whichever preview tab is on screen.
        ViewTabs.SelectionChanged += (_, _) =>
        {
            UpdateZoomReadout();
            if (ViewTabs.SelectedIndex == 1)
            {
                // The cross-section plane is only pushed while the 3D tab shows, so re-sync it when
                // the 3D view comes back to reflect the layer chosen (or played) on the 2D tab.
                ApplySliceZ();
            }
            else if (_selected is not null)
            {
                // Playback may have advanced the layer while the 2D tab was hidden.
                UpdateVolumeSliceImage(_selected);
            }
        };
        // GridViewColumnHeader has no XAML Click hook per column; listen for it on the list.
        FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnFileColumnHeaderClick));
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) LoadDocument(files[0]);
        e.Handled = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.Changed -= OnThemeChanged;
        _exportCts?.Cancel();
        _exportCts?.Dispose();
        _animTimer.Stop();

        // The raw-bytes dialog hides instead of closing, so it must be released explicitly.
        if (_hexDialog is not null)
        {
            _hexDialog.AllowClose();
            _hexDialog.Close();
            _hexDialog = null;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.ApplyTitleBar(this);
        PopulateToolbar();
        RebuildEncoders();
        UpdateThemeIcon();
        UpdateUndoRedo();
        SetAssetLevel(App.Settings.AssetBrowserLevel, persist: false, rebuild: false);
        PruneRecent();

        var args = Environment.GetCommandLineArgs();
        var file = args.Skip(1).FirstOrDefault(File.Exists);
        if (file is not null) LoadDocument(file);
        else ShowTexture(null, null);
    }

    private void OnThemeChanged()
    {
        UpdateThemeIcon();
        // Keep the hosted GL viewport's backdrop in step with the WPF palette.
        GlView.ApplyTheme();
        if (_selected is not null) UpdateValidation(_selected);
    }

    /// <summary>Reflect the active preference in the Theme button's glyph and tooltip.</summary>
    private void UpdateThemeIcon()
    {
        ThemeIcon.Text = ThemeManager.IsDark ? "\uE706" : "\uE708";
        ThemeButton.ToolTip = $"Theme: {App.Settings.Theme}. Click for {NextTheme(App.Settings.Theme)}.";
    }

    /// <summary>
    /// The Theme button flips between light and dark. When the preference is <see cref="AppTheme.System"/>
    /// the resolved appearance decides the target, so a click always changes what is on screen instead of
    /// landing on a preference that resolves to the current look (which made the first light/dark click
    /// appear to do nothing).
    /// </summary>
    private static AppTheme NextTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => AppTheme.Dark,
        AppTheme.Dark => AppTheme.Light,
        _ => ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark,
    };

    private void PopulateToolbar()
    {
        _suppressPreview = true;
        try
        {
            RebuildChannelCombo();

            VolumeModeCombo.ItemsSource = new[] { "RGB", "Alpha", "MIP" };
            VolumeModeCombo.SelectedIndex = App.Settings.OpaqueAlphaByDefault ? VolumeModeRgb : VolumeModeAlpha;
            ShowSlicesCheck.IsChecked = false;

            GammaBox.Text = "1.00";
            ExposureBox.Text = "1.00";
            AutoExposureCheck.IsChecked = true;

            OpaqueCheck.IsChecked = App.Settings.OpaqueAlphaByDefault;
        }
        finally
        {
            _suppressPreview = false;
        }

        Preview2D.Channel = ImageChannelMode.Rgba;
        Preview2D.OpaqueAlpha = App.Settings.OpaqueAlphaByDefault;
        SyncPreviewContextMenu();
        UpdateZoomReadout();
        SyncVolumeView();
        GlView.ApplyTheme();
    }

    /// <summary>Fill the channel selector; the alpha entry is optional (settings).</summary>
    private void RebuildChannelCombo()
    {
        int selected = ChannelCombo.SelectedIndex < 0 ? 0 : ChannelCombo.SelectedIndex;
        var items = new List<string> { "RGBA", "Red", "Green", "Blue" };
        if (App.Settings.ShowAlphaChannel) items.Add("Alpha");
        ChannelCombo.ItemsSource = items;
        ChannelCombo.SelectedIndex = Math.Min(selected, items.Count - 1);
    }
    // --- keyboard -----------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        switch (e.Key)
        {
            case Key.O:
                OpenFile();
                e.Handled = true;
                break;
            case Key.S:
                SaveDocument(false);
                e.Handled = true;
                break;
            case Key.Z:
                Undo();
                e.Handled = true;
                break;
            case Key.Y:
                Redo();
                e.Handled = true;
                break;
        }
    }

    // --- menu ---------------------------------------------------------------------------

    private void OnOpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnMenuOpenFile(object sender, RoutedEventArgs e) => OpenFile();

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open texture bank",
            Filter = "Texture banks (*.wta;*.dat;*.dtt;*.wtb;*.xt1)|*.wta;*.dat;*.dtt;*.wtb;*.xt1|All files (*.*)|*.*",
        };
        if (App.Settings.LastDirectory is { } directory) dialog.InitialDirectory = directory;
        if (dialog.ShowDialog(this) == true) LoadDocument(dialog.FileName);
    }

    // --- recent files -------------------------------------------------------------------

    private const int RecentFileLimit = 8;

    private void OnAppFileMenuOpened(object sender, RoutedEventArgs e) => RebuildRecentMenu();

    /// <summary>
    /// Rebuild "Open Recent" from the stored paths. Entries are only removed at startup, so a path
    /// that is briefly unavailable (a disconnected network or removable drive) is greyed out here
    /// rather than dropped from history.
    /// </summary>
    private void RebuildRecentMenu()
    {
        var files = App.Settings.RecentFiles ?? new List<string>();

        MenuOpenRecent.Items.Clear();
        MenuOpenRecent.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (files.Count == 0) return;

        foreach (var path in files)
        {
            bool exists = File.Exists(path);
            var item = new MenuItem
            {
                Header = Path.GetFileName(path),
                ToolTip = exists ? path : path + "  (file not found)",
                Tag = path,
                IsEnabled = exists,
            };
            item.Click += OnOpenRecent;
            MenuOpenRecent.Items.Add(item);
        }

        MenuOpenRecent.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        var clear = new MenuItem { Header = "Clear recent list" };
        clear.Click += OnClearRecent;
        MenuOpenRecent.Items.Add(clear);
    }

    private void OnOpenRecent(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path }) LoadDocument(path);
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        App.Settings.RecentFiles = new List<string>();
        App.Settings.Save();
        RebuildRecentMenu();
    }

    /// <summary>Move a path to the front of "Open Recent" (de-duplicated, capped) and persist it.</summary>
    private void RecordRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return; }

        var files = App.Settings.RecentFiles ?? new List<string>();
        files.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        files.Insert(0, full);
        if (files.Count > RecentFileLimit)
            files.RemoveRange(RecentFileLimit, files.Count - RecentFileLimit);

        App.Settings.RecentFiles = files;
        App.Settings.Save();
    }

    /// <summary>Drop entries whose file no longer exists (called once at startup).</summary>
    private void PruneRecent()
    {
        var files = App.Settings.RecentFiles;
        if (files is null) return;
        if (files.RemoveAll(p => !File.Exists(p)) > 0) App.Settings.Save();
    }

    private void OnMenuNewBank(object sender, RoutedEventArgs e)
    {
        var dialog = new NewBankDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var save = new SaveFileDialog
        {
            Title = "Choose the new bank file",
            Filter = dialog.Standalone ? "Standalone WTB (*.wtb)|*.wtb" : "Split bank (*.wta)|*.wta",
            FileName = dialog.Standalone ? "new.wtb" : "new.wta",
        };
        if (App.Settings.LastDirectory is { } directory) save.InitialDirectory = directory;
        if (save.ShowDialog(this) != true) return;

        try
        {
            var kind = dialog.Standalone ? BankKind.StandaloneWtb : BankKind.EffectWta;
            AdoptDocument(BankDocument.CreateEmpty(kind, save.FileName));
            App.Settings.LastDirectory = Path.GetDirectoryName(save.FileName);
            RecordRecent(save.FileName); // also persists LastDirectory with the updated history
            StatusLeft.Text = $"New empty {(dialog.Standalone ? "standalone WTB" : "split WTA")} bank - " +
                              "right-click the bank in the tree and choose \"Add texture from image...\", then Save.";
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Could not create the bank: " + ex.Message;
        }
    }

    private void OnMenuReload(object sender, RoutedEventArgs e)
    {
        if (_document?.FilePath is { } path) LoadDocument(path);
    }

    private void OnMenuSave(object sender, RoutedEventArgs e) => SaveDocument(false);

    private void OnMenuSaveAs(object sender, RoutedEventArgs e) => SaveDocument(true);

    private void SaveDocument(bool saveAs)
    {
        if (_document is null)
        {
            StatusLeft.Text = "Nothing to save.";
            return;
        }

        string? outputDirectory = null;
        if (saveAs)
        {
            var picker = new OpenFolderDialog { Title = "Choose the output folder" };
            if (picker.ShowDialog(this) != true) return;
            outputDirectory = picker.FolderName;
        }

        try
        {
            // A replaced texture gets a NEW payload array, so a raw .xt1 container would otherwise be
            // saved with the new header over the old payload (the WinForms sync, MainForm
            // SyncRawDocument). The wrapper shares the document's header, but not its payload array.
            SyncRawDocument();

            var options = new WriteOptions { OutputDirectory = outputDirectory };
            var result = BankWriter.Write(_document, options);
            result.SaveAll();

            StatusLeft.Text = result.Warnings.Count == 0
                ? $"Saved {result.Files.Count} file(s)"
                : $"Saved {result.Files.Count} file(s): {string.Join("; ", result.Warnings)}";
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Save failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Copy the edited raw wrapper's payload back into the container before writing it. Replace-style
    /// operations assign a new array to the wrapper's <c>Payload</c>, while the header is the shared
    /// instance, so without this a raw .xt1 would be written with a mismatched header/payload pair.
    /// </summary>
    private void SyncRawDocument()
    {
        if (_document?.RawXt1 is { } raw && _rawTexture is not null) raw.Payload = _rawTexture.Payload;
    }

    private void OnMenuExit(object sender, RoutedEventArgs e) => Close();

    private void OnMenuUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnMenuRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (!_history.Undo()) return;
        RefreshAfterHistory();
    }

    private void Redo()
    {
        if (!_history.Redo()) return;
        RefreshAfterHistory();
    }

    /// <summary>
    /// Commands that add or remove a texture change the tree, so an undo/redo that moved one across
    /// the stack needs a rebuild. Everything else only changes values or pixels: the inspector is
    /// refreshed in place, and the preview too because a replace/raw-byte undo changes the payload.
    /// </summary>
    private void RefreshAfterHistory()
    {
        bool structural = IsStructural(_history.UndoName) || IsStructural(_history.RedoName);
        if (structural)
        {
            BuildTree();
            return;
        }

        RefreshInspector();
        if (_selected is not null) UpdatePreview(_selected);
    }

    private static bool IsStructural(string? commandName) =>
        commandName is "Delete texture" or "Add texture";

    private void OnMenuExportContainer(object sender, RoutedEventArgs e) =>
        ExportContainerTextures(FormatFromTag(sender));

    /// <summary>The export format carried by a menu item's <c>Tag</c> ("png"/"dds"/"xt1").</summary>
    private static TextureExportFormat FormatFromTag(object sender) =>
        sender is MenuItem { Tag: string tag } && TextureExport.TryParse(tag, out var format)
            ? format
            : TextureExportFormat.Png;

    private void OnMenuAbout(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog { Owner = this };
        dialog.ShowDialog();
    }

    private void OnMenuSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(App.Settings, _encoderDescription, _nvttActive) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var result = dialog.Result;
        bool themeChanged = App.Settings.Theme != result.Theme;
        bool alphaChanged = App.Settings.ShowAlphaChannel != result.ShowAlphaChannel;

        CopySettings(result);
        App.Settings.Save();

        if (themeChanged)
        {
            ThemeManager.Set(App.Settings.Theme);
            UpdateThemeIcon();
        }

        if (alphaChanged) RebuildChannelCombo();
        // The preview resolution cap may have changed with the settings.
        GlView.VolumeResolutionCap = App.Settings.VolumeResolutionCap;
        // The native tool paths may have changed, so the encoder set is rebuilt from the new settings.
        RebuildEncoders();

        StatusLeft.Text = $"Settings saved. Encoder: {_encoderDescription}.";
    }

    /// <summary>
    /// Build the encoders: NVTT when its DLL is present, with the built-in managed encoder as the
    /// only fallback. Re-run whenever the settings change, since the nvtt DLL path decides that.
    /// </summary>
    private void RebuildEncoders()
    {
        var tools = App.Settings.Resolve();
        NvttEncoder? nvtt = null;
        if (tools.NvttDll is not null)
        {
            var native = NvttNative.TryCreate(out _, tools.NvttDll);
            if (native is not null) nvtt = new NvttEncoder(native);
        }

        _encoders = new EncoderPicker(nvtt, cli: null);
        _recompressor = new BatchRecompressor(_encoders);
        _nvttActive = nvtt is not null;
        _encoderDescription = nvtt is not null
            ? "NVTT (nvtt30205.dll)"
            : "Built-in managed encoder (NVTT not found)";
    }

    /// <summary>Which encoder the app will actually use, for the status bar and the Settings dialog.</summary>
    private string _encoderDescription = "Built-in managed encoder";

    /// <summary>Whether NVTT (rather than the built-in fallback) is the encoder the app resolved.</summary>
    private bool _nvttActive;

    /// <summary>The encoder description shown in Settings; the app never asks the user to choose one.</summary>
    internal string EncoderDescription => _encoderDescription;

    private void OnMenuBatchRecompress(object sender, RoutedEventArgs e)
    {
        var dialog = new BatchRecompressDialog(RunBatchRecompressAsync, _document is not null)
        {
            Owner = this,
        };
        dialog.ShowDialog();

        if (_document is not null)
        {
            // A recompress replaced payloads (and possibly a raw .xt1's), so the preview wrappers and
            // tree labels are rebound from scratch rather than refreshed in place.
            AdoptDocument(_document);
            StatusLeft.Text = "Batch recompress finished.";
        }
    }

    private Task RunBatchRecompressAsync(RecompressRequest request, IProgress<string> progress,
        CancellationToken token) =>
        Task.Run(() =>
        {
            if (request.CurrentDocument)
            {
                if (_document is not null) _recompressor.RunOnDocument(_document, request.Options, progress, token);
                return;
            }

            _recompressor.RunOnFiles(request.Files, request.Options, progress, token);
        }, token);

    private void OnMenuExportFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new ExportTexturesDialog(RunExportTexturesAsync) { Owner = this };
        dialog.ShowDialog();
    }

    private Task RunExportTexturesAsync(ExportTexturesRequest request, IProgress<string> progress,
        CancellationToken token) =>
        Task.Run(() =>
        {
            var options = new FolderExportOptions(
                request.Format, request.SourceRoot, request.OutputFolder, request.SubfolderPerBank);
            var stats = new TextureFolderExporter().Run(request.Files, options, progress, token);
            progress.Report(
                $"exported {stats.Written} of {stats.Textures} texture(s) from {stats.Files} file(s)" +
                (stats.Skipped > 0 ? $", skipped {stats.Skipped}" : string.Empty));
        }, token);

    /// <summary>Copy every field of the dialog result onto the live settings object.</summary>
    private static void CopySettings(AppSettings source)
    {
        var target = App.Settings;
        target.Theme = source.Theme;
        target.AssetBrowserLevel = source.AssetBrowserLevel;
        target.DefaultImportFormat = source.DefaultImportFormat;
        target.ImportFormats = source.ImportFormats is null ? null : new List<Xt1Format>(source.ImportFormats);
        target.OpaqueAlphaByDefault = source.OpaqueAlphaByDefault;
        target.ShowAlphaChannel = source.ShowAlphaChannel;
        target.SyncFlagsFromFormat = source.SyncFlagsFromFormat;
        target.VolumeResolutionCap = source.VolumeResolutionCap;
        target.NvttDllPath = source.NvttDllPath;
        target.NvttCliPath = source.NvttCliPath;
        target.NvttDefaultApplied = source.NvttDefaultApplied;
        target.LastDirectory = source.LastDirectory;
        target.RecentFiles = source.RecentFiles is null ? null : new List<string>(source.RecentFiles);
    }

    private void OnThemeButtonClick(object sender, RoutedEventArgs e) => SetTheme(NextTheme(App.Settings.Theme));

    private void SetTheme(AppTheme theme)
    {
        App.Settings.Theme = theme;
        App.Settings.Save();
        ThemeManager.Set(theme);
        UpdateThemeIcon();
    }

    private void UpdateUndoRedo()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        UndoButton.ToolTip = _history.UndoName ?? "Nothing to undo";
        RedoButton.ToolTip = _history.RedoName ?? "Nothing to redo";
        UndoMenuItem.IsEnabled = _history.CanUndo;
        RedoMenuItem.IsEnabled = _history.CanRedo;
    }

    // --- preview controls -----------------------------------------------------------------

    private const int VolumeModeRgb = 0;
    private const int VolumeModeAlpha = 1;
    private const int VolumeModeMip = 2;

    private void OnMipChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreview) return;
        _mipIndex = Math.Max(0, MipCombo.SelectedIndex);
        if (_selected is not null)
        {
            UpdateSliceControls(_selected, reset: true);
            UpdatePreview(_selected);
            UpdateAnimationControls(_selected, reset: true);
            UpdateValidation(_selected);
        }
    }

    private void OnChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreview) return;
        Preview2D.Channel = ChannelCombo.SelectedIndex switch
        {
            1 => ImageChannelMode.Red,
            2 => ImageChannelMode.Green,
            3 => ImageChannelMode.Blue,
            4 => ImageChannelMode.Alpha,
            _ => ImageChannelMode.Rgba,
        };
        GlView.Channel = Preview2D.Channel;
        SyncPreviewContextMenu();
    }

    private void OnOpaqueChanged(object sender, RoutedEventArgs e) =>
        ApplyOpaque(OpaqueCheck.IsChecked == true, persist: true);

    private void ApplyOpaque(bool on, bool persist)
    {
        Preview2D.OpaqueAlpha = on;
        GlView.OpaqueAlpha = on;
        if (OpaqueCheck.IsChecked != on) OpaqueCheck.IsChecked = on;
        if (persist)
        {
            App.Settings.OpaqueAlphaByDefault = on;
            App.Settings.Save();
        }

        // "Opaque" also picks the volume mode: RGB luminance instead of the stored alpha.
        if (!_suppressPreview && VolumeModeCombo.SelectedIndex != VolumeModeMip)
        {
            _suppressPreview = true;
            try
            {
                VolumeModeCombo.SelectedIndex = on ? VolumeModeRgb : VolumeModeAlpha;
            }
            finally
            {
                _suppressPreview = false;
            }
        }

        SyncPreviewContextMenu();
    }

    /// <summary>Fit the 2D preview and reset the 3D camera.</summary>
    private void OnResetView(object sender, RoutedEventArgs e)
    {
        Preview2D.FitToWindow();
        GlView.ResetCamera();
        StatusLeft.Text = "Reset the view.";
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void UpdateZoomReadout()
    {
        // The readout belongs to the tab on screen: the 3D viewport has no zoom factor of its own.
        if (ViewTabs.SelectedIndex == 1)
        {
            StatusZoom.Text = "3D";
            return;
        }

        StatusZoom.Text = Preview2D.IsEmpty ? string.Empty : $"{Preview2D.Zoom * 100:0}%";
    }

    // --- preview context menu -------------------------------------------------------------

    private MenuItem _ctxChannelRgba = null!;
    private MenuItem _ctxChannelRed = null!;
    private MenuItem _ctxChannelGreen = null!;
    private MenuItem _ctxChannelBlue = null!;
    private MenuItem _ctxChannelAlpha = null!;
    private MenuItem _ctxOpaque = null!;
    private MenuItem _ctxChecker = null!;

    /// <summary>
    /// The 2D view's right-click menu. Built in code because the view is a separate name scope, so
    /// x:Name inside its XAML would not be reachable from the window.
    /// </summary>
    private void BuildPreviewContextMenu()
    {
        _ctxChannelRgba = ChannelItem("RGBA", "Rgba", true);
        _ctxChannelRed = ChannelItem("Red", "Red");
        _ctxChannelGreen = ChannelItem("Green", "Green");
        _ctxChannelBlue = ChannelItem("Blue", "Blue");
        _ctxChannelAlpha = ChannelItem("Alpha", "Alpha");

        var channel = new MenuItem { Header = "Channel" };
        channel.Items.Add(_ctxChannelRgba);
        channel.Items.Add(_ctxChannelRed);
        channel.Items.Add(_ctxChannelGreen);
        channel.Items.Add(_ctxChannelBlue);
        channel.Items.Add(_ctxChannelAlpha);

        _ctxOpaque = new MenuItem { Header = "Opaque Preview", IsCheckable = true };
        _ctxOpaque.Click += (_, _) => ApplyOpaque(_ctxOpaque.IsChecked, persist: true);

        _ctxChecker = new MenuItem { Header = "Show checkerboard", IsCheckable = true, IsChecked = true };
        _ctxChecker.Click += (_, _) => Preview2D.ShowCheckerboard = _ctxChecker.IsChecked;

        var fit = new MenuItem { Header = "Fit to view" };
        fit.Click += (_, _) => Preview2D.FitToWindow();

        var actual = new MenuItem { Header = "Actual size (100%)" };
        actual.Click += (_, _) => Preview2D.ActualSize();

        var copy = new MenuItem { Header = "Copy image" };
        copy.Click += OnPreviewCopyImage;

        var separator = (Style)FindResource("MenuSeparator");
        var menu = new ContextMenu();
        menu.Items.Add(fit);
        menu.Items.Add(actual);
        menu.Items.Add(new Separator { Style = separator });
        menu.Items.Add(channel);
        menu.Items.Add(_ctxOpaque);
        menu.Items.Add(_ctxChecker);
        menu.Items.Add(new Separator { Style = separator });
        menu.Items.Add(copy);

        Preview2D.ContextMenu = menu;
    }

    private MenuItem ChannelItem(string header, string tag, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked, Tag = tag };
        item.Click += OnPreviewChannel;
        return item;
    }

    private void OnPreviewChannel(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        int index = tag switch
        {
            "Red" => 1,
            "Green" => 2,
            "Blue" => 3,
            "Alpha" => 4,
            _ => 0,
        };
        if (index >= ChannelCombo.Items.Count) index = 0;
        ChannelCombo.SelectedIndex = index;
    }

    private void OnPreviewCopyImage(object sender, RoutedEventArgs e)
    {
        if (_previewImage is null)
        {
            StatusLeft.Text = "There is no decoded image to copy.";
            return;
        }

        try
        {
            Clipboard.SetImage(ToBitmapSource(_previewImage));
            StatusLeft.Text = $"Copied texture #{_selected?.Index} to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Could not copy the image: " + ex.Message;
        }
    }

    /// <summary>Keep the preview context menu ticks in step with the toolbar controls.</summary>
    private void SyncPreviewContextMenu()
    {
        _ctxChannelRgba.IsChecked = Preview2D.Channel == ImageChannelMode.Rgba;
        _ctxChannelRed.IsChecked = Preview2D.Channel == ImageChannelMode.Red;
        _ctxChannelGreen.IsChecked = Preview2D.Channel == ImageChannelMode.Green;
        _ctxChannelBlue.IsChecked = Preview2D.Channel == ImageChannelMode.Blue;
        _ctxChannelAlpha.IsChecked = Preview2D.Channel == ImageChannelMode.Alpha;
        _ctxChannelAlpha.IsEnabled = App.Settings.ShowAlphaChannel;
        _ctxOpaque.IsChecked = Preview2D.OpaqueAlpha;
    }

    // --- volume controls ------------------------------------------------------------------

    private void OnSliceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreview || _suppressAnim || _selected is null) return;
        if (!_selected.Header.Type.Is3D()) return;

        // The combo lists every layer; a header edit can change the count before the combo is
        // rebuilt, so the current layout value is authoritative for clamping.
        SetSlice(SliceCombo.SelectedIndex, CurrentSliceCount(_selected));
        StatusLeft.Text = $"Volume slice {_sliceIndex + 1} of {CurrentSliceCount(_selected)}";
    }

    /// <summary>
    /// Map the slice combo index onto the viewport's 0..1 cross-section position. The plane only
    /// exists in the 3D viewport, so nothing is pushed while the 2D tab is showing; otherwise every
    /// animation frame would invalidate the volume render off screen.
    /// </summary>
    private void ApplySliceZ()
    {
        if (ViewTabs.SelectedIndex != 1) return;

        int count = SliceCombo.Items.Count;
        if (count > 1) GlView.SliceZ = _sliceIndex / (float)(count - 1);
    }

    // --- layer animation ------------------------------------------------------------------

    /// <summary>Number of layers (depth slices) at the current mip; 1 for a flat texture.</summary>
    private int CurrentSliceCount(EditableTexture texture)
    {
        var layout = texture.Layout;
        return layout.Applicable && _mipIndex >= 0 && _mipIndex < layout.Mips.Count
            ? Math.Max(1, layout.Mips[_mipIndex].SliceCount)
            : 1;
    }

    /// <summary>A multilayer (3D) texture whose layers can be read as animation frames.</summary>
    private static bool CanAnimate(EditableTexture texture) => texture.Header.Type.Is3D();

    /// <summary>
    /// Frames actually playable: the layer count capped to the depth the viewport composed. A volume
    /// deeper than the preview cap is truncated for the GPU, so playback stops at the last held layer
    /// instead of decoding past it on every tick.
    /// </summary>
    private int AnimationFrameCount(EditableTexture texture)
    {
        int count = CurrentSliceCount(texture);
        if (GlView.VolumeInfo is { } volume) count = Math.Min(count, volume.Depth);
        return Math.Max(1, count);
    }

    /// <summary>
    /// Show the 2D playback bar only for a multilayer (3D) texture; a regular 2D texture or a
    /// cubemap hides it entirely. <paramref name="reset"/> stops playback when the selection changes.
    /// </summary>
    private void UpdateAnimationControls(EditableTexture? texture, bool reset = false)
    {
        int count = texture is not null && CanAnimate(texture) ? AnimationFrameCount(texture) : 0;
        bool show = count > 1;

        if (reset) SetAnimationPlaying(false);
        AnimBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (!show)
        {
            SetAnimationPlaying(false);
            _suppressAnim = true;
            try
            {
                AnimSlider.Maximum = 1;
                AnimSlider.Value = 0;
            }
            finally
            {
                _suppressAnim = false;
            }
            AnimFrameText.Text = string.Empty;
            return;
        }

        int clamped = Math.Clamp(_sliceIndex, 0, count - 1);
        bool moved = clamped != _sliceIndex;
        _sliceIndex = clamped;

        _suppressAnim = true;
        try
        {
            AnimSlider.Maximum = count - 1;
            if (AnimSlider.Value != _sliceIndex) AnimSlider.Value = _sliceIndex;
        }
        finally
        {
            _suppressAnim = false;
        }
        UpdateAnimFrameText(count);

        // The image may have been decoded at a now-clamped frame; refresh it to match the bar.
        if (moved && texture is not null) UpdateVolumeSliceImage(texture);
    }

    private void OnAnimPlayToggle(object sender, RoutedEventArgs e)
    {
        SetAnimationPlaying(!_animPlaying);
        StatusLeft.Text = _animPlaying && _selected is not null
            ? $"Playing {AnimationFrameCount(_selected)} layers at {AnimationFps():0.##} fps."
            : "Animation paused.";
    }

    private void SetAnimationPlaying(bool playing)
    {
        bool allowed = playing && _selected is not null && CanAnimate(_selected)
                       && AnimationFrameCount(_selected) > 1;
        _animPlaying = allowed;

        if (allowed)
        {
            _animTimer.Interval = AnimationInterval();
            _animTimer.Start();
        }
        else
        {
            _animTimer.Stop();
        }

        UpdateAnimPlayButton();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (!_animPlaying || _selected is null) return;

        int count = AnimationFrameCount(_selected);
        if (count <= 1)
        {
            SetAnimationPlaying(false);
            return;
        }

        SetSlice(_sliceIndex + 1 >= count ? 0 : _sliceIndex + 1, count);
    }

    private void OnAnimSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressAnim || _selected is null) return;

        // Scrubbing is a deliberate look at one layer, so it pauses playback.
        SetAnimationPlaying(false);
        int count = AnimationFrameCount(_selected);
        SetSlice((int)Math.Round(e.NewValue), count);
        StatusLeft.Text = $"Layer {_sliceIndex + 1} of {count}";
    }

    /// <summary>Move to a layer, keeping the 2D image, the slice combo and the time bar in step.</summary>
    private void SetSlice(int index, int count)
    {
        if (_selected is null) return;

        count = Math.Max(1, count);
        _sliceIndex = Math.Clamp(index, 0, count - 1);

        _suppressAnim = true;
        try
        {
            int sliderMax = Math.Max(1, count - 1);
            if (AnimSlider.Maximum != sliderMax) AnimSlider.Maximum = sliderMax;
            if (AnimSlider.Value != _sliceIndex) AnimSlider.Value = _sliceIndex;
            if (SliceCombo.Items.Count == count && SliceCombo.SelectedIndex != _sliceIndex)
            {
                _suppressPreview = true;
                try
                {
                    SliceCombo.SelectedIndex = _sliceIndex;
                }
                finally
                {
                    _suppressPreview = false;
                }
            }
        }
        finally
        {
            _suppressAnim = false;
        }

        ApplySliceZ();
        // The 2D pass is only needed while its tab is on screen; playback can continue on the 3D tab.
        if (ViewTabs.SelectedIndex == 0) UpdateVolumeSliceImage(_selected);
        UpdateAnimFrameText(count);
    }

    private void UpdateAnimFrameText(int count) =>
        AnimFrameText.Text = count > 0 ? $"{_sliceIndex + 1} / {count}" : string.Empty;

    private void UpdateAnimPlayButton()
    {
        AnimPlayIcon.Text = _animPlaying ? "\uE769" : "\uE768";
        AnimPlayText.Text = _animPlaying ? "Pause" : "Play";
    }

    private void OnFpsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitAnimationFps();
        e.Handled = true;
    }

    private void OnFpsChanged(object sender, RoutedEventArgs e) => CommitAnimationFps();

    /// <summary>Clamp the FPS box to a usable range and retime a running animation.</summary>
    private void CommitAnimationFps()
    {
        FpsBox.Text = AnimationFps().ToString("0.##", CultureInfo.InvariantCulture);
        if (_animPlaying) _animTimer.Interval = AnimationInterval();
    }

    /// <summary>Playback speed; the field defaults to 15 fps and is clamped to 1 - 240.</summary>
    private double AnimationFps()
    {
        // Match ClampNumber: accept the local decimal separator first, so "12,5" works on a
        // comma-decimal locale instead of silently resetting to 15.
        bool parsed = double.TryParse(FpsBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var fps)
                      || double.TryParse(FpsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);
        if (parsed && !double.IsNaN(fps)) return Math.Clamp(fps, 1, 240);
        return 15;
    }

    private TimeSpan AnimationInterval() => TimeSpan.FromMilliseconds(1000.0 / AnimationFps());

    private void OnVolumeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreview) return;
        GlView.RenderMode = VolumeModeCombo.SelectedIndex switch
        {
            VolumeModeAlpha => VolumeRenderMode.Alpha,
            VolumeModeMip => VolumeRenderMode.Mip,
            _ => VolumeRenderMode.Rgb,
        };
        StatusLeft.Text = "Volume render mode: " + (VolumeModeCombo.SelectedItem as string ?? "RGB");
    }

    private void OnShowSlicesChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPreview) return;
        GlView.ShowSlices = ShowSlicesCheck.IsChecked == true;
        StatusLeft.Text = ShowSlicesCheck.IsChecked == true
            ? "Cross-section planes on."
            : "Cross-section planes off.";
    }

    private void OnGammaExposureKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitGammaExposure();
        e.Handled = true;
    }

    private void OnGammaExposureChanged(object sender, RoutedEventArgs e) => CommitGammaExposure();

    /// <summary>
    /// Toggle the sky's auto exposure. It is independent of gamma/exposure, which are applied after it.
    /// </summary>
    private void OnAutoExposureChanged(object sender, RoutedEventArgs e)
    {
        bool on = AutoExposureCheck.IsChecked == true;
        GlView.AutoExposure = on;
        StatusLeft.Text = on ? "Sky auto exposure on." : "Sky auto exposure off.";
    }

    /// <summary>Clamp the volume gamma/exposure boxes to the WinForms numeric ranges.</summary>
    private void CommitGammaExposure()
    {
        GammaBox.Text = ClampNumber(GammaBox.Text, 0.10, 8.00, "1.00");
        ExposureBox.Text = ClampNumber(ExposureBox.Text, 0.05, 20.00, "1.00");
        GlView.Gamma = ParseFloat(GammaBox.Text, 1f);
        GlView.Exposure = ParseFloat(ExposureBox.Text, 1f);
    }

    private static float ParseFloat(string? text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string ClampNumber(string? text, double min, double max, string fallback)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return fallback;

        value = Math.Clamp(value, min, max);
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    // --- documents ----------------------------------------------------------------------

    private static int TextureCount(BankDocument document) =>
        document.Sections.Sum(s => s.Bank.Count) + (document.RawXt1 is null ? 0 : 1);

    private void LoadDocument(string path)
    {
        BankDocument document;
        try
        {
            document = BankDocument.Load(path);
        }
        catch (Exception ex)
        {
            StatusLeft.Text = $"Failed to open {Path.GetFileName(path)}: {ex.Message}";
            return;
        }

        AdoptDocument(document);
        App.Settings.LastDirectory = Path.GetDirectoryName(path);
        RecordRecent(path); // also persists LastDirectory with the updated history

        StatusLeft.Text = document.Warnings.Count == 0
            ? $"Loaded {document.DisplayName}"
            : $"Loaded {document.DisplayName} - {string.Join("; ", document.Warnings)}";
    }

    /// <summary>Make a freshly loaded or created document the active one and rebuild the UI around it.</summary>
    private void AdoptDocument(BankDocument document)
    {
        _document = document;
        _rawTexture = document.RawXt1 is { } raw ? EditableTexture.FromRawReadOnly(raw) : null;
        _history.Clear();
        _selected = null;
        _gridSelection = (null, null);
        _fileRows = null;
        _fileListFolder = null;

        // Thumbnails belong to the previous document's textures; drop them and any pending decodes.
        _thumbCts?.Cancel();
        _thumbCache.Clear();

        BuildTree();
        RefreshFileList();
        if (_assetLevel != AssetViewLevel.List) BuildAssetGrid();

        StatusRight.Text = $"{document.Kind} \u00B7 {TextureCount(document)} textures";
    }

    private void BuildTree()
    {
        DocumentTree.Items.Clear();
        if (_document is null) return;

        // The hierarchy, grouping and labels come from the shared core outline (container -> bank ->
        // texture), so the tree matches the WinForms app and stays unit-testable.
        var outline = BankOutline.Build(_document, _rawTexture);
        var root = BuildTreeNode(outline);
        DocumentTree.Items.Add(root);

        if (FindFirstTexture(root) is { } first) first.IsSelected = true;
        else ShowTexture(null, null);
    }

    private TreeViewItem BuildTreeNode(BankOutlineNode node)
    {
        var item = new TreeViewItem
        {
            Header = NodeHeader(GlyphFor(node.Kind), node.Label),
            IsExpanded = true,
            ToolTip = TooltipFor(node),
        };

        if (node.Texture is not null) item.Tag = new TextureRef(node.Texture, node.Section);
        else if (node.Section is not null) item.Tag = node.Section;

        foreach (var child in node.Children) item.Items.Add(BuildTreeNode(child));
        return item;
    }

    private static string GlyphFor(OutlineKind kind) => kind switch
    {
        OutlineKind.Document => "\uE8B7",
        OutlineKind.SubFile => "\uE8A5",
        _ => "\uE91B",
    };

    private string TooltipFor(BankOutlineNode node) => node.Kind switch
    {
        OutlineKind.Document => _document?.FilePath ?? "(untitled)",
        OutlineKind.Texture when node.Texture is not null => TextureTooltip(node.Texture),
        _ => node.Label,
    };

    private static TreeViewItem? FindFirstTexture(TreeViewItem node)
    {
        if (node.Tag is TextureRef) return node;
        foreach (var child in node.Items.OfType<TreeViewItem>())
        {
            if (FindFirstTexture(child) is { } match) return match;
        }
        return null;
    }

    private void RefreshFileList(bool rescan = true)
    {
        if (_document?.FilePath is not { } active)
        {
            FileList.ItemsSource = null;
            _fileRows = null;
            _fileListFolder = null;
            return;
        }

        var folder = Path.GetDirectoryName(active);

        // A pure re-sort (column header click) reuses the rows already known for this folder:
        // enumerating the directory and FileInfo-ing every sibling again would be wasted work.
        if (!rescan && _fileRows is not null
            && string.Equals(_fileListFolder, folder, StringComparison.OrdinalIgnoreCase))
        {
            SortFileRows(_fileRows);
            _suppressFileSelection = true;
            FileList.ItemsSource = null;
            FileList.ItemsSource = _fileRows;
            FileList.SelectedItem = _fileRows.FirstOrDefault(r => SamePath(r.Path, active));
            _suppressFileSelection = false;
            UpdateFileColumnHeaders();
            return;
        }

        var rows = new List<FileRow>();
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    var extension = Path.GetExtension(path);
                    if (OpenableExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
                        rows.Add(ToRow(path, SamePath(path, active)));
                }
            }
            catch
            {
                // Unreadable folder: fall back to the active file only.
            }
        }

        if (!rows.Any(r => SamePath(r.Path, active))) rows.Add(ToRow(active, true));
        SortFileRows(rows);

        // Switching between siblings keeps the same folder and file set: re-bold in place rather
        // than replacing ItemsSource, which would flash and churn the selection.
        if (_fileRows is not null && string.Equals(_fileListFolder, folder, StringComparison.OrdinalIgnoreCase)
            && SameFileSet(_fileRows, rows))
        {
            foreach (var row in _fileRows)
                row.Weight = SamePath(row.Path, active) ? FontWeights.Bold : FontWeights.Normal;

            _suppressFileSelection = true;
            FileList.SelectedItem = _fileRows.FirstOrDefault(r => SamePath(r.Path, active));
            _suppressFileSelection = false;
            UpdateFileColumnHeaders();
            return;
        }

        _fileRows = rows;
        _fileListFolder = folder;
        _suppressFileSelection = true;
        FileList.ItemsSource = rows;
        FileList.SelectedItem = rows.FirstOrDefault(r => SamePath(r.Path, active));
        _suppressFileSelection = false;
        UpdateFileColumnHeaders();
    }

    private static bool SameFileSet(List<FileRow> a, List<FileRow> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Path, b[i].Path, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void SortFileRows(List<FileRow> rows)
    {
        int Compare(FileRow a, FileRow b)
        {
            int result = _fileSortColumn switch
            {
                "modified" => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
                "size" => a.SizeBytes.CompareTo(b.SizeBytes),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            };
            if (result == 0) result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return _fileSortAscending ? result : -result;
        }

        rows.Sort(Compare);
    }

    private void OnFileColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        SortByColumn(header.Column);
    }

    /// <summary>
    /// Sort by a column, reversing when it is already the sort column, and refresh the list without
    /// re-enumerating the folder. The header click and the test hooks both come through here, so the
    /// mapping from a column to its sort key has a single implementation.
    /// </summary>
    private void SortByColumn(GridViewColumn? column)
    {
        string key = column == FileColumnModified ? "modified"
            : column == FileColumnSize ? "size"
            : "name";

        if (string.Equals(_fileSortColumn, key, StringComparison.Ordinal))
        {
            _fileSortAscending = !_fileSortAscending;
        }
        else
        {
            _fileSortColumn = key;
            _fileSortAscending = true;
        }

        // Sorting only reorders what is already listed; the folder does not need re-enumerating.
        RefreshFileList(rescan: false);
    }

    private void UpdateFileColumnHeaders()
    {
        var indicator = _fileSortAscending ? " \u25B2" : " \u25BC";
        FileColumnName.Header = _fileSortColumn == "name" ? "Name" + indicator : "Name";
        FileColumnModified.Header = _fileSortColumn == "modified" ? "Modified" + indicator : "Modified";
        FileColumnSize.Header = _fileSortColumn == "size" ? "Size" + indicator : "Size";
    }

    private static FileRow ToRow(string path, bool active)
    {
        var info = new FileInfo(path);
        bool exists = info.Exists;
        return new FileRow
        {
            Name = info.Name,
            Modified = exists ? info.LastWriteTime.ToString("yyyy-MM-dd") : "--",
            Size = exists ? ByteSize.Format(info.Length) : "--",
            Path = path,
            Weight = active ? FontWeights.Bold : FontWeights.Normal,
            ModifiedUtc = exists ? info.LastWriteTimeUtc : DateTime.MinValue,
            SizeBytes = exists ? info.Length : 0,
        };
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // An invalid path can never be the same as another one.
            return false;
        }
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFileSelection) return;
        if (FileList.SelectedItem is not FileRow row) return;
        if (_document?.FilePath is { } active && SamePath(active, row.Path)) return;
        LoadDocument(row.Path);
    }

    // --- file browser context menu ------------------------------------------------------

    private void OnFilePreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { } item)
            item.IsSelected = true;
    }

    private void OnFileMenuOpened(object sender, RoutedEventArgs e)
    {
        bool has = FileList.SelectedItem is FileRow;
        FileMenuOpen.IsEnabled = has;
        FileMenuReload.IsEnabled = has;
        FileMenuCopyPath.IsEnabled = has;
        FileMenuReveal.IsEnabled = has;
    }

    private void OnFileMenuOpen(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileRow row) LoadDocument(row.Path);
    }

    private void OnFileMenuReload(object sender, RoutedEventArgs e) => RefreshFileList();

    private void OnFileMenuCopyPath(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row) return;
        try
        {
            Clipboard.SetText(row.Path);
            StatusLeft.Text = "Copied " + row.Path;
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Could not copy the path: " + ex.Message;
        }
    }

    /// <summary>
    /// Open Explorer with the given file selected. Uses the shell API rather than building an
    /// <c>explorer.exe /select,"…"</c> command line, so a file name can never inject extra arguments
    /// (a path is passed as a value, not as parsed command-line text).
    /// </summary>
    private void OnFileMenuReveal(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row) return;

        IntPtr item = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(row.Path, IntPtr.Zero, out item, 0, out _) != 0 || item == IntPtr.Zero)
            {
                StatusLeft.Text = "Could not locate that file in Explorer.";
                return;
            }

            // A null folder pidl means the child id list must be absolute, which is what parsing the
            // full path gives us.
            int hr = SHOpenFolderAndSelectItems(IntPtr.Zero, 1, new[] { item }, 0);
            if (hr != 0) StatusLeft.Text = $"Could not open Explorer (0x{hr:X8}).";
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Could not open Explorer: " + ex.Message;
        }
        finally
        {
            if (item != IntPtr.Zero) ILFree(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl,
        uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
    // --- tree / inspector ---------------------------------------------------------------

    private static StackPanel NodeHeader(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        // A live resource reference, so the glyph follows a theme switch without a tree rebuild.
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");

        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static string TextureTooltip(EditableTexture texture)
    {
        var info = texture.FormatInfo;
        var header = texture.Header;
        string format = info?.Name ?? "unknown format";
        return $"{format} \u00B7 {header.Width}\u00D7{header.Height} \u00B7 {header.MipCount} mip(s) " +
               $"\u00B7 hash 0x{texture.EffectiveHash():X8}";
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: TextureRef reference })
        {
            // Keep the grid's target in step, so switching views keeps the same asset selected.
            _gridSelection = (reference.Texture, reference.Section);
            ShowTexture(reference.Texture, reference.Section);
        }
        else if (e.NewValue is TreeViewItem { Tag: BankSection section })
        {
            _gridSelection = (null, section);
        }
    }

    /// <summary>The texture/section a tree context-menu action targets.</summary>
    private (EditableTexture? Texture, BankSection? Section) SelectedTreeTarget()
    {
        if (DocumentTree.SelectedItem is TreeViewItem item)
        {
            if (item.Tag is TextureRef reference) return (reference.Texture, reference.Section);
            if (item.Tag is BankSection section) return (null, section);
        }
        return (null, null);
    }

    /// <summary>
    /// The bank an action applies to: the selected bank node, or the first bank when nothing (or the
    /// document root) is selected — a brand-new empty bank has no texture node to select, but must
    /// still be fillable. Both the menu's enabled state and the actions themselves use this, so an
    /// offered item always works.
    /// </summary>
    private BankSection? TargetBank() =>
        SelectedAssetTarget().Section ?? _document?.Sections.FirstOrDefault();

    /// <summary>
    /// The asset a menu action targets: the tree selection in List mode, or the clicked tile in grid
    /// mode. The two views share one context menu, so this is the single source of truth for it.
    /// </summary>
    private (EditableTexture? Texture, BankSection? Section) SelectedAssetTarget() =>
        _assetLevel == AssetViewLevel.List ? SelectedTreeTarget() : _gridSelection;

    private void OnTreePreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
            item.IsSelected = true;
    }

    private void OnTreeMenuOpened(object sender, RoutedEventArgs e) => UpdateTreeMenuState();

    /// <summary>Enable the tree menu entries for the current selection (also used by the test hooks).</summary>
    private void UpdateTreeMenuState()
    {
        var (texture, _) = SelectedAssetTarget();
        var section = TargetBank();

        bool hasTexture = texture is not null;
        var info = texture?.FormatInfo;
        bool canDecode = TextureCodec.CanDecode(info);
        bool canExportDds = info is not null && DdsIo.CanExport(info);
        bool bankHasTextures = section is not null && section.Bank.Textures.Count > 0;

        TreeMenuReplace.IsEnabled = hasTexture;
        TreeMenuReplaceXt1.IsEnabled = hasTexture;
        TreeMenuAddTexture.IsEnabled = section is not null;
        TreeMenuExportGroup.IsEnabled = hasTexture;
        TreeMenuExportPng.IsEnabled = hasTexture && canDecode;
        TreeMenuExportDds.IsEnabled = hasTexture && canExportDds;
        TreeMenuExportXt1.IsEnabled = hasTexture;
        TreeMenuDelete.IsEnabled = hasTexture && section is not null;
        TreeMenuCopyHash.IsEnabled = hasTexture;
        TreeMenuDummy.IsEnabled = hasTexture;
        TreeMenuExportBank.IsEnabled = bankHasTextures;
        TreeMenuBatchReplace.IsEnabled = bankHasTextures;
    }

    // ---------------------------------------------------------------------------------------------
    // Asset Browser zoom: the List tree and a grid of texture thumbnails.
    // ---------------------------------------------------------------------------------------------

    private static readonly AssetViewLevel[] AssetLevels =
    {
        AssetViewLevel.List,
        AssetViewLevel.Grid64,
        AssetViewLevel.Grid96,
        AssetViewLevel.Grid128,
        AssetViewLevel.Grid192,
    };

    private static int GridTileSize(AssetViewLevel level) => level switch
    {
        AssetViewLevel.Grid64 => 64,
        AssetViewLevel.Grid96 => 96,
        AssetViewLevel.Grid128 => 128,
        AssetViewLevel.Grid192 => 192,
        _ => 0,
    };

    private static string LevelLabel(AssetViewLevel level)
    {
        int size = GridTileSize(level);
        return size == 0 ? "List" : $"{size} px";
    }

    /// <summary>Ctrl+wheel steps the zoom; a plain wheel keeps scrolling the tree/grid.</summary>
    private void OnAssetWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        int index = Array.IndexOf(AssetLevels, _assetLevel);
        index = Math.Clamp(index + (e.Delta > 0 ? 1 : -1), 0, AssetLevels.Length - 1);
        SetAssetLevel(AssetLevels[index]);
        e.Handled = true;
    }

    private void OnAssetZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressZoom || !IsLoaded) return;
        int index = Math.Clamp((int)Math.Round(e.NewValue), 0, AssetLevels.Length - 1);
        SetAssetLevel(AssetLevels[index]);
    }

    /// <summary>Switch the Asset Browser between the list tree and a grid of thumbnail tiles.</summary>
    private void SetAssetLevel(AssetViewLevel level, bool persist = true, bool rebuild = true)
    {
        bool fromList = _assetLevel == AssetViewLevel.List;
        _assetLevel = level;

        _suppressZoom = true;
        AssetZoomSlider.Value = Array.IndexOf(AssetLevels, level);
        _suppressZoom = false;
        AssetZoomLabel.Text = LevelLabel(level);

        bool grid = level != AssetViewLevel.List;
        if (grid && fromList)
        {
            // Carry the tree selection only when entering the grid; a grid->grid zoom must keep the
            // tile that is already selected (the collapsed tree still holds the old node).
            _gridSelection = SelectedTreeTarget();
        }
        else if (!grid && _gridSelection.Texture is not null && FindTextureNode(_gridSelection.Texture) is { } node)
        {
            node.IsSelected = true;
        }

        DocumentTree.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        AssetGridScroll.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        if (grid && rebuild)
        {
            BuildAssetGrid();
        }
        else if (!grid)
        {
            // Drop the tiles (and any pending thumbnail decodes) while the tree is shown.
            _thumbCts?.Cancel();
            _pendingThumbs.Clear();
            AssetGridPanel.Children.Clear();
            ClearSelectedTile();
        }
        UpdateTreeMenuState();

        if (persist)
        {
            App.Settings.AssetBrowserLevel = level;
            App.Settings.Save();
        }
    }

    /// <summary>
    /// Rebuild the grid from the same outline the tree uses: a bank heading per bank, then a tile per
    /// texture with its preview and <c>tex&lt;index&gt;_0x&lt;hash&gt;</c> caption.
    /// </summary>
    private void BuildAssetGrid()
    {
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();

        AssetGridPanel.Children.Clear();
        _pendingThumbs.Clear();
        ClearSelectedTile();

        int size = GridTileSize(_assetLevel);
        if (_document is null || size == 0) return;

        var outline = BankOutline.Build(_document, _rawTexture);
        foreach (var bankNode in outline.Children)
        {
            if (bankNode.Kind == OutlineKind.SubFile)
            {
                var heading = new TextBlock
                {
                    Text = BankHeading(bankNode),
                    Margin = new Thickness(0, AssetGridPanel.Children.Count == 0 ? 2 : 14, 0, 8),
                };
                heading.SetResourceReference(FrameworkElement.StyleProperty, "SectionHeader");
                AssetGridPanel.Children.Add(heading);
            }

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var child in bankNode.Children)
            {
                if (child.Texture is null) continue;
                wrap.Children.Add(CreateTextureTile(child.Texture, child.Section, size));
            }
            AssetGridPanel.Children.Add(wrap);
        }

        HighlightGridSelection();

        // Decode only what is near the viewport, now and after each scroll.
        AssetGridScroll.UpdateLayout();
        ScheduleVisibleThumbs();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScheduleVisibleThumbs));
    }

    /// <summary>
    /// Start thumbnail decodes for tiles near the visible area only. Keeps decode work and retained
    /// bitmaps proportional to the viewport rather than the whole document; pending tiles are decoded
    /// as they scroll in, and dropped (with their placeholder) when the grid is rebuilt.
    /// </summary>
    private void ScheduleVisibleThumbs()
    {
        if (_pendingThumbs.Count == 0) return;

        AssetGridScroll.UpdateLayout();
        double viewport = AssetGridScroll.ViewportHeight;
        double viewTop = viewport > 0 ? AssetGridScroll.VerticalOffset : double.NegativeInfinity;
        double viewBottom = viewport > 0 ? viewTop + viewport : double.PositiveInfinity;
        double margin = Math.Max(200, viewport);
        var token = _thumbCts?.Token ?? CancellationToken.None;

        for (int i = _pendingThumbs.Count - 1; i >= 0; i--)
        {
            var pending = _pendingThumbs[i];
            double y;
            try
            {
                y = pending.Tile.TransformToAncestor(AssetGridPanel).Transform(new Point(0, 0)).Y;
            }
            catch (InvalidOperationException)
            {
                continue; // not attached yet; the Loaded-priority pass will retry
            }

            if (y > viewBottom + margin || y + pending.Tile.ActualHeight < viewTop - margin) continue;

            _pendingThumbs.RemoveAt(i);
            _ = LoadThumbnailAsync(pending.Image, pending.Placeholder, pending.Texture, pending.Size, token);
        }
    }

    private void OnAssetGridScroll(object sender, ScrollChangedEventArgs e) => ScheduleVisibleThumbs();

    /// <summary>A single-bank document names the bank by its file; multi-bank containers use the label.</summary>
    private string BankHeading(BankOutlineNode bankNode) =>
        _document is not null && _document.Sections.Count <= 1 ? _document.DisplayName : bankNode.Label;

    private Border CreateTextureTile(EditableTexture texture, BankSection? section, int size)
    {
        string captionText = $"tex{texture.Index}_0x{texture.EffectiveHash():X8}";

        var placeholder = new TextBlock
        {
            Text = "\uE91B",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = Math.Max(18, size / 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");

        var host = new Grid { Width = size, Height = size };
        host.Children.Add(placeholder);
        var image = new Image { Stretch = Stretch.Uniform };
        host.Children.Add(image);

        var caption = new TextBlock
        {
            Text = captionText,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11,
            Margin = new Thickness(4, 3, 4, 4),
            ToolTip = captionText,
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");

        var tile = new Border
        {
            Width = size,
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = new TextureRef(texture, section),
            ToolTip = TextureTooltip(texture),
            Child = new StackPanel { Children = { host, caption } },
        };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.Control");
        tile.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        // The tree's context menu operates through SelectedAssetTarget(), so the tiles reuse the very
        // same menu instance rather than a duplicate that could drift.
        tile.ContextMenu = DocumentTree.ContextMenu;
        tile.PreviewMouseLeftButtonDown += OnTileMouseDown;
        tile.PreviewMouseRightButtonDown += OnTileMouseDown;

        // Decoding is deferred until the tile nears the viewport (see ScheduleVisibleThumbs), so a
        // large bank does not decode every thumbnail up front.
        _pendingThumbs.Add(new PendingThumb(tile, image, placeholder, texture, size));
        return tile;
    }

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: TextureRef reference } tile) return;
        SelectTile(tile);
        _gridSelection = (reference.Texture, reference.Section);
        ShowTexture(reference.Texture, reference.Section);
        UpdateTreeMenuState();
    }

    private void SelectTile(Border tile)
    {
        if (ReferenceEquals(_selectedTile, tile)) return;
        ClearSelectedTile();
        _selectedTile = tile;
        tile.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.ControlHover");
    }

    private void ClearSelectedTile()
    {
        if (_selectedTile is null) return;
        _selectedTile.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _selectedTile.SetResourceReference(Border.BackgroundProperty, "Brush.Control");
        _selectedTile = null;
    }

    /// <summary>Re-highlight the tile matching the current selection after a grid rebuild.</summary>
    private void HighlightGridSelection()
    {
        if (_gridSelection.Texture is null) return;
        foreach (var tile in EnumerateTiles(AssetGridPanel))
        {
            if (tile.Tag is TextureRef reference && ReferenceEquals(reference.Texture, _gridSelection.Texture))
            {
                SelectTile(tile);
                return;
            }
        }

        // The selected texture is gone (for example it was deleted): drop it so the shared menu cannot
        // target a detached texture, while keeping the bank context for actions like "Add texture".
        _gridSelection = (null, _gridSelection.Section);
    }

    private static IEnumerable<Border> EnumerateTiles(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is Border border) yield return border;
            else if (child is Panel nested)
                foreach (var tile in EnumerateTiles(nested)) yield return tile;
        }
    }

    /// <summary>Decode the cheapest mip for a tile off the UI thread; the placeholder stays until it lands.</summary>
    private async Task LoadThumbnailAsync(Image image, UIElement placeholder, EditableTexture texture, int size,
        CancellationToken token)
    {
        var key = ThumbKeyFor(texture, size);
        if (!_thumbCache.TryGetValue(key, out var bitmap))
        {
            try
            {
                await _thumbGate.WaitAsync(token);
                try
                {
                    bitmap = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        var rgba = Thumbnailer.Render(texture, new ThumbnailOptions(size), out _, out _);
                        return rgba is null ? null : ToBitmapSource(rgba);
                    }, token);
                }
                finally
                {
                    _thumbGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                return;
            }

            if (bitmap is null) return;
            if (_thumbCache.Count >= ThumbCacheLimit) _thumbCache.Clear();
            _thumbCache[key] = bitmap;
        }

        if (token.IsCancellationRequested) return;
        image.Source = bitmap;
        placeholder.Visibility = Visibility.Collapsed;
    }

    private static ThumbKey ThumbKeyFor(EditableTexture texture, int size) =>
        new(texture, texture.Payload.GetHashCode(), PayloadStamp(texture.Payload),
            HeaderStamp(texture.Header), size);

    /// <summary>Cheap change stamp for a payload that does not retain the array itself.</summary>
    private static int PayloadStamp(byte[] payload)
    {
        unchecked
        {
            int hash = payload.Length;
            if (payload.Length >= 8)
            {
                hash = hash * 31 + BitConverter.ToInt32(payload, 0);
                hash = hash * 31 + BitConverter.ToInt32(payload, payload.Length - 4);
            }
            return hash;
        }
    }

    /// <summary>Cheap stamp of the header bytes, which decide the layout the payload is decoded with.</summary>
    private static int HeaderStamp(Xt1Header header)
    {
        var raw = header.Raw;
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < raw.Length; i++) hash = hash * 31 + raw[i];
            return hash;
        }
    }

    private string FileStem => Path.GetFileNameWithoutExtension(_document?.FilePath ?? "texture");

    private void OnTreeCopyHash(object sender, RoutedEventArgs e)
    {
        var (texture, _) = SelectedAssetTarget();
        if (texture is null) return;
        var text = "0x" + texture.EffectiveHash().ToString("X8", CultureInfo.InvariantCulture);
        try
        {
            Clipboard.SetText(text);
            StatusLeft.Text = $"Copied {text}.";
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Could not copy the hash: " + ex.Message;
        }
    }

    private void OnTreeExportPng(object sender, RoutedEventArgs e)
    {
        var (texture, _) = SelectedAssetTarget();
        if (texture is null) return;
        if (!TextureCodec.CanDecode(texture.FormatInfo))
        {
            StatusLeft.Text = "PNG export is not supported for this format.";
            return;
        }

        var image = TextureCodec.Decode(texture, 0, 0, 0, out var error);
        if (image is null)
        {
            StatusLeft.Text = error ?? "Decode failed.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = $"{FileStem}_{texture.Index}_mip0.png",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            ImageIo.Save(image, dialog.FileName);
            StatusLeft.Text = "Exported " + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "PNG export failed: " + ex.Message;
        }
    }

    private void OnTreeExportDds(object sender, RoutedEventArgs e)
    {
        var (texture, _) = SelectedAssetTarget();
        if (texture is null) return;
        var info = texture.FormatInfo;
        if (info is null || !DdsIo.CanExport(info))
        {
            StatusLeft.Text = $"DDS export is not supported for {info?.Name ?? "this format"} (use raw XT1 or PNG).";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "DDS texture (*.dds)|*.dds",
            FileName = $"{FileStem}_{texture.Index}.dds",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, DdsExport.Build(texture));
            StatusLeft.Text = "Exported " + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "DDS export failed: " + ex.Message;
        }
    }

    private void OnTreeExportXt1(object sender, RoutedEventArgs e)
    {
        var (texture, _) = SelectedAssetTarget();
        if (texture is null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Raw XT1 (*.xt1)|*.xt1",
            FileName = $"{FileStem}_{texture.Index}.xt1",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var raw = new RawXt1(texture.Header.Clone(), texture.Payload);
            File.WriteAllBytes(dialog.FileName, raw.ToBytes());
            StatusLeft.Text = "Exported " + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "XT1 export failed: " + ex.Message;
        }
    }

    private void OnTreeDelete(object sender, RoutedEventArgs e)
    {
        var (texture, section) = SelectedAssetTarget();
        if (texture is null || section is null) return;

        // Destructive and written on the next save, so confirm first like the WinForms shell did.
        if (!ConfirmDialog.Confirm(this, "Delete texture",
                $"Delete texture #{texture.Index}?", "Delete"))
        {
            StatusLeft.Text = "Delete cancelled.";
            return;
        }

        _history.Execute(TextureOperations.CreateDeleteTextureCommand(section, texture));
        RefreshAfterEdit();
        StatusLeft.Text = "Texture deleted.";
    }

    // --- importing -----------------------------------------------------------------------------

    private const string ImportFilter =
        "Images (*.png;*.bmp;*.jpg;*.jpeg;*.tga;*.dds)|*.png;*.bmp;*.jpg;*.jpeg;*.tga;*.dds|All files (*.*)|*.*";

    /// <summary>Replace the selected texture from an image file, or losslessly from a DDS.</summary>
    private void OnTreeReplace(object sender, RoutedEventArgs e)
    {
        var (texture, _) = SelectedAssetTarget();
        if (texture is null) return;

        var picker = new OpenFileDialog { Filter = ImportFilter, Title = "Choose an image or DDS to import" };
        if (picker.ShowDialog(this) != true) return;

        var fileName = Path.GetFileName(picker.FileName);
        try
        {
            // A DDS is already compressed: import it losslessly (surfaces copied, mips preserved)
            // instead of decoding and re-encoding through the import dialog.
            if (picker.FileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                DdsImage dds;
                try
                {
                    dds = DdsIo.Read(File.ReadAllBytes(picker.FileName));
                }
                catch (Exception ex)
                {
                    StatusLeft.Text = "Could not read the DDS: " + ex.Message;
                    return;
                }

                var replaced = TextureOperations.ReplaceWithDds(texture, dds, App.Settings.SyncFlagsFromFormat);
                if (!replaced.Success)
                {
                    RefreshAfterEdit(texture);
                    StatusLeft.Text = "Import failed: " + replaced.Error;
                    return;
                }

                PushPendingCommand();
                RefreshAfterEdit(texture);
                StatusLeft.Text = $"Imported {fileName} losslessly ({dds.MipCount} mip(s), {dds.Format?.Name}).";
                return;
            }

            var image = ImageIo.Load(picker.FileName);
            if (image is null)
            {
                StatusLeft.Text = "Could not read " + fileName;
                return;
            }

            var options = new TextureImportDialog(fileName, image,
                texture.FormatInfo ?? FormatTable.Get(Xt1Format.Bc7Unorm)!, EncodeQuality.Normal,
                generateMips: true, solidify: false, makeOpaque: false,
                visibleFormats: App.Settings.ImportFormats);
            options.Owner = this;
            if (options.ShowDialog() != true) return;

            var prepared = Solidify.Process(image, options.Solidify, options.MakeOpaque);

            // Captured before the format is switched: the undo snapshot inside the operation must
            // record the header as it was, otherwise undoing leaves the new format over the old
            // payload. The same snapshot restores the header if the encode fails.
            var originalHeader = texture.Header.Raw.ToArray();
            texture.Header.Format = options.SelectedFormat!.Format;
            var encoder = _encoders.Pick(options.Backend, options.SelectedFormat);

            var result = TextureOperations.Replace2D(texture, prepared, encoder, options.GenerateMips,
                options.Quality, originalHeader, App.Settings.SyncFlagsFromFormat);

            if (!result.Success)
            {
                originalHeader.CopyTo(texture.Header.Raw, 0);
                RefreshAfterEdit(texture);
                StatusLeft.Text = "Import failed: " + result.Error;
                return;
            }

            PushPendingCommand();
            RefreshAfterEdit(texture);
            StatusLeft.Text = "Imported " + fileName;
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Import failed: " + ex.Message;
        }
    }

    /// <summary>Replace the selected 2D texture with a solid 8x8 ASTC 8x8 dummy (White/Black/Normal).</summary>
    private void OnTreeDummyOut(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } ||
            !TextureOperations.TryParseDummyFill(tag, out var fill)) return;
        DummyOutSelected(fill);
    }

    private void DummyOutSelected(TextureOperations.DummyFill fill)
    {
        var (texture, _) = SelectedAssetTarget();
        if (texture is null) return;

        var type = texture.Header.Type;
        if (type.Is3D() || type.IsCube())
        {
            StatusLeft.Text = "Dummy out supports 2D textures only.";
            return;
        }

        var format = TextureOperations.DummyFormat(texture);
        if (format is null)
        {
            StatusLeft.Text = "Dummy out failed: ASTC 8x8 is not available.";
            return;
        }

        var encoder = _encoders.Pick(EncoderBackend.Nvtt, format);
        var result = TextureOperations.DummyOut(texture, fill, encoder, EncodeQuality.Normal,
            App.Settings.SyncFlagsFromFormat);
        if (!result.Success)
        {
            RefreshAfterEdit(texture);
            StatusLeft.Text = "Dummy out failed: " + result.Error;
            return;
        }

        PushPendingCommand();
        RefreshAfterEdit(texture);
        StatusLeft.Text = $"Dummied out #{texture.Index} as {format.Name} 8x8.";
    }

    /// <summary>Replace the selected texture from a raw .xt1 file (or a bare payload).</summary>
    private void OnTreeReplaceXt1(object sender, RoutedEventArgs e)
    {
        var (texture, _) = SelectedAssetTarget();
        if (texture is null) return;

        var picker = new OpenFileDialog
        {
            Filter = "Raw XT1 (*.xt1)|*.xt1|Raw payload (*.*)|*.*",
            Title = "Choose a raw .xt1 or payload",
        };
        if (picker.ShowDialog(this) != true) return;

        var bytes = File.ReadAllBytes(picker.FileName);
        var fileName = Path.GetFileName(picker.FileName);

        if (RawXt1.HasMagic(bytes))
        {
            var replaced = TextureOperations.ReplaceWithXt1(texture, bytes);
            if (!replaced.Success)
            {
                StatusLeft.Text = replaced.Error ?? "Replace failed.";
                return;
            }

            PushPendingCommand();
            RefreshAfterEdit(texture);
            StatusLeft.Text = "Replaced from " + fileName;
            return;
        }

        var result = TextureOperations.ReplaceRawPayload(texture, bytes, requireExactSize: true);
        if (!result.Success)
        {
            // A wrong-sized payload is often still what the user wants; ask before forcing it.
            bool proceed = ConfirmDialog.Confirm(this, "Size mismatch",
                $"{result.Error}{Environment.NewLine}{Environment.NewLine}Apply anyway?", "Apply");
            if (!proceed)
            {
                StatusLeft.Text = "Payload replace cancelled.";
                return;
            }

            result = TextureOperations.ReplaceRawPayload(texture, bytes, requireExactSize: false);
        }

        if (!result.Success)
        {
            StatusLeft.Text = result.Error ?? "Failed.";
            return;
        }

        PushPendingCommand();
        RefreshAfterEdit(texture);
        StatusLeft.Text = result.Warning is null ? "Payload replaced." : "Payload replaced: " + result.Warning;
    }

    /// <summary>Add a new texture to the selected bank, from an image file or a DDS.</summary>
    private void OnTreeAddTexture(object sender, RoutedEventArgs e)
    {
        var section = TargetBank();
        if (section is null)
        {
            StatusLeft.Text = "Select a bank (not a texture) to add into it.";
            return;
        }

        var picker = new OpenFileDialog { Filter = ImportFilter, Title = "Choose an image or DDS to add" };
        if (picker.ShowDialog(this) != true) return;

        var name = Path.GetFileNameWithoutExtension(picker.FileName);
        try
        {
            EditableTexture? texture;
            if (picker.FileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var dds = DdsIo.Read(File.ReadAllBytes(picker.FileName));
                texture = TextureOperations.BuildFromDds(dds, name, out var ddsError);
                if (texture is null)
                {
                    StatusLeft.Text = "Add failed: " + (ddsError ?? "could not import the DDS");
                    return;
                }
            }
            else
            {
                var image = ImageIo.Load(picker.FileName);
                if (image is null)
                {
                    StatusLeft.Text = "Could not read " + Path.GetFileName(picker.FileName);
                    return;
                }

                var defaultFormat = FormatTable.Get(App.Settings.DefaultImportFormat)
                                    ?? FormatTable.Get(Xt1Format.Bc7Unorm)!;
                var options = new TextureImportDialog(Path.GetFileName(picker.FileName), image, defaultFormat,
                    EncodeQuality.Normal, generateMips: true, solidify: false, makeOpaque: false,
                    visibleFormats: App.Settings.ImportFormats);
                options.Owner = this;
            if (options.ShowDialog() != true) return;

                var prepared = Solidify.Process(image, options.Solidify, options.MakeOpaque);
                texture = EditableTexture.CreateNew(options.SelectedFormat!.Format,
                    (uint)prepared.Width, (uint)prepared.Height);
                var encoder = _encoders.Pick(options.Backend, options.SelectedFormat);
                var result = TextureOperations.Replace2D(texture, prepared, encoder, options.GenerateMips,
                    options.Quality);
                if (!result.Success)
                {
                    StatusLeft.Text = "Add failed: " + result.Error;
                    return;
                }
            }

            // New textures default to carrying a CRC32 hash and, per the flag-sync preference, get
            // their sRGB/cubemap bits from the format they were encoded with.
            texture.Name = name;
            texture.HashMode = HashMode.AutoFromName;
            texture.Hash = Crc32NameHash.ComputeMasked(name);
            texture.SetModelBankClass(true);
            if (App.Settings.SyncFlagsFromFormat) TextureOperations.SyncFlagsFromFormat(texture);

            _history.Execute(TextureOperations.CreateAddTextureCommand(section, texture));
            RefreshAfterEdit(texture);
            StatusLeft.Text = $"Added \"{name}\" (hash 0x{texture.Hash:X8}).";
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Add failed: " + ex.Message;
        }
    }

    /// <summary>An operation that built an undo command leaves it pending; move it onto the history.</summary>
    private void PushPendingCommand()
    {
        var command = TextureOperations.TakePendingCommand();
        if (command is not null) _history.Push(command);
    }

    /// <summary>Rebuild the tree after an edit and keep the selection sensible.</summary>
    private void RefreshAfterEdit(EditableTexture? select = null)
    {
        if (select is not null) _selected = select;

        BuildTree();
        if (select is not null && FindTextureNode(select) is { } node) node.IsSelected = true;
        else if (DocumentTree.Items.Count > 0 && DocumentTree.Items[0] is TreeViewItem root
                 && FindFirstTexture(root) is { } first) first.IsSelected = true;

        RefreshFileList();
        if (_assetLevel != AssetViewLevel.List) BuildAssetGrid();
        StatusRight.Text = _document is null ? string.Empty : $"{_document.Kind} \u00B7 {TextureCount(_document)} textures";
    }

    /// <summary>Find the tree node that carries a given texture (identity, not value, comparison).</summary>
    private TreeViewItem? FindTextureNode(EditableTexture texture) => FindTextureNode(DocumentTree.Items, texture);

    private static TreeViewItem? FindTextureNode(ItemCollection items, EditableTexture texture)
    {
        foreach (var entry in items.OfType<TreeViewItem>())
        {
            if (entry.Tag is TextureRef reference && ReferenceEquals(reference.Texture, texture)) return entry;
            if (FindTextureNode(entry.Items, texture) is { } match) return match;
        }
        return null;
    }

    private async void OnTreeExportBank(object sender, RoutedEventArgs e)
    {
        var section = TargetBank();
        if (section is null) return;
        if (section.Bank.Count == 0)
        {
            StatusLeft.Text = "This bank has no textures.";
            return;
        }

        var format = FormatFromTag(sender);
        var picker = new OpenFolderDialog { Title = $"Choose an output folder for {section.Label}" };
        if (picker.ShowDialog(this) != true) return;

        string extension = TextureExport.Extension(format);
        var textures = section.Bank.Textures.ToList();
        await ExportTexturesAsync(textures, picker.FolderName, section.Label, format,
            texture => $"tex{texture.Index}_0x{texture.EffectiveHash():X8}{extension}");
    }

    /// <summary>Replace every texture in the selected bank from a folder of exported files.</summary>
    private void OnTreeBatchReplace(object sender, RoutedEventArgs e)
    {
        var section = TargetBank();
        if (section is null) return;
        if (section.Bank.Count == 0)
        {
            StatusLeft.Text = "This bank has no textures.";
            return;
        }

        // The same folder picker the bank export uses; no file is read until the dialog scans it.
        var picker = new OpenFolderDialog { Title = $"Choose the replacement folder for {section.Label}" };
        if (picker.ShowDialog(this) != true) return;

        // Only this bank's textures are considered, matched by index and hash against the exported
        // names, so a folder shared with other banks cannot pull in another bank's files.
        var textures = section.Bank.Textures.ToList();
        var dialog = new BatchReplaceDialog(section.Label, picker.FolderName,
            (folder, token) => Task.Run(() => TextureBatchReplacer.BuildPlan(textures, folder, token), token),
            RunBatchReplaceAsync,
            ApplyBatchReplaceResult)
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private Task<BatchReplaceRunResult> RunBatchReplaceAsync(BatchReplaceRequest request,
        IProgress<string> progress, CancellationToken token) =>
        Task.Run(() => new TextureBatchReplacer(_encoders).Apply(request.Plan, request.Options, progress, token),
            CancellationToken.None);

    /// <summary>Turn a finished batch into one undo step and refresh the shell (UI thread).</summary>
    private void ApplyBatchReplaceResult(BatchReplaceRunResult result)
    {
        if (result.Commands.Count > 0)
            _history.Push(new CompositeCommand("Batch replace textures", result.Commands));

        RefreshAfterEdit();
        StatusLeft.Text = $"Batch replace: {result.Stats.Replaced} replaced, {result.Stats.Skipped} skipped, " +
                          $"{result.Stats.Unused} unused file(s).";
    }

    private async void ExportContainerTextures(TextureExportFormat format)
    {
        if (_document is null) return;

        var picker = new OpenFolderDialog { Title = "Choose an output folder for this container" };
        if (picker.ShowDialog(this) != true) return;

        var textures = _document.AllTextures.ToList();
        if (textures.Count == 0)
        {
            StatusLeft.Text = "This container has no textures.";
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(_document.FilePath ?? "container");
        string extension = TextureExport.Extension(format);
        await ExportTexturesAsync(textures, picker.FolderName, "container", format,
            texture => $"{stem}_tex{texture.Index}_0x{texture.EffectiveHash():X8}{extension}");
    }

    private async Task ExportTexturesAsync(IReadOnlyList<EditableTexture> textures, string folder,
        string what, TextureExportFormat format, Func<EditableTexture, string> fileName)
    {
        _exportCts?.Cancel();
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;

        IProgress<int> progress = new Progress<int>(done =>
            StatusLeft.Text = $"Exporting {what}: {done}/{textures.Count}...");

        try
        {
            var (exported, skipped) = await Task.Run(() =>
            {
                int ok = 0, failed = 0, done = 0;
                foreach (var texture in textures)
                {
                    token.ThrowIfCancellationRequested();
                    if (TextureExport.TryWrite(texture, format, Path.Combine(folder, fileName(texture)), out _))
                        ok++;
                    else
                        failed++;

                    if (++done % 16 == 0) progress.Report(done);
                }

                progress.Report(done);
                return (ok, failed);
            }, token);

            string kind = TextureExport.Extension(format).TrimStart('.').ToUpperInvariant();
            StatusLeft.Text = $"Exported {exported} {kind} texture(s) from {what}" +
                              (skipped == 0 ? "." : $", skipped {skipped} that could not be written.");
        }
        catch (OperationCanceledException)
        {
            StatusLeft.Text = "Export canceled.";
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Export failed: " + ex.Message;
        }
    }
    private void ShowTexture(EditableTexture? texture, BankSection? section)
    {
        _selected = texture;

        bool hasTexture = texture is not null;
        bool raw = _document?.RawXt1 is not null;
        bool editable = hasTexture && !raw;

        ModelCheck.IsEnabled = editable;
        SrgbCheck.IsEnabled = editable;
        CubeCheck.IsEnabled = editable;

        ApplyInspectorTooltips(raw);
        UpdateHashControls();

        // The developer field editor follows the selection; raw .xt1 wrappers stay read-only.
        FieldEditor.Bind(_document, section, texture);
        RawBytesButton.IsEnabled = editable;

        if (texture is null)
        {
            TitleText.Text = "No texture selected";
            SubtitleText.Text = "Open a bank and pick a texture from the tree.";
            SpecText.Text = string.Empty;
            HashBox.Text = "\u2014";
            Table2Text.Text = "table2 = -- -- -- --";
            ValidationBanner.Visibility = Visibility.Collapsed;
            _suppressPreview = true;
            MipCombo.ItemsSource = new[] { "mip 0 (1x1)" };
            MipCombo.SelectedIndex = 0;
            SliceCombo.ItemsSource = null;
            _suppressPreview = false;
            VolumeBar.Visibility = Visibility.Collapsed;
            UpdateAnimationControls(null);
            Preview3DTitle.Text = "No preview";
            Preview3DNote.Text = "Select a cube or volume texture to orbit it here.";
            GlView.SetTexture(null);
            Set2DImage(null, null, null);
            Update3DPlaceholder();
            SummaryText.Text = "(no technical summary)";
            return;
        }

        var header = texture.Header;
        var info = texture.FormatInfo;
        var type = header.Type.Describe();

        TitleText.Text = $"Texture #{texture.Index}";
        SubtitleText.Text = $"{_document?.DisplayName ?? "(untitled)"} / {section?.Label ?? "xt1 (raw payload)"} / {type}";
        SpecText.Text = info is null
            ? "unknown format"
            : $"{info.Name} (0x{info.Value:X}) | {header.Width} \u00D7 {header.Height} \u00D7 {header.Depth} | {type} | " +
              $"{header.MipCount} mip levels | {header.ImageSize / 1048576.0:0.00} MiB";

        ValidationBanner.Visibility = Visibility.Visible;

        _suppressFlags = true;
        HashBox.Text = $"0x{texture.Hash:X8}";
        ModelCheck.IsChecked = texture.IsModelBankClass;
        SrgbCheck.IsChecked = texture.IsSrgbFlag;
        CubeCheck.IsChecked = texture.IsCubeUsageFlag;
        Table2Text.Text = FormatTable2(texture);
        _suppressFlags = false;

        _suppressPreview = true;
        RebuildMipCombo(texture);
        UpdateSliceControls(texture, reset: true);
        _suppressPreview = false;

        UpdateSummary(texture);
        UpdatePreview(texture);      // fills the 2D view and _lastDecodeError
        UpdateAnimationControls(texture, reset: true);   // needs the viewport's composed depth
        UpdateValidation(texture);   // ...then reports the layout warnings plus that error
    }

    /// <summary>
    /// Refresh the inspector's values after an edit, without re-decoding. Mirrors the WinForms
    /// <c>UpdateInfo</c>: header/hash/flag/table edits never change the stored pixels, so re-running
    /// the decoder (and, for a volume, re-decoding every slice and re-uploading it) would be wasted
    /// work on every committed field change.
    /// </summary>
    private void RefreshInspector()
    {
        if (_selected is null) return;

        _suppressFlags = true;
        HashBox.Text = $"0x{_selected.Hash:X8}";
        ModelCheck.IsChecked = _selected.IsModelBankClass;
        SrgbCheck.IsChecked = _selected.IsSrgbFlag;
        CubeCheck.IsChecked = _selected.IsCubeUsageFlag;
        Table2Text.Text = FormatTable2(_selected);
        _suppressFlags = false;

        UpdateHashControls();
        UpdateSummary(_selected);
        UpdateValidation(_selected);
        FieldEditor.RefreshValues();
    }

    /// <summary>Raw `.xt1` payloads cannot be edited yet, so their controls explain why.</summary>
    private void ApplyInspectorTooltips(bool raw)
    {
        ModelCheck.ToolTip = raw ? RawReadOnlyTip : HashFlagTip;
        SrgbCheck.ToolTip = raw ? RawReadOnlyTip : SrgbFlagTip;
        CubeCheck.ToolTip = raw ? RawReadOnlyTip : CubeFlagTip;
    }

    /// <summary>
    /// The CRC32 hash only exists when the "Has CRC32 hash" flag is set, so the field and its
    /// Calculate button are greyed out otherwise. Raw payloads stay read-only as before.
    /// </summary>
    private void UpdateHashControls()
    {
        if (_selected is null)
        {
            HashBox.IsReadOnly = true;
            HashBox.IsEnabled = false;
            HashCalcButton.IsEnabled = false;
            HashBox.ToolTip = null;
            HashCalcButton.ToolTip = null;
            return;
        }

        bool editable = _document?.RawXt1 is null;
        bool hasHash = _selected.IsModelBankClass;

        HashBox.IsReadOnly = !editable;
        HashBox.IsEnabled = hasHash;
        HashCalcButton.IsEnabled = editable && hasHash;

        HashBox.ToolTip = !editable ? RawReadOnlyTip : hasHash ? HashTip : NoHashTip;
        HashCalcButton.ToolTip = !editable ? RawReadOnlyTip : hasHash ? HashCalcTip : NoHashTip;
    }

    private void UpdateValidation(EditableTexture texture)
    {
        var warnings = texture.Validate();
        var lines = warnings.Select(w => "\u2022 " + w).ToList();
        if (_lastDecodeError is not null) lines.Insert(0, "\u2022 Decode failed: " + _lastDecodeError);

        bool bad = lines.Count > 0;
        ValidationBanner.Background = ThemeManager.Brush(bad ? "Brush.Warning" : "Brush.Success");
        var foreground = ThemeManager.Brush(bad ? "Brush.WarningText" : "Brush.SuccessText");
        ValidationText.Foreground = foreground;
        ValidationIcon.Foreground = foreground;
        ValidationIcon.Text = bad ? "\uE7BA" : "\uE73E";
        ValidationText.Text = bad
            ? string.Join(Environment.NewLine, lines)
            : "Decoded cleanly \u2013 the layout matches the stored image size.";
    }

    private static string FormatTable2(EditableTexture texture)
    {
        var bytes = texture.Table2Bytes;
        return $"table2 = {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}";
    }

    private void OnModelFlagChanged(object sender, RoutedEventArgs e)
    {
        ApplyFlag("Set CRC32 hash flag", ModelCheck.IsChecked == true,
            t => t.IsModelBankClass, (t, v) => t.SetModelBankClass(v));
        // The hash field follows the flag: greyed out when the record has no CRC32 hash.
        UpdateHashControls();
    }

    private void OnSrgbFlagChanged(object sender, RoutedEventArgs e) =>
        ApplyFlag("Set sRGB flag", SrgbCheck.IsChecked == true,
            t => t.IsSrgbFlag, (t, v) => t.SetSrgbFlag(v));

    private void OnCubeFlagChanged(object sender, RoutedEventArgs e) =>
        ApplyFlag("Set cube usage flag", CubeCheck.IsChecked == true,
            t => t.IsCubeUsageFlag, (t, v) => t.SetCubeUsageFlag(v));

    private void ApplyFlag(string name, bool value, Func<EditableTexture, bool> get,
        Action<EditableTexture, bool> set)
    {
        if (_suppressFlags || _selected is null) return;

        var texture = _selected;
        bool before = get(texture);
        if (before == value) return;

        set(texture, value);
        _history.Push(new DelegateCommand(name,
            () => set(texture, value),
            () => set(texture, before)));

        Table2Text.Text = FormatTable2(texture);
        UpdateSummary(texture);
        UpdateValidation(texture);
    }

    private void OnHashBoxLostFocus(object sender, RoutedEventArgs e) => CommitHashEdit();

    private void OnHashBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitHashEdit();
        e.Handled = true;
    }

    private void CommitHashEdit()
    {
        if (_suppressFlags || _selected is null) return;
        if (!TryParseHash(HashBox.Text, out uint value))
        {
            RefreshInspector();
            return;
        }

        var texture = _selected;
        uint before = texture.Hash;
        var beforeMode = texture.HashMode;
        if (before == value && beforeMode == HashMode.Forced) return;

        texture.Hash = value;
        texture.HashMode = HashMode.Forced;
        _history.Push(new DelegateCommand("Set table3 hash",
            () =>
            {
                texture.Hash = value;
                texture.HashMode = HashMode.Forced;
            },
            () =>
            {
                texture.Hash = before;
                texture.HashMode = beforeMode;
            }));

        RefreshInspector();
    }

    private static bool TryParseHash(string? text, out uint value)
    {
        value = 0;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private void OnCalculateHash(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var prompt = new TextPromptDialog("Calculate CRC32", "Texture name:", _selected.Name ?? string.Empty)
        {
            Owner = this,
        };
        if (prompt.ShowDialog() != true) return;

        if (string.IsNullOrWhiteSpace(prompt.Value))
        {
            StatusLeft.Text = "A name is needed to compute the hash.";
            return;
        }

        var texture = _selected;
        texture.Name = prompt.Value.Trim();
        uint before = texture.Hash;
        var beforeMode = texture.HashMode;
        texture.ComputeHashFromName();
        uint after = texture.Hash;
        _history.Push(new DelegateCommand("Compute hash from name",
            () =>
            {
                texture.Hash = after;
                texture.HashMode = HashMode.Forced;
            },
            () =>
            {
                texture.Hash = before;
                texture.HashMode = beforeMode;
            }));

        RefreshInspector();
    }
    /// <summary>
    /// Open the XT1 header in the reused hex dialog. The buffer is the live header bytes, so an
    /// applied edit is recorded as an undoable raw-byte command rather than copied away.
    /// </summary>
    private void OnRawBytes(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _document?.RawXt1 is not null) return;
        ShowHexDialog(_selected.Header.Raw, "XT1 header");
    }

    /// <summary>Reuse one hidden raw-bytes dialog; the buffer is the live array so edits apply in place.</summary>
    private void ShowHexDialog(byte[] bytes, string caption)
    {
        if (bytes.Length == 0) return;
        if (_hexDialog is null)
        {
            _hexDialog = new HexEditorDialog { Owner = this };
            var view = _hexDialog.View;
            view.BytesApplied += (after, before) =>
            {
                var target = view.Buffer;
                if (target is null) return;
                // The buffer is the live header array: apply the edit now and record it as undoable.
                after.CopyTo(target, 0);
                _history.Push(new DelegateCommand("Raw byte edit",
                    () => after.CopyTo(target, 0),
                    () => before.CopyTo(target, 0)));
                RefreshInspector();
            };
        }

        _hexDialog.ShowFor(bytes, caption);
    }

    private void UpdatePreview(EditableTexture texture)
    {
        var type = texture.Header.Type;

        if (type.Is3D())
        {
            // Feed the whole volume to the ray-marched viewport; the 2D tab still shows the
            // selected slice so slice/channel/gamma inspection keeps working.
            GlView.SetTexture(texture, _mipIndex);
            UpdateVolumeSliceImage(texture);
            ViewTabs.SelectedIndex = 1;
            Update3DPlaceholder();
            return;
        }

        if (type.IsCube())
        {
            // Face 0 also stays available in the 2D tab; the orbitable skybox is the 3D tab.
            var image = DecodeSubresource(texture, _mipIndex, 0, 0, out var error);
            Set2DImage(image, error, null);
            GlView.SetTexture(texture, _mipIndex);
            ViewTabs.SelectedIndex = 1;
            Update3DPlaceholder();
            return;
        }

        var decoded = DecodeSubresource(texture, _mipIndex, 0, 0, out var decodeError);
        Set2DImage(decoded, decodeError, null);
        GlView.SetTexture(null);
        ViewTabs.SelectedIndex = 0;
        Update3DPlaceholder();
    }

    /// <summary>
    /// Refresh the 2D tab with the volume's current slice. Reuses the slices the viewport already
    /// decoded (matching the WinForms cache) and only falls back to a single-slice decode.
    /// </summary>
    private void UpdateVolumeSliceImage(EditableTexture texture)
    {
        if (!texture.Header.Type.Is3D()) return;

        // Reuse one slice buffer: the viewport copies the composed volume's slice into it in place,
        // so playback does not allocate (and discard) a multi-megabyte image every frame.
        if (GlView.VolumeInfo is { } size)
        {
            if (_sliceBuffer is null || _sliceBuffer.Width != size.Width || _sliceBuffer.Height != size.Height)
                _sliceBuffer = new RgbaImage(size.Width, size.Height);

            if (GlView.CopyVolumeSlice(_sliceIndex, _sliceBuffer))
            {
                Set2DImage(_sliceBuffer, null, null, preserveView: true);
                return;
            }
        }

        _sliceBuffer = null;
        var slice = DecodeSubresource(texture, _mipIndex, 0, _sliceIndex, out var error);
        Set2DImage(slice, error, null, preserveView: true);
    }

    /// <summary>
    /// Decide whether the 3D placeholder must cover the viewport. A WindowsFormsHost renders above
    /// WPF content, so the hosted view is (re)attached while the GL context initialises but collapsed
    /// once there is nothing to show or GL has failed, so the placeholder stays readable.
    /// </summary>
    private void Update3DPlaceholder()
    {
        bool content = GlView.Has3DContent;
        bool ready = GlView.IsReady;
        bool failed = GlView.Error is { Length: > 0 };

        // Keep the host attached while waiting for the context: a collapsed WindowsFormsHost never
        // creates the GL control's handle, so it would never become ready.
        bool showHost = content && ready;
        bool initialising = content && !ready && !failed;

        GlView.Visibility = (showHost || initialising) ? Visibility.Visible : Visibility.Collapsed;
        // While initialising the placeholder sits behind the host (airspace), so it is only shown as
        // soon as it is actually readable.
        Preview3DEmpty.Visibility = showHost ? Visibility.Collapsed : Visibility.Visible;

        if (showHost) return;

        if (failed)
        {
            Preview3DTitle.Text = "GPU preview unavailable";
            Preview3DNote.Text = GlView.Error;
        }
        else if (initialising)
        {
            Preview3DTitle.Text = "Initialising 3D preview";
            Preview3DNote.Text = "Waiting for the OpenGL context...";
        }
        // Otherwise the type-specific "Volume preview"/"Cubemap preview"/"No preview" text set by
        // UpdateSliceControls is left in place.
    }

    /// <summary>A freshly fed volume forced RGB opacity (uniform alpha); keep the toolbar combo in step.</summary>
    private void OnGlRenderModeSuggested(VolumeRenderMode mode)
    {
        int index = mode switch
        {
            VolumeRenderMode.Alpha => VolumeModeAlpha,
            VolumeRenderMode.Mip => VolumeModeMip,
            _ => VolumeModeRgb,
        };

        bool previous = _suppressPreview;
        _suppressPreview = true;
        try
        {
            VolumeModeCombo.SelectedIndex = index;
        }
        finally
        {
            _suppressPreview = previous;
        }
    }

    /// <summary>Push the toolbar's current 3D settings into the hosted viewport.</summary>
    private void SyncVolumeView()
    {
        GlView.RenderMode = VolumeModeCombo.SelectedIndex switch
        {
            VolumeModeAlpha => VolumeRenderMode.Alpha,
            VolumeModeMip => VolumeRenderMode.Mip,
            _ => VolumeRenderMode.Rgb,
        };
        GlView.ShowSlices = ShowSlicesCheck.IsChecked == true;
        GlView.Gamma = ParseFloat(GammaBox.Text, 1f);
        GlView.Exposure = ParseFloat(ExposureBox.Text, 1f);
        GlView.AutoExposure = AutoExposureCheck.IsChecked == true;
        GlView.SliceZ = 0.5f;
        GlView.OpaqueAlpha = OpaqueCheck.IsChecked == true;
        GlView.Channel = Preview2D.Channel;
        GlView.VolumeResolutionCap = App.Settings.VolumeResolutionCap;
    }

    /// <summary>Decode one subresource, turning any failure into a message instead of an exception.</summary>
    private static RgbaImage? DecodeSubresource(EditableTexture texture, int mip, int face, int slice, out string? error)
    {
        error = null;
        var info = texture.FormatInfo;
        if (!TextureCodec.CanDecode(info))
        {
            error = $"No decoder for {info?.Name ?? "this format"}.";
            return null;
        }

        try
        {
            var image = TextureCodec.Decode(texture, mip, face, slice, out var message);
            if (image is null) error = message ?? "The decoder returned no image.";
            return image;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Show a decoded image (or the empty state) and remember any decode error for the banner.</summary>
    private void Set2DImage(RgbaImage? image, string? error, string? note, bool preserveView = false)
    {
        _previewImage = image;
        _lastDecodeError = image is null ? error : null;

        // preserveView keeps the current zoom/pan, which is what slice stepping wants: the WinForms
        // viewer passed preserveView: true for volume slices so scrubbing does not re-fit every step.
        Preview2D.SetImage(image, preserveView);
        PreviewEmpty.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;

        if (note is not null) StatusLeft.Text = note;
        else if (image is null && error is not null) StatusLeft.Text = error;

        UpdateZoomReadout();
        SyncPreviewContextMenu();
    }

    /// <summary>Rebuild the mip list for the selected texture; always starts at mip 0.</summary>
    private void RebuildMipCombo(EditableTexture texture)
    {
        int mips = Math.Max(1, texture.Header.MipCount);
        bool is3D = texture.Header.Type.Is3D();
        var items = new List<string>(mips);
        for (int m = 0; m < mips; m++)
        {
            int w = Math.Max(1, (int)(texture.Header.Width >> m));
            int h = Math.Max(1, (int)(texture.Header.Height >> m));
            // Volumes are described by all three dimensions; the slice count moves with the mip.
            int d = is3D ? Math.Max(1, (int)(texture.Header.Depth >> m)) : 1;
            items.Add(is3D ? $"mip {m} ({w}x{h}x{d})" : $"mip {m} ({w}x{h})");
        }

        MipCombo.ItemsSource = items;
        _mipIndex = 0;
        MipCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// Fill the slice list for a volume and show the preview controls. The bar is up whenever the 3D
    /// viewport is in use; the two groups inside it are mutually exclusive because they drive
    /// different shaders: slice/render for the ray-marched volume, gamma/exposure for the sky
    /// (cubemap/HDR) render.
    /// </summary>
    private void UpdateSliceControls(EditableTexture texture, bool reset = false)
    {
        bool is3D = texture.Header.Type.Is3D();
        bool isCube = texture.Header.Type.IsCube();
        VolumeBar.Visibility = (is3D || isCube) ? Visibility.Visible : Visibility.Collapsed;
        VolumeOnlyGroup.Visibility = is3D ? Visibility.Visible : Visibility.Collapsed;
        SkyOnlyGroup.Visibility = isCube ? Visibility.Visible : Visibility.Collapsed;
        Preview3DTitle.Text = is3D ? "Volume preview" : isCube ? "Cubemap preview" : "No preview";
        Preview3DNote.Text = is3D
            ? "Drag to orbit, wheel to zoom, double-click or Reset View to reset."
            : isCube
                ? "Drag to look around, wheel to zoom; gamma and exposure apply to the sky render."
                : "Select a cube or volume texture to orbit it here.";

        bool previous = _suppressPreview;
        _suppressPreview = true;
        try
        {
            if (!is3D)
            {
                SliceCombo.ItemsSource = null;
                _sliceIndex = 0;
                return;
            }

            // One source of truth for the slice count (shared with the animation bar's guards).
            int slices = CurrentSliceCount(texture);

            var items = new List<string>(slices);
            for (int s = 0; s < slices; s++) items.Add($"slice {s}");
            SliceCombo.ItemsSource = items;

            // Start in the middle so the cross-section plane and the 2D slice are both meaningful.
            _sliceIndex = reset ? slices / 2 : Math.Clamp(_sliceIndex, 0, slices - 1);
            SliceCombo.SelectedIndex = _sliceIndex;
        }
        finally
        {
            _suppressPreview = previous;
        }

        // Mirror the middle-slice default onto the viewport's cross-section plane.
        ApplySliceZ();
    }

    private static BitmapSource ToBitmapSource(RgbaImage image)
    {
        var pixels = new byte[image.Pixels.Length];
        var source = image.Pixels;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            pixels[i] = source[i + 2];
            pixels[i + 1] = source[i + 1];
            pixels[i + 2] = source[i];
            pixels[i + 3] = source[i + 3];
        }

        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null,
            pixels, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void UpdateSummary(EditableTexture texture)
    {
        var header = texture.Header;
        var info = texture.FormatInfo;
        var bytes = texture.Table2Bytes;
        var lines = new[]
        {
            $"Texture #{texture.Index}   {info?.Name ?? "unknown"}",
            $"  {header.Width} \u00D7 {header.Height} \u00D7 {header.Depth}   type {header.Type.Describe()} (0x{(uint)header.Type:X})",
            $"  {header.MipCount} mips   imageSize {header.ImageSize} (0x{header.ImageSize:X})",
            $"  format      0x{header.FormatValue:X}   codecTag 0x{header.CodecTag:X4}",
            $"  textureFlag 0x{header.TextureFlag:X}",
            $"  layout      0x{header.Layout:X8}   blockHeightLog2 {header.BlockHeightLog2}",
            $"  layout2     0x{header.Layout2:X8}",
            $"  table2      {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}",
            $"  hash        0x{texture.Hash:X8}",
        };
        SummaryText.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>Walk up the visual tree to the first ancestor of the requested type.</summary>
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}









