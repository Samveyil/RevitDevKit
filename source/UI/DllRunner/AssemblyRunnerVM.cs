using BIMPlugins.ExtStorage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core;
using RevitDevKit.UI.Console;
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
    public partial class AssemblyRunnerVM : ObservableObject
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        [ObservableProperty] private bool _isClearOnRun = true;

        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private bool _isNothingFound;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RunCommand))]
        [NotifyCanExecuteChangedFor(nameof(ReloadAssemblyCommand))]
        [NotifyCanExecuteChangedFor(nameof(RemoveAssemblyCommand))]
        [NotifyCanExecuteChangedFor(nameof(RevealInExplorerCommand))]
        private AssemblyTreeNodeVM _selectedNode;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RunCommand))]
        private bool _isRunning;

        public ObservableCollection<AssemblyNodeVM> Assemblies { get; } = [];

        public AppListVM Apps { get; } = new AppListVM();

        // 0 - commands, 1 - applications. The toolbar and hotkeys follow the open tab.
        [ObservableProperty] private int _selectedTabIndex;

        private bool IsAppsTab => SelectedTabIndex == 1;

        public event EventHandler CloseRequested;

        public AssemblyRunnerVM() => Restore();

        partial void OnSearchTextChanged(string value) => ApplySearch();

        [RelayCommand]
        private void RunHotkey() => Execute(IsAppsTab ? Apps.ApplyCommand : RunCommand);

        [RelayCommand]
        private void AddHotkey() => Execute(IsAppsTab ? Apps.AddAssemblyCommand : AddAssemblyCommand);

        [RelayCommand]
        private void ReloadHotkey() => Execute(IsAppsTab ? Apps.ApplyCommand : ReloadAssemblyCommand);

        private static void Execute(System.Windows.Input.ICommand command)
        {
            if (command.CanExecute(null)) command.Execute(null);
        }

        private AssemblyNodeVM TargetAssembly(AssemblyNodeVM node) => node ?? (SelectedNode as TargetNodeVM)?.Owner;

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
                Refresh(file);
        }

        private bool HasAssembly(AssemblyNodeVM node) => TargetAssembly(node) != null;

        [RelayCommand(CanExecute = nameof(HasAssembly))]
        private void ReloadAssembly(AssemblyNodeVM node)
        {
            node = TargetAssembly(node);
            if (node != null)
                Refresh(node.FilePath);
        }

        [RelayCommand(CanExecute = nameof(HasAssembly))]
        private void RemoveAssembly(AssemblyNodeVM node)
        {
            node = TargetAssembly(node);
            if (node == null) return;

            CachedAssemblyLoader.Forget(node.FilePath);
            Assemblies.Remove(node);
            if ((SelectedNode as TargetNodeVM)?.Owner == node)
                SelectedNode = null;
            
            Save();
            ApplySearch();
        }

        [RelayCommand(CanExecute = nameof(HasAssembly))]
        private void RevealInExplorer(AssemblyNodeVM node)
        {
            node = TargetAssembly(node);
            if (node == null || !File.Exists(node.FilePath)) return;
            Process.Start("explorer.exe", "/select,\"" + node.FilePath + "\"");
        }

        private void Refresh(string path)
        {
            try
            {
                var loaded = CachedAssemblyLoader.Load(path);
                var commands = EntryPointResolver.FindAll(loaded.Assembly, includeMethods: false).Select(t => t.TypeName);
                var node = new AssemblyNodeVM(loaded.OriginalPath, commands, n => SelectedNode = n);

                var previous = FindAssembly(loaded.OriginalPath);
                var selected = SelectedNode as TargetNodeVM;

                if (previous == null)
                    Assemblies.Add(node);
                else
                    Assemblies[Assemblies.IndexOf(previous)] = node;

                if (selected != null && selected.Owner == previous)
                {
                    var again = node.Targets.FirstOrDefault(t => t.TypeName == selected.TypeName);
                    if (again != null)
                        again.IsSelected = true;
                    else
                        SelectedNode = null;
                }

                Save();
                ApplySearch();
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось загрузить " + path + ":");
            }
        }

        private AssemblyNodeVM FindAssembly(string path) =>
            Assemblies.FirstOrDefault(a => string.Equals(a.FilePath, path, StringComparison.OrdinalIgnoreCase));

        [RelayCommand]
        private void CollapseAll()
        {
            foreach (var assembly in Assemblies)
            {
                assembly.IsExpanded = false;
                foreach (var folder in assembly.Children.OfType<NamespaceNodeVM>())
                    folder.IsExpanded = false;
            }
        }

        // Expansion state to restore when the search box is cleared.
        private Dictionary<AssemblyTreeNodeVM, bool> _expandedBeforeSearch;

        private IEnumerable<AssemblyTreeNodeVM> ExpandableNodes() =>
            Assemblies.SelectMany(a => new AssemblyTreeNodeVM[] { a }.Concat(a.Children.OfType<NamespaceNodeVM>()));

        private void ApplySearch()
        {
            var query = SearchText?.Trim() ?? string.Empty;

            if (query.Length > 0 && _expandedBeforeSearch == null)
                _expandedBeforeSearch = ExpandableNodes().ToDictionary(n => n, n => n.IsExpanded);

            foreach (var assembly in Assemblies)
                assembly.Filter(query);

            if (query.Length == 0 && _expandedBeforeSearch != null)
            {
                foreach (var node in ExpandableNodes())
                    if (_expandedBeforeSearch.TryGetValue(node, out var expanded))
                        node.IsExpanded = expanded;

                _expandedBeforeSearch = null;
            }

            IsNothingFound = query.Length > 0 && Assemblies.Count > 0 && !Assemblies.Any(a => a.IsVisible);

            if (SelectedNode != null && !SelectedNode.IsVisible)
            {
                SelectedNode.IsSelected = false;
                SelectedNode = null;
            }
        }

        private bool CanRun(TargetNodeVM node) => (node ?? SelectedNode as TargetNodeVM) != null && !IsRunning;

        [RelayCommand(CanExecute = nameof(CanRun))]
        // Always resolves the command from the DLL on disk, so a rebuilt class with the same
        // name runs without refreshing the list.
        private void Run(TargetNodeVM node)
        {
            node ??= SelectedNode as TargetNodeVM;
            if (node == null) return;

            RunTarget target;
            try
            {
                var loaded = CachedAssemblyLoader.Load(node.Owner.FilePath);
                target = EntryPointResolver.FindAll(loaded.Assembly, includeMethods: false)
                    .FirstOrDefault(t => t.TypeName == node.TypeName);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось загрузить " + node.Owner.FilePath + ":");
                return;
            }

            if (target == null)
            {
                ConsoleOutput.Error("В актуальной " + node.Owner.DisplayName + " нет команды " + node.TypeName + ". Обновите список команд.");
                return;
            }

            if (IsClearOnRun) ConsoleOutput.Clear();

            IsRunning = true;

            var request = new RunRequest
            {
                Title = target.DisplayName,
                TransactionName = "DevKit: " + target.Type.Name,
                Body = () =>
                {
                    // The window closes on run; showing the console pane needs an API context, i.e. here.
                    ConsolePane.TryShow(RevitAPI.UIApplication);
                    return target.Invoke();
                },
                Completed = error => _dispatcher.Invoke(() => IsRunning = false)
            };

            try
            {
                RevitTaskRunner.Run(request, needsCommandContext: true);
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось передать запуск в Revit:");
                IsRunning = false;
            }
        }

        // Format: DLL path at line start, its command type names below, indented.
        private static string StorePath => Path.Combine(App.DataDirectory, "assemblies.txt");

        private void Save()
        {
            try
            {
                var text = new StringBuilder();
                foreach (var assembly in Assemblies)
                {
                    text.AppendLine(assembly.FilePath);
                    foreach (var target in assembly.Targets)
                        text.AppendLine("    " + target.TypeName);
                }

                File.WriteAllText(StorePath, text.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось сохранить список DLL: " + ex.Message);
            }
        }

        private void Restore()
        {
            try
            {
                if (!File.Exists(StorePath)) return;

                string path = null;
                var commands = new List<string>();

                foreach (var line in File.ReadAllLines(StorePath, Encoding.UTF8).Concat(new[] { string.Empty }))
                {
                    if (line.StartsWith(" ") && path != null)
                    {
                        commands.Add(line.Trim());
                        continue;
                    }

                    if (path != null)
                        Assemblies.Add(new AssemblyNodeVM(path, commands, n => SelectedNode = n));

                    path = string.IsNullOrWhiteSpace(line) ? null : line.Trim();
                    commands = new List<string>();
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось прочитать список DLL: " + ex.Message);
            }
        }
    }
}
