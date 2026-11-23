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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RevitDevKit.Localization;

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
                    ? ExplorerStrings.NotLoadedYet
                    : string.Format(ExplorerStrings.LoadedFrom, string.Join(ExplorerStrings.LoadedAnd, Group.Loaded.Select(l => string.Format(ExplorerStrings.VersionFrom, l.FileVersion, l.Owner ?? Path.GetDirectoryName(l.Path)))));
                return AddinDependencies.Reason(Group) + " · " + loaded;
            }
        }

        // What the short reason means for the add-ins, in full.
        public string ToolTip
        {
            get
            {
                var signed = Group.Copies.All(c => c.Dll.IsSigned);
                var versionsDiffer = Group.Copies.All(c => c.Dll.IsManaged) && Group.Copies.Select(c => c.Dll.AssemblyVersion).Distinct().Count() > 1;
                if (!signed && versionsDiffer)
                    return ExplorerStrings.UnsignedTip;

                switch (Group.Level)
                {
                    case DependencyLevel.Assembly:
                        return ExplorerStrings.RedirectOlderTip;
                    case DependencyLevel.FileOnly:
                        return ExplorerStrings.FileOnlyTip;
                    case DependencyLevel.SideBySide:
                        return ExplorerStrings.SideBySideTip;
                    default:
                        return ExplorerStrings.SameVersionTip;
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

        public string ToolTip => Copy.Dll.Path + string.Format(ExplorerStrings.FileVersionLine, Copy.Dll.FileVersion)
                                 + (!Copy.Dll.IsManaged ? ExplorerStrings.CopyNative
                                     : string.Format(ExplorerStrings.CopyAssemblyVersion, Copy.Dll.AssemblyVersion)
                                       + (Copy.Dll.IsSigned ? string.Format(ExplorerStrings.CopySigned, Copy.Dll.PublicKeyToken) : ExplorerStrings.CopyUnsigned)
                                       + (Copy.EffectiveVersion != null && Copy.EffectiveVersion != Copy.Dll.AssemblyVersion
                                           ? string.Format(ExplorerStrings.CopyGetsVersion, Copy.EffectiveVersion) : string.Empty));

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
        [NotifyCanExecuteChangedFor(nameof(SaveReportCommand))]
        private bool _isBusy;

        // The status bar: the check on the left, a short note of the last action on the right, gone after a while.
        [ObservableProperty] private string _status = ExplorerStrings.PressCheck;
        [ObservableProperty] private string _notice = string.Empty;
        private DispatcherTimer _noticeTimer;
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
            Status = ExplorerStrings.Checking;

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
            SaveReportCommand.NotifyCanExecuteChanged();
            IsBusy = false;

            var conflicts = report.Groups.Count(g => g.IsConflict);
            Status = string.Format(ExplorerStrings.CheckedAt, report.CheckedAt.ToString("HH:mm:ss"), conflicts == 0 ? ExplorerStrings.NoConflictsShort : string.Format(ExplorerStrings.ConflictsCount, conflicts));
            Rebuild();
        }

        private void Failed(Exception ex)
        {
            ConsoleOutput.Exception(ex, ExplorerStrings.CheckFailed);
            Status = ExplorerStrings.CheckFailedStatus;
            IsBusy = false;
        }

        private static List<Tuple<string, string>> LoadedAssemblies()
        {
            var result = new List<Tuple<string, string>>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || assembly.Location.IsNullOrEmpty()) continue;
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
            bool Matches(params string[] values) => query.Length == 0 || values.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase));

            // Conflicts always; with the box cleared, also what loads side by side, what is the same everywhere,
            // and what only Revit and Autodesk add-ins share, which the user cannot change anyway.
            AddGroups(ExplorerStrings.SectionConflicts, "SourceBranch", g => g.IsConflict, Matches);
            if (!OnlyProblems)
            {
                AddGroups(ExplorerStrings.SectionSideBySide, "LayersOutline", g => !g.OnlyAutodesk && g.Level == DependencyLevel.SideBySide, Matches);
                AddGroups(ExplorerStrings.SectionSame, "CheckAll", g => !g.OnlyAutodesk && g.Level == DependencyLevel.None, Matches);
                AddGroups(ExplorerStrings.SectionAutodesk, "LockOutline", g => g.OnlyAutodesk, Matches);
            }

            IsEmpty = Sections.Count == 0;
        }

        private void AddGroups(string title, string icon, Func<VersionGroup, bool> which, Func<string[], bool> matches)
        {
            var groups = _report.Groups.Where(which).Where(g => matches(new[] { g.FileName }.Concat(g.Copies.Select(c => c.Owner)).ToArray())).ToList();
            if (groups.Count == 0) return;

            var section = new DependencySectionVM(title + " · " + groups.Count, icon);
            foreach (var group in groups)
            {
                var expanded = _expanded.TryGetValue(group.FileName, out var was) ? was : group.IsConflict && group.Level == DependencyLevel.Assembly;
                section.Children.Add(new VersionGroupVM(group, expanded, Select, (name, value) => _expanded[name] = value));
            }

            Sections.Add(section);
        }

        private void Select(DependencyNodeVM node) => SelectedNode = node;

        [RelayCommand]
        private void CollapseAll()
        {
            foreach (var group in Sections.SelectMany(s => s.Children).OfType<VersionGroupVM>())
                group.IsExpanded = false;
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

        private bool CanSave() => _report != null && !IsBusy;

        // The report goes to a text file, shown in Explorer.
        [RelayCommand(CanExecute = nameof(CanSave))]
        private void SaveReport()
        {
            try
            {
                var path = ReportFiles.Save("Dependencies", AddinDependencies.ToMarkdown(_report, OnlyProblems));
                Notify(string.Format(ExplorerStrings.ReportSaved, Path.GetFileName(path)));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, ExplorerStrings.ReportSaveFailed);
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
                Notify(ExplorerStrings.PathCopied);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, ExplorerStrings.PathCopyFailed);
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
