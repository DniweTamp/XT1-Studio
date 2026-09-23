using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Util;
using Xt1.Core.Xt1;

namespace Xt1.App.Wpf.Inspector;

/// <summary>
/// Typed editor for every XT1 header field, every WTB table entry and the bank-level values.
/// Port of <c>Xt1.App.Controls.FieldEditorPanel</c>: every change is reported as an undoable
/// <see cref="IEditCommand"/> through <see cref="CommandProduced"/>; nothing is applied silently.
/// </summary>
public partial class FieldEditorPanel : UserControl
{
    private static readonly FontFamily MonoFont = new("Consolas");

    private readonly List<Action> _refreshers = new();

    private EditableTexture? _texture;
    private BankSection? _section;
    private BankDocument? _document;

    public FieldEditorPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised with a command that has already been applied and can be pushed onto the history.</summary>
    public event Action<IEditCommand>? CommandProduced;

    public void Bind(BankDocument? document, BankSection? section, EditableTexture? texture)
    {
        _document = document;
        _section = section;
        _texture = texture;
        Rebuild();
    }

    public void RefreshValues()
    {
        foreach (var refresher in _refreshers) refresher();
    }

    private void Rebuild()
    {
        RowsHost.Children.Clear();
        _refreshers.Clear();

        var texture = _texture;
        if (texture is null)
        {
            HintText.Text = "Select a texture to edit its fields.";
            return;
        }

        // A raw .xt1 wrapper is read-only until the editing pipeline is ported.
        bool editable = _document?.RawXt1 is null;
        HintText.Text = _document is null
            ? "Editing fields (no document)."
            : $"{_document.DisplayName} / {_section?.Label} / texture {texture.Index}" +
              (editable ? string.Empty : " (read-only raw .xt1)");

        var header = texture.Header;

        AddHeader("XT1 header (0x38 bytes)");
        AddHex("magic @0x00", () => header.Magic, v => header.Magic = v);
        AddHex("flags @0x04", () => header.Flags, v => header.Flags = v);
        AddHex64("imageSize @0x08", () => header.ImageSize, v => header.ImageSize = v);
        AddDec("headerSize @0x10", () => header.HeaderSize, v => header.HeaderSize = v);
        AddDec("mipCount @0x14 lo", () => (uint)header.MipCount, v => header.MipCount = (int)v);
        AddHex("codecTag @0x14 hi", () => header.CodecTag, v => header.CodecTag = (ushort)v);
        AddDec("textureType @0x18", () => (uint)header.Type, v => header.Type = (TextureType)v);
        AddDec("width @0x20", () => header.Width, v => header.Width = v);
        AddDec("height @0x24", () => header.Height, v => header.Height = v);
        AddDec("depth @0x28", () => header.Depth, v => header.Depth = v);
        AddHex("textureFlag @0x2C", () => header.TextureFlag, v => header.TextureFlag = v);
        AddLayoutRow(header);
        AddHex("layout2 @0x34", () => header.Layout2, v => header.Layout2 = v);

        AddHeader("WTB table entry");
        AddHex("table0 payload offset", () => texture.PayloadOffset, v => texture.PayloadOffset = v);
        AddHex("table1 payload size", () => texture.PayloadSize, v => texture.PayloadSize = v);
        AddPayloadLengthWarning(texture);
        AddTable2BytesRow(texture);

        var section = _section;
        if (section is not null)
        {
            AddHeader("Bank");
            AddDec("texture count", () => (uint)section.Bank.Count, v =>
            {
                if (v <= 4096) section.Bank.Count = (int)v;
            });
            AddBool("include table3 array", () => section.Bank.IncludeTable3,
                v => section.Bank.IncludeTable3 = v);
            AddBool("sync table1 from imageSize", () => section.Bank.SyncTable1WithImageSize,
                v => section.Bank.SyncTable1WithImageSize = v);
            AddAction("Recompute imageSize from layout", () =>
            {
                var beforeImageSize = texture.Header.ImageSize;
                var beforeTable1 = texture.PayloadSize;
                TextureOperations.RecomputeImageSize(texture);
                var afterImageSize = texture.Header.ImageSize;
                var afterTable1 = texture.PayloadSize;
                CommandProduced?.Invoke(new DelegateCommand("Recompute imageSize",
                    () =>
                    {
                        texture.Header.ImageSize = afterImageSize;
                        texture.PayloadSize = afterTable1;
                    },
                    () =>
                    {
                        texture.Header.ImageSize = beforeImageSize;
                        texture.PayloadSize = beforeTable1;
                    }));
                RefreshValues();
            });
        }

        if (_document?.HeaderDat is not null) AddDatSection("DAT container (header side)", _document.HeaderDat);
        if (_document?.PayloadDat is not null) AddDatSection("DAT container (payload side)", _document.PayloadDat);

        AddHint("The format, texture type, dimensions, hash and table2 flags are edited in the panel above.");

        RowsHost.IsEnabled = editable;
    }

    // --- row builders -------------------------------------------------------------------

    private static Style RequireStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style
        ?? throw new InvalidOperationException($"Missing style '{key}'.");

    private static TextBlock MutedText(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");
        return block;
    }

    private void AddHeader(string text)
    {
        var header = new TextBlock { Text = text, Style = RequireStyle("SectionHeader"), Margin = new Thickness(0, 10, 0, 4) };
        RowsHost.Children.Add(header);
    }

    private void AddHint(string text)
    {
        var hint = MutedText(text);
        hint.FontSize = 11.5;
        hint.Margin = new Thickness(0, 6, 0, 3);
        RowsHost.Children.Add(hint);
    }

    private void AddRow(string label, FrameworkElement editor, Action? refresh = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (label.Length > 0)
        {
            var caption = MutedText(label);
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(caption, 0);
            grid.Children.Add(caption);
        }

        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        RowsHost.Children.Add(grid);
        if (refresh is not null) _refreshers.Add(refresh);
    }

    private void AddFullWidth(FrameworkElement element, Action? refresh = null)
    {
        element.Margin = new Thickness(0, 0, 0, 6);
        RowsHost.Children.Add(element);
        if (refresh is not null) _refreshers.Add(refresh);
    }

    private static TextBox NewBox(double width)
    {
        return new TextBox
        {
            Width = width,
            Style = RequireStyle("FieldBox"),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private static void Hook(TextBox box, Action commit)
    {
        box.LostFocus += (_, _) => commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            commit();
            e.Handled = true;
        };
    }

    private void AddHex(string label, Func<uint> get, Action<uint> set)
    {
        var box = NewBox(150);
        box.FontFamily = MonoFont;
        void Refresh() => box.Text = "0x" + get().ToString("X8", CultureInfo.InvariantCulture);
        Refresh();
        Hook(box, () =>
        {
            if (!TryParseUInt(box.Text, out uint value))
            {
                Refresh();
                return;
            }
            uint before = get();
            if (before == value) return;
            set(value);
            CommandProduced?.Invoke(new DelegateCommand(label, () => set(value), () => set(before)));
        });
        AddRow(label, box, Refresh);
    }

    private void AddHex64(string label, Func<ulong> get, Action<ulong> set)
    {
        var box = NewBox(200);
        box.FontFamily = MonoFont;
        void Refresh() => box.Text = Mem.Hex(get());
        Refresh();
        Hook(box, () =>
        {
            if (!TryParseULong(box.Text, out ulong value))
            {
                Refresh();
                return;
            }
            ulong before = get();
            if (before == value) return;
            set(value);
            CommandProduced?.Invoke(new DelegateCommand(label, () => set(value), () => set(before)));
        });
        AddRow(label, box, Refresh);
    }

    private void AddDec(string label, Func<uint> get, Action<uint> set)
    {
        var box = NewBox(150);
        void Refresh() => box.Text = get().ToString(CultureInfo.InvariantCulture);
        Refresh();
        Hook(box, () =>
        {
            if (!TryParseUInt(box.Text, out uint value))
            {
                Refresh();
                return;
            }
            uint before = get();
            if (before == value) return;
            set(value);
            CommandProduced?.Invoke(new DelegateCommand(label, () => set(value), () => set(before)));
        });
        AddRow(label, box, Refresh);
    }

    private void AddBool(string label, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox
        {
            Style = RequireStyle("PillCheckBox"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        void Refresh() => box.IsChecked = get();
        Refresh();
        box.Click += (_, _) =>
        {
            bool value = box.IsChecked == true;
            bool before = get();
            if (before == value) return;
            set(value);
            CommandProduced?.Invoke(new DelegateCommand(label, () => set(value), () => set(before)));
        };
        AddRow(label, box, Refresh);
    }

    private void AddAction(string label, Action action)
    {
        var button = new Button
        {
            Style = RequireStyle("PillButton"),
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => action();
        AddRow(string.Empty, button);
    }

    private void AddLayoutRow(Xt1Header header)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal };
        var box = NewBox(120);
        box.FontFamily = MonoFont;
        var info = MutedText(string.Empty);
        info.VerticalAlignment = VerticalAlignment.Center;
        info.Margin = new Thickness(8, 0, 0, 0);

        void Refresh()
        {
            box.Text = "0x" + header.Layout.ToString("X8", CultureInfo.InvariantCulture);
            info.Text = $"blockHeightLog2 = {header.BlockHeightLog2} (high bits preserved)";
        }
        Refresh();
        Hook(box, () =>
        {
            if (!TryParseUInt(box.Text, out uint value))
            {
                Refresh();
                return;
            }
            uint before = header.Layout;
            if (before == value) return;
            header.Layout = value;
            CommandProduced?.Invoke(new DelegateCommand("Set layout",
                () => header.Layout = value, () => header.Layout = before));
            Refresh();
        });
        host.Children.Add(box);
        host.Children.Add(info);
        AddRow("layout @0x30", host, Refresh);
    }

    private void AddPayloadLengthWarning(EditableTexture texture)
    {
        var label = MutedText(string.Empty);
        void Refresh()
        {
            long expected = (long)texture.Header.ImageSize;
            long actual = texture.Payload.LongLength;
            label.Text = expected == actual
                ? $"payload {actual} bytes"
                : $"payload {actual} bytes but imageSize is {expected}";
            label.SetResourceReference(TextBlock.ForegroundProperty,
                expected == actual ? "Brush.Muted" : "Brush.WarningText");
        }
        AddRow(string.Empty, label, Refresh);
    }

    /// <summary>
    /// Raw table2 bytes. The decoded flags and the hash live in the inspector above; this is the
    /// byte-exact fallback for values the flags do not cover.
    /// </summary>
    private void AddTable2BytesRow(EditableTexture texture)
    {
        var box = NewBox(150);
        box.FontFamily = MonoFont;
        void RefreshBytes() => box.Text = Mem.HexBytes(texture.Table2Bytes);

        RefreshBytes();
        Hook(box, () =>
        {
            var parsed = ParseHexBytes(box.Text, 4);
            if (parsed is null)
            {
                RefreshBytes();
                return;
            }
            var before = (byte[])texture.Table2Bytes.Clone();
            var after = parsed;
            for (int i = 0; i < 4; i++) texture.Table2Bytes[i] = after[i];
            CommandProduced?.Invoke(new DelegateCommand("Set table2",
                () => { for (int i = 0; i < 4; i++) texture.Table2Bytes[i] = after[i]; },
                () => { for (int i = 0; i < 4; i++) texture.Table2Bytes[i] = before[i]; }));
        });
        AddRow("table2 (4 bytes)", box, RefreshBytes);
    }

    /// <summary>
    /// One row of the DAT sub-file list. Observable, and <see cref="SyncFrom"/> only raises changes
    /// for values that actually moved, so refreshing a 5,000-row DAT after an unrelated edit costs a
    /// few comparisons per row instead of reallocating and rebinding the whole list.
    /// </summary>
    private sealed class DatRow : INotifyPropertyChanged
    {
        private string _tag = string.Empty;
        private string _name = string.Empty;
        private string _offsetText = string.Empty;
        private string _sizeText = string.Empty;

        public required int Index { get; init; }

        public required DatSubFile Sub { get; init; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string IndexText => Index.ToString(CultureInfo.InvariantCulture);

        public string Tag
        {
            get => _tag;
            private set => Set(ref _tag, value, nameof(Tag));
        }

        public string Name
        {
            get => _name;
            private set => Set(ref _name, value, nameof(Name));
        }

        public string OffsetText
        {
            get => _offsetText;
            private set => Set(ref _offsetText, value, nameof(OffsetText));
        }

        public string SizeText
        {
            get => _sizeText;
            private set => Set(ref _sizeText, value, nameof(SizeText));
        }

        public void SyncFrom(DatSubFile sub)
        {
            Tag = sub.Tag;
            Name = sub.Name;
            OffsetText = "0x" + sub.Offset.ToString("X8", CultureInfo.InvariantCulture);
            SizeText = "0x" + sub.DescriptorSize.ToString("X8", CultureInfo.InvariantCulture);
        }

        private void Set(ref string field, string value, string property)
        {
            if (string.Equals(field, value, StringComparison.Ordinal)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    /// <summary>
    /// DAT sub-file table. The list is virtualized: some DATs hold thousands of sub-files
    /// (shader.dat has 5588). Only the selected sub-file gets an editor.
    /// </summary>
    private void AddDatSection(string title, DatFile dat)
    {
        AddHeader(title);

        var list = new ListView
        {
            Height = 200,
            SelectionMode = SelectionMode.Single,
        };
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "#", Width = 34, DisplayMemberBinding = new Binding(nameof(DatRow.IndexText)) });
        view.Columns.Add(new GridViewColumn { Header = "tag", Width = 42, DisplayMemberBinding = new Binding(nameof(DatRow.Tag)) });
        view.Columns.Add(new GridViewColumn { Header = "name", Width = 132, DisplayMemberBinding = new Binding(nameof(DatRow.Name)) });
        view.Columns.Add(new GridViewColumn { Header = "offset", Width = 76, DisplayMemberBinding = new Binding(nameof(DatRow.OffsetText)) });
        view.Columns.Add(new GridViewColumn { Header = "size", Width = 76, DisplayMemberBinding = new Binding(nameof(DatRow.SizeText)) });
        list.View = view;

        var editor = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var tagBox = NewBox(48);
        tagBox.FontFamily = MonoFont;
        var nameBox = NewBox(150);
        var offsetBox = NewBox(84);
        offsetBox.FontFamily = MonoFont;
        var sizeBox = NewBox(84);
        sizeBox.FontFamily = MonoFont;
        var detail = MutedText(string.Empty);
        detail.VerticalAlignment = VerticalAlignment.Center;
        detail.Margin = new Thickness(6, 4, 0, 0);

        editor.Children.Add(MutedText("tag"));
        editor.Children.Add(tagBox);
        editor.Children.Add(MutedText("name"));
        editor.Children.Add(nameBox);
        editor.Children.Add(MutedText("offset"));
        editor.Children.Add(offsetBox);
        editor.Children.Add(MutedText("size"));
        editor.Children.Add(sizeBox);
        editor.Children.Add(detail);

        // Declared before RefreshRows is first referenced: the local function captures it.
        List<DatRow>? rows = null;

        var host = new StackPanel();
        host.Children.Add(list);
        host.Children.Add(editor);
        AddFullWidth(host, RefreshRows);

        void LoadEditor()
        {
            int index = list.SelectedIndex;
            bool valid = index >= 0 && index < dat.SubFiles.Count;
            tagBox.IsEnabled = nameBox.IsEnabled = offsetBox.IsEnabled = sizeBox.IsEnabled = valid;
            if (!valid)
            {
                tagBox.Text = nameBox.Text = offsetBox.Text = sizeBox.Text = string.Empty;
                detail.Text = "select a sub-file to edit it";
                return;
            }

            var sub = dat.SubFiles[index];
            tagBox.Text = sub.Tag;
            nameBox.Text = sub.Name;
            offsetBox.Text = "0x" + sub.Offset.ToString("X8", CultureInfo.InvariantCulture);
            sizeBox.Text = "0x" + sub.DescriptorSize.ToString("X8", CultureInfo.InvariantCulture);
            detail.Text = $"[{index}] {sub.Bytes.Length} bytes";
        }

        void RefreshRows()
        {
            int selected = list.SelectedIndex;

            // Bind once; afterwards only push values that actually changed. Rebuilding the row list
            // here would reallocate and rebind every sub-file on each inspector refresh (5588 rows for
            // shader.dat), defeating the ListView's virtualization.
            if (rows is null || rows.Count != dat.SubFiles.Count)
            {
                rows = new List<DatRow>(dat.SubFiles.Count);
                for (int i = 0; i < dat.SubFiles.Count; i++)
                    rows.Add(new DatRow { Index = i, Sub = dat.SubFiles[i] });
                foreach (var row in rows) row.SyncFrom(row.Sub);
                list.ItemsSource = rows;
            }
            else
            {
                foreach (var row in rows) row.SyncFrom(row.Sub);
            }

            if (selected >= 0 && selected < rows.Count) list.SelectedIndex = selected;
            LoadEditor();
        }

        void ApplyTo(int index, string what, string before, string after, Action<DatSubFile, string> set)
        {
            if (before == after) return;
            set(dat.SubFiles[index], after);
            CommandProduced?.Invoke(new DelegateCommand($"Set sub-file {index} {what}",
                () => set(dat.SubFiles[index], after),
                () => set(dat.SubFiles[index], before)));
            RefreshRows();
        }

        list.SelectionChanged += (_, _) => LoadEditor();

        Hook(tagBox, () =>
        {
            int index = list.SelectedIndex;
            if (index >= 0) ApplyTo(index, "tag", dat.SubFiles[index].Tag, tagBox.Text, (s, v) => s.Tag = v);
        });

        Hook(nameBox, () =>
        {
            int index = list.SelectedIndex;
            if (index >= 0) ApplyTo(index, "name", dat.SubFiles[index].Name, nameBox.Text, (s, v) => s.Name = v);
        });

        Hook(offsetBox, () =>
        {
            int index = list.SelectedIndex;
            if (index < 0 || !TryParseUInt(offsetBox.Text, out uint value)) return;
            if (value == dat.SubFiles[index].Offset) return;

            // A manual offset only means something when the writer does not recompute the layout.
            dat.RecomputeOffsetsOnWrite = false;
            ApplyTo(index, "offset", dat.SubFiles[index].Offset.ToString(CultureInfo.InvariantCulture),
                value.ToString(CultureInfo.InvariantCulture),
                (s, v) => s.Offset = uint.Parse(v, CultureInfo.InvariantCulture));
        });

        Hook(sizeBox, () =>
        {
            int index = list.SelectedIndex;
            if (index < 0 || !TryParseUInt(sizeBox.Text, out uint value)) return;
            if (value == dat.SubFiles[index].DescriptorSize) return;

            ApplyTo(index, "descriptor", dat.SubFiles[index].DescriptorSize.ToString(CultureInfo.InvariantCulture),
                value.ToString(CultureInfo.InvariantCulture),
                (s, v) => s.DescriptorSize = uint.Parse(v, CultureInfo.InvariantCulture));
        });

        RefreshRows();
    }

    // --- parsing ------------------------------------------------------------------------

    private static bool TryParseUInt(string text, out uint value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseULong(string text, out ulong value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static byte[]? ParseHexBytes(string text, int expected)
    {
        var tokens = text.Split(new[] { ' ', '\t', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != expected) return null;
        var bytes = new byte[expected];
        for (int i = 0; i < expected; i++)
        {
            if (!byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }
}

