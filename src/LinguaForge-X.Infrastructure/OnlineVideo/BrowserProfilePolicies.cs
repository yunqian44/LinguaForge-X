using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LinguaForge_X.Infrastructure.OnlineVideo;

internal sealed class BrowserProfilePolicyException : InvalidOperationException;

internal static class BrowserProfilePolicies
{
    internal static bool PreventsIsolation(string executable)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return ReadWindowsPolicy(executable);
    }

    internal static bool OverridesDirectory(object? value) => value is string text ? !string.IsNullOrWhiteSpace(text) : value is not null;

    [SupportedOSPlatform("windows")]
    private static bool ReadWindowsPolicy(string executable)
    {
        var policyPath = Path.GetFileNameWithoutExtension(executable).Equals("msedge", StringComparison.OrdinalIgnoreCase)
            ? @"SOFTWARE\Policies\Microsoft\Edge" : @"SOFTWARE\Policies\Google\Chrome";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var policy = registry.OpenSubKey(policyPath);
            // This mandatory policy overrides --user-data-dir. Do not launch or read the forced profile.
            if (OverridesDirectory(policy?.GetValue("UserDataDir", null, RegistryValueOptions.DoNotExpandEnvironmentNames))) return true;
        }
        return false;
    }

    internal static void RequireIsolation(string executable, Func<string, bool> policyCheck)
    {
        try
        {
            if (policyCheck(executable)) throw new BrowserProfilePolicyException();
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // A policy we cannot inspect could point at an existing profile. Fail closed before starting a process.
            throw new BrowserProfilePolicyException();
        }
    }
}
