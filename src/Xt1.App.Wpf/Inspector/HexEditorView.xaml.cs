using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Xt1.App.Wpf.Inspector;

/// <summary>
/// Byte-level hex editor. Port of <c>Xt1.App.Controls.HexEditorControl</c>: 16 bytes per line with
/// offsets and an ASCII column; edits are validated and raised as a single replacement so the caller
/// can make them undoable.
/// </summary>
public partial class HexEditorView : UserControl
{
    private byte[]? _buffer;
    private string _caption = string.Empty;

    public HexEditorView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user applies an edit; args are (newBytes, oldBytes).</summary>
    public event Action<byte[], byte[]>? BytesApplied;

    /// <summary>The live buffer passed to <see cref="Bind"/>; edits are copied into it by the caller.</summary>
    public byte[]? Buffer => _buffer;

    public void Bind(byte[] buffer, string caption)
    {
        _buffer = buffer;
        _caption = caption;
        RefreshText();
    }

    public void RefreshText()
    {
        var buffer = _buffer;
        if (buffer is null)
        {
            HexText.Text = string.Empty;
            return;
        }

        var builder = new StringBuilder(buffer.Length * 4);
        for (int i = 0; i < buffer.Length; i += 16)
        {
            builder.Append(i.ToString("X4", CultureInfo.InvariantCulture)).Append(": ");
            int count = Math.Min(16, buffer.Length - i);
            for (int j = 0; j < 16; j++)
            {
                builder.Append(j < count ? buffer[i + j].ToString("X2", CultureInfo.InvariantCulture) : "  ");
                builder.Append(j == 7 ? "  " : " ");
            }
            builder.Append(" |");
            for (int j = 0; j < count; j++)
            {
                byte b = buffer[i + j];
                builder.Append(b is >= 32 and < 127 ? (char)b : '.');
            }
            builder.AppendLine("|");
        }
        HexText.Text = builder.ToString();
        StatusText.Text = $"{_caption}: {buffer.Length} bytes";
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void OnRevert(object sender, RoutedEventArgs e) => RefreshText();

    private void Apply()
    {
        var buffer = _buffer;
        if (buffer is null) return;

        var parsed = ParseHex(HexText.Text, out var error);
        if (parsed is null)
        {
            StatusText.Text = "parse error: " + error;
            return;
        }

        if (parsed.Length != buffer.Length)
        {
            StatusText.Text = $"length mismatch: {parsed.Length} bytes entered, {buffer.Length} expected";
            return;
        }

        var before = (byte[])buffer.Clone();
        BytesApplied?.Invoke(parsed, before);
        StatusText.Text = $"{_caption}: applied {buffer.Length} bytes";
        RefreshText();
    }

    private static byte[]? ParseHex(string text, out string? error)
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

