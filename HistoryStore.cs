using System.Globalization;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Framelet;

internal sealed record Preferences(bool Hotkeys = true, bool KeepHistory = true, string Language = "ru", bool PrintScreen = true,
    string? StorageFolder = null, string[]? HistoryFolders = null, TrayShortcut? WorkspaceShortcut = null,
    WorkspaceSize? WindowSize = null, PalettePreferences? Palette = null);

internal sealed record WorkspaceSize(double Width, double Height, bool Maximized)
{
    internal bool IsValid => double.IsFinite(Width) && Width > 0 && double.IsFinite(Height) && Height > 0;
}

internal sealed record PalettePreferences(string Color, string[] RecentColors);

internal sealed record HistoryItem(string FilePath, BitmapSource Thumbnail, DateTime CreatedUtc, int Width, int Height)
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
    internal const int MaxItems = 50;
    private const string DateFormat = "yyyyMMdd-HHmmss-fffffff";
    private const string EditIdQuery = "/tEXt/{str=FrameletEditId}";
    private const string HistoryQuery = "/tEXt/{str=FrameletHistory}";
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly string _editFolder;
    private string[] _libraryFolders;

    internal HistoryStore()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Framelet");
        DefaultFolder = Path.Combine(data, "History");
        Folder = DefaultFolder;
        _libraryFolders = [DefaultFolder];
        _editFolder = Path.Combine(data, "Edits");
        _settingsPath = Path.Combine(data, "preferences.json");
    }

    internal string DefaultFolder { get; }
    internal string Folder { get; private set; }

    internal void ConfigureStorage(Preferences preferences)
    {
        lock (_gate)
        {
            Folder = NormalizeFolder(preferences.StorageFolder) ?? DefaultFolder;
            _libraryFolders = (preferences.HistoryFolders ?? []).Select(NormalizeFolder).OfType<string>()
                .Append(DefaultFolder).Append(Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        {
            var items = new List<HistoryItem>();
            foreach (var path in Entries().Take(MaxItems))
            {
                try
                {
                    items.Add(ReadItem(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException)
                {
                    // A missing or damaged entry should not hide the rest of the history.
                }
            }
            return items;
        }
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
                }, createdUtc);
            }
            catch
            {
                TryDeleteJournal(id);
                throw;
            }
            TryDeleteJournal(previousId);
            if (!exists)
                foreach (var expired in Entries().Skip(MaxItems))
                    DeleteEntry(expired);

            return ReadItem(path, image.PixelWidth, image.PixelHeight);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            foreach (var path in Entries())
                DeleteEntry(path);

            if (_libraryFolders.Length == 1 && Directory.Exists(_editFolder))
                foreach (var journal in Directory.EnumerateFiles(_editFolder, "*.framelet"))
                    File.Delete(journal);
        }
    }

    internal void Delete(string path)
    {
        lock (_gate)
            DeleteEntry(path);
    }

    private void DeleteEntry(string path)
    {
        if (!BelongsToHistory(path))
            return;
        var id = ReadEditId(path);
        File.Delete(path);
        TryDeleteJournal(id);
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
        if (image.Metadata is not BitmapMetadata metadata)
            return null;

        var id = metadata.GetQuery(HistoryQuery) as string ?? metadata.GetQuery(EditIdQuery) as string;
        return Guid.TryParseExact(id, "N", out _) ? id : null;
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

    private string[] Entries()
    {
        var entries = new List<string>();
        foreach (var folder in _libraryFolders)
        {
            try
            {
                if (Directory.Exists(folder))
                    entries.AddRange(Directory.GetFiles(folder, "*.png").Where(BelongsToHistory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine(ex);
            }
        }
        return entries.OrderByDescending(ReadCreatedUtc).ThenByDescending(path => path, StringComparer.Ordinal).ToArray();
    }

    private bool BelongsToHistory(string path)
    {
        if (string.Equals(Path.GetDirectoryName(path), DefaultFolder, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            return frame.Metadata is BitmapMetadata metadata && metadata.GetQuery(HistoryQuery) is string marker
                && (marker == "1" || Guid.TryParseExact(marker, "N", out _));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException)
        {
            return false;
        }
    }

    private static HistoryItem ReadItem(string path, int width = 0, int height = 0)
    {
        using var stream = File.OpenRead(path);
        if (width == 0 || height == 0)
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            stream.Position = 0;
        }

        var thumbnail = new BitmapImage();
        thumbnail.BeginInit();
        thumbnail.StreamSource = stream;
        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height)
            thumbnail.DecodePixelWidth = Math.Min(width, 420);
        else
            thumbnail.DecodePixelHeight = Math.Min(height, 240);
        thumbnail.EndInit();
        thumbnail.Freeze();

        return new HistoryItem(path, thumbnail, ReadCreatedUtc(path), width, height);
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

    private static void AtomicWrite(string path, Action<Stream> write, DateTime? createdUtc = null)
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
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
