using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RevitScriptRunner.UI
{
    /// <summary>
    /// Узел дерева DLL. TreeView не даёт привязать SelectedItem,
    /// поэтому выбор приходит через IsSelected каждого узла.
    /// </summary>
    public abstract partial class AssemblyTreeNodeVM : ObservableObject
    {
        private readonly Action<AssemblyTreeNodeVM> _onSelected;

        [ObservableProperty] private bool _isSelected;
        [ObservableProperty] private bool _isExpanded = true;
        [ObservableProperty] private bool _isVisible = true;

        protected AssemblyTreeNodeVM(Action<AssemblyTreeNodeVM> onSelected)
        {
            _onSelected = onSelected;
        }

        partial void OnIsSelectedChanged(bool value)
        {
            if (value) _onSelected?.Invoke(this);
        }

        protected static bool Matches(string text, string query) =>
            text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Показывает узел, если он сам подходит под запрос или под него подошёл кто-то
        /// из детей. Родителя, совпавшего с запросом, показываем целиком; папку, которая
        /// видна только ради детей, раскрываем.
        /// </summary>
        protected bool ApplyFilter(string ownName, IEnumerable<AssemblyTreeNodeVM> children, string query, bool parentMatched)
        {
            var self = parentMatched || Matches(ownName, query);
            var anyChild = false;

            foreach (var child in children)
                anyChild |= child.Filter(query, self);

            IsVisible = self || anyChild;
            if (anyChild && !self) IsExpanded = true;

            return IsVisible;
        }

        /// <summary>Применяет поиск к узлу и его детям; пустой запрос показывает всё.</summary>
        public abstract bool Filter(string query, bool parentMatched = false);
    }

    /// <summary>
    /// DLL и имена её команд: сначала папки пространств имён, затем команды без
    /// пространства имён. Сама сборка здесь не хранится: при запуске команда
    /// берётся из актуального файла на диске.
    /// </summary>
    public sealed class AssemblyNodeVM : AssemblyTreeNodeVM
    {
        public string FilePath { get; }
        public IReadOnlyList<TargetNodeVM> Targets { get; }
        public IReadOnlyList<AssemblyTreeNodeVM> Children { get; }

        public string DisplayName => Path.GetFileName(FilePath);
        public bool HasTargets => Targets.Count > 0;

        public AssemblyNodeVM(string filePath, IEnumerable<string> commandTypeNames, Action<AssemblyTreeNodeVM> onSelected)
            : base(onSelected)
        {
            FilePath = filePath;
            Targets = commandTypeNames.Select(name => new TargetNodeVM(this, name, onSelected)).ToList();

            var folders = Targets
                .Where(t => t.Namespace.Length > 0)
                .GroupBy(t => t.Namespace)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new NamespaceNodeVM(this, g.Key, g.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase), onSelected));

            var loose = Targets
                .Where(t => t.Namespace.Length == 0)
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase);

            Children = folders.Cast<AssemblyTreeNodeVM>().Concat(loose).ToList();
        }

        public override bool Filter(string query, bool parentMatched = false) =>
            ApplyFilter(DisplayName, Children, query, parentMatched || string.IsNullOrEmpty(query));
    }

    /// <summary>Пространство имён внутри DLL и его команды.</summary>
    public sealed class NamespaceNodeVM : AssemblyTreeNodeVM
    {
        public AssemblyNodeVM Owner { get; }
        public string Name { get; }
        public IReadOnlyList<TargetNodeVM> Targets { get; }

        public NamespaceNodeVM(AssemblyNodeVM owner, string name, IEnumerable<TargetNodeVM> targets, Action<AssemblyTreeNodeVM> onSelected)
            : base(onSelected)
        {
            Owner = owner;
            Name = name;
            Targets = targets.ToList();
        }

        public override bool Filter(string query, bool parentMatched = false) =>
            ApplyFilter(Name, Targets, query, parentMatched);
    }

    /// <summary>Команда — полное имя класса с IExternalCommand.</summary>
    public sealed class TargetNodeVM : AssemblyTreeNodeVM
    {
        public AssemblyNodeVM Owner { get; }
        public string TypeName { get; }

        public string DisplayName => TypeName.Substring(TypeName.LastIndexOf('.') + 1);
        public string Namespace => TypeName.LastIndexOf('.') < 0 ? string.Empty : TypeName.Substring(0, TypeName.LastIndexOf('.'));

        public TargetNodeVM(AssemblyNodeVM owner, string typeName, Action<AssemblyTreeNodeVM> onSelected)
            : base(onSelected)
        {
            Owner = owner;
            TypeName = typeName;
        }

        public override bool Filter(string query, bool parentMatched = false) =>
            ApplyFilter(DisplayName, Array.Empty<AssemblyTreeNodeVM>(), query, parentMatched);
    }
}
