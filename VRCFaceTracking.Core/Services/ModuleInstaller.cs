using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Helpers;
using VRCFaceTracking.Core.Models;

namespace VRCFaceTracking.Core.Services;

public class ModuleInstaller(ILogger<ModuleInstaller> logger, ILibManager libManager, string? moduleDirectory = null)
{
    public ModuleInstaller(ILogger<ModuleInstaller> logger, ILibManager libManager)
        : this(logger, libManager, null) { }

    private readonly string _moduleDirectory = moduleDirectory ?? Utils.CustomLibsDirectory;

    private void EnsureCustomLibsDirectoryExists()
    {
        if (!Directory.Exists(_moduleDirectory))
        {
            Directory.CreateDirectory(_moduleDirectory);
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
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(moduleMetadata.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(filePath))
        {
            var buffer = new byte[81920];
            var total = response.Content.Headers.ContentLength;
            long received = 0;
            progress?.Report(new ModuleInstallProgress(ModuleInstallStage.Downloading, total > 0 ? 0 : null,
                0, total > 0 ? total : null));
            var reportInterval = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                int count;
                try
                {
                    count = await source.ReadAsync(buffer.AsMemory(), readTimeout.Token);
                }
                catch (IOException ex)
                {
                    throw new HttpRequestException("The connection was interrupted while downloading the module.", ex);
                }
                if (count == 0)
                    break;
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                received += count;
                if (reportInterval.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new ModuleInstallProgress(ModuleInstallStage.Downloading,
                        total > 0 ? Math.Min(100, received * 100.0 / total.Value) : null,
                        received, total > 0 ? total : null));
                    reportInterval.Restart();
                }
            }
            if (total > 0 && received < total)
                throw new HttpRequestException($"The download ended after {received} of {total} bytes.");
            progress?.Report(new ModuleInstallProgress(ModuleInstallStage.Downloading,
                total > 0 ? 100 : null, received, total > 0 ? total : null));
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
        var dllFile = dllFiles.Select(x => new { FileName = Path.GetFileName(x), Distance = LevenshteinDistance.Calculate(targetFileName, Path.GetFileNameWithoutExtension(x)) }).MinBy(x => x.Distance);

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
        if (!File.Exists(zipPath)) return null;
        EnsureCustomLibsDirectoryExists();
        var stagedDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await ZipFile.ExtractToDirectoryAsync(zipPath, stagedDirectory);
            var metadataPath = Path.Combine(stagedDirectory, "module.json");
            if (!File.Exists(metadataPath))
                throw new InvalidDataException("The module archive does not contain module.json.");
            var metadata = await Json.ToObjectAsync<TrackingModuleMetadata>(await File.ReadAllTextAsync(metadataPath))
                ?? throw new InvalidDataException("The module archive contains invalid metadata.");
            metadata.IsLocal = true;
            metadata.DllFileName ??= TryFindModuleDll(stagedDirectory, metadata);
            return await ApplyStagedModule(stagedDirectory, metadata, CancellationToken.None);
        }
        finally
        {
            DeleteTemporaryDirectory(stagedDirectory);
        }
    }

    public async Task<string?> InstallRemoteModule(
        TrackingModuleMetadata moduleMetadata, IProgress<ModuleInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureCustomLibsDirectoryExists();

        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var stagedDirectory = Path.Combine(tempDirectory, "content");
        Directory.CreateDirectory(stagedDirectory);

        try
        {
            progress?.Report(new ModuleInstallProgress(ModuleInstallStage.Connecting));
            if (!string.Equals(Path.GetExtension(moduleMetadata.DownloadUrl), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                var archivePath = Path.Combine(tempDirectory, "module.zip");
                await DownloadModuleToFile(moduleMetadata, archivePath, moduleMetadata.FileHash, progress, cancellationToken);
                progress?.Report(new ModuleInstallProgress(ModuleInstallStage.Extracting));
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

            progress?.Report(new ModuleInstallProgress(ModuleInstallStage.Installing));
            var installedPath = await ApplyStagedModule(stagedDirectory, moduleMetadata, cancellationToken);
            progress?.Report(new ModuleInstallProgress(ModuleInstallStage.Installed, 100));
            return installedPath;
        }
        finally
        {
            DeleteTemporaryDirectory(tempDirectory);
        }
    }

    private async Task<string> ApplyStagedModule(string stagedDirectory,
        TrackingModuleMetadata metadata, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(metadata.DllFileName) ||
            Path.GetFileName(metadata.DllFileName) != metadata.DllFileName ||
            !File.Exists(Path.Combine(stagedDirectory, metadata.DllFileName)) ||
            new FileInfo(Path.Combine(stagedDirectory, metadata.DllFileName)).Length == 0)
            throw new InvalidDataException("Downloaded module does not contain a valid DLL.");

        await File.WriteAllTextAsync(Path.Combine(stagedDirectory, "module.json"),
            JsonConvert.SerializeObject(metadata, Formatting.Indented), cancellationToken);
        if (OperatingSystem.IsWindows())
        {
            foreach (var dll in Directory.GetFiles(stagedDirectory, "*.dll", SearchOption.AllDirectories))
                RemoveZoneIdentifier(dll);
        }

        var moduleDirectory = Path.Combine(_moduleDirectory, metadata.ModuleId.ToString());
        var backupDirectory = Path.Combine(Path.GetDirectoryName(_moduleDirectory)!,
            $".{metadata.ModuleId}.{Guid.NewGuid():N}.backup");
        var oldVersionMoved = false;
        var replacementCreated = false;
        try
        {
            await libManager.ChangeModules(() =>
            {
                if (Directory.Exists(moduleDirectory))
                {
                    Directory.Move(moduleDirectory, backupDirectory);
                    oldVersionMoved = true;
                }
                Directory.CreateDirectory(moduleDirectory);
                replacementCreated = true;
                MoveDirectory(stagedDirectory, moduleDirectory);
            }, () =>
            {
                // The original directory still belongs to the old version until its move succeeds.
                if (replacementCreated && Directory.Exists(moduleDirectory))
                    Directory.Delete(moduleDirectory, true);
                if (oldVersionMoved)
                    Directory.Move(backupDirectory, moduleDirectory);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException(Directory.Exists(backupDirectory)
                ? $"Could not install {metadata.ModuleName}. Previous version backup: {backupDirectory}"
                : $"Could not install {metadata.ModuleName}; existing module files were preserved or restored.", ex);
        }

        if (oldVersionMoved)
        {
            try { Directory.Delete(backupDirectory, true); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove backup for {module}", metadata.ModuleId); }
        }
        logger.LogInformation("Installed module {module} to {moduleDirectory}", metadata.ModuleId, moduleDirectory);
        return Path.Combine(moduleDirectory, metadata.DllFileName);
    }

    private void DeleteTemporaryDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not remove temporary module files at {Directory}", directory); }
    }

    public async Task UninstallModule(TrackingModuleMetadata metadata)
    {
        var moduleDirectory = Path.Combine(_moduleDirectory, metadata.ModuleId.ToString());
        await libManager.ChangeModules(() =>
        {
            if (metadata.ModuleId == Guid.Empty && metadata is InstallableTrackingModule legacy)
                File.Delete(legacy.AssemblyLoadPath);
            else if (Directory.Exists(moduleDirectory))
                Directory.Delete(moduleDirectory, true);
        });
        logger.LogInformation("Uninstalled module {Module}", metadata.ModuleName);
    }
}
