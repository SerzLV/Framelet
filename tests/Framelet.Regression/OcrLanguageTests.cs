using System.IO;
using System.Windows;
using System.Windows.Controls;
using Framelet;

internal static partial class Program
{
    private static void OcrLanguageCatalogKeepsTheDefault(string root)
    {
        var languages = OcrLanguages.Catalog.ToArray();
        Require(languages.Length == 123 && languages.Select(language => language.Code).Distinct().Count() == languages.Length
            && languages.All(language => language.Size > 0 && language.Hash.Length == 40)
            && OcrLanguages.Find("chi_sim_vert") is not null,
            "The pinned catalog contains all supported language models, including vertical variants");
        Require(OcrLanguages.Automatic(null) == "rus+lav+eng" && OcrLanguages.Automatic(["..", "unknown"]) == "rus+lav+eng"
            && OcrLanguages.Automatic(["eng", "lav", "eng", "unknown"]) == "eng+lav",
            "Auto preserves the default for old or invalid settings and retains valid choices in priority order");
        var directory = OcrLanguages.ModelDirectory(OcrService.DefaultAutomaticLanguages);
        foreach (var code in OcrService.DefaultAutomaticLanguages.Split('+'))
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, $"{code}.traineddata"));
            var model = OcrLanguages.Find(code)!;
            Require(OcrLanguages.MatchesModel(model, bytes), $"Bundled {code} model matches the pinned download catalog");
            bytes[0] ^= 1;
            Require(!OcrLanguages.MatchesModel(model, bytes), $"A modified model is rejected even with the expected length ({code})");
        }

        var cache = new HistoryTextCache(Path.Combine(root, "language-ocr-cache"));
        var image = Path.Combine(root, "language-ocr-image.png");
        var stamp = new ImageStamp(1, DateTime.UnixEpoch, DateTime.UnixEpoch);
        cache.Write(image, stamp, "last result", "eng+lav");
        Require(cache.Read(image, stamp, "eng+lav") == "last result" && cache.Read(image, stamp) is null,
            "History OCR invalidates an entry when Auto uses a different language combination");
    }

    private static void OcrSettingsSwitchTabsAndSaveAutoLanguages(string root)
    {
        var store = new HistoryStore(Path.Combine(root, "ocr-settings"));
        store.SavePreferences(new Preferences(Language: "en", OcrTemplates: [InvoiceTemplate]));
        var window = new MainWindow(store, IntPtr.Zero);
        try
        {
            var general = (RadioButton)window.FindName("GeneralSettingsTab");
            var ocr = (RadioButton)window.FindName("OcrSettingsTab");
            var templates = (RadioButton)window.FindName("OcrTemplatesSettingsTab");
            var generalPage = (ScrollViewer)window.FindName("GeneralSettingsPage");
            var ocrPage = (ScrollViewer)window.FindName("OcrSettingsPage");
            var templatesPage = (ScrollViewer)window.FindName("OcrTemplatesSettingsPage");
            var catalog = (ListBox)window.FindName("OcrCatalog");
            var search = (TextBox)window.FindName("OcrCatalogSearch");
            var languages = (WrapPanel)window.FindName("OcrAutoModels");
            var label = (TextBlock)window.FindName("OcrLanguageLabel");
            Require(general.IsChecked == true && generalPage.Visibility == Visibility.Visible
                && ocrPage.Visibility == Visibility.Collapsed && label.Text == "Auto",
                "General settings open by default and the editor starts in Auto");
            ocr.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            Require(ocr.IsChecked == true && ocrPage.Visibility == Visibility.Visible && generalPage.Visibility == Visibility.Collapsed,
                "The OCR tab shows language settings and hides general settings");
            search.Text = "Deutsch";
            Require(OcrLanguages.IsInstalled(OcrLanguages.Find("deu")!) || catalog.Items.Cast<OcrLanguage>().Any(model => model.Code == "deu"),
                "The install catalog finds languages by their native names");
            var russian = languages.Children.OfType<CheckBox>().Single(choice => Equals(choice.Tag, "rus"));
            russian.IsChecked = false;
            russian.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Require(OcrService.AutomaticLanguages == "lav+eng" && store.LoadPreferences().AutomaticOcrLanguages!.SequenceEqual(["lav", "eng"]),
                "Changing Auto languages updates recognition and survives reloading preferences");
            templates.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            var savedTemplates = (ItemsControl)window.FindName("SettingsOcrTemplates");
            var manage = (Button)window.FindName("OpenSettingsOcrTemplates");
            Require(templates.IsChecked == true && templatesPage.Visibility == Visibility.Visible
                && ocrPage.Visibility == Visibility.Collapsed && generalPage.Visibility == Visibility.Collapsed
                && savedTemplates.Items.Cast<OcrTemplate>().Single().Name == InvoiceTemplate.Name && manage.IsEnabled,
                "The dedicated templates tab lists saved rules and allows management without an open screenshot");
            general.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            Require(general.IsChecked == true && ocrPage.Visibility == Visibility.Collapsed && templatesPage.Visibility == Visibility.Collapsed,
                "Switching back returns to general settings");
        }
        finally
        {
            window.Close();
            OcrService.ConfigureAutomaticLanguages(null);
        }
    }
}
