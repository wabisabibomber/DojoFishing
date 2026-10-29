using System.ComponentModel;
using System.Diagnostics;

namespace DojoFishing.Processes;

public sealed record ProcessChoice(int Id, string Executable, string WindowTitle, DateTime StartTime)
{
    public string Label => $"{Executable} [{Id}] {WindowTitle}";

    public static bool Matches(string keyword, string executable, string title) =>
        string.IsNullOrWhiteSpace(keyword) ||
        executable.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase) ||
        title.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ProcessChoice> Find(string keyword)
    {
        var result = new List<ProcessChoice>();
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                try
                {
                    if (process.Id == self.Id || process.SessionId != self.SessionId) continue;
                    string executable = process.ProcessName + ".exe";
                    string title = process.MainWindowTitle;
                    if (Matches(keyword, executable, title))
                        result.Add(new(process.Id, executable, title, process.StartTime));
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
        }
        return result.OrderBy(x => x.Executable, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id).ToArray();
    }

    public bool IsAlive()
    {
        try { using var p = Process.GetProcessById(Id); return !p.HasExited && p.StartTime == StartTime; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }
}
