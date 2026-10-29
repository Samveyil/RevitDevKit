using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;

namespace RevitDevKit.UI.DllRunner
{
    public sealed partial class AppNodeVM : ObservableObject
    {
        [ObservableProperty] private IReadOnlyList<string> _applicationTypes;
        [ObservableProperty] private string _status = "не загружено";
        [ObservableProperty] private bool _isLoaded;

        public string FilePath { get; }
        public string DisplayName => Path.GetFileName(FilePath);

        public AppNodeVM(string filePath, IEnumerable<string> applicationTypes)
        {
            FilePath = filePath;
            _applicationTypes = applicationTypes.ToList();
        }

        public string ApplicationsText =>
            ApplicationTypes.Count == 0 ? "нет IExternalApplication" : string.Join(", ", ApplicationTypes.Select(t => t.Substring(t.LastIndexOf('.') + 1)));

        partial void OnApplicationTypesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(ApplicationsText));
    }

    // IExternalApplication add-ins that DevKit starts itself, so their ribbon is rebuilt on reload.
    public sealed partial class AppListVM : ObservableObject
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
        [NotifyCanExecuteChangedFor(nameof(UnloadCommand))]
        [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
        [NotifyCanExecuteChangedFor(nameof(RevealInExplorerCommand))]
        private AppNodeVM _selectedItem;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
        [NotifyCanExecuteChangedFor(nameof(UnloadCommand))]
        [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
        private bool _isBusy;

        public ObservableCollection<AppNodeVM> Items { get; } = [];

        public AppListVM()
        {
            Restore();

            foreach (var item in Items)
                UpdateStatus(item);
        }

        private AppNodeVM Target(AppNodeVM node) => node ?? SelectedItem;

        [RelayCommand]
        private void AddAssembly()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Сборки .NET (*.dll)|*.dll|Все файлы (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() != true)
                return;

            foreach (var file in dialog.FileNames)
            {
                var node = Items.FirstOrDefault(i => SamePath(i.FilePath, file));
                if (node == null)
                {
                    node = new AppNodeVM(Path.GetFullPath(file), Array.Empty<string>());
                    Items.Add(node);
                }

                SelectedItem = node;
                Reload(node);
            }

            Save();
        }

        private bool CanChange(AppNodeVM node) => Target(node) != null && !IsBusy;

        private bool CanUnload(AppNodeVM node) => CanChange(node) && Target(node).IsLoaded;

        private bool HasTarget(AppNodeVM node) => Target(node) != null;

        [RelayCommand(CanExecute = nameof(CanChange))]
        private void Reload(AppNodeVM node)
        {
            node = Target(node);
            if (node == null) return;

            Enqueue(node, "Обновление " + node.DisplayName, () =>
            {
                var app = AppHost.Reload(node.FilePath);
                node.ApplicationTypes = app.ApplicationTypes;
            });
        }

        [RelayCommand(CanExecute = nameof(CanUnload))]
        private void Unload(AppNodeVM node)
        {
            node = Target(node);
            if (node == null) return;

            Enqueue(node, "Выгрузка " + node.DisplayName, () => AppHost.Unload(node.FilePath));
        }

        [RelayCommand(CanExecute = nameof(CanChange))]
        private void Remove(AppNodeVM node)
        {
            node = Target(node);
            if (node == null) return;

            if (node.IsLoaded)
                Enqueue(node, "Выгрузка " + node.DisplayName, () => AppHost.Unload(node.FilePath));

            Items.Remove(node);
            if (SelectedItem == node) SelectedItem = null;
            Save();
        }

        [RelayCommand(CanExecute = nameof(HasTarget))]
        private void RevealInExplorer(AppNodeVM node)
        {
            node = Target(node);
            if (node == null || !File.Exists(node.FilePath)) return;
            Process.Start("explorer.exe", "/select,\"" + node.FilePath + "\"");
        }

        // OnStartup of the add-in and every ribbon change need a Revit API context.
        private void Enqueue(AppNodeVM node, string title, Action action)
        {
            IsBusy = true;

            RevitTaskRunner.Enqueue(new RunRequest
            {
                Title = title,
                Body = () =>
                {
                    action();
                    return null;
                },
                Completed = error => _dispatcher.Invoke(() =>
                {
                    UpdateStatus(node);
                    IsBusy = false;
                    UnloadCommand.NotifyCanExecuteChanged();
                    Save();
                })
            });
        }

        private static void UpdateStatus(AppNodeVM node)
        {
            var app = AppHost.Find(node.FilePath);
            node.IsLoaded = app != null;
            node.Status = app == null ? "не загружено" : "загружено в " + app.LoadedAt.ToString("HH:mm:ss");
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        // Format: DLL path at line start, its IExternalApplication type names below, indented.
        private static string StorePath => Path.Combine(App.DataDirectory, "applications.txt");

        private void Save()
        {
            try
            {
                var text = new StringBuilder();
                foreach (var item in Items)
                {
                    text.AppendLine(item.FilePath);
                    foreach (var type in item.ApplicationTypes)
                        text.AppendLine("    " + type);
                }

                File.WriteAllText(StorePath, text.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось сохранить список приложений: " + ex.Message);
            }
        }

        private void Restore()
        {
            try
            {
                if (!File.Exists(StorePath)) return;

                string path = null;
                var types = new List<string>();

                foreach (var line in File.ReadAllLines(StorePath, Encoding.UTF8).Concat(new[] { string.Empty }))
                {
                    if (line.StartsWith(" ") && path != null)
                    {
                        types.Add(line.Trim());
                        continue;
                    }

                    if (path != null)
                        Items.Add(new AppNodeVM(path, types));

                    path = string.IsNullOrWhiteSpace(line) ? null : line.Trim();
                    types = new List<string>();
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось прочитать список приложений: " + ex.Message);
            }
        }
    }
}
