using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitDevKit.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RevitDevKit.UI.DllRunner
{
    public enum Severity
    {
        Normal,
        Minor,
        Critical
    }

    public abstract partial class DependencyNodeVM : ObservableObject
    {
        private readonly Action<DependencyNodeVM> _onSelected;

        [ObservableProperty] private bool _isExpanded;
        [ObservableProperty] private bool _isSelected;

        protected DependencyNodeVM(Action<DependencyNodeVM> onSelected) => _onSelected = onSelected;

        public ObservableCollection<DependencyNodeVM> Children { get; } = [];

        // The file "Show in Explorer" goes to.
        public virtual string FilePath => null;

        partial void OnIsSelectedChanged(bool value)
        {
            if (value) _onSelected?.Invoke(this);
        }
    }

    // "Разные версии · 3": a gray heading over its items, always open.
    public sealed class DependencySectionVM : DependencyNodeVM
    {
        public DependencySectionVM(string title, string icon) : base(null)
        {
            Title = title;
            Icon = icon;
            IsExpanded = true;
        }

        public string Title { get; }
        public string Icon { get; }
    }

    public sealed class VersionGroupVM : DependencyNodeVM
    {
        private readonly Action<string, bool> _onExpanded;

        public VersionGroupVM(VersionGroup group, bool expanded, Action<DependencyNodeVM> onSelected, Action<string, bool> onExpanded) : base(onSelected)
        {
            Group = group;
            IsExpanded = expanded;
            _onExpanded = onExpanded;

            // The check says what each add-in gets: its own copy, another file of its version, or an older version.
            foreach (var copy in group.Copies)
            {
                var severity = copy.State == CopyState.Breaks ? Severity.Critical
                    : copy.State == CopyState.Differs ? Severity.Minor
                    : Severity.Normal;
                Children.Add(new DllCopyVM(copy, severity, onSelected));
            }
        }

        public VersionGroup Group { get; }
        public string Name => Group.FileName;

        public Severity Severity => Group.Level == DependencyLevel.Assembly ? Severity.Critical
            : Group.Level == DependencyLevel.FileOnly ? Severity.Minor
            : Severity.Normal;

        // Why it is in its section, then what is loaded now.
        public string Summary
        {
            get
            {
                var loaded = Group.Loaded.Count == 0
                    ? "ещё не загружена"
                    : "загружена " + string.Join(" и ", Group.Loaded.Select(l => l.FileVersion + " из " + (l.Owner ?? Path.GetDirectoryName(l.Path))));
                return AddinDependencies.Reason(Group) + " · " + loaded;
            }
        }

        // What the short reason means for the add-ins, in full.
        public string ToolTip
        {
            get
            {
                var signed = Group.Copies.All(c => c.Dll.IsSigned);
                switch (Group.Level)
                {
                    case DependencyLevel.Assembly when !signed:
                        return "Не подписана: Revit загружает одну копию, ту, что понадобилась первой, и её получают все надстройки.\n"
                               + "Более новая обычно подходит, если в библиотеке только добавляли (оранжевым), более старая может не подойти (красным).\n"
                               + "Какая копия будет первой, зависит от порядка загрузки надстроек: в другой раз старую версию может получить другая.";
                    case DependencyLevel.Assembly:
                        return "Revit.exe.config перенаправляет на более старую версию, чем та, под которую собрана надстройка (красным).";
                    case DependencyLevel.FileOnly:
                        return "У всех одна копия этой версии сборки, файл той, что загрузится первой. Обычно это безопасно.";
                    case DependencyLevel.SideBySide:
                        return "Подписана: каждая версия сборки загрузится отдельно, каждая надстройка получит свою.";
                    default:
                        return "У всех одна и та же версия.";
                }
            }
        }

        public override string FilePath => Group.Loaded.FirstOrDefault()?.Path ?? Group.Copies.First().Dll.Path;

        // The open or closed state of each DLL is kept across checks and filtering.
        protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName == nameof(IsExpanded)) _onExpanded?.Invoke(Name, IsExpanded);
        }
    }

    public sealed class DllCopyVM : DependencyNodeVM
    {
        public DllCopyVM(DllCopy copy, Severity severity, Action<DependencyNodeVM> onSelected) : base(onSelected)
        {
            Copy = copy;
            Severity = severity;
        }

        public DllCopy Copy { get; }
        public Severity Severity { get; }
        public string Owner => Copy.Owner;
        public string Version => Copy.Dll.FileVersion;
        public bool IsLoaded => Copy.IsLoaded;

        public string ToolTip => Copy.Dll.Path + "\nВерсия файла " + Copy.Dll.FileVersion
                                 + (!Copy.Dll.IsManaged ? ", нативная DLL"
                                     : ", версия сборки " + Copy.Dll.AssemblyVersion
                                       + (Copy.Dll.IsSigned ? ", подписана (" + Copy.Dll.PublicKeyToken + ")" : ", не подписана")
                                       + (Copy.EffectiveVersion != null && Copy.EffectiveVersion != Copy.Dll.AssemblyVersion
                                           ? "\nПолучит версию " + Copy.EffectiveVersion : string.Empty));

        public override string FilePath => Copy.Dll.Path;
    }

    // DLLs the add-ins of this Revit version, and Revit itself, bring in different versions.
    // The check reads hundreds of DLL headers, so it runs in the background, on opening the tab and on F5.
    public sealed partial class DependenciesVM : ObservableObject
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly Dictionary<string, bool> _expanded = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private DependencyReport _report;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
        private bool _isBusy;

        [ObservableProperty] private string _status = "Нажмите «Проверить»";
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private bool _onlyProblems = true;
        [ObservableProperty] private bool _isEmpty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RevealInExplorerCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyPathCommand))]
        private DependencyNodeVM _selectedNode;

        public ObservableCollection<DependencyNodeVM> Sections { get; } = [];

        public bool HasReport => _report != null;

        partial void OnSearchTextChanged(string value) => Rebuild();

        partial void OnOnlyProblemsChanged(bool value) => Rebuild();

        // Once per window: later only F5 checks again, the check is not free.
        public void EnsureChecked()
        {
            if (_report == null && !IsBusy) Refresh();
        }

        private bool CanRefresh() => !IsBusy;

        [RelayCommand(CanExecute = nameof(CanRefresh))]
        public void Refresh()
        {
            IsBusy = true;
            Status = "Проверка…";

            List<AddinManifest> manifests;
            List<Tuple<string, string>> loaded;
            try
            {
                manifests = AddinManifests.Scan();
                loaded = LoadedAssemblies();
            }
            catch (Exception ex)
            {
                Failed(ex);
                return;
            }

            Task.Run(() => AddinDependencies.Scan(manifests, loaded, LoadedModules(), CancellationToken.None))
                .ContinueWith(task => _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (task.IsFaulted) Failed(task.Exception?.GetBaseException());
                    else Show(task.Result);
                })));
        }

        private void Show(DependencyReport report)
        {
            _report = report;
            OnPropertyChanged(nameof(HasReport));
            CopyReportCommand.NotifyCanExecuteChanged();
            IsBusy = false;

            var conflicts = report.Groups.Count(IsConflict);
            Status = "Проверено в " + report.CheckedAt.ToString("HH:mm:ss") + " · " + (conflicts == 0 ? "конфликтов нет" : "конфликтов: " + conflicts);
            Rebuild();
        }

        private void Failed(Exception ex)
        {
            ConsoleOutput.Exception(ex, "Не удалось проверить зависимости надстроек:");
            Status = "Проверка не удалась, подробности в консоли";
            IsBusy = false;
        }

        private static List<Tuple<string, string>> LoadedAssemblies()
        {
            var result = new List<Tuple<string, string>>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
                    result.Add(Tuple.Create(assembly.GetName().Name, assembly.Location));
                }
                catch
                {
                    // An assembly loaded from bytes has no location: no file to compare with.
                }
            }

            return result;
        }

        private static List<string> LoadedModules()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                    return process.Modules.Cast<ProcessModule>().Select(m => m.FileName).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private void Rebuild()
        {
            Sections.Clear();
            SelectedNode = null;
            if (_report == null)
            {
                IsEmpty = false;
                return;
            }

            var query = SearchText?.Trim() ?? string.Empty;
            bool Matches(params string[] values) => query.Length == 0 || values.Any(v => v != null && v.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            // Conflicts always; with the box cleared, also what loads side by side and what is the same everywhere.
            AddGroups("Конфликты", "SourceBranch", IsConflict, Matches);
            if (!OnlyProblems)
            {
                AddGroups("Загрузятся рядом", "LayersOutline", g => g.Level == DependencyLevel.SideBySide, Matches);
                AddGroups("Одинаковые у нескольких", "CheckAll", g => g.Level == DependencyLevel.None, Matches);
            }

            IsEmpty = Sections.Count == 0;
        }

        private static bool IsConflict(VersionGroup group) =>
            group.Level == DependencyLevel.Assembly || group.Level == DependencyLevel.FileOnly;

        private void AddGroups(string title, string icon, Func<VersionGroup, bool> which, Func<string[], bool> matches)
        {
            var groups = _report.Groups.Where(which).Where(g => matches(new[] { g.FileName }.Concat(g.Copies.Select(c => c.Owner)).ToArray())).ToList();
            if (groups.Count == 0) return;

            var section = new DependencySectionVM(title + " · " + groups.Count, icon);
            foreach (var group in groups)
            {
                var expanded = _expanded.TryGetValue(group.FileName, out var was) ? was : group.Level == DependencyLevel.Assembly;
                section.Children.Add(new VersionGroupVM(group, expanded, Select, (name, value) => _expanded[name] = value));
            }

            Sections.Add(section);
        }

        private void Select(DependencyNodeVM node) => SelectedNode = node;

        private bool CanCopy() => _report != null && !IsBusy;

        [RelayCommand(CanExecute = nameof(CanCopy))]
        private void CopyReport()
        {
            try
            {
                Clipboard.SetText(AddinDependencies.ToMarkdown(_report, OnlyProblems));
                Status = "Отчёт скопирован";
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось скопировать отчёт:");
            }
        }

        // A row's own menu passes its node; the toolbar button works on the selected one.
        private bool HasFile(DependencyNodeVM node) => (node ?? SelectedNode)?.FilePath != null;

        [RelayCommand(CanExecute = nameof(HasFile))]
        private void CopyPath(DependencyNodeVM node)
        {
            var path = (node ?? SelectedNode)?.FilePath;
            if (path == null) return;
            try
            {
                Clipboard.SetText(path);
                Status = "Путь скопирован";
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось скопировать путь:");
            }
        }

        [RelayCommand(CanExecute = nameof(HasFile))]
        private void RevealInExplorer(DependencyNodeVM node)
        {
            var path = (node ?? SelectedNode)?.FilePath;
            if (path == null || !File.Exists(path)) return;
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }
    }
}
