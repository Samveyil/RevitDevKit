using MaterialDesignColors;
using MaterialDesignThemes.Wpf;
using RevitDevKit.Core.Ribbon;
using System;
using System.Linq;
using System.Windows;

namespace RevitDevKit.UI.Shared
{
    // MaterialDesign's styles, from the copy of MaterialDesign DevKit is built against, whatever other add-ins loaded.
    // Revit loads every add-in into one process, and another add-in may bring an older MaterialDesign (4.x). WPF takes
    // a pack URI with only the assembly's short name to the copy loaded last, so a plain
    // pack://application:,,,/MaterialDesignThemes.Wpf;component/... would give DevKit's windows that add-in's styles,
    // missing resources or a XamlParseException. So the URI names the version and the public key token, and while it
    // loads, MaterialDesign's own links between its dictionaries (short names too) also go to DevKit's copies.
    public sealed class MaterialDesignDefaults : ResourceDictionary
    {
        public MaterialDesignDefaults()
        {
            using (PackResources.Prefer(typeof(PaletteHelper).Assembly, typeof(SwatchHelper).Assembly))
                Source = ThemeUri("Themes/MaterialDesign3.Defaults.xaml");
        }

        // "pack://application:,,,/MaterialDesignThemes.Wpf;v5.2.1.0;df2a72020bd7962a;component/Themes/...",
        // read from the assembly itself so that it follows the package on update.
        internal static Uri ThemeUri(string path)
        {
            var name = typeof(PaletteHelper).Assembly.GetName();
            var token = name.GetPublicKeyToken();
            var key = token == null || token.Length == 0 ? "" : string.Concat(token.Select(b => b.ToString("x2"))) + ";";
            return new Uri("pack://application:,,,/" + name.Name + ";v" + name.Version + ";" + key + "component/" + path, UriKind.Absolute);
        }
    }
}
