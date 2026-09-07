using System.IO;
using System.Text.Json;

namespace YeziCompanion.Services;

internal static class AppStorage
{
    internal static bool IsSelfTest => Environment.GetCommandLineArgs().Any(arg =>
        arg.Equals("--self-test-ui", StringComparison.OrdinalIgnoreCase) || arg.Equals("--diagnose-connection", StringComparison.OrdinalIgnoreCase));
    internal static string DirectoryPath => IsSelfTest
        ? Path.Combine(AppContext.BaseDirectory, "qa")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HexARAMCompanion.V2");

    internal static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true }, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal sealed record UserPreferences(bool AutoGrab = true, bool AutoAccept = true, bool AutoOpenWebsite = true);
