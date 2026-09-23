using System.Windows;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;

namespace Xt1.App.Wpf.Dialogs;

/// <summary>Options for importing/replacing a texture from an image file.</summary>
public partial class TextureImportDialog : ThemedDialog
{
    private readonly List<FormatInfo> _formats;

    public FormatInfo? SelectedFormat =>
        FormatCombo.SelectedIndex >= 0 && FormatCombo.SelectedIndex < _formats.Count
            ? _formats[FormatCombo.SelectedIndex]
            : null;

    /// <summary>Always NVTT when available; EncoderPicker substitutes the built-in encoder otherwise.</summary>
    public EncoderBackend Backend => EncoderBackend.Nvtt;

    public EncodeQuality Quality =>
        QualityCombo.SelectedItem is EncodeQuality quality ? quality : EncodeQuality.Normal;

    public bool GenerateMips => MipsCheck.IsChecked == true;

    /// <summary>Edge-pad the RGB of transparent texels before encoding (avoids dark halos).</summary>
    public bool Solidify => SolidifyCheck.IsChecked == true;

    /// <summary>Set the encoded texture's alpha to 255 everywhere.</summary>
    public bool MakeOpaque => OpaqueCheck.IsChecked == true;

    public TextureImportDialog(string sourceDescription, RgbaImage image, FormatInfo? currentFormat,
        EncodeQuality defaultQuality, bool generateMips, bool solidify, bool makeOpaque)
    {
        InitializeComponent();

        SourceText.Text = sourceDescription;
        SizeText.Text = $"{image.Width} x {image.Height} x {image.Depth}";

        // Every encodable format is offered. BC7 is the 0x4D/0x4E pair the game actually uses; the
        // id in the label makes it explicit for anyone checking against other tools.
        _formats = FormatTable.All
            .Where(f => f.Codec is CodecKind.Bc or CodecKind.Astc or CodecKind.Raw)
            .OrderBy(f => f.Value)
            .ToList();
        FormatCombo.ItemsSource = _formats.Select(f => f.DisplayName).ToList();
        int index = currentFormat is null ? -1 : _formats.FindIndex(f => f.Format == currentFormat.Format);
        if (index < 0) index = _formats.FindIndex(f => f.Format == Xt1Format.Bc7Unorm);
        FormatCombo.SelectedIndex = index >= 0 ? index : 0;

        QualityCombo.ItemsSource = Enum.GetValues<EncodeQuality>();
        QualityCombo.SelectedItem = defaultQuality;
        MipsCheck.IsChecked = generateMips;
        SolidifyCheck.IsChecked = solidify;
        OpaqueCheck.IsChecked = makeOpaque;
    }

    private void OnImport(object sender, RoutedEventArgs e) => DialogResult = true;
}
