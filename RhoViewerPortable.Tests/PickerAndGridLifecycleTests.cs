using System.Diagnostics;
using RhoViewerPortable;

namespace RhoViewerPortable.Tests;

internal static class PickerAndGridLifecycleTests
{
    public static void Run()
    {
        TestPinFileAndRhoFolderPickersStayDistinct();
        TestSelectedPinBecomesItsGameDirectory();
        TestShownUpdateGridStaysVirtualAndUnsorted();
        TestShownResourceGridCompletesOnUiThread();
    }

    private static void TestPinFileAndRhoFolderPickersStayDistinct()
    {
        using var update = new GameUpdateForm(AppSettings.CreateDefault());
        using var pinDialog = update.CreatePinFileDialog();
        if (pinDialog is not OpenFileDialog)
            throw new InvalidOperationException("KartRider.pin selection must use a file dialog.");
        if (pinDialog.Multiselect || !pinDialog.CheckFileExists)
            throw new InvalidOperationException("KartRider.pin dialog must select one existing file.");
        if (pinDialog.Filter.IndexOf(LocationDialogs.PinFileName, StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("KartRider.pin dialog does not offer the pin file.");

        using var main = new MainForm();
        using var rhoDialog = main.CreateRhoDirectoryDialog();
        if (rhoDialog is not FolderBrowserDialog)
            throw new InvalidOperationException("Rho directory selection must stay a folder dialog.");
        if (!rhoDialog.Description.Contains(".rho", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Rho folder dialog no longer describes package folders.");
        main.AllowCloseForTests();
    }

    private static void TestSelectedPinBecomesItsGameDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "RhoViewerPortable.PinPicker." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pinPath = Path.Combine(root, LocationDialogs.PinFileName);
            File.WriteAllBytes(pinPath, new byte[] { 0x53 });
            var resolved = KartPatchService.ResolveGameDirectory(pinPath);
            if (!string.Equals(resolved, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"selected pin did not resolve to its directory: {resolved}");

            var directoryResolved = KartPatchService.ResolveGameDirectory(root);
            if (!string.Equals(directoryResolved, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("an existing game directory must still be accepted");

            var otherFile = Path.Combine(root, "readme.txt");
            File.WriteAllBytes(otherFile, new byte[] { 1 });
            Expect<InvalidDataException>(() => KartPatchService.ResolveGameDirectory(otherFile), "non-pin file");

            var volumeRoot = Path.GetPathRoot(root);
            if (!string.IsNullOrEmpty(volumeRoot))
                Expect<InvalidOperationException>(() => KartPatchService.ResolveGameDirectory(volumeRoot), "volume root");

            using var update = new GameUpdateForm(AppSettings.CreateDefault());
            if (!update.TryApplySelectedPin(pinPath, out var error))
                throw new InvalidOperationException("applying a selected pin failed: " + error);
            if (!string.Equals(update.GameDirectoryText, resolved, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("the update form did not keep the directory derived from KartRider.pin");
            if (update.TryApplySelectedPin(otherFile, out _))
                throw new InvalidOperationException("the update form accepted a file that is not KartRider.pin");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestShownUpdateGridStaysVirtualAndUnsorted()
    {
        const int itemCount = 5_000;
        var item = new KartPatchPlanItem
        {
            File = new KartPatchFile
            {
                RelativePath = "Data/sample.bin",
                Crc = 0x12345678,
                Size = 12,
                SizeGzipped = 8
            },
            TargetPath = Path.Combine("Data", "sample.bin"),
            State = KartPatchFileState.Mismatch,
            LocalSize = 10
        };
        var plan = new KartPatchPlan
        {
            GameDirectory = Path.GetTempPath(),
            PinPath = Path.Combine(Path.GetTempPath(), LocationDialogs.PinFileName),
            ClientVersion = 1,
            ServerVersion = 2,
            Region = KartUpdateRegion.Auto,
            ServerHost = "127.0.0.1",
            ServerPort = 39311,
            PatchBaseUrl = "https://example.invalid/patch/",
            AllowInsecureHttp = false,
            Items = Enumerable.Repeat(item, itemCount).ToArray()
        };

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var form = new GameUpdateForm(AppSettings.CreateDefault());
        form.Show();
        try
        {
            var started = Stopwatch.StartNew();
            form.RenderPlan(plan);
            Pump(TimeSpan.FromMilliseconds(400));
            if (started.Elapsed > TimeSpan.FromSeconds(3))
                throw new TimeoutException($"shown update grid was still binding after {started.Elapsed}.");
            if (!form.IsUpdateGridVirtualized || form.UpdateGridRowCount != itemCount)
                throw new InvalidOperationException($"shown update grid did not expose {itemCount} virtual rows.");
            if (!form.UpdateGridColumnsAreUnsorted)
                throw new InvalidOperationException("virtual update grid columns must not use automatic sorting.");
        }
        finally
        {
            form.Close();
        }
    }

    private static void TestShownResourceGridCompletesOnUiThread()
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var form = new MainForm();
        form.Show();
        try
        {
            Application.DoEvents();
            if (!form.ResourceGridColumnsAreUnsorted)
                throw new InvalidOperationException("virtual resource grid columns must stay unsorted.");

            form.RequestResourceTreeRefreshForTests();
            PumpUntil(
                () => form.ResourceTreeNodesCreatedOnUiThread.HasValue,
                TimeSpan.FromSeconds(3),
                "resource tree did not finish on the shown form");
            if (form.ResourceTreeNodesCreatedOnUiThread != true)
                throw new InvalidOperationException("resource tree nodes were created off the UI thread.");

            form.SetBusyForTests(true, "正在讀取 1 個封包…");
            Application.DoEvents();
            if (!form.IsBusyForTests)
                throw new InvalidOperationException("resource load did not enter the busy state.");

            form.SetBusyForTests(false, "已載入 0 個封包、0 個資源");
            PumpUntil(
                () => !form.IsBusyForTests && form.ResourceGridCursor != Cursors.WaitCursor,
                TimeSpan.FromSeconds(2),
                "resource grid stayed busy after load completion");
            if (form.ResourceGridRowCount != 0)
                throw new InvalidOperationException("empty resource grid changed row count while clearing busy state.");
        }
        finally
        {
            form.AllowCloseForTests();
            form.Close();
        }
    }

    private static void Pump(TimeSpan budget)
    {
        var start = Stopwatch.StartNew();
        while (start.Elapsed < budget)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static void PumpUntil(Func<bool> done, TimeSpan budget, string message)
    {
        var start = Stopwatch.StartNew();
        while (!done() && start.Elapsed < budget)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }

        if (!done())
            throw new TimeoutException(message + ".");
    }

    private static void Expect<TException>(Action action, string description) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(description + " was accepted.");
    }
}
