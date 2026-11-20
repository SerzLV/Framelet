using System.Globalization;
using System.Windows;

namespace Framelet;

internal static class UiText
{
    private static ResourceDictionary? _strings;

    internal static string Language { get; private set; } = "ru";
    internal static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ru-RU");

    internal static void Apply(string? language)
    {
        var english = language == "en";
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(english ? "Translations/English.xaml" : "Translations/Russian.xaml", UriKind.Relative)
        };
        var resources = Application.Current.Resources.MergedDictionaries;
        if (_strings is null)
            resources.Add(dictionary);
        else
            resources[resources.IndexOf(_strings)] = dictionary;

        _strings = dictionary;
        Language = english ? "en" : "ru";
        Culture = CultureInfo.GetCultureInfo(english ? "en-US" : "ru-RU");
        CultureInfo.CurrentUICulture = Culture;
    }

    internal static string Key(string name) => $"Ui.{name}";

    internal static string Get(string name) => (string)_strings![Key(name)];

    internal static string Format(string name, params object[] values) => string.Format(Culture, Get(name), values);
}

