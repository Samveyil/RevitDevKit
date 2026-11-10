using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace RevitDevKit.UI.DllRunner
{
    public sealed partial class AddinItemVM : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FilePath), nameof(IsEnabled), nameof(CanChange), nameof(IsLocked), nameof(HasProblem), nameof(Details), nameof(ToolTip))]
        private AddinManifest _manifest;

        public AddinItemVM(AddinManifest manifest) => _manifest = manifest;

        public string Name => Manifest.Name;
        public string FilePath => Manifest.FilePath;
        public AddinScope Scope => Manifest.Scope;
        public bool IsAllUsers => Scope == AddinScope.AllUsers;

        public bool IsEnabled => Manifest.IsEnabled;
        public bool CanChange => Manifest.CanChange && !Manifest.HasTwin;
        public bool IsLocked => !Manifest.CanChange;

        public bool HasProblem => Manifest.Error != null || Manifest.HasTwin || Manifest.MissingAssemblies.Count > 0;

        public string GroupTitle => StartupListVM.GroupTitle(Scope);

        // The second line: what is wrong first, then why it cannot be changed, then what is inside.
        public string Details
        {
            get
            {
                var m = Manifest;
                if (m.Error != null) return "не читается: " + m.Error;
                if (m.HasTwin) return "есть и " + m.Name + AddinManifests.Extension + ", и " + m.Name + AddinManifests.DisabledExtension;
                if (m.MissingAssemblies.Count > 0) return "DLL не найдена: " + m.MissingAssemblies[0];
                if (IsLocked) return "только чтение: нужны права администратора";
                if (!m.IsEnabled) return "отключён · " + Path.GetFileName(m.FilePath);
                return Contents(m) + (m.Assemblies.Count == 0 ? string.Empty : " · " + string.Join(", ", m.Assemblies.Select(Path.GetFileName)));
            }
        }

        public string ToolTip => FilePath;

        private static string Contents(AddinManifest m)
        {
            var parts = new List<string>();
            if (m.Applications > 0) parts.Add(m.Applications + " " + Plural(m.Applications, "приложение", "приложения", "приложений"));
            if (m.Commands > 0) parts.Add(m.Commands + " " + Plural(m.Commands, "команда", "команды", "команд"));
            return parts.Count == 0 ? "пусто" : string.Join(", ", parts);
        }

        private static string Plural(int n, string one, string few, string many)
        {
            var tens = n % 100;
            var units = n % 10;
            if (tens >= 11 && tens <= 14) return many;
            if (units == 1) return one;
            if (units >= 2 && units <= 4) return few;
            return many;
        }

        // Lets the check box catch up with the file when a rename did not happen.
        internal void Resync() => OnPropertyChanged(nameof(IsEnabled));
    }

    // The .addin files Revit of this version loads at startup, with a check box to turn each on or off
    // for the next start: the file is renamed to .addin.disable and back, as Addin Manager does.
    public sealed partial class StartupListVM : ObservableObject
    {
        // The state as Revit loaded it, taken on the first look, so the note shows only real changes.
        private Dictionary<string, bool> _atStartup;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RevealInExplorerCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
        private AddinItemVM _selectedItem;

        [ObservableProperty] private bool _isRestartNeeded;

        public ObservableCollection<AddinItemVM> Items { get; } = [];

        // Grouped by folder: the user's add-ins first, then those for all users.
        public ICollectionView View { get; }

        // Asked before DevKit turns itself off; tests answer it without a message box.
        public Func<bool> ConfirmDisableDevKit { get; set; } = AskDisableDevKit;

        public StartupListVM()
        {
            View = new ListCollectionView(Items);
            View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AddinItemVM.GroupTitle)));
        }

        internal static string GroupTitle(AddinScope scope) => scope == AddinScope.CurrentUser
            ? "Для текущего пользователя · " + Short(AddinManifests.CurrentUserFolder, Environment.SpecialFolder.ApplicationData, "%AppData%")
            : "Для всех пользователей · " + Short(AddinManifests.AllUsersFolder, Environment.SpecialFolder.CommonApplicationData, "%ProgramData%");

        // %AppData%\…\Addins\2022: the root as a variable and the last two folders, to fit the narrow window.
        private static string Short(string folder, Environment.SpecialFolder root, string variable)
        {
            if (string.IsNullOrEmpty(folder)) return "папка не найдена";

            var rootPath = Environment.GetFolderPath(root).TrimEnd('\\');
            if (!folder.StartsWith(rootPath + "\\", StringComparison.OrdinalIgnoreCase)) return folder;

            var parts = folder.Substring(rootPath.Length + 1).Split('\\');
            return parts.Length <= 2 ? variable + "\\" + string.Join("\\", parts) : variable + "\\…\\" + string.Join("\\", parts.Skip(parts.Length - 2));
        }

        [RelayCommand]
        public void Refresh()
        {
            var selected = SelectedItem?.FilePath;
            List<AddinManifest> manifests;
            try
            {
                manifests = AddinManifests.Scan();
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось прочитать папки надстроек:");
                return;
            }

            Items.Clear();
            foreach (var manifest in manifests)
                Items.Add(new AddinItemVM(manifest));

            _atStartup ??= Items.GroupBy(Key).ToDictionary(g => g.Key, g => g.Any(i => i.IsEnabled));
            SelectedItem = Items.FirstOrDefault(i => string.Equals(i.FilePath, selected, StringComparison.OrdinalIgnoreCase));
            UpdateRestartNote();
        }

        [RelayCommand]
        private void Toggle(AddinItemVM item)
        {
            if (item == null) return;

            var enable = !item.IsEnabled;
            if (!enable && item.Manifest.IsDevKit && !ConfirmDisableDevKit())
            {
                item.Resync();
                return;
            }

            try
            {
                item.Manifest = AddinManifests.SetEnabled(item.Manifest, enable);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Error("Не удалось " + (enable ? "включить " : "отключить ") + item.Name + ": " + ex.Message);
                item.Resync();
            }

            RevealInExplorerCommand.NotifyCanExecuteChanged();
            OpenFileCommand.NotifyCanExecuteChanged();
            UpdateRestartNote();
        }

        private bool HasFile(AddinItemVM item) => (item ?? SelectedItem) != null;

        [RelayCommand(CanExecute = nameof(HasFile))]
        private void RevealInExplorer(AddinItemVM item)
        {
            item ??= SelectedItem;
            if (item == null || !File.Exists(item.FilePath)) return;
            Process.Start("explorer.exe", "/select,\"" + item.FilePath + "\"");
        }

        [RelayCommand(CanExecute = nameof(HasFile))]
        private void OpenFile(AddinItemVM item)
        {
            item ??= SelectedItem;
            if (item == null || !File.Exists(item.FilePath)) return;
            Process.Start("notepad.exe", "\"" + item.FilePath + "\"");
        }

        private void UpdateRestartNote() =>
            IsRestartNeeded = _atStartup != null && Items.GroupBy(Key)
                .Any(g => _atStartup.TryGetValue(g.Key, out var was) && was != g.Any(i => i.IsEnabled));

        private static string Key(AddinItemVM item) => item.Scope + "|" + item.Name.ToLowerInvariant();

        private static bool AskDisableDevKit() =>
            MessageBox.Show(
                "После перезапуска Revit DevKit не загрузится, и этого окна не будет.\n\n" +
                "Включить его обратно можно, переименовав RevitDevKit.addin.disable в RevitDevKit.addin в папке надстроек.\n\n" +
                "Отключить DevKit?",
                "DevKit", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
