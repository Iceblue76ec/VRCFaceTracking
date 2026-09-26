using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Helpers;
using VRCFaceTracking.Core.Models;

namespace VRCFaceTracking.Core.Services;

public class ModuleInstaller(ILogger<ModuleInstaller> logger, ILibManager libManager)
{
    private void EnsureCustomLibsDirectoryExists()
    {
        if (!Directory.Exists(Utils.CustomLibsDirectory))
        {
            Directory.CreateDirectory(Utils.CustomLibsDirectory);
        }
    }
    
    // Move a directory using just Copy and Remove as MoveDirectory is not usable across drives
    private static void MoveDirectory(string source, string dest)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
        {
            return;
        }

        if (!Directory.Exists(dest))
        {
            Directory.CreateDirectory(dest);
        }

        // Get files recursively and preserve directory structure
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var path = Path.GetDirectoryName(file);
            var newPath = path?.Replace(source, dest);
            if (newPath == null)
            {
                continue;
            }

            Directory.CreateDirectory(newPath);
            File.Copy(file, Path.Combine(newPath, Path.GetFileName(file)), true);
        }

        // Now we delete the source directory
        Directory.Delete(source, true);
    }

    private static async Task DownloadModuleToFile(
        TrackingModuleMetadata moduleMetadata, string filePath, string md5Hash,
        IProgress<ModuleInstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var client = HappyEyeballsHttp.CreateHttpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(moduleMetadata.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var destination = File.Create(filePath))
        {
            var buffer = new byte[81920];
            var total = response.Content.Headers.ContentLength;
            long received = 0;
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), timeout.Token)) != 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                received += count;
                progress?.Report(new ModuleInstallProgress("Downloading module...",
                    total > 0 ? received * 100.0 / total.Value : null));
            }
        }

        if (!string.IsNullOrEmpty(md5Hash))
        {
            using var md5 = MD5.Create();
            await using var downloadedFile = File.OpenRead(filePath);
            var hash = await md5.ComputeHashAsync(downloadedFile);
            var hashStr = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();

            if (!string.Equals(hashStr, md5Hash.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"MD5 hash mismatch. Expected {md5Hash}, got {hashStr}");
            }
        }
    }

    /* Removes the 'downloaded from the internet' attribute from a module
     * @param DLL file path
     * @return error; if true then the module should be skipped
     */
    [SupportedOSPlatform("windows")]
    private bool RemoveZoneIdentifier(string path)
    {
        string zoneFile = path + ":Zone.Identifier";

        if (Utils.GetFileAttributes(zoneFile) == 0xffffffff) // INVALID_FILE_ATTRIBUTES
            //zone file doesn't exist, everything's good
            return false;

        if (Utils.DeleteFile(zoneFile))
            logger.LogDebug("Removing the downloaded file identifier from " + path);
        else
        {
            logger.LogError("Couldn't removed the 'file downloaded' mark from the " + path + " module! Please unblock the file manually");
            return true;
        }

        return false;
    }

    private string TryFindModuleDll(string moduleDirectory, TrackingModuleMetadata moduleMetadata)
    {
        // Attempt to find the first DLL. If there's more than one, try find the one with the same name as the module
        var dllFiles = Directory.GetFiles(moduleDirectory, "*.dll");

        switch (dllFiles.Length)
        {
            case 0:
                return null;
            // If there's only one, just return it
            case 1:
                return Path.GetFileName(dllFiles[0]);
        }

        // Else we'll try find the one with the closest name to the module using Levenshtein distance
        var targetFileName = Path.GetFileNameWithoutExtension(moduleMetadata.DownloadUrl);
        var dllFile = dllFiles.Select(x => new { FileName = Path.GetFileNameWithoutExtension(x), Distance = LevenshteinDistance.Calculate(targetFileName, Path.GetFileNameWithoutExtension(x)) }).MinBy(x => x.Distance);

        if (dllFile == null)
        {
            logger.LogError(
                "Module {module} has no .dll file name specified and no .dll files were found in the extracted zip",
                moduleMetadata.ModuleId);
            return null;
        }

        logger.LogDebug("Module {module} didn't specify a target dll, and contained multiple. Using {dll} as its distance of {distance} was closest to the module name",
            moduleMetadata.ModuleId, dllFile.FileName, dllFile.Distance);
        return Path.GetFileName(dllFile.FileName);
    }

    public async Task<string?> InstallLocalModule(string zipPath)
    {
        if (!Path.Exists(zipPath)) return null;
        
        EnsureCustomLibsDirectoryExists();
        
        // First, we copy the zip to our custom libs directory
        var fileName = Path.GetFileName(zipPath);
        var newZipPath = Path.Combine(Utils.CustomLibsDirectory, fileName);
        File.Copy(zipPath, newZipPath, true);

        // Second, we unzip it 
        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(zipPath));
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, true);
        }
        Directory.CreateDirectory(tempDirectory);
        await ZipFile.ExtractToDirectoryAsync(newZipPath, tempDirectory);
        File.Delete(newZipPath);

        // Now, we need to find the module.json file and deserialize it
        var moduleJsonPath = Path.Combine(tempDirectory, "module.json");
        if (!File.Exists(moduleJsonPath))
        {
            logger.LogError("Module {module} does not contain a module.json file", fileName);
            Directory.Delete(tempDirectory, true);
            return null;
        }

        var moduleMetadata = await Json.ToObjectAsync<TrackingModuleMetadata>(await File.ReadAllTextAsync(moduleJsonPath));
        if (moduleMetadata == null)
        {
            logger.LogError("Module {module} contains an invalid module.json file", fileName);
            Directory.Delete(tempDirectory, true);
            return null;
        }
        moduleMetadata.IsLocal = true;

        // Now we move to a directory named after the module id and delete the temp directory
        var moduleDirectory = Path.Combine(Utils.CustomLibsDirectory, moduleMetadata.ModuleId.ToString());
        if (Directory.Exists(moduleDirectory))
        {
            Directory.Delete(moduleDirectory, true);
        }

        MoveDirectory(tempDirectory, moduleDirectory);

        // Now we need to find the module's dll
        moduleMetadata.DllFileName ??= TryFindModuleDll(moduleDirectory, moduleMetadata);
        if (moduleMetadata.DllFileName == null)
        {
            logger.LogError("Module {module} has no .dll file name specified and no .dll files were found in the extracted zip", moduleMetadata.ModuleId);
            return null;
        }

        // Now we write the module.json file to the module directory
        await File.WriteAllTextAsync(Path.Combine(moduleDirectory, "module.json"), JsonConvert.SerializeObject(moduleMetadata, Formatting.Indented));

        // Finally, we return the module's dll file name
        return Path.Combine(moduleDirectory, moduleMetadata.DllFileName);
    }

    public async Task<string?> InstallRemoteModule(
        TrackingModuleMetadata moduleMetadata, IProgress<ModuleInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureCustomLibsDirectoryExists();

        var moduleDirectory = Path.Combine(Utils.CustomLibsDirectory, moduleMetadata.ModuleId.ToString());
        var backupDirectory = Path.Combine(Path.GetDirectoryName(Utils.CustomLibsDirectory)!,
            $".{moduleMetadata.ModuleId}.{Guid.NewGuid():N}.backup");
        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var stagedDirectory = Path.Combine(tempDirectory, "content");
        Directory.CreateDirectory(stagedDirectory);

        try
        {
            progress?.Report(new ModuleInstallProgress("Downloading module..."));
            if (!string.Equals(Path.GetExtension(moduleMetadata.DownloadUrl), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                var archivePath = Path.Combine(tempDirectory, "module.zip");
                await DownloadModuleToFile(moduleMetadata, archivePath, moduleMetadata.FileHash, progress, cancellationToken);
                progress?.Report(new ModuleInstallProgress("Extracting module..."));
                await ZipFile.ExtractToDirectoryAsync(archivePath, stagedDirectory, cancellationToken);
                moduleMetadata.DllFileName ??= TryFindModuleDll(stagedDirectory, moduleMetadata);
            }
            else
            {
                moduleMetadata.DllFileName = string.IsNullOrEmpty(moduleMetadata.DllFileName)
                    ? Path.GetFileName(new Uri(moduleMetadata.DownloadUrl).AbsolutePath)
                    : moduleMetadata.DllFileName;
                if (Path.GetFileName(moduleMetadata.DllFileName) != moduleMetadata.DllFileName)
                    throw new InvalidDataException("Module DLL name contains a directory path.");
                await DownloadModuleToFile(moduleMetadata,
                    Path.Combine(stagedDirectory, moduleMetadata.DllFileName), moduleMetadata.FileHash, progress, cancellationToken);
            }

            if (string.IsNullOrEmpty(moduleMetadata.DllFileName) ||
                Path.GetFileName(moduleMetadata.DllFileName) != moduleMetadata.DllFileName ||
                !File.Exists(Path.Combine(stagedDirectory, moduleMetadata.DllFileName)) ||
                new FileInfo(Path.Combine(stagedDirectory, moduleMetadata.DllFileName)).Length == 0)
            {
                throw new InvalidDataException("Downloaded module does not contain a valid DLL.");
            }

            await File.WriteAllTextAsync(Path.Combine(stagedDirectory, "module.json"),
                JsonConvert.SerializeObject(moduleMetadata, Formatting.Indented));
            if (OperatingSystem.IsWindows())
            {
                foreach (var dll in Directory.GetFiles(stagedDirectory, "*.dll", SearchOption.AllDirectories))
                    RemoveZoneIdentifier(dll);
            }

            progress?.Report(new ModuleInstallProgress("Installing module..."));
            cancellationToken.ThrowIfCancellationRequested();
            await libManager.TeardownAllModules();
            var oldVersionMoved = false;
            try
            {
                if (Directory.Exists(moduleDirectory))
                {
                    Directory.Move(moduleDirectory, backupDirectory);
                    oldVersionMoved = true;
                }

                MoveDirectory(stagedDirectory, moduleDirectory);
                await libManager.Initialize();
            }
            catch (Exception installError)
            {
                try { await libManager.TeardownAllModules(); }
                catch (Exception ex) { logger.LogWarning(ex, "Cleanup failed while restoring module {module}", moduleMetadata.ModuleId); }
                if (Directory.Exists(moduleDirectory))
                    Directory.Delete(moduleDirectory, true);
                if (oldVersionMoved)
                    Directory.Move(backupDirectory, moduleDirectory);
                try { await libManager.Initialize(); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not restart modules after restoring {module}", moduleMetadata.ModuleId); }
                throw new IOException($"Could not install {moduleMetadata.ModuleName}; the previous version was restored.", installError);
            }

            if (oldVersionMoved)
            {
                try { Directory.Delete(backupDirectory, true); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not remove backup for {module}", moduleMetadata.ModuleId); }
            }

            logger.LogInformation("Installed module {module} to {moduleDirectory}", moduleMetadata.ModuleId, moduleDirectory);
            progress?.Report(new ModuleInstallProgress("Installed.", 100));
            return Path.Combine(moduleDirectory, moduleMetadata.DllFileName);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                try { Directory.Delete(tempDirectory, true); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not remove temporary files for {module}", moduleMetadata.ModuleId); }
            }
        }
    }

    public async Task UninstallModule(TrackingModuleMetadata moduleMetadata)
    {
        logger.LogDebug("Uninstalling module {module}", moduleMetadata.ModuleId);
        await libManager.TeardownAllModules();
        
        var moduleDirectory = Path.Combine(Utils.CustomLibsDirectory, moduleMetadata.ModuleId.ToString());
        if (Directory.Exists(moduleDirectory))
        {
            try
            {
                Directory.Delete(moduleDirectory, true);
                logger.LogInformation("Uninstalled module {module} from {moduleDirectory}", moduleMetadata.ModuleId, moduleDirectory);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to uninstall module {module} from {moduleDirectory}", moduleMetadata.ModuleId, moduleDirectory);
            }
        }
        else
        {
            logger.LogDebug("Module {module} could not be found where it was expected in {moduleDirectory}", moduleMetadata.ModuleId, moduleDirectory);
        }

        await libManager.Initialize();
    }
}
