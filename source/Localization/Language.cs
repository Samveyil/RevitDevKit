using System;
using System.Globalization;

namespace RevitDevKit.Localization
{
    // The language of DevKit's text. English by default and Russian in a Russian Revit, unless settings.json says
    // otherwise ("language": "auto", "en" or "ru"). The text is in Localization\*Strings.resx, English, and
    // *Strings.ru.resx, Russian, built into ru\RevitDevKit.resources.dll. Each English file gets a class of its own
    // name (ConsoleStrings.ClearToolTip), public so that XAML takes the text with {x:Static}.
    // The language is set once at start-up on DevKit's own text, never on Revit's thread culture.
    public static class Language
    {
        public const string Auto = "auto";
        public const string English = "en";
        public const string Russian = "ru";

        private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo(English);
        private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo(Russian);

        // The culture DevKit's text is in: English until Apply.
        public static CultureInfo Culture { get; private set; } = EnglishCulture;

        public static bool IsRussian => Culture.Equals(RussianCulture);

        // setting: "auto", "en" or "ru" from settings.json; anything else counts as "auto".
        public static void Apply(bool revitIsRussian, string setting)
        {
            Culture = Choose(revitIsRussian, setting);

            // Every generated class keeps its own culture: a new *Strings.resx is added here.
            CoreStrings.Culture = Culture;
            RibbonStrings.Culture = Culture;
            ConsoleStrings.Culture = Culture;
        }

        public static CultureInfo Choose(bool revitIsRussian, string setting)
        {
            if (string.Equals(setting, English, StringComparison.OrdinalIgnoreCase)) return EnglishCulture;
            if (string.Equals(setting, Russian, StringComparison.OrdinalIgnoreCase)) return RussianCulture;
            return revitIsRussian ? RussianCulture : EnglishCulture;
        }
    }
}
