using System.Diagnostics;
using System.Text;
using KartLibrary.File;
using RhoViewerPortable;

namespace RhoViewerPortable.Tests;

internal static class PickerAndGridLifecycleTests
{
    public static void Run()
    {
        TestPinFileAndRhoFolderPickersStayDistinct();
        TestSelectedPinBecomesItsGameDirectory();
        TestShownUpdateGridStaysVirtualAndUnsorted();
        TestShownFormLoadsSyntheticArchive();
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

    private static void TestShownFormLoadsSyntheticArchive()
    {
        const int fileCount = 1;
        var root = Path.Combine(Path.GetTempPath(), "RhoViewerPortable.LoadLifecycle." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rhoPath = Path.Combine(root, "synthetic.rho");
        try
        {
            WriteSyntheticRho(rhoPath);

            using var form = new MainForm();
            Exception? failure = null;
            var handlerFinished = false;
            var heartbeats = 0;
            using var heartbeat = new System.Windows.Forms.Timer { Interval = 50 };
            heartbeat.Tick += (_, _) => heartbeats++;
            using var timeout = new System.Windows.Forms.Timer { Interval = 8000 };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                if (!handlerFinished)
                {
                    failure ??= new TimeoutException(
                        "shown archive load did not finish." + Environment.NewLine +
                        "status=" + form.StatusTextForTests + Environment.NewLine +
                        "busy=" + form.IsBusyForTests + Environment.NewLine +
                        "rows=" + form.ResourceGridRowCount + Environment.NewLine +
                        "tree=" + form.ResourceTreeOutcome + Environment.NewLine +
                        form.ResourceTreeLog);
                }
                form.AllowCloseForTests();
                form.Close();
            };
            form.Shown += async (_, _) =>
            {
                try
                {
                    heartbeat.Start();
                    var beatsBeforeLoad = heartbeats;
                    await form.LoadPathsForSmokeAsync(new[] { rhoPath });
                    var beatsAfterLoad = heartbeats;
                    if (form.IsBusyForTests)
                        throw new InvalidOperationException("LoadPathsAsync returned while the form was still busy." + Diagnose(form));
                    if (form.ResourceGridRowCount != fileCount)
                        throw new InvalidOperationException($"loaded grid row count was {form.ResourceGridRowCount}, expected {fileCount}." + Diagnose(form));

                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < deadline &&
                           !form.ResourceTreeOutcome.StartsWith("attached", StringComparison.Ordinal) &&
                           !form.ResourceTreeOutcome.StartsWith("failed", StringComparison.Ordinal))
                        await Task.Delay(50);
                    if (!form.ResourceTreeOutcome.StartsWith("attached", StringComparison.Ordinal))
                        throw new InvalidOperationException("resource tree did not attach." + Diagnose(form));

                    var beatsAtAttach = heartbeats;
                    await Task.Delay(200);
                    if (heartbeats <= beatsAtAttach)
                        throw new InvalidOperationException("UI heartbeat stopped after the archive load." + Diagnose(form));
                    if (beatsAfterLoad < beatsBeforeLoad)
                        throw new InvalidOperationException("UI heartbeat counter moved backwards." + Diagnose(form));

                    form.SetResourceSearchForTests("sample");
                    await Task.Delay(500);
                    if (form.IsBusyForTests || form.ResourceGridRowCount != 1)
                        throw new InvalidOperationException($"search filter did not narrow the shown grid (rows={form.ResourceGridRowCount}, busy={form.IsBusyForTests})." + Diagnose(form));

                    form.SetResourceSearchForTests(string.Empty);
                    await Task.Delay(500);
                    if (form.IsBusyForTests || form.ResourceGridRowCount != fileCount)
                        throw new InvalidOperationException($"clearing the search did not restore the shown grid (rows={form.ResourceGridRowCount}, busy={form.IsBusyForTests})." + Diagnose(form));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    handlerFinished = true;
                    heartbeat.Stop();
                    timeout.Stop();
                    form.AllowCloseForTests();
                    form.Close();
                }
            };

            timeout.Start();
            Application.Run(form);
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Diagnose(MainForm form)
        => Environment.NewLine + "status=" + form.StatusTextForTests +
           Environment.NewLine + "busy=" + form.IsBusyForTests +
           Environment.NewLine + "rows=" + form.ResourceGridRowCount +
           Environment.NewLine + "tree=" + form.ResourceTreeOutcome +
           Environment.NewLine + form.ResourceTreeLog;

    private static void WriteSyntheticRho(string rhoPath)
    {
        var payload = Encoding.ASCII.GetBytes("lifecycle sample resource");
        File.WriteAllBytes(rhoPath, HighFixRegressionTests.CreateSyntheticRhoFixture(payload));

        // Fail at the fixture boundary instead of allowing MainForm's error dialog
        // to turn a broken test archive into an empty-grid lifecycle failure.
        using var archive = new RhoArchive();
        archive.Open(rhoPath);
        var file = archive.RootFolder.GetFile("sample.bin")
            ?? throw new InvalidDataException("Synthetic lifecycle Rho fixture did not contain sample.bin.");
        if (!file.GetBytes().AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("Synthetic lifecycle Rho fixture payload did not round-trip.");
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
