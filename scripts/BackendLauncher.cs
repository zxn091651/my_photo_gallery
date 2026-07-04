using System;
using System.Diagnostics;
using System.IO;

internal static class BackendLauncher
{
    private static int Main()
    {
        string exeName = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]) ?? string.Empty;
        bool stopMode = exeName.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0;
        string projectRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string scriptName = stopMode ? "stop-gallery.ps1" : "start-gallery.ps1";
        string scriptPath = Path.Combine(projectRoot, "scripts", scriptName);

        if (!File.Exists(scriptPath))
        {
            return 2;
        }

        string arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File " + Quote(scriptPath);
        if (!stopMode)
        {
            arguments += " -ProjectRoot " + Quote(projectRoot);
        }

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = projectRoot
        };

        Process process = Process.Start(startInfo);
        if (stopMode && process != null)
        {
            process.WaitForExit();
        }

        return 0;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
