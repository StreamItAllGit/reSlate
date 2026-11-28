// @module: reStation.Tests.TestTempCleanup
// @purpose: Shared temp-directory cleanup helper for test Dispose() paths with non-empty Trace logging on locked/denied files.
// @depends-on: System.Diagnostics
// @exports: TestTempCleanup

using System.Diagnostics;

namespace reStation.Tests;

internal static class TestTempCleanup
{
    public static void TryDeleteDirectory(string path, string caller)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[{caller}] Cleanup skipped for '{path}': {ex.Message}");
        }
    }
}
