using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Framelet;

internal static partial class Program
{
    [STAThread]
    private static void Main()
    {
        var directory = Directory.CreateTempSubdirectory("Framelet-regression-");
        try
        {
            HistoryPreservesFiles(directory.FullName);
            HistoryCleansUnreferencedJournals(directory.FullName);
            HistoryViewportIncludesLastRow();
            OcrCacheTracksImageChanges(directory.FullName);
            LanguageFollowsSystem();
            OcrDistinguishesYearsFromPhones();
            RegionSelectionUsesDesktopCoordinates();
            RegionSelectionResizesFromCorners();
            RegionCaptureComposesMonitors();
            ExportPreservesLargeImage();
            ScrollStitchingPreservesContent();
            ScrollStitchingKeepsOneFooter();
            PrintScreenHookRestarts();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void HistoryCleansUnreferencedJournals(string root)
    {
        var data = Path.Combine(root, "JournalCleanup");
        var edits = Path.Combine(data, "Edits");
        var otherFolder = Directory.CreateDirectory(Path.Combine(root, "OtherHistory")).FullName;
        var store = new HistoryStore(data);
        var settings = new Preferences(HistoryFolders: [otherFolder]);
        store.ConfigureStorage(settings);
        var image = new WriteableBitmap(36, 24, 96, 96, PixelFormats.Bgr32, null);
        image.Freeze();
        var redaction = new MarkState(EditorTool.Redact, 0xFF000000, 4, [new(1, 1), new(6, 6)], null, false);
        var document = new EditorDocument(image, new EditJournal(EditJournal.CurrentVersion, [redaction], [], []));
        var path = Path.Combine(store.DefaultFolder, "edited.png");
        var copy = Path.Combine(otherFolder, "renamed-copy.png");
        store.Put(path, image, document);
        File.Copy(path, copy);
        var png = File.ReadAllBytes(path);
        var journal = Directory.GetFiles(edits, "*.framelet").Single();

        File.Delete(path);
        store.Load();
        Require(File.Exists(journal) && store.OpenEditorDocument(copy).Journal is not null,
            "A copy in another history folder keeps its edit journal");

        File.Delete(copy);
        var offline = Path.Combine(root, "OfflineHistory");
        store.ConfigureStorage(settings with { HistoryFolders = [otherFolder, offline] });
        store.Load();
        Require(File.Exists(journal), "An unavailable history folder defers journal cleanup");
        Directory.CreateDirectory(offline);

        var locked = Path.Combine(otherFolder, "locked-copy.png");
        File.WriteAllBytes(locked, png);
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Load();
            Require(File.Exists(journal), "A locked PNG can still reference a journal and defers cleanup");
        }
        File.Delete(locked);

        var corrupt = Path.Combine(otherFolder, "damaged.png");
        File.WriteAllBytes(corrupt, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        store.Load();
        Require(!File.Exists(journal) && File.Exists(corrupt),
            "A corrupt PNG is left in place and does not block orphan journal cleanup");

        File.WriteAllBytes(path, png);
        Require(store.Load().Single().FilePath == path && store.OpenEditorDocument(path).Journal is null,
            "The restored PNG opens as a finished image after journal cleanup");

        store.Put(path, image, document);
        store.ConfigureStorage(settings with { HistoryFolders = [otherFolder, Path.Combine(root, "MissingDuringClear")] });
        var cutoff = DateTime.UtcNow.AddSeconds(1);
        store.ForgetHistory(cutoff);
        Require(File.Exists(path) && store.Load().Count == 0 && Directory.GetFiles(edits, "*.framelet").Length == 0,
            "Explicit clearing drops edit originals even with an offline folder and retains the saved PNG");
    }

    private static void HistoryPreservesFiles(string root)
    {
        var data = Path.Combine(root, "AppData");
        var custom = Path.Combine(root, "Pictures");
        var store = new HistoryStore(data);
        var settings = new Preferences(StorageFolder: custom);
        store.ConfigureStorage(settings);
        var image = new WriteableBitmap(36, 24, 96, 96, PixelFormats.Bgr32, null);
        image.Freeze();
        var foreignDefault = Path.Combine(store.DefaultFolder, "external.png");
        var foreignCustom = Path.Combine(custom, "external.png");
        HistoryStore.WritePng(foreignDefault, image);
        HistoryStore.WritePng(foreignCustom, image);
        var saved = new List<string>();
        for (var index = 0; index < 125; index++)
        {
            var path = Path.Combine(index < 4 ? store.DefaultFolder : custom, $"capture-{index:D2}.png");
            store.Put(path, image);
            saved.Add(path);
        }
        var display = store.Load();
        Require(display.Count == saved.Count && display.All(item => saved.Contains(item.FilePath)),
            "History includes every owned snapshot across both folders");
        var thumbnail = HistoryStore.ReadThumbnail(display[^1]);
        Require(thumbnail.IsFrozen && thumbnail.PixelWidth == 36 && thumbnail.PixelHeight == 24,
            "The oldest thumbnail can be created on demand");
        Require(saved.All(File.Exists) && File.Exists(foreignDefault) && File.Exists(foreignCustom),
            "Saving more than 50 screenshots preserves every file");
        RequireThrows<IOException>(() => store.Put(foreignDefault, image), "An existing foreign PNG cannot be overwritten by history");
        RequireThrows<IOException>(() => store.Delete(foreignDefault), "A foreign PNG cannot be deleted from history");

        var cutoff = DateTime.UtcNow;
        var cleared = settings with { HistoryClearedUtc = cutoff };
        store.SavePreferences(cleared);
        store.ForgetHistory(cutoff);
        var reopened = new HistoryStore(data);
        reopened.ConfigureStorage(reopened.LoadPreferences());
        Require(reopened.Load().Count == 0 && saved.All(File.Exists) && File.Exists(foreignDefault),
            "Clearing history survives restart and leaves the files in place");

        var next = Path.Combine(custom, "after-clear.png");
        store.Put(next, image);
        File.SetCreationTimeUtc(next, cutoff.AddSeconds(1));
        Require(reopened.Load().Single().FilePath == next, "New snapshots appear after clearing the list");
    }

    private static void HistoryViewportIncludesLastRow()
    {
        const int count = 20_001, columns = 4;
        const double viewportHeight = 590;
        var width = columns * HistoryViewport.TileWidth + 15;
        var rows = (int)Math.Ceiling((double)count / columns);
        var extent = rows * HistoryViewport.TileHeight;
        var first = HistoryViewport.VisibleRange(count, width, viewportHeight, 0);
        var last = HistoryViewport.VisibleRange(count, width, viewportHeight, viewportHeight - extent);
        Require(first.Start == 0 && first.Count <= 24, "The initial viewport realizes only a few rows");
        Require(last.Start + last.Count == count && last.After == 0 && last.Count <= 24,
            "Scrolling to the end reaches the oldest item in a 20,001-item history");
        Require(last.Before + Math.Ceiling((double)last.Count / columns) * HistoryViewport.TileHeight == extent,
            "Unrealized rows preserve the full scroll extent");
    }

    private static void OcrCacheTracksImageChanges(string root)
    {
        var owned = Path.Combine(root, "OcrIndex");
        var png = Path.Combine(root, "test-image.png");
        File.WriteAllBytes(png, [1, 2, 3]);
        var stamp = ImageStamp.FromPath(png);
        var cache = new HistoryTextCache(owned);
        cache.Write(png, stamp, "Restart-safe text");
        Require(new HistoryTextCache(owned).Read(png, stamp) == "Restart-safe text", "Search text is reused after restart");
        File.AppendAllText(png, "changed");
        Require(cache.Read(png, ImageStamp.FromPath(png)) is null, "Changed images miss the OCR cache");
        cache.Clear();
        Require(cache.Read(png, stamp) is null, "Clearing the index removes cached text");

        var paths = Enumerable.Range(0, 300).Select(index => Path.Combine(root, $"indexed-{index:D3}.png")).ToArray();
        foreach (var path in paths)
            cache.Write(path, stamp, Path.GetFileName(path));
        var reopened = new HistoryTextCache(owned);
        Require(paths.All(path => reopened.Read(path, stamp) == Path.GetFileName(path)),
            "All 300 OCR entries remain available after reopening the cache");
        reopened.Prune(paths[1..]);
        Require(reopened.Read(paths[0], stamp) is null && paths[1..].All(path => reopened.Read(path, stamp) is not null),
            "Cache pruning drops removed images and retains all live entries");
    }

    private static void LanguageFollowsSystem()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("lv-LV");
        Require(UiText.ResolveLanguage(null) == "en", "First launch uses English for an unsupported system language");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        Require(UiText.ResolveLanguage(null) == "ru", "First launch follows a Russian system");
        Require(UiText.ResolveLanguage("en") == "en", "A saved language overrides the system");
    }

    private static void ExportPreservesLargeImage()
    {
        const int width = 1250, height = 32000;
        var image = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        image.Freeze();
        var editor = new EditorSurface();
        var mark = new MarkState(EditorTool.Redact, 0xFF000000, 4, [new(20, height - 20), new(80, height - 3)], null, false);
        editor.Reset(image, new EditJournal(EditJournal.CurrentVersion, [mark], [], []));
        var flattened = editor.ExportAsync().GetAwaiter().GetResult();
        var pixel = new byte[4];
        flattened.CopyPixels(new Int32Rect(50, height - 6, 1, 1), pixel, pixel.Length, 0);
        Require(flattened.IsFrozen && flattened.PixelHeight == height && flattened.PixelWidth == width
            && pixel.SequenceEqual(new byte[] { 24, 19, 16, 255 }), "A background 40 MP render keeps a redaction at the bottom");
    }

    private static void PrintScreenHookRestarts()
    {
        using var hook = new PrintScreenShortcut(System.Windows.Threading.Dispatcher.CurrentDispatcher, () => { });
        hook.Configure(true);
        Require(hook.IsActive, "Print Screen registers on a dedicated message loop");
        hook.Configure(false);
        Require(!hook.IsActive, "Disabling Print Screen releases the hook");
        hook.Configure(true);
        Require(hook.IsActive, "Print Screen can register again after shutdown");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }

    private static void RequireThrows<T>(Action operation, string description) where T : Exception
    {
        try { operation(); }
        catch (T)
        {
            Console.WriteLine("PASS: " + description);
            return;
        }
        throw new InvalidOperationException(description);
    }
}
