using BIMPlugins.ExtStorage.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core.Addins;
using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace RevitDevKit.UI.DllRunner
{
    public sealed class LoadedAssemblyVM
    {
        // digits: of the largest number in the list, so the numbers line up right in a column just as wide.
        public LoadedAssemblyVM(ProcessAssembly assembly, IReadOnlyList<ProcessAssembly> sameName, int digits)
        {
            Assembly = assembly;
            Number = assembly.Number.ToString().PadLeft(digits);
            ToolTip = (assembly.FullName ?? assembly.Name)
                      + (assembly.FileVersion.IsNullOrEmpty() ? string.Empty : "\nВерсия файла " + assembly.FileVersion)
                      + "\n" + ProcessAssemblies.Where(assembly)
                      + (sameName.Count > 1
                          ? "\nЗагружена " + sameName.Count + " раза: " + string.Join(", ", sameName.Select(a => "№" + a.Number + " " + a.Version
                              + (a.Owner.IsNullOrEmpty() ? string.Empty : " (" + a.Owner + ")")))
                          : string.Empty);
        }

        public ProcessAssembly Assembly { get; }
        public string Number { get; }
        public string Name => Assembly.Name;
        public string Version => Assembly.Version?.ToString() ?? string.Empty;
        public string Owner => Assembly.Owner ?? string.Empty;
        public string Where => ProcessAssemblies.Where(Assembly);
        public bool IsDuplicate => Assembly.IsDuplicate;
        public bool IsInMemory => Assembly.Location == null;
        public string FilePath => Assembly.FilePath;
        public string ToolTip { get; }
    }

    // Every assembly in Revit's AppDomain, numbered as .NET lists them. Reading the list is quick, so it is read
    // again whenever the tab is opened and on F5.
    public sealed partial class AssembliesVM : ObservableObject
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private List<ProcessAssembly> _all = new List<ProcessAssembly>();
        private DispatcherTimer _noticeTimer;

        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private bool _hideSystem = true;
        [ObservableProperty] private string _status = string.Empty;
        [ObservableProperty] private string _notice = string.Empty;
        [ObservableProperty] private bool _isEmpty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RevealInExplorerCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyPathCommand))]
        private LoadedAssemblyVM _selectedItem;

        public ObservableCollection<LoadedAssemblyVM> Items { get; } = [];

        // Tests hand in their own list; Revit's is the AppDomain's.
        public Func<List<ProcessAssembly>> Source { get; set; } = ProcessAssemblies.Collect;

        partial void OnSearchTextChanged(string value) => Rebuild();

        partial void OnHideSystemChanged(bool value) => Rebuild();

        [RelayCommand]
        public void Refresh()
        {
            try
            {
                _all = Source();
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось получить список сборок:");
                _all = new List<ProcessAssembly>();
            }

            Rebuild();
        }

        private void Rebuild()
        {
            var selected = SelectedItem?.Assembly.Number;
            var query = SearchText?.Trim() ?? string.Empty;
            bool Matches(ProcessAssembly a) => query.Length == 0
                || new[] { a.Number.ToString(), a.Name, a.Version?.ToString(), a.FileVersion, a.Owner, ProcessAssemblies.Where(a) }
                    .Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase));

            var byName = _all.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ProcessAssembly>)g.ToList(), StringComparer.OrdinalIgnoreCase);

            Items.Clear();
            var shown = _all.Where(a => !HideSystem || !a.IsSystem).Where(Matches).ToList();
            var digits = shown.Count == 0 ? 1 : shown.Max(a => a.Number).ToString().Length;
            foreach (var assembly in shown)
                Items.Add(new LoadedAssemblyVM(assembly, byName[assembly.Name], digits));

            SelectedItem = Items.FirstOrDefault(i => i.Assembly.Number == selected);
            IsEmpty = Items.Count == 0;
            Status = "Загружено: " + _all.Count + " · показано: " + Items.Count;
        }

        private void Notify(string text)
        {
            Notice = text;
            if (_noticeTimer == null)
            {
                _noticeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(4) };
                _noticeTimer.Tick += (s, e) =>
                {
                    _noticeTimer.Stop();
                    Notice = string.Empty;
                };
            }

            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        // The list as shown goes to a text file, shown in Explorer.
        [RelayCommand]
        private void SaveList()
        {
            try
            {
                var path = ReportFiles.Save("Assemblies", ProcessAssemblies.ToMarkdown(Items.Select(i => i.Assembly), _all.Count));
                Notify("Список сохранён: " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось сохранить список:");
            }
        }

        // A row's own menu passes its item; the toolbar button works on the selected one.
        private bool HasFile(LoadedAssemblyVM item) => (item ?? SelectedItem)?.FilePath != null;

        [RelayCommand(CanExecute = nameof(HasFile))]
        private void CopyPath(LoadedAssemblyVM item)
        {
            var path = (item ?? SelectedItem)?.FilePath;
            if (path == null) return;
            try
            {
                Clipboard.SetText(path);
                Notify("Путь скопирован");
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось скопировать путь:");
            }
        }

        [RelayCommand(CanExecute = nameof(HasFile))]
        private void RevealInExplorer(LoadedAssemblyVM item)
        {
            var path = (item ?? SelectedItem)?.FilePath;
            if (path == null || !File.Exists(path)) return;
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }
    }
}
