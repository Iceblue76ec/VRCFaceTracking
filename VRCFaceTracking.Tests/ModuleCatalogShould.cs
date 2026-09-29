using Microsoft.VisualStudio.TestTools.UnitTesting;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Tests;

[TestClass, DoNotParallelize]
public class ModuleCatalogShould
{
    [TestMethod]
    public async Task KeepNewCatalogWhenAnOlderPreferenceReadFinishesLater()
    {
        var id = Guid.NewGuid();
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var fetches = 0;
        var data = new ModuleData
        {
            Catalog = () => Task.FromResult(new ModuleCatalogResult([
                new InstallableTrackingModule { ModuleId = id, ModuleName = ++fetches == 1 ? "Old" : "New", Version = "1.0" }
            ], false)),
            Installed = () => [new InstallableTrackingModule { ModuleId = id, Version = "1.0" }],
            Enabled = _ => ++reads == 1 ? blocked.Task : Task.FromResult(true)
        };
        var model = new ModuleRegistryViewModel(data);
        var old = model.OnNavigatedTo();
        await model.OnNavigatedTo();
        Assert.AreEqual("New", model.ModuleInfos.Single().TrackingModuleMetadata.ModuleName);
        blocked.SetResult(true);
        await old;
        Assert.AreEqual("New", model.ModuleInfos.Single().TrackingModuleMetadata.ModuleName);
    }

    [TestMethod]
    public async Task KeepPostInstallRowsWhenAnOlderCatalogRefreshResumes()
    {
        var id = Guid.NewGuid();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var installed = true;
        var data = new ModuleData
        {
            Catalog = () => Task.FromResult(new ModuleCatalogResult([
                new InstallableTrackingModule { ModuleId = id, ModuleName = "Remote", Version = "1.0" }
            ], false)),
            Installed = () => installed ? [new InstallableTrackingModule { ModuleId = id, Version = "1.0" }] : [],
            Enabled = _ => ++reads == 2 ? release.Task : Task.FromResult(true)
        };
        var model = new ModuleRegistryViewModel(data);
        await model.OnNavigatedTo();
        var stale = model.OnNavigatedTo();
        installed = false;
        await model.RefreshInstalledAsync();
        release.SetResult(true);
        await stale;
        Assert.AreEqual(InstallState.NotInstalled, model.ModuleInfos.Single().InstallationState);
    }

    [DataTestMethod]
    [DataRow("1.0", "1.0.0", false)]
    [DataRow("1.0.0", "1.0", false)]
    [DataRow("1.0.0.0", "1.0", false)]
    [DataRow("1.0.1", "1.0", true)]
    [DataRow("1.0", "1.0.1", false)]
    public void CompareVersionsWithMissingComponentsAsZero(string remote, string local, bool newer)
        => Assert.AreEqual(newer, ModuleVersion.IsNewer(remote, local));
}
