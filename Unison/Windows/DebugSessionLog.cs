using System.Text.Json;

namespace Unison.Windows;

internal static class DebugSessionLog
{
    public static void Write(string hypothesisId, string location, string message, object data)
    {
        try
        {
            // #region agent log
            var payload = new Dictionary<string, object?>
            {
                ["sessionId"] = "6e1b69",
                ["hypothesisId"] = hypothesisId,
                ["location"] = location,
                ["message"] = message,
                ["data"] = data,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["runId"] = "post-fix"
            };
            var line = JsonSerializer.Serialize(payload) + Environment.NewLine;
            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Unison");
            Directory.CreateDirectory(localDir);
            File.AppendAllText(Path.Combine(localDir, "debug-6e1b69.log"), line);
            try
            {
                File.AppendAllText(@"w:\Unison\debug-6e1b69.log", line);
            }
            catch
            {
            }
            // #endregion
        }
        catch
        {
        }
    }
}
