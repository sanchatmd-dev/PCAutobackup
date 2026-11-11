using System.Net.Sockets;
using System.Text.Json;

namespace PCAutobackup.Core.Google;

internal static class GoogleDiagnostics
{
    private static readonly object Gate = new();
    internal static string LogPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCAutobackup", "logs", $"google-{Environment.ProcessId}.jsonl");

    // Only allowlisted structural fields. Never serialize messages, URLs, headers, bodies,
    // access/refresh tokens, upload session URLs, file names or user configuration.
    internal static object[] Causes(Exception? error)
    {
        var causes = new List<object>();
        for (var e = error; e is not null && causes.Count < 12; e = e.InnerException)
            causes.Add(new { Type = e.GetType().Name, HResult = e.HResult,
                HttpError = (e as HttpRequestException)?.HttpRequestError.ToString(),
                HttpStatus = (int?)(e as HttpRequestException)?.StatusCode,
                SocketError = (e as SocketException)?.SocketErrorCode.ToString() });
        return causes.ToArray();
    }

    internal static void Write(string operation, string requestId, int attempt, string outcome, Exception? error = null, int? status = null, double? delaySeconds = null, IReadOnlyDictionary<string, string>? metadataChecks = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Operation = operation,
                RequestId = requestId, Attempt = attempt, Outcome = outcome, HttpStatus = status, DelaySeconds = delaySeconds, Causes = Causes(error), MetadataChecks = metadataChecks });
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 4 * 1024 * 1024)
                    File.Move(LogPath, LogPath + ".previous", true);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Diagnostics must not fail a backup. */ }
    }
}
