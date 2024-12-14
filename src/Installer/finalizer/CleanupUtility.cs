// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.Sdk.WorkloadManifestReader;
using Microsoft.Win32;
using Microsoft.Win32.Msi;

namespace Microsoft.DotNet.Finalizer;

internal class CleanupUtility
{
    public static bool DetectSdk(SdkFeatureBand featureBandVersion, string platform)
    {
        string registryPath = $@"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\{platform}\sdk";
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(registryPath);

        if (key is null)
        {
            Logger.Log("SDK registry path not found.");
            return false;
        }

        foreach (var keyValueName in key.GetValueNames())
        {
            try
            {
                // Convert the full SDK version into an SdkFeatureBand to see whether it matches the SDK being removed.
                SdkFeatureBand installedFeatureBand = new(keyValueName);
                if (installedFeatureBand.Equals(featureBandVersion))
                {
                    Logger.Log($"Another SDK with the same feature band is installed: {keyValueName} ({installedFeatureBand})");
                    return true;
                }
            }
            catch
            {
                Logger.Log($"Failed to check installed SDK version: {keyValueName}");
            }
        }
        return false;
    }

    public static bool RemoveDependent(string dependent)
    {
        bool restartRequired = false;

        // Open the installer dependencies registry key.
        // This has to be an exhaustive search as we're not looking for a specific provider key,
        // but for a specific dependent that could be registered against any provider key.
        using var hkInstallerDependenciesKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\Installer\Dependencies", writable: true);
        if (hkInstallerDependenciesKey is null)
        {
            Logger.Log("Installer dependencies key does not exist.");
            return false;
        }

        // Iterate over each provider key in the dependencies
        foreach (string providerKeyName in hkInstallerDependenciesKey.GetSubKeyNames())
        {
            Logger.Log($"Processing provider key: {providerKeyName}");

            using var hkProviderKey = hkInstallerDependenciesKey.OpenSubKey(providerKeyName, writable: true);
            if (hkProviderKey is null)
            {
                continue;
            }

            // Open the Dependents subkey
            using var hkDependentsKey = hkProviderKey.OpenSubKey("Dependents", writable: true);
            if (hkDependentsKey is null)
            {
                continue;
            }

            // Check if the dependent exists and continue if it does not
            if (!hkDependentsKey.GetSubKeyNames().Any(dkn => string.Equals(dkn, dependent, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Logger.Log($"Dependent match found: {dependent}");

            // Attempt to remove the dependent key
            try
            {
                hkDependentsKey.DeleteSubKey(dependent);
                Logger.Log("Dependent deleted");
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception while removing dependent key: {ex.Message}");
                return false;
            }

            // Check if any dependents are left
            if (hkDependentsKey.SubKeyCount != 0)
            {
                continue;
            }

            // No remaining dependents, handle product uninstallation
            try
            {
                // Default value should be a REG_SZ containing the product code.
                if (hkProviderKey.GetValue(null) is not string productCode)
                {
                    Logger.Log($"No product ID found, provider key: {providerKeyName}");
                    continue;
                }

                // Let's make sure the product is actually installed.
                // The provider key for an MSI typically stores the ProductCode, DisplayName, and Version,
                // but by calling into MsiGetProductInfo, we're doing an implicit detect and getting a property back.
                // This avoids reading additional registry keys.
                uint error = WindowsInstaller.GetProductInfo(productCode, "ProductName", out string productName);
                if (error != Error.SUCCESS)
                {
                    Logger.Log($"Failed to detect product, ProductCode: {productCode}, result: 0x{error:x8}");
                    continue;
                }

                // Need to set the UI level before executing the MSI.
                _ = WindowsInstaller.SetInternalUI(InstallUILevel.None);

                // Configure the product to be absent (uninstall the product)
                error = WindowsInstaller.ConfigureProduct(productCode,
                    WindowsInstaller.INSTALLLEVEL_DEFAULT,
                    InstallState.ABSENT,
                    "MSIFASTINSTALL=7 IGNOREDEPENDENCIES=ALL REBOOT=ReallySuppress");
                Logger.Log($"Uninstall of {productName} ({productCode}) exited with 0x{error:x8}");

                if (error == Error.SUCCESS_REBOOT_INITIATED || error == Error.SUCCESS_REBOOT_REQUIRED)
                {
                    restartRequired = true;
                }

                // Remove the provider key. Typically these are removed by the engine,
                // but since the workload packs and manifest were installed by the CLI, the finalizer needs to clean these up.
                hkInstallerDependenciesKey.DeleteSubKeyTree(providerKeyName, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to process dependent process: {ex.Message}");
                return restartRequired;
            }
        }

        return restartRequired;
    }

    public static void DeleteWorkloadRecords(SdkFeatureBand featureBandVersion, string platform)
    {
        string workloadKeyName = $@"SOFTWARE\Microsoft\dotnet\InstalledWorkloads\Standalone\{platform}";
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(workloadKeyName, writable: true);
        if (key is not null)
        {
            key.DeleteSubKeyTree(featureBandVersion.ToString(), throwOnMissingSubKey: false);
            Logger.Log($"Deleted workload records for '{featureBandVersion}'.");
        }
        else
        {
            Logger.Log("No workload records found to delete.");
        }

        // Delete the empty keys by walking up to the root.
        DeleteEmptyKeyToRoot(workloadKeyName);
    }

    private static void DeleteEmptyKeyToRoot(string workloadKeyName)
    {
        string subKeyName = Path.GetFileName(workloadKeyName.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        string tempName = workloadKeyName;
        while (!string.IsNullOrWhiteSpace(tempName))
        {
            using RegistryKey? tempSubKey = Registry.LocalMachine.OpenSubKey(tempName);
            if (tempSubKey is null || tempSubKey.SubKeyCount != 0 || tempSubKey.ValueCount != 0)
            {
                break;
            }

            tempName = Path.GetDirectoryName(tempName) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tempName))
            {
                continue;
            }

            try
            {
                using RegistryKey? parentKey = Registry.LocalMachine.OpenSubKey(tempName, writable: true);
                if (parentKey is null)
                {
                    continue;
                }

                parentKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                Logger.Log($"Deleted empty key: {subKeyName}");
                subKeyName = Path.GetFileName(tempName.TrimEnd(Path.DirectorySeparatorChar));
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to delete key: {tempName}, error: {ex.Message}");
                break;
            }
        }
    }

    public static void RemoveInstallStateFile(SdkFeatureBand featureBandVersion, string platform)
    {
        string programDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string installStatePath = Path.Combine(programDataPath, "dotnet", "workloads", platform, featureBandVersion.ToString(), "installstate", "default.json");
        if (!File.Exists(installStatePath))
        {
            Logger.Log("Install state file does not exist.");
            return;
        }

        File.Delete(installStatePath);
        Logger.Log($"Deleted install state file: {installStatePath}");

        var dir = new DirectoryInfo(installStatePath).Parent;
        while (dir is not null && dir.Exists && dir.GetFiles().Length == 0 && dir.GetDirectories().Length == 0)
        {
            dir.Delete();
            dir = dir.Parent;
        }
    }
}
