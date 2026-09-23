namespace Xt1.App.Theming;

/// <summary>
/// Base form for every window in the app. It paints itself with the active palette on creation,
/// which happens before the first paint, so the theme never flashes the wrong colours on open.
/// </summary>
public class ThemedForm : Form
{
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Only the non-client title bar needs the handle; the full control tree is themed in
        // OnLoad (which runs after this), so the recursive pass is not done twice.
        ThemeManager.ApplyTitleBar(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ThemeManager.Apply(this);
    }
}
