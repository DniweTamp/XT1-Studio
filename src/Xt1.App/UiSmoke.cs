using System.Diagnostics;
using Xt1.App.Controls;
using Xt1.Core.Xt1;
using Xt1.Core.Containers;
using Xt1.Core.Settings;

namespace Xt1.App;

/// <summary>
/// <c>Xt1.App.exe --uitest [file]</c>: builds the real UI control tree off-screen, binds a texture
/// to the properties panel and pumps the message loop, printing a timestamp per step. Used to
/// locate layout/UI hangs without a human at the keyboard.
/// </summary>
internal static class UiSmoke
{
    internal static int Run(string? path, Action<string> log)
    {
        var watch = Stopwatch.StartNew();
        void Step(string message) => log($"[{watch.ElapsedMilliseconds,6} ms] {message}");

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

            var document = BankDocument.Load(target);
            Step($"loaded {document.DisplayName}: {document.Sections.Count} bank(s)");

            var section = document.Sections[0];
            var texture = section.Bank.Textures[0];
            Step($"picked texture #{texture.Index} {texture.FormatInfo?.Name}");

            var form = new Form { ClientSize = new Size(1400, 900), Text = "UITest" };
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var infoPage = new TabPage("Info");
            var propertiesPage = new TabPage("Properties");
            var hexPage = new TabPage("Raw hex");

            var panel = new FieldEditorPanel { Dock = DockStyle.Fill };
            propertiesPage.Controls.Add(panel);
            tabs.TabPages.Add(infoPage);
            tabs.TabPages.Add(propertiesPage);
            tabs.TabPages.Add(hexPage);
            form.Controls.Add(tabs);
            Step("control tree built");

            form.Show();
            Pump(log, ref watch, "form shown", 20);

            panel.Bind(document, section, texture);
            Pump(log, ref watch, "Bind() returned", 20);

            tabs.SelectedIndex = 1;
            Pump(log, ref watch, "properties tab selected", 40);

            // Force a resize, which is what triggers AutoSize/AutoScroll relayout.
            form.ClientSize = new Size(1000, 700);
            Pump(log, ref watch, "resized smaller", 40);
            form.ClientSize = new Size(1500, 950);
            Pump(log, ref watch, "resized larger", 40);

            panel.RefreshValues();
            Pump(log, ref watch, "RefreshValues()", 20);

            form.Close();
            Step("closed");

            // Phase 2: the real MainForm, driven the way a user would click the Advanced toggle.
            Step("--- real MainForm ---");
            var main = new Forms.MainForm(target);
            main.Show();
            Pump(log, ref watch, "MainForm shown (file loaded in OnShown)", 40);
            log($"       status: {main.UiTestStatus}");

            main.UiTestSelectRightTab(1);
            Pump(log, ref watch, "advanced fields shown", 40);

            main.UiTestSelectTexture(0, 1);
            Pump(log, ref watch, "selected texture #1", 40);

            main.UiTestSelectRightTab(0);
            Pump(log, ref watch, "advanced fields hidden", 20);
            main.UiTestSelectRightTab(1);
            Pump(log, ref watch, "advanced fields shown again", 40);

            main.Close();
            Step("MainForm closed");

            // Phase 3: the inspector, driven the way typing and clicking would: the hash can be pasted,
            // the CRC32 button computes from the name, and the tree offers the edit/export actions.
            Step("--- inspector ---");
            var inspectorForm = new Forms.MainForm(target);
            inspectorForm.Show();
            Pump(log, ref watch, "inspector MainForm shown", 40);
            inspectorForm.UiTestSelectTexture(0, 1);
            Pump(log, ref watch, "selected texture #1", 30);

            log($"       inspector:  {inspectorForm.UiTestInspectorState}");
            log($"       validation: {inspectorForm.UiTestInspectorValidation}");
            Expect(log, inspectorForm.UiTestInspectorState.Contains("hash=0x"),
                "the inspector shows the CRC32 hash", inspectorForm.UiTestInspectorState);
            Expect(log, inspectorForm.UiTestInspectorValidation.Length > 0,
                "the inspector shows a validation verdict", inspectorForm.UiTestInspectorValidation);

            inspectorForm.UiTestPasteHash("DEADBEEF");
            Pump(log, ref watch, "hash pasted as DEADBEEF (no 0x)", 10);
            log($"       inspector:  {inspectorForm.UiTestInspectorState}");
            Expect(log, inspectorForm.UiTestInspectorState.Contains("hash=0xDEADBEEF"),
                "a pasted hash without 0x is parsed as hex", inspectorForm.UiTestInspectorState);

            inspectorForm.UiTestComputeHashFor("test_texture");
            Pump(log, ref watch, "CRC32 computed from the name", 10);
            var expected = Xt1.Core.Xt1.Crc32NameHash.ComputeMasked("test_texture");
            log($"       inspector:  {inspectorForm.UiTestInspectorState}");
            Expect(log, inspectorForm.UiTestInspectorState.Contains($"hash=0x{expected:X8}"),
                "Compute CRC32 matches crc32(name) & 0x7FFFFFFF", $"0x{expected:X8}");

            // The raw hex editor now lives in its own dialog; make sure it still binds the header.
            var hexState = inspectorForm.UiTestOpenHex();
            Pump(log, ref watch, "raw bytes dialog opened", 10);
            log($"       hex dialog: {hexState}");
            Expect(log, hexState.Contains("visible=True") && !hexState.Contains("bytes=-1"),
                "the raw bytes dialog binds the header", hexState);

            var textureMenu = inspectorForm.UiTestTreeMenuItems();
            foreach (var item in textureMenu) log($"       menu: {item}");
            Expect(log, textureMenu.Contains("Replace...|enabled"),
                "the texture menu offers replace", "Replace...");
            Expect(log, textureMenu.Contains("Replace with .xt1...|enabled"),
                "the texture menu offers a raw replace", "Replace with .xt1...");
            Expect(log, textureMenu.Contains("Delete texture|enabled"),
                "the texture menu offers delete", "Delete texture");
            Expect(log, textureMenu.Contains("Export|enabled"),
                "the texture menu offers the grouped export", "Export");

            // The bank node keeps the bank-level actions.
            var sectionNode = inspectorForm.UiTestSelectNode("textures)");
            Pump(log, ref watch, $"selected the bank node ({sectionNode})", 20);
            var bankMenu = inspectorForm.UiTestTreeMenuItems();
            Expect(log, bankMenu.Contains("Export every texture in this bank (PNG folder)...|enabled"),
                "the bank menu offers the bank export", "Export every texture in this bank (PNG folder)...");

            inspectorForm.Close();
            Step("inspector MainForm closed");

            // Phase 3: the dialogs, built and laid out headlessly so a broken AutoSize/AutoScroll
            // arrangement is caught without a human opening each one.
            Step("--- dialogs ---");
            using (var settingsDialog = new Forms.SettingsDialog(AppSettings.Load()))
            {
                settingsDialog.Show();
                Pump(log, ref watch, "settings dialog shown", 40);
                var rows = settingsDialog.UiTestRowAlignment();
                int misaligned = rows.Count(r =>
                    r.EditorLeft < r.LabelRight - 2 || Math.Abs(r.LabelCenterY - r.EditorCenterY) > 6);
                Expect(log, rows.Count > 0 && misaligned == 0, "settings rows align",
                    $"{rows.Count} rows, {misaligned} misaligned");
                settingsDialog.Close();
            }
            using (var newBank = new Forms.NewBankDialog())
            {
                newBank.Show();
                Pump(log, ref watch, "new-bank dialog shown", 20);
                newBank.Close();
            }
            using (var recompress = new Forms.BatchRecompressDialog(
                       (_, _, _) => Task.CompletedTask, Xt1.Core.Encoding.EncoderBackend.Nvtt, hasDocument: true))
            {
                recompress.Show();
                Pump(log, ref watch, "batch-recompress dialog shown", 40);
                recompress.Close();
            }

            // Phase 3: a 3D texture, to exercise the volume view wiring the way clicks would:
            // the assembled render, per-channel isolation, the opaque switch and the slice planes.
            var volumeTarget = new[]
            {
                @"D:\Bayonetta3\romfs\core\core.dat",
                @"D:\Bayonetta3\romfs\core\coreui.dat",
            }.FirstOrDefault(File.Exists);
            if (volumeTarget is not null)
            {
                Step("--- 3D volume ---");
                var volumeForm = new Forms.MainForm(volumeTarget);
                volumeForm.Show();
                Pump(log, ref watch, "3D MainForm shown", 40);

                var picked = volumeForm.UiTestSelectFirstVolume();
                PumpUntil(log, ref watch, () => volumeForm.UiTestStatus.Contains("assembled", StringComparison.OrdinalIgnoreCase),
                    $"volume assembled ({picked})");
                log($"       status: {volumeForm.UiTestStatus}");
                log($"       state:  {volumeForm.UiTestVolumeState}");
                Expect(log, picked != "none", "a 3D texture was found", picked);
                Expect(log, volumeForm.UiTestStatus.Contains("assembled", StringComparison.OrdinalIgnoreCase),
                    "the volume is assembled", volumeForm.UiTestStatus);
                Expect(log, volumeForm.UiTestVolumeState.Contains("tab=1"),
                    "the 3D tab is shown", volumeForm.UiTestVolumeState);
                Expect(log, volumeForm.UiTestVolumeState.Contains("planes=False"),
                    "slice planes are off by default (assembled view)", volumeForm.UiTestVolumeState);

                volumeForm.UiTestSelectChannel(1);
                Pump(log, ref watch, "channel R selected", 10);
                log($"       state:  {volumeForm.UiTestVolumeState}");
                Expect(log, volumeForm.UiTestVolumeState.Contains("channel=Red"),
                    "channel isolation reaches the 3D viewport", volumeForm.UiTestVolumeState);

                string beforeOpaque = RenderModeOf(volumeForm.UiTestVolumeState);
                volumeForm.UiTestToggleOpaque();
                Pump(log, ref watch, "Opaque toggled", 10);
                log($"       state:  {volumeForm.UiTestVolumeState}");
                Expect(log, RenderModeOf(volumeForm.UiTestVolumeState) != beforeOpaque,
                    "the opaque switch reaches the 3D viewport",
                    $"{beforeOpaque} -> {RenderModeOf(volumeForm.UiTestVolumeState)}");

                volumeForm.UiTestSelectVolumeMode(2);
                Pump(log, ref watch, "MIP selected", 10);
                log($"       state:  {volumeForm.UiTestVolumeState}");
                Expect(log, volumeForm.UiTestVolumeState.Contains("render=Mip"),
                    "the render mode reaches the 3D viewport", volumeForm.UiTestVolumeState);

                // Switching to a 2D texture must leave the volume view cleanly behind.
                volumeForm.UiTestSelectTexture(0, 0);
                Pump(log, ref watch, "switched to a 2D texture", 20);
                log($"       state:  {volumeForm.UiTestVolumeState}");
                Expect(log, volumeForm.UiTestVolumeState.Contains("tab=0"),
                    "a 2D texture shows the 2D tab", volumeForm.UiTestVolumeState);

                volumeForm.Close();
                Step("3D MainForm closed");
            }

            if (failedChecks > 0)
            {
                log($"UI-SMOKE FAILED ({failedChecks} check(s))");
                return 1;
            }

            if (slowSteps > 0)
            {
                log($"UI-SMOKE FAILED ({slowSteps} step(s) over {SlowStepMs} ms)");
                return 1;
            }

            log("UI-SMOKE OK");
            return 0;
        }
        catch (Exception ex)
        {
            log($"[error] {ex}");
            return 1;
        }
    }

    private static int failedChecks;

    private static void Expect(Action<string> log, bool passed, string what, string detail)
    {
        log($"  [{(passed ? "ok" : "XX")}] {what}: {detail}");
        if (!passed) failedChecks++;
    }

    /// <summary>
    /// Pump the message loop until a condition holds. Volume decoding runs off the UI thread and posts
    /// its result back, so this waits in real time (a tight DoEvents loop only starves the decode) and
    /// gives up after a budget rather than spinning forever.
    /// </summary>
    private static void PumpUntil(Action<string> log, ref Stopwatch watch, Func<bool> condition, string label,
        int budgetMs = 15000)
    {
        var waiter = Stopwatch.StartNew();
        int pumps = 0;
        while (!condition() && waiter.ElapsedMilliseconds < budgetMs)
        {
            Application.DoEvents();
            Thread.Sleep(5);
            pumps++;
        }
        Pump(log, ref watch, $"{label} (after {pumps} pump(s), {waiter.ElapsedMilliseconds} ms)", 0);
    }

    /// <summary>Pull <c>render=...</c> out of a <c>UiTestVolumeState</c> string.</summary>
    private static string RenderModeOf(string state)
    {
        const string key = "render=";
        int at = state.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return string.Empty;
        at += key.Length;
        int end = state.IndexOf(' ', at);
        return end < 0 ? state[at..] : state[at..end];
    }

    /// <summary>Steps slower than this are reported as hangs (the panel used to take 112 s here).</summary>
    private const long SlowStepMs = 5000;

    private static int slowSteps;

    private static void Pump(Action<string> log, ref Stopwatch watch, string label, int iterations)
    {
        for (int i = 0; i < iterations; i++)
            Application.DoEvents();
        watch.Stop();

        long elapsed = watch.ElapsedMilliseconds;
        bool slow = elapsed > SlowStepMs;
        if (slow) slowSteps++;
        log($"[{elapsed,6} ms] {label} (+{iterations} pumps){(slow ? "   <-- SLOW" : string.Empty)}");

        watch.Restart();
    }
}
