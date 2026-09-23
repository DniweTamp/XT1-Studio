using System.ComponentModel;

namespace Xt1.App.Wpf.Inspector;

/// <summary>
/// Host for <see cref="HexEditorView"/>. Port of the WinForms raw-bytes dialog: a single instance is
/// reused, and the window hides instead of closing so its byte-apply wiring survives between opens.
/// </summary>
public partial class HexEditorDialog : Dialogs.ThemedDialog
{
    private bool _allowClose;

    public HexEditorDialog()
    {
        InitializeComponent();
    }

    public HexEditorView View => HexView;

    /// <summary>Bind a live buffer and show the (reused) dialog.</summary>
    public void ShowFor(byte[] buffer, string caption)
    {
        View.Bind(buffer, caption);
        Title = "Raw bytes - " + caption;
        if (IsVisible) Activate();
        else Show();
    }

    /// <summary>Permit a real close, used when the owning window shuts down.</summary>
    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        // Hide rather than close, so the control (and its undo wiring) survives.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
