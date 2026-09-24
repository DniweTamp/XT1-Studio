using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Xt1.Core.Settings;
using Xt1.Core.Xt1;

namespace Xt1.App.Wpf.Cli;

/// <summary>
/// <c>XT1 Studio.exe --uitest [file]</c>: build the real shell off-screen, bind banks and textures
/// to it and pump the dispatcher, printing a timestamp per step. Used to locate layout/UI hangs and to
/// exercise the shell (inspector hash/flags, hex dialog, tree menu, file browser sorting) without a
/// human at the keyboard.
///
/// This is the WPF counterpart of the WinForms <c>--uitest</c>. Checks that were WinForms-specific
/// (the GDI icon handle, the native-tool reload path) are reported through their WPF equivalents.
/// </summary>
internal static class UiSmoke
{
    internal static int Run(string? path, Action<string> log)
    {
        var watch = Stopwatch.StartNew();
        int failures = 0;
        int slowSteps = 0;
        long lastStepMs = 0;

        // The step time is the delta since the previous step, not the total, so the budget below
        // measures the step itself. A step over the budget counts as a failure: this harness exists to
        // notice a layout blow-up, and a number nobody checks is not a check.
        void Step(string message)
        {
            long now = watch.ElapsedMilliseconds;
            long delta = now - lastStepMs;
            lastStepMs = now;

            bool slow = delta > SlowStepMs;
            if (slow)
            {
                slowSteps++;
                failures++;
            }

            log($"[{now,6} ms]{(slow ? " SLOW" : string.Empty)} (+{delta,4} ms) {message}");
        }

        void Expect(string what, bool passed, string detail)
        {
            log($"  [{(passed ? "ok" : "XX")}] {what}: {detail}");
            if (!passed) failures++;
        }

        try
        {
            Step("begin");

            var target = path;
            if (target is null || !File.Exists(target))
            {
                target = new[]
                {
                    @"D:\Bayonetta3\romfs\ui\ui_core.dat",
                    @"D:\Bayonetta3\romfs\pl\pl0080.dat",
                    @"D:\Bayonetta3\romfs\em\em0000.dat",
                }.FirstOrDefault(File.Exists);
            }

            if (target is null)
            {
                log("[skip] no sample file available");
                return 0;
            }

            return HeadlessCli.RunOnUiThread(log, () =>
            {
                var window = OffscreenWindow();
                try
                {
                    window.Show();
                    Pump(20);
                    Step($"shell shown ({window.Width:0}x{window.Height:0})");

                    window.Width = 1100;
                    window.Height = 720;
                    Pump(20);
                    Step("resized narrow");
                    window.Width = 1500;
                    window.Height = 950;
                    Pump(20);
                    Step("resized wide");

                    // The window icon must come from the embedded resource, not a system fallback.
                    Expect("the app icon loads from the embedded resource", window.Icon is not null,
                        window.Icon is null ? "no icon" : "icon set");

                    // --- real shell with a bank ---
                    Step("--- shell with a bank ---");
                    window.UiTestLoadFile(target);
                    Pump(30);
                    Step($"loaded {Path.GetFileName(target)}");
                    log($"       status: {window.UiTestStatus}");
                    Expect("the bank reports as loaded", window.UiTestStatus.Contains("Loaded"),
                        window.UiTestStatus);

                    window.UiTestSelectTexture(0, 1);
                    Pump(30);
                    Step("selected texture #1");
                    log($"       inspector:  {window.UiTestInspectorState}");
                    log($"       validation: {window.UiTestInspectorValidation}");
                    Expect("the inspector shows the CRC32 hash",
                        window.UiTestInspectorState.Contains("hash=0x"), window.UiTestInspectorState);
                    Expect("the inspector shows a validation verdict",
                        window.UiTestInspectorValidation.Length > 0, window.UiTestInspectorValidation);

                    window.UiTestPasteHash("DEADBEEF");
                    Pump(10);
                    log($"       inspector:  {window.UiTestInspectorState}");
                    Expect("a pasted hash without 0x is parsed as hex",
                        window.UiTestInspectorState.Contains("hash=0xDEADBEEF"), window.UiTestInspectorState);

                    window.UiTestComputeHashFor("test_texture");
                    Pump(10);
                    var expected = Crc32NameHash.ComputeMasked("test_texture");
                    log($"       inspector:  {window.UiTestInspectorState}");
                    Expect("Compute CRC32 matches crc32(name) & 0x7FFFFFFF",
                        window.UiTestInspectorState.Contains($"hash=0x{expected:X8}"), $"0x{expected:X8}");

                    var hexState = window.UiTestOpenHex();
                    Pump(10);
                    log($"       hex dialog: {hexState}");
                    Expect("the raw bytes dialog binds the header",
                        hexState.Contains("visible=True") && !hexState.Contains("bytes=-1"), hexState);

                    var textureMenu = window.UiTestTreeMenuItems();
                    foreach (var item in textureMenu) log($"       menu: {item}");
                    Expect("the texture menu offers replace", textureMenu.Contains("Replace...|enabled"),
                        "Replace...");
                    Expect("the texture menu offers a raw replace",
                        textureMenu.Contains("Replace with .xt1...|enabled"), "Replace with .xt1...");
                    Expect("the texture menu offers delete",
                        textureMenu.Contains("Delete texture|enabled"), "Delete texture");
                    Expect("the texture menu offers the grouped export",
                        textureMenu.Contains("Export|enabled"), "Export");

                    // The bank node keeps the bank-level actions.
                    var sectionNode = window.UiTestSelectBankNode();
                    Pump(20);
                    Step($"selected the bank node ({sectionNode})");
                    var bankMenu = window.UiTestTreeMenuItems();
                    Expect("the bank menu offers the bank export",
                        bankMenu.Contains("Export every texture in this bank (PNG folder)...|enabled"),
                        "Export every texture in this bank (PNG folder)...");

                    // The file browser lists every openable file in the folder, in sortable columns.
                    var files = window.UiTestFileListItems();
                    string active = Path.GetFileName(target);
                    Expect("the file browser lists the folder's openable files", files.Count > 1,
                        $"{files.Count} files, first: {files.FirstOrDefault()}");
                    Expect("the active file is in the list",
                        files.Any(f => string.Equals(f, active, StringComparison.OrdinalIgnoreCase)),
                        string.Join(", ", files.Take(3)));
                    Expect("payload-only files are not listed",
                        files.All(f => !f.Contains(".dtt", StringComparison.OrdinalIgnoreCase)),
                        string.Join(", ", files.Take(3)));

                    var headers = window.UiTestFileColumnHeaders();
                    Log(log, headers);
                    Expect("the browser has Modified and Size columns",
                        headers.Count == 3
                        && headers.Any(h => h.StartsWith("Modified", StringComparison.Ordinal))
                        && headers.Any(h => h.StartsWith("Size", StringComparison.Ordinal)),
                        "3 columns");

                    // Clicking a header sorts by it; clicking again reverses, and the indicator follows.
                    var byModified = window.UiTestSortFileColumn(1);
                    var modifiedHeaders = window.UiTestFileColumnHeaders();
                    Log(log, modifiedHeaders);
                    Expect("clicking Modified sorts by it ascending",
                        modifiedHeaders.Any(h => h == "Modified \u25B2"), "Modified ^");
                    var byModifiedDesc = window.UiTestSortFileColumn(1);
                    var descHeaders = window.UiTestFileColumnHeaders();
                    Log(log, descHeaders);
                    Expect("clicking again reverses the sort",
                        descHeaders.Any(h => h == "Modified \u25BC")
                        && byModifiedDesc.FirstOrDefault() != byModified.FirstOrDefault(),
                        "Modified v and a new first row");
                    Expect("sorting by Name keeps every row", window.UiTestSortFileColumn(0).Count == files.Count,
                        "Name");

                    // Picking a sibling row exercises the load path. A bank with no texture bank
                    // (coreph.dat) is a purposeful refusal, not a crash, so both outcomes count.
                    var sibling = files.FirstOrDefault(f =>
                        !string.Equals(f, active, StringComparison.OrdinalIgnoreCase));
                    if (sibling is not null)
                    {
                        window.UiTestPickFile(sibling);
                        Pump(30);
                        Step($"picked a file row ({sibling})");
                        var picked = window.UiTestStatus;
                        Expect("picking a file exercises the load path",
                            picked.Contains("Loaded") || picked.Contains("Failed to open"), picked);
                        Expect("the loaded file shows its banks",
                            window.UiTestSelectBankNode() != "not found", picked);
                    }
                    else
                    {
                        log("  [--] file pick: n/a (no sibling file)");
                    }

                    // A brand-new empty bank must still offer "Add texture".
                    Step("--- empty bank ---");
                    window.UiTestCreateEmptyBank(standalone: false);
                    Pump(30);
                    var emptyMenu = window.UiTestTreeMenuItems();
                    log($"       menu: {string.Join(", ", emptyMenu)}");
                    Expect("an empty bank can still be filled",
                        emptyMenu.Contains("Add texture from image...|enabled"), string.Join(", ", emptyMenu));

                    // ...and the action it offers must resolve a bank: the menu's fallback and the
                    // handler's lookup have to agree, or the item is enabled but does nothing.
                    var targetBank = window.UiTestTargetBank();
                    log($"       add target: {targetBank}");
                    Expect("the add action resolves a bank to add into", targetBank != "none", targetBank);

                    // What the Settings dialog's OK does, without the dialog.
                    Step("--- settings apply ---");
                    log($"       encoder: {window.UiTestBackendState}");
                    window.UiTestApplySettings();
                    Pump(20);
                    Expect("applying settings keeps an encoder available",
                        window.UiTestBackendState.Length > 0, window.UiTestBackendState);
                    Step("settings applied");

                    // The 3D viewport's shared controls, through the same selection path a user takes.
                    // The original bank is reloaded first, since the empty-bank phase replaced it.
                    Step("--- 3D controls ---");
                    window.UiTestLoadFile(target);
                    Pump(30);
                    var volume = window.UiTestSelectFirstVolume();
                    log($"       volume: {volume}");
                    if (volume is "none" or "no document")
                    {
                        log("  [--] 3D controls: n/a (no volume in this bank)");
                    }
                    else
                    {
                        window.UiTestSelectChannel(1);
                        window.UiTestToggleOpaque();
                        window.UiTestSelectVolumeMode(1);
                        Pump(20);
                        log($"       3D state: {window.UiTestVolumeState}");
                        Expect("the 3D controls reach the viewport",
                            window.UiTestVolumeState.Contains("channel=")
                            && window.UiTestVolumeState.Contains("render="), window.UiTestVolumeState);
                    }

                    // The 2D layer-animation bar: a volume offers it, a flat texture never does.
                    Step("--- layer animation ---");
                    if (window.UiTestSelectFirstVolume() is "none" or "no document")
                    {
                        log("  [--] layer animation: n/a (no volume in this bank)");
                    }
                    else
                    {
                        window.UiTestSelectRightTab(0);
                        Pump(10);
                        var anim = window.UiTestAnimationState;
                        log($"       animation: {anim}");
                        Expect("a volume shows the 2D layer-animation bar", anim.Contains("visible=True"), anim);
                        Expect("the playback speed defaults to 15 fps", anim.Contains("fps=15"), anim);

                        window.UiTestToggleAnimation();
                        Pump(10);
                        Expect("play starts the layer animation",
                            window.UiTestAnimationState.Contains("playing=True"),
                            window.UiTestAnimationState);

                        // Switching preview tabs tears down and re-attaches the hosted GL window; a
                        // paint queued against the old context must not take the window down.
                        for (int i = 0; i < 4; i++)
                        {
                            window.UiTestSelectRightTab((i + 1) % 2);
                            Pump(5);
                        }
                        window.UiTestSelectRightTab(0);
                        Pump(5);
                        Expect("playback survives preview tab switches",
                            window.UiTestAnimationState.Contains("playing=True"),
                            window.UiTestAnimationState);

                        var beforeTick = window.UiTestAnimationState;
                        window.UiTestAnimationTick();
                        Pump(5);
                        var afterTick = window.UiTestAnimationState;
                        Expect("a playback tick advances to the next layer", beforeTick != afterTick,
                            $"{beforeTick} -> {afterTick}");
                        window.UiTestToggleAnimation();
                        Expect("pause stops the layer animation",
                            window.UiTestAnimationState.Contains("playing=False"),
                            window.UiTestAnimationState);

                        window.UiTestSetAnimationFps("300");
                        Expect("an over-range FPS clamps to 240",
                            window.UiTestAnimationState.Contains("fps=240 "),
                            window.UiTestAnimationState);
                        window.UiTestSetAnimationFps("0");
                        Expect("a sub-range FPS clamps to 1",
                            window.UiTestAnimationState.Contains("fps=1 "),
                            window.UiTestAnimationState);

                        window.UiTestSetAnimationFps("30");
                        window.UiTestScrubAnimation(1);
                        Pump(10);
                        var scrubbed = window.UiTestAnimationState;
                        log($"       scrubbed: {scrubbed}");
                        Expect("scrubbing moves the time bar to the requested layer",
                            scrubbed.Contains("frame=2/"), scrubbed);
                        Expect("the FPS field accepts a new playback speed",
                            scrubbed.Contains("fps=30"), scrubbed);
                        Expect("scrubbing pauses playback",
                            scrubbed.Contains("playing=False"), scrubbed);

                        var flat = window.UiTestSelectFirstFlatTexture();
                        log($"       flat texture: {flat}");
                        if (flat is "none" or "no document")
                        {
                            log("  [--] regular 2D texture: n/a (none in this bank)");
                        }
                        else
                        {
                            window.UiTestSelectRightTab(0);
                            Pump(10);
                            var flatState = window.UiTestAnimationState;
                            log($"       flat animation: {flatState}");
                            Expect("a regular 2D texture hides the animation bar",
                                flatState.Contains("visible=False"), flatState);
                        }
                    }

                    // Sky auto exposure: on by default for a cubemap, and toggling it drives the viewport.
                    Step("--- sky auto exposure ---");
                    if (window.UiTestSelectFirstCube() is "none" or "no document")
                    {
                        log("  [--] sky auto exposure: n/a (no cubemap in this bank)");
                    }
                    else
                    {
                        Pump(10);
                        var on = window.UiTestAutoExposureState;
                        log($"       auto exposure: {on}");
                        Expect("sky auto exposure is on by default", on == "check=True applied=True", on);

                        window.UiTestToggleAutoExposure();
                        var off = window.UiTestAutoExposureState;
                        Expect("turning auto exposure off reaches the viewport",
                            off == "check=False applied=False", off);

                        window.UiTestToggleAutoExposure();
                        Expect("turning auto exposure back on reaches the viewport",
                            window.UiTestAutoExposureState == "check=True applied=True",
                            window.UiTestAutoExposureState);
                    }

                    Step("shell closed");
                }
                finally
                {
                    window.Close();
                }

                log($"steps over {SlowStepMs} ms: {slowSteps}");
                log(failures == 0 ? "UI-TEST PASSED" : $"UI-TEST FAILED ({failures} check(s))");
                return failures == 0 ? 0 : 1;
            });
        }
        catch (Exception ex)
        {
            log($"[error] {ex}");
            return 1;
        }
    }

    /// <summary>A shell window that lays out without appearing on screen.</summary>
    private static MainWindow OffscreenWindow() => new()
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -20000,
        Top = 0,
        ShowInTaskbar = false,
    };

    /// <summary>Print column headers with the arrows as ^/v so a console shows them correctly.</summary>
    private static void Log(Action<string> log, IReadOnlyList<string> headers) =>
        log("       headers: " + string.Join(" | ", headers.Select(h => h.Replace('\u25B2', '^').Replace('\u25BC', 'v'))));

    /// <summary>
    /// Let queued work run: the Win32 message loop (the GL viewport's WinForms host needs it) and the
    /// WPF dispatcher. No sleeping — the harness reports per-step times and fails on a slow step, so
    /// any sleep here would be counted as the app's own cost.
    /// </summary>
    private static void Pump(int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            System.Windows.Forms.Application.DoEvents();
            Application.Current?.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    /// <summary>A step taking longer than this counts as a failure (a layout blow-up).</summary>
    private const int SlowStepMs = 5000;
}


