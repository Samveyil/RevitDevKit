using BIMPlugins.ExtStorage.Comparers;
using BIMPlugins.ExtStorage.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitDevKit.UI.DllRunner
{
    // TreeView.SelectedItem is read-only, so selection is tracked via IsSelected on each node.
    public abstract partial class AssemblyTreeNodeVM : ObservableObject
    {
        private readonly Action<AssemblyTreeNodeVM> _onSelected;

        [ObservableProperty] private bool _isSelected;
        [ObservableProperty] private bool _isExpanded = true;
        [ObservableProperty] private bool _isVisible = true;

        public virtual bool IsSelectable => false;

        protected AssemblyTreeNodeVM(Action<AssemblyTreeNodeVM> onSelected)
        {
            _onSelected = onSelected;
        }

        partial void OnIsSelectedChanged(bool value)
        {
            if (value && IsSelectable) _onSelected?.Invoke(this);
        }

        protected static bool Matches(string text, string query) =>
            text.Contains(query, StringComparison.OrdinalIgnoreCase);

        // A matching parent shows all its children; while searching, nodes expand only along
        // the paths that lead to a name match.
        protected bool ApplyFilter(string ownName, IEnumerable<AssemblyTreeNodeVM> children, string query, bool parentMatched)
        {
            var ownMatch = query.Length > 0 && Matches(ownName, query);
            var shown = parentMatched || ownMatch;
            var matchInside = false;
            var anyChildVisible = false;

            foreach (var child in children)
            {
                matchInside |= child.Filter(query, shown);
                anyChildVisible |= child.IsVisible;
            }

            IsVisible = shown || anyChildVisible;
            if (query.Length > 0)
                IsExpanded = matchInside;

            return ownMatch || matchInside;
        }

        public abstract bool Filter(string query, bool parentMatched = false);
    }

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
                .Select(g => new NamespaceNodeVM(this, g.Key, g.OrderBy(t => t.DisplayName, NaturalComparer.OrdinalIgnoreCase), onSelected));

            var loose = Targets
                .Where(t => t.Namespace.Length == 0)
                .OrderBy(t => t.DisplayName, NaturalComparer.OrdinalIgnoreCase);

            Children = folders.Cast<AssemblyTreeNodeVM>().Concat(loose).ToList();
        }

        public override bool Filter(string query, bool parentMatched = false) =>
            ApplyFilter(DisplayName, Children, query ?? string.Empty, parentMatched || query.IsNullOrEmpty());
    }

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
            IsExpanded = false;
        }

        public override bool Filter(string query, bool parentMatched = false) =>
            ApplyFilter(Name, Targets, query, parentMatched);
    }

    public sealed class TargetNodeVM : AssemblyTreeNodeVM
    {
        public AssemblyNodeVM Owner { get; }
        public string TypeName { get; }

        public override bool IsSelectable => true;

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
