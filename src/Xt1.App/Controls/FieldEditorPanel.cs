using System.Globalization;
using Xt1.App.Theming;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Format;
using Xt1.Core.Util;
using Xt1.Core.Xt1;

namespace Xt1.App.Controls;

/// <summary>
/// Typed editor for every XT1 header field, every WTB table entry and the bank-level values.
/// Every change is applied immediately and reported as an undoable command; nothing is silently
/// normalized, so reserved bytes and custom values survive.
/// </summary>
public sealed class FieldEditorPanel : UserControl, IThemedControl
{
    private static ThemePalette Palette => ThemeManager.Palette;

    private readonly TableLayoutPanel _table;
    private readonly Label _hint;
    private readonly List<Action> _refreshers = new();

    private EditableTexture? _texture;
    private BankSection? _section;
    private BankDocument? _document;

    public FieldEditorPanel()
    {
        BackColor = Palette.Window;

        _table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(6),
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 24,
            ForeColor = Palette.MutedText,
            Text = "Select a texture to edit its fields.",
        };
        ThemeManager.SetRole(_hint, ThemeRole.Muted);

        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        host.Controls.Add(_table);
        Controls.Add(host);
        Controls.Add(_hint);
        Dock = DockStyle.Fill;
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
        _table.SuspendLayout();
        _table.Controls.Clear();
        _table.RowStyles.Clear();
        _table.RowCount = 0;
        _refreshers.Clear();

        if (_texture is null)
        {
            _hint.Text = "Select a texture to edit its fields.";
            _table.ResumeLayout();
            ThemeManager.ApplyTo(this);
            return;
        }

        _hint.Text = _document is null
            ? "Editing fields (no document)."
            : $"{_document.DisplayName} / {_section?.Label} / texture {_texture.Index}";

        var header = _texture.Header;

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
        AddHex("table0 payload offset", () => _texture.PayloadOffset, v => _texture.PayloadOffset = v);
        AddHex("table1 payload size", () => _texture.PayloadSize, v => _texture.PayloadSize = v);
        AddPayloadLengthWarning();
        AddTable2BytesRow(_texture);

        if (_section is not null)
        {
            AddHeader("Bank");
            AddDec("texture count", () => (uint)_section.Bank.Count, v =>
            {
                if (v <= 4096) _section.Bank.Count = (int)v;
            });
            AddBool("include table3 array", () => _section.Bank.IncludeTable3, v => _section.Bank.IncludeTable3 = v);
            AddBool("sync table1 from imageSize", () => _section.Bank.SyncTable1WithImageSize,
                v => _section.Bank.SyncTable1WithImageSize = v);
            AddAction("Recompute imageSize from layout", () =>
            {
                var beforeImageSize = _texture.Header.ImageSize;
                var beforeTable1 = _texture.PayloadSize;
                TextureOperations.RecomputeImageSize(_texture);
                var afterImageSize = _texture.Header.ImageSize;
                var afterTable1 = _texture.PayloadSize;
                CommandProduced?.Invoke(new DelegateCommand("Recompute imageSize",
                    () =>
                    {
                        _texture.Header.ImageSize = afterImageSize;
                        _texture.PayloadSize = afterTable1;
                    },
                    () =>
                    {
                        _texture.Header.ImageSize = beforeImageSize;
                        _texture.PayloadSize = beforeTable1;
                    }));
                RefreshValues();
            });
        }

        if (_document?.HeaderDat is not null) AddDatSection("DAT container (header side)", _document.HeaderDat);
        if (_document?.PayloadDat is not null) AddDatSection("DAT container (payload side)", _document.PayloadDat);

        var rawHint = new Label
        {
            AutoSize = true,
            ForeColor = Palette.MutedText,
            Margin = new Padding(3, 6, 3, 3),
            Text = "The format, texture type, dimensions, hash and table2 flags are edited in the panel above.",
        };
        ThemeManager.SetRole(rawHint, ThemeRole.Muted);
        AddRow(string.Empty, rawHint);

        _table.ResumeLayout();
        ThemeManager.ApplyTo(this);
    }

    /// <summary>Repaint the whole editor for the active theme; dynamic labels recompute their colour.</summary>
    public void ApplyTheme(ThemePalette palette)
    {
        BackColor = palette.Window;
        foreach (Control child in Controls) ThemeManager.ApplyTo(child);
        _hint.ForeColor = palette.MutedText;
        RefreshValues();
    }

    // --- row builders -------------------------------------------------------------------

    private void AddHeader(string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 10, 3, 3),
        };
        int row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.Controls.Add(label, 0, row);
        _table.SetColumnSpan(label, 2);
    }

    private void AddRow(string label, Control editor, Action? refresh = null)
    {
        var caption = new Label { Text = label, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        int row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.Controls.Add(caption, 0, row);
        _table.Controls.Add(editor, 1, row);
        if (refresh is not null) _refreshers.Add(refresh);
    }

    private void AddHex(string label, Func<uint> get, Action<uint> set)
    {
        var box = new TextBox { Width = 130, Margin = new Padding(3, 3, 3, 3) };
        void Refresh() => box.Text = "0x" + get().ToString("X8", CultureInfo.InvariantCulture);
        Refresh();
        box.Validating += (_, _) =>
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
        };
        AddRow(label, box, Refresh);
    }

    private void AddHex64(string label, Func<ulong> get, Action<ulong> set)
    {
        var box = new TextBox { Width = 180, Margin = new Padding(3, 3, 3, 3) };
        void Refresh() => box.Text = Mem.Hex(get());
        Refresh();
        box.Validating += (_, _) =>
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
        };
        AddRow(label, box, Refresh);
    }

    private void AddDec(string label, Func<uint> get, Action<uint> set)
    {
        var box = new TextBox { Width = 130, Margin = new Padding(3, 3, 3, 3) };
        void Refresh() => box.Text = get().ToString(CultureInfo.InvariantCulture);
        Refresh();
        box.Validating += (_, _) =>
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
        };
        AddRow(label, box, Refresh);
    }

    private void AddBool(string label, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
        void Refresh() => box.Checked = get();
        Refresh();
        box.CheckedChanged += (_, _) =>
        {
            bool value = box.Checked;
            bool before = get();
            if (before == value) return;
            set(value);
            CommandProduced?.Invoke(new DelegateCommand(label, () => set(value), () => set(before)));
        };
        AddRow(label, box, Refresh);
    }

    private void AddAction(string label, Action action)
    {
        var button = new Button { Text = label, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
        button.Click += (_, _) => action();
        AddRow(string.Empty, button);
    }

    private void AddLayoutRow(Xt1Header header)
    {
        var host = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        var box = new TextBox { Width = 110 };
        var info = new Label { AutoSize = true, Margin = new Padding(6, 6, 3, 3) };

        void Refresh()
        {
            box.Text = "0x" + header.Layout.ToString("X8", CultureInfo.InvariantCulture);
            info.Text = $"blockHeightLog2 = {header.BlockHeightLog2} (high bits preserved)";
        }
        Refresh();
        box.Validating += (_, _) =>
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
        };
        host.Controls.Add(box);
        host.Controls.Add(info);
        AddRow("layout @0x30", host, Refresh);
    }

    private void AddPayloadLengthWarning()
    {
        var label = new Label { AutoSize = true, ForeColor = Palette.WarnText, Margin = new Padding(3, 6, 3, 3) };
        void Refresh()
        {
            if (_texture is null) return;
            long expected = (long)_texture.Header.ImageSize;
            long actual = _texture.Payload.LongLength;
            label.Text = expected == actual
                ? $"payload {actual} bytes"
                : $"payload {actual} bytes but imageSize is {expected}";
            label.ForeColor = expected == actual ? Palette.MutedText : Palette.WarnText;
        }
        AddRow(string.Empty, label, Refresh);
    }

    /// <summary>
    /// Raw table2 bytes. The decoded flags and the hash live in the inspector above; this is the
    /// byte-exact fallback for values the flags do not cover.
    /// </summary>
    private void AddTable2BytesRow(EditableTexture texture)
    {
        var box = new TextBox { Width = 130, Margin = new Padding(3, 3, 3, 3) };
        void RefreshBytes() => box.Text = Mem.HexBytes(texture.Table2Bytes);

        RefreshBytes();
        box.Validating += (_, _) =>
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
        };
        AddRow("table2 (4 bytes)", box, RefreshBytes);
    }

    /// <summary>
    /// DAT sub-file table. The list is virtualized: some DATs hold thousands of sub-files
    /// (shader.dat has 5588), and creating a control per sub-file made the panel take minutes to
    /// lay out. Only the selected sub-file gets an editor.
    /// </summary>
    private void AddDatSection(string title, DatFile dat)
    {
        AddHeader(title);

        var host = new Panel
        {
            Height = 214,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0),
        };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false,
            VirtualMode = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        list.Columns.Add("#", 46);
        list.Columns.Add("tag", 46);
        list.Columns.Add("name", 190);
        list.Columns.Add("offset", 84);
        list.Columns.Add("size", 84);
        list.VirtualListSize = dat.SubFiles.Count;
        list.RetrieveVirtualItem += (_, e) =>
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= dat.SubFiles.Count)
            {
                e.Item = new ListViewItem(string.Empty);
                return;
            }
            var sub = dat.SubFiles[e.ItemIndex];
            e.Item = new ListViewItem(new[]
            {
                e.ItemIndex.ToString(CultureInfo.InvariantCulture),
                sub.Tag,
                sub.Name,
                "0x" + sub.Offset.ToString("X8", CultureInfo.InvariantCulture),
                "0x" + sub.DescriptorSize.ToString("X8", CultureInfo.InvariantCulture),
            });
        };

        var editor = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            WrapContents = false,
            AutoSize = false,
            Padding = new Padding(0),
        };
        var tagBox = new TextBox { Width = 48, Enabled = false, Margin = new Padding(3, 3, 6, 3) };
        var nameBox = new TextBox { Width = 170, Enabled = false, Margin = new Padding(3, 3, 6, 3) };
        var offsetBox = new TextBox { Width = 88, Enabled = false, Margin = new Padding(3, 3, 6, 3) };
        var sizeBox = new TextBox { Width = 88, Enabled = false, Margin = new Padding(3, 3, 0, 3) };
        var detail = new Label { AutoSize = true, ForeColor = Palette.MutedText, Margin = new Padding(6, 8, 0, 0) };
        ThemeManager.SetRole(detail, ThemeRole.Muted);

        editor.Controls.Add(new Label { Text = "tag", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        editor.Controls.Add(tagBox);
        editor.Controls.Add(new Label { Text = "name", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        editor.Controls.Add(nameBox);
        editor.Controls.Add(new Label { Text = "offset", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        editor.Controls.Add(offsetBox);
        editor.Controls.Add(new Label { Text = "size", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        editor.Controls.Add(sizeBox);
        editor.Controls.Add(detail);

        host.Controls.Add(list);
        host.Controls.Add(editor);
        AddRow("sub-files", host, () =>
        {
            list.VirtualListSize = dat.SubFiles.Count;
            list.Invalidate();
            LoadEditor();
        });

        int SelectedIndex()
        {
            return list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1;
        }

        void LoadEditor()
        {
            int index = SelectedIndex();
            bool valid = index >= 0 && index < dat.SubFiles.Count;
            tagBox.Enabled = nameBox.Enabled = offsetBox.Enabled = sizeBox.Enabled = valid;
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

        void ApplyTo(int index, string what, string before, string after, Action<DatSubFile, string> set)
        {
            if (before == after) return;
            set(dat.SubFiles[index], after);
            CommandProduced?.Invoke(new DelegateCommand($"Set sub-file {index} {what}",
                () => set(dat.SubFiles[index], after),
                () => set(dat.SubFiles[index], before)));
            list.Invalidate();
        }

        list.SelectedIndexChanged += (_, _) => LoadEditor();

        tagBox.Validating += (_, _) =>
        {
            int index = SelectedIndex();
            if (index >= 0) ApplyTo(index, "tag", dat.SubFiles[index].Tag, tagBox.Text, (s, v) => s.Tag = v);
        };

        nameBox.Validating += (_, _) =>
        {
            int index = SelectedIndex();
            if (index >= 0) ApplyTo(index, "name", dat.SubFiles[index].Name, nameBox.Text, (s, v) => s.Name = v);
        };

        offsetBox.Validating += (_, _) =>
        {
            int index = SelectedIndex();
            if (index < 0 || !TryParseUInt(offsetBox.Text, out uint value)) return;
            if (value == dat.SubFiles[index].Offset) return;

            // A manual offset only means something when the writer does not recompute the layout.
            dat.RecomputeOffsetsOnWrite = false;
            ApplyTo(index, "offset", dat.SubFiles[index].Offset.ToString(CultureInfo.InvariantCulture),
                value.ToString(CultureInfo.InvariantCulture), (s, v) => s.Offset = uint.Parse(v, CultureInfo.InvariantCulture));
            LoadEditor();
        };

        sizeBox.Validating += (_, _) =>
        {
            int index = SelectedIndex();
            if (index < 0 || !TryParseUInt(sizeBox.Text, out uint value)) return;
            if (value == dat.SubFiles[index].DescriptorSize) return;

            ApplyTo(index, "descriptor", dat.SubFiles[index].DescriptorSize.ToString(CultureInfo.InvariantCulture),
                value.ToString(CultureInfo.InvariantCulture), (s, v) => s.DescriptorSize = uint.Parse(v, CultureInfo.InvariantCulture));
            LoadEditor();
        };

        LoadEditor();
    }

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
