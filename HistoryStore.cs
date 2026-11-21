using System.Globalization;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.FileIO;

namespace Framelet;

internal sealed record Preferences(bool Hotkeys = true, bool KeepHistory = true, string? Language = null, bool PrintScreen = true,
    string? StorageFolder = null, string[]? HistoryFolders = null, TrayShortcut? WorkspaceShortcut = null,
    WorkspaceSize? WindowSize = null, PalettePreferences? Palette = null, DateTime? HistoryClearedUtc = null,
    Dictionary<int, TrayShortcut>? CaptureShortcuts = null);

internal sealed record WorkspaceSize(double Width, double Height, bool Maximized)
{
    internal bool IsValid => double.IsFinite(Width) && Width > 0 && double.IsFinite(Height) && Height > 0;
}

internal sealed record PalettePreferences(string Color, string[] RecentColors, double StrokeWidth = 4);

internal sealed record HistoryItem(string FilePath, DateTime CreatedUtc, int Width, int Height)
{
    private DateTime LocalDate => CreatedUtc.ToLocalTime();

    public string FileName => Path.GetFileName(FilePath);
    public string DisplayName => HistoryStore.TryReadLegacyTimestamp(FilePath, out _)
        ? HistoryStore.DefaultFileName(LocalDate)
        : FileName;
    public string DateLabel => LocalDate.Date == DateTime.Today
        ? UiText.Format("HistoryToday", LocalDate)
        : LocalDate.ToString(UiText.Get("HistoryDateFormat"), UiText.Culture);
    public string Dimensions => $"{Width} × {Height}";
    public string Tooltip => $"{DisplayName}\n{LocalDate.ToString(UiText.Get("HistoryTooltipFormat"), UiText.Culture)} · {Dimensions}";
}

internal sealed class HistoryStore
{
    private const string DateFormat = "yyyyMMdd-HHmmss-fffffff";
    private const string EditIdQuery = "/tEXt/{str=FrameletEditId}";
    private const string HistoryQuery = "/tEXt/{str=FrameletHistory}";
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly string _editFolder;
    private readonly HistoryTextCache _textCache;
    private string[] _libraryFolders;
    private DateTime? _historyClearedUtc;

    internal HistoryStore(string? dataDirectory = null)
    {
        var data = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Framelet");
        DefaultFolder = Path.Combine(data, "History");
        Folder = DefaultFolder;
        _libraryFolders = [DefaultFolder];
        _editFolder = Path.Combine(data, "Edits");
        _settingsPath = Path.Combine(data, "preferences.json");
        _textCache = new HistoryTextCache(Path.Combine(data, "OcrIndex"));
    }

    internal string DefaultFolder { get; }
    internal string Folder { get; private set; }

    internal void ConfigureStorage(Preferences preferences)
    {
        lock (_gate)
        {
            Folder = NormalizeFolder(preferences.StorageFolder) ?? DefaultFolder;
            _historyClearedUtc = preferences.HistoryClearedUtc;
            _libraryFolders = (preferences.HistoryFolders ?? []).Select(NormalizeFolder).OfType<string>()
                .Append(DefaultFolder).Append(Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    internal void ForgetHistory(DateTime cutoffUtc)
    {
        lock (_gate)
        {
            _historyClearedUtc = cutoffUtc;
            _textCache.Clear();
            PruneJournals(live: null, writtenBy: cutoffUtc);
        }
    }

    private static string? NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    internal Preferences LoadPreferences()
    {
        try
        {
            return File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_settingsPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    internal void SavePreferences(Preferences settings)
    {
        AtomicWrite(_settingsPath, stream => JsonSerializer.Serialize(stream, settings));
    }

    internal string CreateEntryPath()
    {
        var name = DefaultFileName(DateTime.Now);
        var path = Path.Combine(Folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++)
            path = Path.Combine(Folder, $"{stem}-{suffix}.png");
        return path;
    }

    internal static string DefaultFileName(DateTime localTime)
        => "Framelet-" + localTime.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture) + ".png";

    internal IReadOnlyList<HistoryItem> Load()
    {
        lock (_gate)
            return LoadAndClean();
    }

    private IReadOnlyList<HistoryItem> LoadAndClean()
    {
        var items = new List<HistoryItem>();
        var liveJournals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        foreach (var folder in _libraryFolders)
        {
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.png"))
                {
                    var created = TryReadLegacyTimestamp(file.Name, out var legacy) ? legacy : file.CreationTimeUtc;
                    if (_historyClearedUtc is { } cutoff && created <= cutoff)
                        continue;
                    try
                    {
                        using var stream = File.OpenRead(file.FullName);
                        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
                        if (ReadHistoryMarker(frame, out var id))
                        {
                            if (id is not null)
                                liveJournals.Add(id);
                            items.Add(new HistoryItem(file.FullName, created, frame.PixelWidth, frame.PixelHeight));
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        // The file was removed after enumeration.
                    }
                    catch (Exception error) when (error is NotSupportedException or FormatException)
                    {
                        Trace.WriteLine(error);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        complete = false;
                        Trace.WriteLine(error);
                    }
                }
            }
            catch (DirectoryNotFoundException) when (string.Equals(folder, DefaultFolder, StringComparison.OrdinalIgnoreCase))
            {
                // Framelet creates the default folder on its first save.
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                complete = false;
                Trace.WriteLine(error);
            }
        }

        if (complete)
        {
            PruneJournals(liveJournals);
            _textCache.Prune(items.Select(item => item.FilePath));
        }
        return items.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.FilePath, StringComparer.Ordinal).ToArray();
    }

    internal HistoryItem Put(string path, BitmapSource image, EditorDocument? document = null)
    {
        lock (_gate)
        {
            var exists = File.Exists(path);
            if (exists && !BelongsToHistory(path))
                throw new IOException("The history file was replaced outside Framelet.");
            var createdUtc = exists ? ReadCreatedUtc(path) : DateTime.UtcNow;
            var previousId = exists ? ReadEditId(path) : null;
            var id = document?.Journal is null ? null : Guid.NewGuid().ToString("N");
            try
            {
                AtomicWrite(path, stream =>
                {
                    EncodePng(stream, image, id, history: true);
                    if (id is not null)
                    {
                        stream.Position = 0;
                        WriteJournal(id, document!, Convert.ToHexString(SHA256.HashData(stream)));
                    }
                }, createdUtc, overwrite: exists);
            }
            catch
            {
                TryDeleteJournal(id);
                throw;
            }
            TryDeleteJournal(previousId);
            if (exists)
                _textCache.Remove(path);
            return new HistoryItem(path, createdUtc, image.PixelWidth, image.PixelHeight);
        }
    }

    internal void Delete(string path)
    {
        lock (_gate)
        {
            if (!BelongsToHistory(path))
                throw new IOException("The screenshot is no longer a Framelet history file.");
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            _textCache.Remove(path);
        }
    }

    internal EditorDocument OpenEditorDocument(string path)
    {
        lock (_gate)
        {
            using var stream = File.OpenRead(path);
            try
            {
                var header = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
                if (GetEditId(header) is { } id && File.Exists(JournalPath(id)))
                {
                    var width = header.PixelWidth;
                    var height = header.PixelHeight;
                    stream.Position = 0;
                    var digest = Convert.ToHexString(SHA256.HashData(stream));
                    if (ReadJournal(id, digest, width, height) is { } document)
                        return document;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidDataException or NotSupportedException or FormatException)
            {
                Trace.WriteLine(ex);
            }
            stream.Position = 0;
            return new EditorDocument(DecodeImage(stream));
        }
    }

    internal (HistoryItem? Item, string? Error) Rename(HistoryItem item, string input)
    {
        var stem = input.Trim();
        if (stem.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4].TrimEnd();

        if (stem.Length is 0 or > 251 || stem.EndsWith('.') || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            IsDeviceName(stem))
            return (null, "HistoryInvalidName");

        var target = Path.Combine(Path.GetDirectoryName(item.FilePath)!, stem + ".png");
        if (string.Equals(item.FilePath, target, StringComparison.Ordinal))
            return (item, null);

        lock (_gate)
        {
            if (File.Exists(item.FilePath) && !BelongsToHistory(item.FilePath))
                throw new IOException("The history file was replaced outside Framelet.");
            var caseOnly = string.Equals(item.FilePath, target, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
                return (null, "HistoryNameTaken");

            File.SetCreationTimeUtc(item.FilePath, item.CreatedUtc);
            try
            {
                File.Move(item.FilePath, target);
                _textCache.Remove(item.FilePath);
                return (item with { FilePath = target }, null);
            }
            catch (IOException) when (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            {
                return (null, "HistoryNameTaken");
            }
        }
    }

    private static bool IsDeviceName(string stem)
    {
        var device = stem.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) ||
                device.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(device[3]));
    }

    internal static BitmapSource OpenImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return DecodeImage(stream);
    }

    internal async Task<string> ReadHistoryTextAsync(string path, CancellationToken token)
    {
        var entry = await Task.Run(() =>
        {
            lock (_gate)
            {
                var stamp = ImageStamp.FromPath(path);
                return (Stamp: stamp, Text: _textCache.Read(path, stamp), ClearedUtc: _historyClearedUtc);
            }
        }, token).ConfigureAwait(false);
        if (entry.Text is { } cached)
            return cached;

        var image = await Task.Run(() => OpenImage(path), token).ConfigureAwait(false);
        var text = await OcrService.RecognizeTextAsync(image, token).ConfigureAwait(false);
        await Task.Run(() =>
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(path) && entry.ClearedUtc == _historyClearedUtc
                    && (_historyClearedUtc is null || ReadCreatedUtc(path) > _historyClearedUtc)
                    && ImageStamp.FromPath(path) == entry.Stamp)
                    _textCache.Write(path, entry.Stamp, text);
            }
        }, token).ConfigureAwait(false);
        return text;
    }

    private static BitmapFrame DecodeImage(Stream stream)
    {
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }

    internal static void WritePng(string path, BitmapSource image)
    {
        AtomicWrite(path, stream => EncodePng(stream, image));
    }

    internal static void EncodePng(Stream stream, BitmapSource image) => EncodePng(stream, image, null);

    private static void EncodePng(Stream stream, BitmapSource image, string? editId, bool history = false)
    {
        var encoder = new PngBitmapEncoder();
        BitmapMetadata? metadata = null;
        if (history)
        {
            metadata = new BitmapMetadata("png");
            metadata.SetQuery(HistoryQuery, editId ?? "1");
        }
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        encoder.Save(stream);
    }

    private string JournalPath(string id) => Path.Combine(_editFolder, id + ".framelet");

    private void WriteJournal(string id, EditorDocument document, string digest)
    {
        AtomicWrite(JournalPath(id), stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            using (var state = archive.CreateEntry("journal.json", CompressionLevel.Fastest).Open())
                JsonSerializer.Serialize(state, new StoredJournal(digest, document.Journal!));
            using var buffer = new MemoryStream();
            EncodePng(buffer, document.Image);
            using var source = archive.CreateEntry("source.png", CompressionLevel.NoCompression).Open();
            buffer.WriteTo(source);
        });
    }

    private EditorDocument? ReadJournal(string id, string digest, int width, int height)
    {
        using var archive = ZipFile.OpenRead(JournalPath(id));
        if (archive.GetEntry("journal.json") is not { } state || archive.GetEntry("source.png") is not { } source)
            return null;

        using var stateStream = state.Open();
        var stored = JsonSerializer.Deserialize<StoredJournal>(stateStream);
        if (stored is null || stored.Digest != digest || stored.Journal?.IsUsable() != true)
            return null;

        using var input = source.Open();
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        buffer.Position = 0;
        var image = DecodeImage(buffer);
        return stored.Journal.MatchesImage(image.PixelWidth, image.PixelHeight, width, height)
            ? new EditorDocument(image, stored.Journal) : null;
    }

    private static string? ReadEditId(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            return GetEditId(BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException)
        {
            Trace.WriteLine(ex);
            return null;
        }
    }

    private static string? GetEditId(BitmapFrame image)
    {
        ReadHistoryMarker(image, out var id);
        return id;
    }

    private void TryDeleteJournal(string? id)
    {
        if (id is null)
            return;
        try
        {
            File.Delete(JournalPath(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine(ex);
        }
    }

    private sealed record StoredJournal(string Digest, EditJournal Journal);

    private void PruneJournals(HashSet<string>? live, DateTime? writtenBy = null)
    {
        try
        {
            if (!Directory.Exists(_editFolder))
                return;
            foreach (var path in Directory.EnumerateFiles(_editFolder, "*.framelet"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (Guid.TryParseExact(id, "N", out _) && live?.Contains(id) != true
                    && (writtenBy is null || File.GetLastWriteTimeUtc(path) <= writtenBy))
                    TryDeleteJournal(id);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine(error);
        }
    }

    private bool BelongsToHistory(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            return ReadHistoryMarker(frame, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException)
        {
            return false;
        }
    }

    private static bool ReadHistoryMarker(BitmapFrame frame, out string? editId)
    {
        editId = null;
        if (frame.Metadata is not BitmapMetadata metadata)
            return false;
        var marker = metadata.GetQuery(HistoryQuery) as string;
        if (Guid.TryParseExact(marker, "N", out _))
            editId = marker;
        else if (metadata.GetQuery(EditIdQuery) is string legacy && Guid.TryParseExact(legacy, "N", out _))
            editId = legacy;
        return marker == "1" || editId is not null;
    }

    internal static BitmapSource ReadThumbnail(HistoryItem item)
    {
        using var stream = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var thumbnail = new BitmapImage();
        thumbnail.BeginInit();
        thumbnail.StreamSource = stream;
        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
        if (item.Width >= item.Height)
            thumbnail.DecodePixelWidth = Math.Min(item.Width, 420);
        else
            thumbnail.DecodePixelHeight = Math.Min(item.Height, 240);
        thumbnail.EndInit();
        thumbnail.Freeze();

        return thumbnail;
    }

    private static DateTime ReadCreatedUtc(string path)
        => TryReadLegacyTimestamp(path, out var date) ? date : File.GetCreationTimeUtc(path);

    internal static bool TryReadLegacyTimestamp(string path, out DateTime time)
    {
        var stem = Path.GetFileNameWithoutExtension(path).AsSpan();
        var separator = stem.IndexOf('_');
        time = default;
        return separator >= 0 && Guid.TryParseExact(stem[(separator + 1)..], "N", out _) &&
            DateTime.TryParseExact(stem[..separator], DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
    }

    internal static void AtomicWrite(string path, Action<Stream> write, DateTime? createdUtc = null, bool overwrite = true)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = File.Create(temporary))
                write(stream);

            if (createdUtc is { } date)
                File.SetCreationTimeUtc(temporary, date);
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
