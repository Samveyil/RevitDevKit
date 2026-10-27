using BIMPlugins.ExtStorage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitScriptRunner.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;

namespace RevitScriptRunner.UI
{
    public partial class AssemblyRunnerVM : ObservableObject
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        [ObservableProperty] private bool _isClearOnRun = true;

        [ObservableProperty] private string _txtStatus = "Готово";
        [ObservableProperty] private string _contextText;

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

        /// <summary>Окно закрывается, как только команда передана в Revit.</summary>
        public event EventHandler CloseRequested;

        public AssemblyRunnerVM()
        {
            Restore();
        }

        private AssemblyNodeVM SelectedAssembly => SelectedNode as AssemblyNodeVM ?? (SelectedNode as TargetNodeVM)?.Owner;

        public void RefreshContext()
        {
            try
            {
                var doc = RevitAPI.Document;
                ContextText = doc == null ? "Документ не открыт" : "Документ: " + doc.Title;
            }
            catch (Exception ex)
            {
                ContextText = "Контекст недоступен";
                ConsoleOutput.Debug("Не удалось прочитать контекст Revit: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Список DLL
        // ------------------------------------------------------------------

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

        private bool HasAssembly() => SelectedAssembly != null;

        /// <summary>Перечитывает список команд из DLL на диске.</summary>
        [RelayCommand(CanExecute = nameof(HasAssembly))]
        private void ReloadAssembly()
        {
            var node = SelectedAssembly;
            if (node != null) Refresh(node.FilePath);
        }

        [RelayCommand(CanExecute = nameof(HasAssembly))]
        private void RemoveAssembly()
        {
            var node = SelectedAssembly;
            if (node == null) return;

            CachedAssemblyLoader.Forget(node.FilePath);
            Assemblies.Remove(node);
            SelectedNode = null;
            Save();

            TxtStatus = "Убрано: " + node.DisplayName;
        }

        [RelayCommand(CanExecute = nameof(HasAssembly))]
        private void RevealInExplorer()
        {
            var node = SelectedAssembly;
            if (node == null || !File.Exists(node.FilePath)) return;
            Process.Start("explorer.exe", "/select,\"" + node.FilePath + "\"");
        }

        /// <summary>Загружает DLL и ставит в список её актуальные команды (на место прежних, если она уже есть).</summary>
        private void Refresh(string path)
        {
            try
            {
                var loaded = CachedAssemblyLoader.Load(path);
                var commands = EntryPointResolver.FindAll(loaded.Assembly, includeMethods: false).Select(t => t.TypeName);
                var node = new AssemblyNodeVM(loaded.OriginalPath, commands, n => SelectedNode = n);

                var previous = FindAssembly(loaded.OriginalPath);
                if (previous == null)
                    Assemblies.Add(node);
                else
                    Assemblies[Assemblies.IndexOf(previous)] = node;

                node.IsSelected = true;
                Save();

                TxtStatus = loaded.DisplayName + ": команд — " + node.Targets.Count;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось загрузить " + path + ":");
                TxtStatus = "Ошибка загрузки";
            }
        }

        private AssemblyNodeVM FindAssembly(string path) =>
            Assemblies.FirstOrDefault(a => string.Equals(a.FilePath, path, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------
        //  Запуск
        // ------------------------------------------------------------------

        private bool CanRun() => SelectedNode is TargetNodeVM && !IsRunning;

        /// <summary>
        /// Берёт команду из актуальной DLL на диске: после пересборки запускается новая
        /// версия класса с тем же именем, обновлять список для этого не нужно.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanRun))]
        private void Run()
        {
            if (!(SelectedNode is TargetNodeVM node)) return;

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
                TxtStatus = "Ошибка загрузки";
                return;
            }

            if (target == null)
            {
                ConsoleOutput.Error("В актуальной " + node.Owner.DisplayName + " нет команды " + node.TypeName + ". Обновите список команд.");
                TxtStatus = "Команда не найдена";
                return;
            }

            if (IsClearOnRun) ConsoleOutput.Clear();

            IsRunning = true;
            TxtStatus = "Выполнение " + node.DisplayName + "…";

            var request = new RunRequest
            {
                Title = target.DisplayName,
                TransactionName = "Script Runner: " + target.Type.Name,
                Body = () =>
                {
                    // Окно закрыто, а вывод команды идёт в консоль — показываем панель
                    // здесь, в контексте API.
                    ConsolePane.TryShow(RevitAPI.UIApplication);
                    return target.Invoke();
                },
                Completed = error => _dispatcher.Invoke(() =>
                {
                    Finish(error == null ? "Готово" : "Завершено с ошибкой");
                    RefreshContext();
                })
            };

            try
            {
                RevitTaskRunner.Run(request, needsCommandContext: true);
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось передать запуск в Revit:");
                Finish("Ошибка запуска");
            }
        }

        private void Finish(string status)
        {
            IsRunning = false;
            TxtStatus = status;
        }

        // ------------------------------------------------------------------
        //  Сохранение списка между сеансами
        // ------------------------------------------------------------------

        // Формат: путь к DLL с начала строки, её команды — ниже, с отступом.
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
