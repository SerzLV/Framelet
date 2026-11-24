using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Framelet;

internal sealed class HistoryTextCache(string folder)
{
    private const int FormatVersion = 1;
    private const int MaximumTextLength = 64 * 1024;

    internal string? Read(string imagePath, ImageStamp stamp, string languages = OcrService.DefaultAutomaticLanguages)
    {
        var path = EntryPath(imagePath);
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            var entry = JsonSerializer.Deserialize<Entry>(stream);
            return entry is { Version: FormatVersion } && entry.Stamp == stamp
                && (entry.Languages ?? OcrService.DefaultAutomaticLanguages) == languages ? entry.Text : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine(error);
            return null;
        }
    }

    internal void Write(string imagePath, ImageStamp stamp, string text, string languages = OcrService.DefaultAutomaticLanguages)
    {
        if (text.Length > MaximumTextLength)
            return;
        TryUpdate(() => HistoryStore.AtomicWrite(EntryPath(imagePath), stream =>
            JsonSerializer.Serialize(stream, new Entry(FormatVersion, stamp, text, languages))));
    }

    internal void Remove(string imagePath) => TryUpdate(() => File.Delete(EntryPath(imagePath)));

    internal void Clear() => Prune([]);

    internal void Prune(IEnumerable<string> imagePaths)
    {
        TryUpdate(() =>
        {
            if (!Directory.Exists(folder))
                return;
            var live = imagePaths.Select(EntryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Directory.EnumerateFiles(folder, "*.json"))
            {
                var key = Path.GetFileNameWithoutExtension(entry);
                if (key.Length == 64 && key.All(Uri.IsHexDigit) && !live.Contains(Path.GetFileName(entry)))
                    TryUpdate(() => File.Delete(entry));
            }
        });
    }

    private string EntryPath(string imagePath) => Path.Combine(folder, EntryName(imagePath));

    private static string EntryName(string imagePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imagePath.ToUpperInvariant()))) + ".json";

    private static void TryUpdate(Action update)
    {
        try { update(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine(error);
        }
    }

    private sealed record Entry(int Version, ImageStamp Stamp, string Text, string? Languages = null);
}

internal sealed record ImageStamp(long Length, DateTime LastWriteUtc, DateTime CreatedUtc)
{
    internal static ImageStamp FromPath(string path)
    {
        var info = new FileInfo(path);
        return new ImageStamp(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
    }
}
