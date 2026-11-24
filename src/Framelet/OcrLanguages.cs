using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace Framelet;

internal sealed record OcrLanguage(string Code, string Name, int Size, string Hash)
{
    public string DisplayName
    {
        get
        {
            var name = Application.Current?.TryFindResource(UiText.Key($"OcrLanguage_{Code}")) as string ?? Name;
            return OcrLanguages.NativeName(Code) is { } native && !native.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? $"{name} · {native}" : name;
        }
    }
    public string DownloadSize => $"{Size / 1_048_576d:0.0} MB";
    internal bool Matches(string query) => query.Length == 0
        || UiText.Culture.CompareInfo.IndexOf(DisplayName, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase) || Code.Contains(query, StringComparison.OrdinalIgnoreCase);
}

internal static class OcrLanguages
{
    // Language models from tesseract-ocr/tessdata_fast 4.1.0 (Apache-2.0).
    private const string Revision = "65727574dfcd264acbb0c3e07860e4e9e9b22185";
    private static readonly string[] BundledCodes = OcrService.DefaultAutomaticLanguages.Split('+');
    private static readonly string BundledDirectory = Path.Combine(
        Path.GetDirectoryName(typeof(OcrLanguages).Assembly.Location) ?? AppContext.BaseDirectory, "Assets", "Ocr");
    private static readonly string DownloadDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Framelet", "OcrModels", "4.1.0");
    private static readonly Lazy<Dictionary<string, OcrLanguage>> Models = new(LoadCatalog);
    private static readonly Lazy<Dictionary<string, string>> NativeNames = new(() => CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .GroupBy(culture => culture.ThreeLetterISOLanguageName, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().NativeName, StringComparer.Ordinal));
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    internal static IEnumerable<OcrLanguage> Catalog => Models.Value.Values;
    internal static OcrLanguage? Find(string code) => Models.Value.GetValueOrDefault(code);
    internal static string? NativeName(string code) => NativeNames.Value.GetValueOrDefault(code);

    internal static bool IsInstalled(OcrLanguage language) => BundledCodes.Contains(language.Code)
        || new FileInfo(ModelPath(language.Code)).LengthIfPresent() == language.Size;

    internal static string Automatic(IEnumerable<string>? codes)
    {
        if (codes is null)
            return OcrService.DefaultAutomaticLanguages;
        var installed = codes.Where(code => code is not null && Find(code) is { } model && IsInstalled(model)).Distinct().ToArray();
        return installed.Length == 0 ? OcrService.DefaultAutomaticLanguages : string.Join('+', installed);
    }

    internal static string ModelDirectory(string languages)
    {
        var codes = languages.Split('+');
        if (codes.All(BundledCodes.Contains))
            return BundledDirectory;

        foreach (var code in codes)
        {
            if (Find(code) is not { } model || !IsInstalled(model))
                throw new UserFacingException("OcrModelUnavailable");
            if (!BundledCodes.Contains(code))
                continue;

            var source = Path.Combine(BundledDirectory, $"{code}.traineddata");
            var destination = ModelPath(code);
            if (new FileInfo(destination).LengthIfPresent() != new FileInfo(source).Length)
                HistoryStore.AtomicWrite(destination, output =>
                {
                    using var input = File.OpenRead(source);
                    input.CopyTo(output);
                });
        }
        return DownloadDirectory;
    }

    internal static async Task InstallAsync(OcrLanguage language, IProgress<int> progress, CancellationToken token)
    {
        if (IsInstalled(language))
            return;
        var url = $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/{Revision}/{language.Code}.traineddata";
        using var response = await GetModelResponseAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var data = await ReadModelAsync(input, language.Size, progress, DownloadIdleTimeout, token).ConfigureAwait(false);
        if (!MatchesModel(language, data))
            throw new InvalidDataException("Unexpected OCR model content.");
        token.ThrowIfCancellationRequested();
        HistoryStore.AtomicWrite(ModelPath(language.Code), output => output.Write(data));
    }

    private static async Task<HttpResponseMessage> GetModelResponseAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(DownloadIdleTimeout);
        try
        {
            return await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The OCR model server did not respond.");
        }
    }

    internal static async Task<byte[]> ReadModelAsync(Stream stream, int size, IProgress<int> progress,
        TimeSpan idleTimeout, CancellationToken token)
    {
        var data = new byte[size];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        var read = 0;
        var percent = 0;
        try
        {
            while (read < size)
            {
                idle.CancelAfter(idleTimeout);
                var length = await stream.ReadAsync(data.AsMemory(read), idle.Token).ConfigureAwait(false);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (length == 0)
                    throw new EndOfStreamException("The OCR model download ended early.");
                read += length;
                var nextPercent = (int)(100L * read / size);
                if (nextPercent != percent)
                {
                    percent = nextPercent;
                    progress.Report(read);
                }
            }
            idle.CancelAfter(idleTimeout);
            if (await stream.ReadAsync(new byte[1], idle.Token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Unexpected OCR model size.");
            return data;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && idle.IsCancellationRequested)
        {
            throw new TimeoutException("The OCR model download stopped receiving data.");
        }
    }

    internal static bool MatchesModel(OcrLanguage language, byte[] data)
    {
        // Git stores the hash of the blob header followed by the file bytes.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes(FormattableString.Invariant($"blob {data.Length}\0")));
        hash.AppendData(data);
        return data.Length == language.Size
            && Convert.ToHexString(hash.GetHashAndReset()).Equals(language.Hash, StringComparison.OrdinalIgnoreCase);
    }

    private static string ModelPath(string code) => Path.Combine(DownloadDirectory, $"{code}.traineddata");

    private static long LengthIfPresent(this FileInfo file) => file.Exists ? file.Length : -1;

    private static Dictionary<string, OcrLanguage> LoadCatalog()
    {
        using var stream = typeof(OcrLanguages).Assembly.GetManifestResourceStream("Framelet.OcrLanguages.json")!;
        return (JsonSerializer.Deserialize<OcrLanguage[]>(stream) ?? []).ToDictionary(language => language.Code, StringComparer.Ordinal);
    }
}
