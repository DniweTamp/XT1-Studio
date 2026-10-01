using System.Windows;
using System.Windows.Controls;
using Xt1.Core.Containers;
using Xt1.Core.Settings;
using Xt1.Core.Xt1;

namespace Xt1.App.Wpf;

/// <summary>
/// Test hooks for the headless diagnostics (<c>--uitest</c>, <c>--panelshot</c>). They drive the
/// shell the way a user would and report state as text, so the UI can be exercised without a person
/// at the keyboard. Kept out of MainWindow's own file so the shell stays readable.
///
/// The reported strings keep the shapes the WinForms harness produced (<c>hash=0x%08X</c>,
/// <c>text|enabled</c> menu rows, <c>visible=… bytes=…</c>) so existing expectations still hold.
/// </summary>
public partial class MainWindow
{
    /// <summary>Load a bank directly instead of going through the file dialog.</summary>
    internal void UiTestLoadFile(string path) => LoadDocument(path);

    /// <summary>0 = the 2D/preview tab, 1 = the cube/3D tab.</summary>
    internal void UiTestSelectRightTab(int index)
    {
        if (index >= 0 && index < ViewTabs.Items.Count) ViewTabs.SelectedIndex = index;
    }

    internal void UiTestSelectTexture(int sectionIndex, int textureIndex)
    {
        var section = _document?.Sections.ElementAtOrDefault(sectionIndex);
        if (section is null) return;

        // A one-texture bank still has something to select, so a missing index falls back to the first.
        var texture = section.Bank.Textures.ElementAtOrDefault(textureIndex)
                      ?? section.Bank.Textures.FirstOrDefault();

        // Select through the tree, the way a user does, so the grid's shared-menu target stays in step.
        _gridSelection = (texture, section);
        if (texture is not null && FindTextureNode(texture) is { } node) node.IsSelected = true;
        else ShowTexture(texture, section);
    }

    internal string UiTestStatus => StatusLeft.Text ?? string.Empty;

    internal string UiTestInspectorState
    {
        get
        {
            string hash = _selected is null ? "none" : $"0x{_selected.Hash:X8}";
            string flags = $"hashflag={ModelCheck.IsChecked == true} srgb={SrgbCheck.IsChecked == true} " +
                           $"cube={CubeCheck.IsChecked == true}";
            return $"advanced={AdvancedToggle.IsChecked == true} hash={hash} flags={flags}";
        }
    }

    internal string UiTestInspectorValidation => ValidationText.Text ?? string.Empty;

    /// <summary>Select the first volume in the document, as the 3D checks need one.</summary>
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

    internal void UiTestSelectChannel(int index)
    {
        if (index >= 0 && index < ChannelCombo.Items.Count) ChannelCombo.SelectedIndex = index;
    }

    internal void UiTestToggleOpaque() => OpaqueCheck.IsChecked = OpaqueCheck.IsChecked != true;

    /// <summary>The sky auto-exposure checkbox value and the value pushed to the viewport.</summary>
    internal string UiTestAutoExposureState =>
        $"check={AutoExposureCheck.IsChecked == true} applied={GlView.AutoExposure}";

    /// <summary>Click the sky auto-exposure checkbox through its real handler.</summary>
    internal void UiTestToggleAutoExposure()
    {
        AutoExposureCheck.IsChecked = AutoExposureCheck.IsChecked != true;
        OnAutoExposureChanged(this, new RoutedEventArgs());
    }

    /// <summary>Select the first cubemap, so the sky controls (incl. auto exposure) can be checked.</summary>
    internal string UiTestSelectFirstCube()
    {
        if (_document is null) return "no document";
        for (int s = 0; s < _document.Sections.Count; s++)
        {
            var textures = _document.Sections[s].Bank.Textures;
            for (int t = 0; t < textures.Count; t++)
            {
                var type = textures[t].Header.Type;
                if (!type.IsCube()) continue;
                UiTestSelectTexture(s, t);
                return $"#{textures[t].Index} {type.Describe()}";
            }
        }
        return "none";
    }

    /// <summary>0 = RGB, 1 = Alpha, 2 = MIP.</summary>
    internal void UiTestSelectVolumeMode(int index)
    {
        if (index >= 0 && index < VolumeModeCombo.Items.Count) VolumeModeCombo.SelectedIndex = index;
    }

    internal string UiTestVolumeState =>
        $"render={VolumeModeCombo.SelectedItem ?? "none"} channel={ChannelCombo.SelectedItem ?? "none"} " +
        $"planes={ShowSlicesCheck.IsChecked == true} sliceZ={GlView.SliceZ:0.##} tab={ViewTabs.SelectedIndex}";

    /// <summary>The 2D layer-animation bar's visibility, play state, speed and current frame.</summary>
    internal string UiTestAnimationState
    {
        get
        {
            int count = _selected is null ? 0 : CurrentSliceCount(_selected);
            return $"visible={AnimBar.Visibility == Visibility.Visible} playing={_animPlaying} " +
                   $"fps={FpsBox.Text} frame={_sliceIndex + 1}/{count}";
        }
    }

    /// <summary>Click the 2D animation bar's play/pause button.</summary>
    internal void UiTestToggleAnimation() => OnAnimPlayToggle(this, new RoutedEventArgs());

    /// <summary>Type a playback speed into the FPS field and commit it like the field does.</summary>
    internal void UiTestSetAnimationFps(string fps)
    {
        FpsBox.Text = fps;
        CommitAnimationFps();
    }

    /// <summary>Drag the time bar to a layer, through the real ValueChanged path.</summary>
    internal void UiTestScrubAnimation(int index) => AnimSlider.Value = index;

    /// <summary>Run one playback tick without waiting for the timer, so a check stays deterministic.</summary>
    internal void UiTestAnimationTick() => OnAnimTick(this, EventArgs.Empty);

    /// <summary>Select the first flat (non-3D, non-cube) texture, so its animation bar can be checked.</summary>
    internal string UiTestSelectFirstFlatTexture()
    {
        if (_document is null) return "no document";
        for (int s = 0; s < _document.Sections.Count; s++)
        {
            var textures = _document.Sections[s].Bank.Textures;
            for (int t = 0; t < textures.Count; t++)
            {
                var type = textures[t].Header.Type;
                if (type.Is3D() || type.IsCube()) continue;
                UiTestSelectTexture(s, t);
                return $"#{textures[t].Index} {type.Describe()}";
            }
        }
        return "none";
    }

    internal string UiTestPasteHash(string text)
    {
        if (_selected is null) return "no texture";
        HashBox.Text = text;
        CommitHashEdit();
        return UiTestInspectorState;
    }

    internal string UiTestComputeHashFor(string name)
    {
        if (_selected is null) return "no texture";
        _selected.Name = name;
        _selected.ComputeHashFromName();
        RefreshInspector();
        return UiTestInspectorState;
    }

    /// <summary>
    /// Open the raw-bytes dialog the way the button does and report what it bound. The dialog is
    /// modeless by design, so this returns immediately.
    /// </summary>
    internal string UiTestOpenHex()
    {
        if (_selected is null) return "no texture";
        ShowHexDialog(_selected.Header.Raw, "XT1 header");
        var state = _hexDialog is null
            ? "no dialog"
            : $"visible={_hexDialog.IsVisible} bytes={_hexDialog.View.Buffer?.Length ?? -1}";
        _hexDialog?.Hide();
        return state;
    }

    internal IReadOnlyList<string> UiTestTreeMenuItems()
    {
        UpdateTreeMenuState();
        var items = new List<string>();
        var menu = DocumentTree.ContextMenu;
        if (menu is null) return items;

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Visibility != Visibility.Visible) continue;
            items.Add($"{item.Header}|{(item.IsEnabled ? "enabled" : "disabled")}");
        }
        return items;
    }

    /// <summary>The file browser's rows in display order.</summary>
    internal IReadOnlyList<string> UiTestFileListItems() =>
        _fileRows?.Select(row => row.Name).ToList() ?? new List<string>();

    /// <summary>The real column headers, including the current sort indicator.</summary>
    internal IReadOnlyList<string> UiTestFileColumnHeaders() => new[]
    {
        FileColumnName.Header?.ToString() ?? string.Empty,
        FileColumnModified.Header?.ToString() ?? string.Empty,
        FileColumnSize.Header?.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Click a column header (0 = Name, 1 = Modified, 2 = Size) through the real sort path. The real
    /// GridViewColumn is used, so a reordered or renamed column changes what this sorts by exactly as
    /// it would for a user instead of the hook keeping its own copy of the mapping.
    /// </summary>
    internal IReadOnlyList<string> UiTestSortFileColumn(int column)
    {
        var view = FileList.View as GridView;
        var target = view is not null && column >= 0 && column < view.Columns.Count
            ? view.Columns[column]
            : null;

        SortByColumn(target);
        return UiTestFileListItems();
    }

    /// <summary>
    /// Select a file row by name fragment and let the real selection handler load it. Loading here as
    /// well would parse the bank twice and would keep passing if the handler were broken.
    /// </summary>
    internal string UiTestPickFile(string nameFragment)
    {
        var row = _fileRows?.FirstOrDefault(r =>
            r.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
        if (row is null) return "not found";

        FileList.SelectedItem = row;
        return row.Name;
    }

    /// <summary>The encoder the app resolved, without going through the Settings dialog.</summary>
    internal string UiTestBackendState => EncoderDescription;

    /// <summary>What the Settings dialog's OK does: apply the copy and rebuild the encoders.</summary>
    internal void UiTestApplySettings()
    {
        CopySettings(App.Settings);
        RebuildEncoders();
    }

    /// <summary>Select the first bank node (the node whose tag is a section), the way a click does.</summary>
    internal string UiTestSelectBankNode()
    {
        foreach (var item in DocumentTree.Items.OfType<TreeViewItem>())
        {
            if (FindBankNode(item) is not { } node) continue;
            node.IsSelected = true;
            node.BringIntoView();
            return NodeLabel(node);
        }
        return "not found";

        static TreeViewItem? FindBankNode(TreeViewItem node)
        {
            if (node.Tag is BankSection) return node;
            foreach (var child in node.Items.OfType<TreeViewItem>())
            {
                if (FindBankNode(child) is { } match) return match;
            }
            return null;
        }
    }

    /// <summary>Adopt a new empty bank without going through the dialogs.</summary>
    internal BankDocument UiTestCreateEmptyBank(bool standalone)
    {
        var kind = standalone ? BankKind.StandaloneWtb : BankKind.EffectWta;
        var directory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "uitest");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, standalone ? "empty.wtb" : "empty.wta");
        var document = BankDocument.CreateEmpty(kind, path);
        AdoptDocument(document);
        return document;
    }

    /// <summary>
    /// The bank a tree action would act on, resolved exactly as the actions resolve it. Lets the smoke
    /// test check that an offered action can actually run, rather than only that it is enabled.
    /// </summary>
    internal string UiTestTargetBank() => TargetBank()?.Label ?? "none";

    internal FrameworkElement UiTestInspectorElement => InspectorRoot;

    internal FrameworkElement UiTestTreeElement => DocumentTree;

    internal FrameworkElement UiTestAssetElement => AssetViewHost;

    /// <summary>Report the Asset Browser zoom state (level, label, grid vs list, tile count).</summary>
    internal string UiTestAssetViewState()
    {
        int tiles = EnumerateTiles(AssetGridPanel).Count();
        return $"level={_assetLevel} label={AssetZoomLabel.Text} " +
               $"grid={DocumentTree.Visibility != Visibility.Visible} tiles={tiles} " +
               $"textures={( _document is null ? 0 : TextureCount(_document))}";
    }

    /// <summary>Set the Asset Browser zoom level without persisting it, for the headless smoke.</summary>
    internal string UiTestSetAssetLevel(AssetViewLevel level)
    {
        SetAssetLevel(level, persist: false);
        return UiTestAssetViewState();
    }

    private static string NodeLabel(TreeViewItem item)
    {
        if (item.Header is StackPanel panel)
            return string.Join(" ", panel.Children.OfType<TextBlock>().Select(text => text.Text));

        return item.Header?.ToString() ?? string.Empty;
    }
}
