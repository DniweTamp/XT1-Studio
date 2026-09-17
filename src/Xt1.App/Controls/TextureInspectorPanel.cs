using System.Globalization;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Util;
using Xt1.Core.Xt1;

namespace Xt1.App.Controls;

/// <summary>
/// The single right-hand inspector: identity, validation, the table3 hash, the confirmed table2
/// flags, and the developer fields behind an "Advanced" toggle.
///
/// Only values that are meaningful to change by hand are editable here. The format, dimensions,
/// type and mip count are read-only: they are properties of the loaded texture, and changing them
/// without re-encoding the payload would only produce a broken record, so the way to change them is
/// to import/replace the texture.
///
/// The card structure is built once and only the values are refreshed, so switching between
/// textures does not rebuild a control tree.
/// </summary>
public sealed class TextureInspectorPanel : UserControl
{
    private static readonly Font TitleFont = new("Segoe UI", 12.5f, FontStyle.Bold);
    private static readonly Font SubtitleFont = new("Segoe UI", 8.75f);
    private static readonly Font CardTitleFont = new("Segoe UI", 9f, FontStyle.Bold);
    private static readonly Font MonoFont = new("Consolas", 9.5f);
    private static readonly Color MutedColor = Color.FromArgb(108, 108, 120);
    private static readonly Color SubtitleColor = Color.FromArgb(132, 132, 146);
    private static readonly Color HeadingColor = Color.FromArgb(46, 46, 56);
    private static readonly Color WarnBack = Color.FromArgb(254, 240, 240);
    private static readonly Color WarnColor = Color.FromArgb(170, 32, 32);
    private static readonly Color OkColor = Color.FromArgb(62, 118, 70);
    private static readonly Color OkBack = Color.FromArgb(240, 248, 240);
    private static readonly Color PendingBack = Color.FromArgb(244, 244, 250);

    /// <summary>One shared tooltip for the whole panel; a per-control instance would be collected.</summary>
    private static readonly ToolTip Tips = new();

    private readonly TextBox _hashBox;
    private readonly Button _hashCompute;
    private readonly CheckBox _modelBox;
    private readonly CheckBox _srgbBox;
    private readonly CheckBox _cubeBox;
    private readonly Label _table2Note;
    private readonly Panel _validationCard;
    private readonly Label _validationText;
    private readonly Button _advancedToggle;
    private readonly Panel _advancedHost;
    private readonly TextBox _summaryBox;
    private readonly FieldEditorPanel _fields = new() { Dock = DockStyle.Top, Height = 620 };
    private readonly List<Action> _refreshers = new();
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly Label _spec;
    private readonly Panel _emptyHint;

    private BankDocument? _document;
    private BankSection? _section;
    private EditableTexture? _texture;
    private bool _suppress;
    private bool _advanced;

    public TextureInspectorPanel()
    {
        BackColor = Color.FromArgb(246, 246, 249);
        Dock = DockStyle.Fill;

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(10, 10, 10, 10),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // --- Identity --------------------------------------------------------------------
        _title = new Label { AutoSize = true, Font = TitleFont, ForeColor = HeadingColor, Margin = new Padding(2, 0, 0, 0) };
        _subtitle = new Label { AutoSize = true, Font = SubtitleFont, ForeColor = SubtitleColor, Margin = new Padding(3, 1, 0, 0) };
        _spec = new Label { AutoSize = true, MaximumSize = new Size(400, 0), Font = SubtitleFont, ForeColor = MutedColor, Margin = new Padding(3, 1, 0, 6) };
        var identity = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6),
        };
        identity.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        identity.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        identity.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        identity.Controls.Add(_title, 0, 0);
        identity.Controls.Add(_subtitle, 0, 1);
        identity.Controls.Add(_spec, 0, 2);
        AddToStack(stack, identity);

        _emptyHint = new Panel { Dock = DockStyle.Fill, AutoSize = true, Height = 40, BackColor = Color.Transparent };
        _emptyHint.Controls.Add(new Label
        {
            Text = "Select a texture in the tree to inspect it.",
            AutoSize = true,
            ForeColor = MutedColor,
            Location = new Point(4, 4),
        });

        // --- Validation ------------------------------------------------------------------
        // Directly under the identity so a texture that cannot be trusted is the first thing seen.
        _validationText = new Label { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(4, 2, 4, 2) };
        _validationCard = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 6, 8, 6),
            Margin = new Padding(0, 0, 0, 8),
            BackColor = OkBack,
        };
        _validationCard.Controls.Add(_validationText);
        AddToStack(stack, _validationCard);

        // --- Hash ------------------------------------------------------------------------
        _hashBox = new TextBox { Width = 110, Font = MonoFont };
        SetTip(_hashBox, "The CRC32 hash (table3), pasted as hex (0x prefix optional). Editing it fixes the " +
                         "value written on save");
        _hashCompute = new Button { Text = "Calculate CRC32", AutoSize = true, FlatStyle = FlatStyle.System };
        SetTip(_hashCompute, "Ask for a name and compute crc32(name) & 0x7FFFFFFF");
        var hashRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
        hashRow.Controls.Add(_hashBox);
        hashRow.Controls.Add(_hashCompute);

        var hashCard = NewCard("Hash", out var hashGrid);
        AddRow(hashGrid, "CRC32 Hash", hashRow);
        AddToStack(stack, hashCard);

        // --- Flags -----------------------------------------------------------------------
        _modelBox = NewFlag("Model / UI bank entry", "table2 byte 0, bit 0x20 - set for model and UI bank entries, clear otherwise");
        _srgbBox = NewFlag("sRGB color space", "table2 byte 3, bit 0x04");
        _cubeBox = NewFlag("Cube map usage", "table2 byte 3, bit 0x80");
        _table2Note = new Label { AutoSize = true, ForeColor = MutedColor, Font = MonoFont, Margin = new Padding(0, 6, 0, 0) };
        var flagsCard = NewCard("Flags", out var flagsGrid);
        AddRow(flagsGrid, "Bank class", _modelBox);
        AddRow(flagsGrid, "Color", _srgbBox);
        AddRow(flagsGrid, "Usage", _cubeBox);
        AddRow(flagsGrid, string.Empty, _table2Note);
        AddToStack(stack, flagsCard);

        // --- Advanced --------------------------------------------------------------------
        _advancedToggle = new Button
        {
            Text = "Show advanced fields",
            AutoSize = true,
            FlatStyle = FlatStyle.System,
            Margin = new Padding(0, 2, 0, 6),
        };
        _advancedToggle.Click += (_, _) => AdvancedExpanded = !AdvancedExpanded;
        // Kept at its natural width instead of stretching across the panel like a card.
        var advancedRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            WrapContents = false,
        };
        advancedRow.Controls.Add(_advancedToggle);
        AddToStack(stack, advancedRow);

        _summaryBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Width = 320,
            Height = 150,
            Font = MonoFont,
            BackColor = Color.White,
            Text = "(no technical summary)",
        };

        var hexButton = new Button { Text = "View raw header bytes...", AutoSize = true, FlatStyle = FlatStyle.System };
        SetTip(hexButton, "Open the XT1 header in the byte-level editor; unknown bytes survive a round trip");
        hexButton.Click += (_, _) => RequestHex?.Invoke(_texture?.Header.Raw ?? Array.Empty<byte>(), "XT1 header");

        _advancedHost = new Panel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false };
        var advancedStack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
        };
        advancedStack.Controls.Add(_summaryBox, 0, 0);
        advancedStack.Controls.Add(hexButton, 0, 1);
        advancedStack.Controls.Add(_fields, 0, 2);
        _advancedHost.Controls.Add(advancedStack);
        AddToStack(stack, _advancedHost);

        var scrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(246, 246, 249) };
        scrollHost.Controls.Add(stack);
        Controls.Add(scrollHost);
        Controls.Add(_emptyHint);

        _fields.CommandProduced += command => CommandProduced?.Invoke(command);

        WireEvents();
        RefreshValues();
    }

    public bool AdvancedExpanded
    {
        get => _advanced;
        set
        {
            _advanced = value;
            _advancedHost.Visible = value;
            _advancedToggle.Text = value ? "Hide advanced fields" : "Show advanced fields";
            if (value) _fields.RefreshValues();
        }
    }

    /// <summary>Raised with an already-applied command so the caller can push it onto the history.</summary>
    public event Action<IEditCommand>? CommandProduced;

    /// <summary>Raised when a byte buffer should be opened in the raw hex editor.</summary>
    public event Action<byte[], string>? RequestHex;

    /// <summary>The raw/typed developer editor shown under "Advanced".</summary>
    public FieldEditorPanel PropertiesPanel => _fields;

    public void SetTechnicalSummary(string text) => _summaryBox.Text = text;

    public void Bind(BankDocument? document, BankSection? section, EditableTexture? texture)
    {
        _document = document;
        _section = section;
        _texture = texture;

        _emptyHint.Visible = texture is null;
        _fields.Bind(document, section, texture);
        RefreshValues();
    }

    public void RefreshValues()
    {
        _suppress = true;
        try
        {
            foreach (var refresher in _refreshers) refresher();
        }
        finally
        {
            _suppress = false;
        }
        _fields.RefreshValues();
    }

    /// <summary>
    /// Show the validation verdict. <paramref name="decodeError"/> means the texture could not be
    /// decoded at all, which is the case worth shouting about. <paramref name="pending"/> is used
    /// while a volume is still being decoded off the UI thread, so the card never asserts a verdict
    /// it does not have yet.
    /// </summary>
    public void SetValidation(IReadOnlyList<string> warnings, string? decodeError, bool pending = false)
    {
        var lines = new List<string>();
        if (decodeError is not null) lines.Add("Decode failed: " + decodeError);
        foreach (var warning in warnings) lines.Add(warning);

        bool bad = lines.Count > 0;
        if (pending && !bad)
        {
            _validationCard.BackColor = PendingBack;
            _validationText.ForeColor = MutedColor;
            _validationText.Text = "Decoding volume...";
            return;
        }

        _validationCard.BackColor = bad ? WarnBack : OkBack;
        _validationText.ForeColor = bad ? WarnColor : OkColor;
        _validationText.Text = bad
            ? string.Join(Environment.NewLine, lines.Select(l => "- " + l))
            : "Decoded cleanly - the layout matches the stored image size.";
    }

    // --- construction helpers -----------------------------------------------------------

    private static void AddToStack(TableLayoutPanel stack, Control card)
    {
        int row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Dock = DockStyle.Fill;
        stack.Controls.Add(card, 0, row);
    }

    private static TableLayoutPanel NewCard(string title, out TableLayoutPanel grid)
    {
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10, 8, 10, 9),
            Margin = new Padding(0, 0, 0, 8),
        };

        var caption = new Label
        {
            Text = title,
            AutoSize = true,
            Font = CardTitleFont,
            ForeColor = HeadingColor,
            Margin = new Padding(0, 0, 0, 4),
        };

        grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(caption, 0, 0);
        card.Controls.Add(grid, 0, 1);
        return card;
    }

    private static void AddRow(TableLayoutPanel grid, string caption, Control editor)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (caption.Length > 0)
        {
            grid.Controls.Add(new Label
            {
                Text = caption,
                AutoSize = true,
                ForeColor = MutedColor,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 5, 6, 0),
            }, 0, row);
        }

        editor.Anchor = AnchorStyles.Left;
        editor.Margin = new Padding(0, 2, 0, 2);
        grid.Controls.Add(editor, 1, row);
    }

    private static CheckBox NewFlag(string text, string tooltip)
    {
        var box = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0) };
        Tips.SetToolTip(box, tooltip);
        return box;
    }

    private void SetTip(Control control, string text) => Tips.SetToolTip(control, text);

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MiB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KiB";
        return bytes + " B";
    }

    // --- wiring -------------------------------------------------------------------------

    private void WireEvents()
    {
        _hashBox.Validating += (_, _) =>
        {
            if (_texture is null) return;
            if (!TryParseHash(_hashBox.Text, out uint value))
            {
                RefreshValues();
                return;
            }
            uint before = _texture.Hash;
            HashMode beforeMode = _texture.HashMode;
            if (before == value && beforeMode == HashMode.Forced) return;
            _texture.Hash = value;
            _texture.HashMode = HashMode.Forced;
            CommandProduced?.Invoke(new DelegateCommand("Set table3 hash",
                () => { _texture.Hash = value; _texture.HashMode = HashMode.Forced; },
                () => { _texture.Hash = before; _texture.HashMode = beforeMode; }));
            RefreshValues();
        };

        _hashCompute.Click += (_, _) => PromptHashFromName();

        WireFlag(_modelBox, t => t.IsModelBankClass, (t, v) => t.SetModelBankClass(v), "Set bank class");
        WireFlag(_srgbBox, t => t.IsSrgbFlag, (t, v) => t.SetSrgbFlag(v), "Set sRGB flag");
        WireFlag(_cubeBox, t => t.IsCubeUsageFlag, (t, v) => t.SetCubeUsageFlag(v), "Set cube usage flag");

        _refreshers.Add(() =>
        {
            var texture = _texture;
            bool has = texture is not null;
            _hashBox.Enabled = has;
            _hashCompute.Enabled = has;
            _modelBox.Enabled = _srgbBox.Enabled = _cubeBox.Enabled = has;

            if (texture is null)
            {
                _title.Text = "No texture selected";
                _subtitle.Text = "Open a bank and pick a texture from the tree.";
                _spec.Text = string.Empty;
                _hashBox.Text = string.Empty;
                _table2Note.Text = string.Empty;
                return;
            }

            var header = texture.Header;

            _title.Text = $"Texture #{texture.Index}";
            _subtitle.Text = Subtitle(texture);
            _spec.Text = Spec(texture);

            _hashBox.Text = "0x" + texture.Hash.ToString("X8", CultureInfo.InvariantCulture);
            _modelBox.Checked = texture.IsModelBankClass;
            _srgbBox.Checked = texture.IsSrgbFlag;
            _cubeBox.Checked = texture.IsCubeUsageFlag;
            _table2Note.Text = "table2 = " + Mem.HexBytes(texture.Table2Bytes);
        });
    }

    /// <summary>Bank branch, position in it and the texture type.</summary>
    private string Subtitle(EditableTexture texture)
    {
        var parts = new List<string>();
        if (_section is not null)
        {
            parts.Add(_section.Label);
            var textures = _section.Bank.Textures;
            for (int i = 0; i < textures.Count; i++)
            {
                if (!ReferenceEquals(textures[i], texture)) continue;
                parts.Add($"{i + 1} of {_section.Bank.Count}");
                break;
            }
        }
        else if (_document?.RawXt1 is not null)
        {
            parts.Add("raw .xt1");
        }
        parts.Add(texture.Header.Type.Describe());
        return string.Join("  |  ", parts);
    }

    /// <summary>
    /// The properties that describe the texture. Read-only on purpose: they are a consequence of the
    /// payload, so the way to change them is to import/replace the texture.
    /// </summary>
    private static string Spec(EditableTexture texture)
    {
        var header = texture.Header;
        var info = texture.FormatInfo;
        var parts = new List<string>
        {
            info is null
                ? $"unknown format (0x{header.FormatValue:X2})"
                : info.DisplayName,
            $"{(ulong)header.Width} x {(ulong)header.Height} x {header.Depth}",
            header.Type.Describe(),
            header.MipCount == 1 ? "1 mip level" : $"{header.MipCount} mip levels",
            FormatSize((long)Math.Min(header.ImageSize, (ulong)long.MaxValue)),
        };
        return string.Join("  |  ", parts);
    }

    private void PromptHashFromName()
    {
        if (_texture is null) return;
        using var dialog = new TextPromptDialog("Calculate CRC32", "Texture name:", _texture.Name ?? string.Empty);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        ComputeHashFromName(dialog.Value);
    }

    private void ComputeHashFromName(string? name)
    {
        if (_texture is null) return;
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(FindForm(), "A name is needed to compute the hash.",
                "Name required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _texture.Name = name.Trim();
        uint before = _texture.Hash;
        HashMode beforeMode = _texture.HashMode;
        _texture.ComputeHashFromName();
        uint after = _texture.Hash;
        CommandProduced?.Invoke(new DelegateCommand("Compute hash from name",
            () => { _texture.Hash = after; _texture.HashMode = HashMode.Forced; },
            () => { _texture.Hash = before; _texture.HashMode = beforeMode; }));
        RefreshValues();
    }

    private void WireFlag(CheckBox box, Func<EditableTexture, bool> get, Action<EditableTexture, bool> set, string name)
    {
        box.CheckedChanged += (_, _) =>
        {
            if (_suppress || _texture is null) return;
            bool value = box.Checked;
            bool before = get(_texture);
            if (before == value) return;
            set(_texture, value);
            CommandProduced?.Invoke(new DelegateCommand(name,
                () => set(_texture!, value),
                () => set(_texture!, before)));
            RefreshValues();
        };
    }

    // --- test hooks ---------------------------------------------------------------------

    /// <summary>Test hook used by <c>--uitest</c>.</summary>
    internal string UiTestHashText => _hashBox.Text;

    /// <summary>Test hook used by <c>--uitest</c>: the confirmed table2 flags the panel exposes.</summary>
    internal string UiTestFlagState =>
        $"model={_modelBox.Checked} srgb={_srgbBox.Checked} cube={_cubeBox.Checked}";

    /// <summary>Test hook used by <c>--uitest</c>: the validation verdict text.</summary>
    internal string UiTestValidationText => _validationText.Text;

    /// <summary>Test hook used by <c>--uitest</c>: set the hash field the way pasting would.</summary>
    internal void UiTestPasteHash(string text)
    {
        _hashBox.Text = text;
        if (!TryParseHash(text, out uint value)) return;
        _texture!.Hash = value;
        _texture.HashMode = HashMode.Forced;
        RefreshValues();
    }

    /// <summary>
    /// Test hook used by <c>--uitest</c>: run the CRC32 button's computation with a supplied name,
    /// since the real button opens a modal prompt that an automated run cannot answer.
    /// </summary>
    internal void UiTestComputeHashFor(string name) => ComputeHashFromName(name);

    // --- parsing ------------------------------------------------------------------------

    /// <summary>Hashes are pasted as hex, with or without the 0x prefix.</summary>
    private static bool TryParseHash(string text, out uint value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        text = text.Replace(" ", string.Empty).Replace("_", string.Empty);
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) ||
               uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
