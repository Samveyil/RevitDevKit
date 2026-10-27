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

        protected AssemblyTreeNodeVM(Action<AssemblyTreeNodeVM> onSelected)
        {
            _onSelected = onSelected;
        }

        partial void OnIsSelectedChanged(bool value)
        {
            if (value) _onSelected?.Invoke(this);
        }
    }

    /// <summary>
    /// DLL и имена её команд. Сама сборка здесь не хранится: при запуске команда
    /// берётся из актуального файла на диске.
    /// </summary>
    public sealed class AssemblyNodeVM : AssemblyTreeNodeVM
    {
        public string FilePath { get; }
        public IReadOnlyList<TargetNodeVM> Targets { get; }

        public string DisplayName => Path.GetFileName(FilePath);
        public bool HasTargets => Targets.Count > 0;

        public AssemblyNodeVM(string filePath, IEnumerable<string> commandTypeNames, Action<AssemblyTreeNodeVM> onSelected)
            : base(onSelected)
        {
            FilePath = filePath;
            Targets = commandTypeNames.Select(name => new TargetNodeVM(this, name, onSelected)).ToList();
        }
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
    }
}
