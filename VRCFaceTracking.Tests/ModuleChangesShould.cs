using System.IO.Compression;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Tests;

[TestClass, DoNotParallelize]
public class ModuleChangesShould
{
    private static async Task<(string Zip, string OldFile)> Package(TestDirectory temp, Guid id)
    {
        var content = System.IO.Path.Combine(temp.Path, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(System.IO.Path.Combine(content, "new.dll"), "new module");
        await File.WriteAllTextAsync(System.IO.Path.Combine(content, "module.json"), JsonConvert.SerializeObject(
            new TrackingModuleMetadata { ModuleId = id, DllFileName = "new.dll" }));
        var zip = System.IO.Path.Combine(temp.Path, "module.zip");
        ZipFile.CreateFromDirectory(content, zip);
        var oldDirectory = System.IO.Path.Combine(temp.Path, "modules", id.ToString());
        Directory.CreateDirectory(oldDirectory);
        var oldFile = System.IO.Path.Combine(oldDirectory, "old.dll");
        await File.WriteAllTextAsync(oldFile, "old module");
        return (zip, oldFile);
    }

    [TestMethod]
    public async Task PreserveOldFilesWhenStoppingModulesFails()
    {
        using var temp = new TestDirectory();
        var package = await Package(temp, Guid.NewGuid());
        var manager = new ScriptedManager { BeforeChange = () => throw new IOException("stop failed") };
        var installer = new ModuleInstaller(NullLogger<ModuleInstaller>.Instance, manager, System.IO.Path.Combine(temp.Path, "modules"));
        await Assert.ThrowsExceptionAsync<IOException>(() => installer.InstallLocalModule(package.Zip));
        Assert.AreEqual("old module", await File.ReadAllTextAsync(package.OldFile));
        Assert.AreEqual(0, Directory.GetDirectories(temp.Path, "*.backup").Length);
    }

    [TestMethod]
    public async Task PreserveOldFilesWhenBackupMoveFails()
    {
        using var temp = new TestDirectory();
        var package = await Package(temp, Guid.NewGuid());
        var manager = new ScriptedManager();
        var installer = new ModuleInstaller(NullLogger<ModuleInstaller>.Instance, manager, System.IO.Path.Combine(temp.Path, "modules"));
        if (OperatingSystem.IsWindows())
        {
            using var lockedFile = new FileStream(package.OldFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            await Assert.ThrowsExceptionAsync<IOException>(() => installer.InstallLocalModule(package.Zip));
        }
        else
        {
            var mode = File.GetUnixFileMode(temp.Path);
            manager.BeforeChange = () =>
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            };
            try { await Assert.ThrowsExceptionAsync<IOException>(() => installer.InstallLocalModule(package.Zip)); }
            finally { File.SetUnixFileMode(temp.Path, mode); }
        }
        Assert.AreEqual("old module", await File.ReadAllTextAsync(package.OldFile));
    }

    [TestMethod]
    public async Task RestoreOldFilesWhenReloadFails()
    {
        using var temp = new TestDirectory();
        var package = await Package(temp, Guid.NewGuid());
        var manager = new ScriptedManager { AfterChange = () => throw new IOException("reload failed") };
        var installer = new ModuleInstaller(NullLogger<ModuleInstaller>.Instance, manager, System.IO.Path.Combine(temp.Path, "modules"));
        await Assert.ThrowsExceptionAsync<IOException>(() => installer.InstallLocalModule(package.Zip));
        Assert.AreEqual("old module", await File.ReadAllTextAsync(package.OldFile));
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(package.OldFile)!, "new.dll")));
        Assert.AreEqual(0, Directory.GetDirectories(temp.Path, "*.backup").Length);
    }

    [TestMethod]
    public async Task InstallValidatedFilesAndRemoveOnlyItsOwnBackup()
    {
        using var temp = new TestDirectory();
        var package = await Package(temp, Guid.NewGuid());
        var installer = new ModuleInstaller(NullLogger<ModuleInstaller>.Instance, new ScriptedManager(), System.IO.Path.Combine(temp.Path, "modules"));
        var installed = await installer.InstallLocalModule(package.Zip);
        Assert.AreEqual("new module", await File.ReadAllTextAsync(installed!));
        Assert.IsFalse(File.Exists(package.OldFile));
        Assert.AreEqual(0, Directory.GetDirectories(temp.Path, "*.backup").Length);
    }

    [TestMethod]
    public async Task RejectMissingDllBeforeStoppingOrReplacingModules()
    {
        using var temp = new TestDirectory();
        var package = await Package(temp, Guid.NewGuid());
        var stopped = false;
        var manager = new ScriptedManager { BeforeChange = () => stopped = true };
        var invalid = System.IO.Path.Combine(temp.Path, "invalid.zip");
        File.Delete(System.IO.Path.Combine(temp.Path, "content", "new.dll"));
        ZipFile.CreateFromDirectory(System.IO.Path.Combine(temp.Path, "content"), invalid);
        var installer = new ModuleInstaller(NullLogger<ModuleInstaller>.Instance, manager, System.IO.Path.Combine(temp.Path, "modules"));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => installer.InstallLocalModule(invalid));
        Assert.IsFalse(stopped);
        Assert.AreEqual("old module", await File.ReadAllTextAsync(package.OldFile));
    }

    [TestMethod]
    public async Task SerializeChangesAndInitializeThroughRollbackAndReload()
    {
        var processPath = System.IO.Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "VRCFaceTracking.ModuleProcess.exe" : "VRCFaceTracking.ModuleProcess");
        var created = !File.Exists(processPath);
        if (created) await File.WriteAllTextAsync(processPath, "test placeholder; never started");
        var data = new ModuleData();
        var coordinator = new SendCoordinator(null!, NullLogger<SendCoordinator>.Instance);
        var manager = new UnifiedLibManager(NullLoggerFactory.Instance, new Dispatcher(), data, coordinator);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var recovered = false;
        var secondEntered = false;
        try
        {
            var first = manager.ChangeModules(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                throw new IOException("change failed");
            }, () => recovered = true);
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            var second = manager.ChangeModules(() => { Assert.IsTrue(recovered); secondEntered = true; });
            var initialize = manager.Initialize();
            await Task.Delay(50);
            Assert.IsFalse(secondEntered);
            Assert.IsFalse(initialize.IsCompleted);
            release.Set();
            await Assert.ThrowsExceptionAsync<IOException>(() => first);
            await Task.WhenAll(second, initialize).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(secondEntered);
        }
        finally
        {
            release.Set();
            await manager.TeardownAllModules();
            var server = typeof(UnifiedLibManager).GetField("_sandboxServer", BindingFlags.NonPublic | BindingFlags.Static)!;
            (server.GetValue(null) as IDisposable)?.Dispose();
            server.SetValue(null, null);
            if (created) File.Delete(processPath);
        }
    }
}
