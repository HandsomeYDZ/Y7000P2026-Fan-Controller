using Microsoft.Win32;
using System;

namespace LegionFanController.Hardware;

internal static class HardwareAccessPolicy
{
    // Hardware identity, never a profile or a user-selectable generation.
    public static bool IsAuditedModernMachine
    {
        get
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return string.Equals(bios?.GetValue("SystemProductName") as string, "83F3", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool LegacyWritesAllowed { get; private set; }

    public static void ConfigureLegacyAccess(int detectedGeneration)
    {
        LegacyWritesAllowed = !IsAuditedModernMachine && detectedGeneration is 5 or 6;
    }

    public static void RequireLegacyWriteAccess()
    {
        if (!LegacyWritesAllowed)
            throw new InvalidOperationException("Legacy EC register writes are disabled on this hardware.");
    }
}
