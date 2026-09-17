using System.Globalization;
using System.Text;

namespace Xt1.App.Controls;

/// <summary>
/// Byte-level hex editor. Shows 16 bytes per line with offsets and an ASCII column; edits are
/// validated and raised as a single replacement so the caller can make them undoable.
/// </summary>
public sealed class HexEditorControl : UserControl
{
    private readonly TextBox _text;
    private readonly Label _status;
    private readonly Button _apply;
    private readonly Button _revert;
    private byte[]? _buffer;
    private string _caption = string.Empty;

    public HexEditorControl()
    {
        _text = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            AcceptsTab = true,
            MaxLength = int.MaxValue,
        };

        _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        _apply = new Button { Text = "Apply", AutoSize = true };
        _revert = new Button { Text = "Revert", AutoSize = true };
        _apply.Click += (_, _) => Apply();
        _revert.Click += (_, _) => RefreshText();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_revert);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 30, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        Controls.Add(_text);
        Controls.Add(bottom);
        Dock = DockStyle.Fill;
    }

    /// <summary>Raised when the user applies an edit; args are (newBytes, oldBytes).</summary>
    public event Action<byte[], byte[]>? BytesApplied;

    public byte[]? Buffer => _buffer;

    public void Bind(byte[] buffer, string caption)
    {
        _buffer = buffer;
        _caption = caption;
        RefreshText();
    }

    public void Clear()
    {
        _buffer = null;
        _text.Text = string.Empty;
        _status.Text = string.Empty;
    }

    public void RefreshText()
    {
        if (_buffer is null)
        {
            _text.Text = string.Empty;
            return;
        }

        var builder = new StringBuilder(_buffer.Length * 4);
        for (int i = 0; i < _buffer.Length; i += 16)
        {
            builder.Append(i.ToString("X4", CultureInfo.InvariantCulture)).Append(": ");
            int count = Math.Min(16, _buffer.Length - i);
            for (int j = 0; j < 16; j++)
            {
                builder.Append(j < count ? _buffer[i + j].ToString("X2", CultureInfo.InvariantCulture) : "  ");
                builder.Append(j == 7 ? "  " : " ");
            }
            builder.Append(" |");
            for (int j = 0; j < count; j++)
            {
                byte b = _buffer[i + j];
                builder.Append(b is >= 32 and < 127 ? (char)b : '.');
            }
            builder.AppendLine("|");
        }
        _text.Text = builder.ToString();
        _status.Text = $"{_caption}: {_buffer.Length} bytes";
    }

    private void Apply()
    {
        if (_buffer is null) return;

        var parsed = ParseHex(_text.Text, out var error);
        if (parsed is null)
        {
            _status.Text = "parse error: " + error;
            return;
        }

        if (parsed.Length != _buffer.Length)
        {
            _status.Text = $"length mismatch: {parsed.Length} bytes entered, {_buffer.Length} expected";
            return;
        }

        var before = (byte[])_buffer.Clone();
        BytesApplied?.Invoke(parsed, before);
        _status.Text = $"{_caption}: applied {_buffer.Length} bytes";
        RefreshText();
    }

    internal static byte[]? ParseHex(string text, out string? error)
    {
        error = null;
        var bytes = new List<byte>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            int bar = line.IndexOf('|');
            if (bar >= 0) line = line[..bar];
            int colon = line.IndexOf(':');
            if (colon >= 0) line = line[(colon + 1)..];

            foreach (var token in line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length is 0 or > 2)
                {
                    error = $"invalid token '{token}'";
                    return null;
                }
                if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                {
                    error = $"invalid hex byte '{token}'";
                    return null;
                }
                bytes.Add(value);
            }
        }
        return bytes.ToArray();
    }
}
