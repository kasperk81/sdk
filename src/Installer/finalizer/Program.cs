// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.Finalizer;
using Microsoft.NET.Sdk.WorkloadManifestReader;
using Microsoft.Win32.Msi;
using static Microsoft.DotNet.Finalizer.CleanupUtility;

if (args.Length < 3)
{
    return (int)Error.INVALID_COMMAND_LINE;
}

string logPath = args[0];
string sdkVersion = args[1];
string platform = args[2];

using StreamWriter logStream = new(logPath);

Logger.Init(logStream);

Logger.Log($"{nameof(logPath)}: {logPath}");
Logger.Log($"{nameof(sdkVersion)}: {sdkVersion}");
Logger.Log($"{nameof(platform)}: {platform}");
int exitCode = (int)Error.SUCCESS;

try
{
    // Step 1: Parse and format SDK feature band version
    SdkFeatureBand featureBandVersion = new(sdkVersion);

    // Step 2: Check if SDK feature band is installed
    if (DetectSdk(featureBandVersion, platform))
    {
        return (int)Error.SUCCESS;
    }

    // Step 3: Remove dependent components if necessary
    string dependent = $"Microsoft.NET.Sdk,{featureBandVersion},{platform}";
    if (RemoveDependent(dependent))
    {
        // Pass potential restart exit codes back to the bundle based on executing the workload related MSIs.
        // The bundle may take additional actions such as prompting the user.
        exitCode = (int)Error.SUCCESS_REBOOT_REQUIRED;
    };

    // Step 4: Delete workload records
    DeleteWorkloadRecords(featureBandVersion, platform);

    // Step 5: Clean up install state file
    RemoveInstallStateFile(featureBandVersion, platform);
}
catch (Exception ex)
{
    Logger.Log($"Error: {ex}");
    exitCode = ex.HResult;
}

return exitCode;
