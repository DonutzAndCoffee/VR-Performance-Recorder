using Microsoft.Win32;
using System;

namespace openXRTK_Graph;

internal static class RegistryHelpers
{
    // Attempts to open the OpenXR Toolkit root registry key under HKCU\Software.
    // Tries common name variants and falls back to scanning subkeys for 'openxr' in the name.
    public static RegistryKey? OpenOpenXrToolkitKey()
    {
        string[] candidates = new[] {
            "Software\\OpenXR_Toolkit",
            "Software\\OpenXR Toolkit",
            "Software\\OpenXR-Toolkit",
            "Software\\OpenXRToolkit",
        };

        foreach (var p in candidates)
        {
            try
            {
                var k = Registry.CurrentUser.OpenSubKey(p);
                if (k != null) return k;
            }
            catch { }
        }

        // Fallback: scan HKCU\Software subkeys for anything containing 'openxr'
        try
        {
            using var software = Registry.CurrentUser.OpenSubKey("Software");
            if (software == null) return null;
            foreach (var name in software.GetSubKeyNames())
            {
                if (name.IndexOf("openxr", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        var k = software.OpenSubKey(name);
                        if (k != null) return k;
                    }
                    catch { }
                }
            }
        }
        catch { }

        return null;
    }
}
