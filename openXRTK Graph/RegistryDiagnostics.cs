using Microsoft.Win32;
using System;
using System.IO;
using System.Text;

namespace openXRTK_Graph;

internal static class RegistryDiagnostics
{
    public static string DiagnoseOpenXrToolkit()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== OpenXR Toolkit Registry Diagnosis ===");
        sb.AppendLine();

        // Try standard path
        sb.AppendLine("1. Trying standard path: HKCU\\Software\\OpenXR_Toolkit");
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey("Software\\OpenXR_Toolkit");
            if (root != null)
            {
                sb.AppendLine("   ✓ Key found");

                // Show all values at root
                sb.AppendLine("   Root values:");
                foreach (var name in root.GetValueNames())
                {
                    var val = root.GetValue(name);
                    sb.AppendLine($"     - {name}: {val?.GetType().Name} = {val}");
                }

                sb.AppendLine("   Subkeys:");
                foreach (var name in root.GetSubKeyNames())
                {
                    sb.AppendLine($"     - {name}");
                    try
                    {
                        using var sub = root.OpenSubKey(name);
                        if (sub != null)
                        {
                            foreach (var vname in sub.GetValueNames())
                            {
                                var val = sub.GetValue(vname);
                                sb.AppendLine($"       - {vname}: {val?.GetType().Name} = {val}");
                            }
                        }
                    }
                    catch (Exception ex) { sb.AppendLine($"       Error reading: {ex.Message}"); }
                }
            }
            else
            {
                sb.AppendLine("   ✗ Key not found");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"   ✗ Error: {ex.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("2. Scanning HKCU\\Software for 'openxr' subkeys:");
        try
        {
            using var software = Registry.CurrentUser.OpenSubKey("Software");
            if (software != null)
            {
                int found = 0;
                foreach (var name in software.GetSubKeyNames())
                {
                    if (name.IndexOf("openxr", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found++;
                        sb.AppendLine($"   Found: {name}");
                    }
                }
                if (found == 0) sb.AppendLine("   No 'openxr' keys found");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"   ✗ Error: {ex.Message}");
        }

        return sb.ToString();
    }

    public static string DiagnoseAndSaveToFile()
    {
        var content = DiagnoseOpenXrToolkit();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "openxrtk-logs");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"openxrtk_registry_diag_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(filePath, content, Encoding.UTF8);
        return filePath;
    }
}
