using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace YeziCompanion.Services;

internal sealed class PortraitService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _slots = new(4, 4);

    internal async Task<ImageSource?> LoadAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "data.aramkit.com") return null;
        await _slots.WaitAsync(token);
        try
        {
            // Decode away from the UI thread; frozen images can safely cross dispatchers.
            return await Task.Run(async () =>
            {
                var directory = Path.Combine(AppStorage.DirectoryPath, "portraits");
                var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img";
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    try { return Decode(await File.ReadAllBytesAsync(path, token)); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* Retry a corrupt image from its source. */ }
                }
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 2_000_000) return null;
                await using var source = await response.Content.ReadAsStreamAsync(token);
                using var data = new MemoryStream();
                var buffer = new byte[16_384];
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    if (data.Length + count > 2_000_000) return null;
                    data.Write(buffer, 0, count);
                }
                var bytes = data.ToArray();
                var image = Decode(bytes);
                try
                {
                    Directory.CreateDirectory(directory);
                    await File.WriteAllBytesAsync(path, bytes, token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return image;
            }, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { return null; }
        finally { _slots.Release(); }
    }

    internal static ImageSource Decode(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Unsupported portrait format");
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(encoded.ToArray());
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 96;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose() => _http.Dispose();
}
