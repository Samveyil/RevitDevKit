using BIMPlugins.ExtStorage.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit;
using RevitScriptRunner.Core;
using RevitScriptRunner.Scripting;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace RevitScriptRunner.UI
{
    public partial class RunnerVM : ObservableObject
    {
        [ObservableProperty] private bool _isAutoTransaction = false;
        [ObservableProperty] private bool _isClearOnRun = true;

        [ObservableProperty] private ObservableCollection<TabItem> _tabItems = [];
        [ObservableProperty] private TabItem _selectedTab;

        [ObservableProperty] private string _txtStatus = "Готово";

        [ObservableProperty] private bool _isAutoScroll = true;
        [ObservableProperty] private bool _isTimestamps = false;
        [ObservableProperty] private bool _isShowDebug = false;

        private readonly List<ScriptTab> _tabs = new List<ScriptTab>();

        private DispatcherTimer _flushTimer;
        private Paragraph _consoleParagraph;
        private bool _atLineStart = true;
        private int _untitledCounter;
        private bool _running;

        public List<string> CodeSamples { get; } = Samples.All.Select(s => s.Title).ToList();

        private ScriptTab Current { get => SelectedTab is TabItem item ? _tabs.FirstOrDefault(t => t.Item == item) : null; }

        public RunnerVM()
        {
            NewTab(Samples.Starter, "Новый скрипт");
        }

        [RelayCommand]
        private void NewTab() => NewTab(Samples.Starter, "Скрипт " + (_untitledCounter + 1));

        [RelayCommand]
        private void OpenFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "C# файлы (*.cs;*.csx)|*.cs;*.csx|Все файлы (*.*)|*.*",
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var text = File.ReadAllText(dialog.FileName);
                var tab = NewTab(text, Path.GetFileName(dialog.FileName), dialog.FileName);
                tab.Dirty = false;
                tab.Item.Header = tab.Header;

                TxtStatus = ("Открыт " + dialog.FileName);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось открыть файл:");
            }
        }

        [RelayCommand]
        private void SaveFile()
        {
            var tab = Current;
            if (tab == null)
                return;

            var path = tab.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "C# файлы (*.cs)|*.cs|Все файлы (*.*)|*.*",
                    FileName = tab.Title.TrimEnd('*', ' ') + ".cs"
                };
                
                if (dialog.ShowDialog() != true)
                    return;

                path = dialog.FileName;
            }

            try
            {
                File.WriteAllText(path, tab.Editor.Text);
                tab.FilePath = path;
                tab.Title = Path.GetFileName(path);
                tab.Dirty = false;
                tab.Item.Header = tab.Header;

                TxtStatus = ("Сохранено: " + path);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось сохранить файл:");
            }
        }

        [RelayCommand]
        private void SampleSelected(string value)
        {
            var sample = Samples.All.FirstOrDefault(s => s.Title == value);

            var tab = _tabs.FirstOrDefault(t => t.Header == value);
            if (tab == null)
                NewTab(sample.Code, value);
            else
                SelectedTab = tab.Item;
        }

        private ScriptTab NewTab(string code, string title, string filePath = null)
        {
            _untitledCounter++;

            var editor = EditorFactory.Create();
            editor.Text = code ?? string.Empty;

            var item = new TabItem { Content = editor };
            var tab = new ScriptTab { Item = item, Editor = editor, FilePath = filePath, Title = title };

            item.Header = tab.Header;
            item.ContextMenu = BuildTabMenu(tab);

            editor.TextChanged += (s, e) =>
            {
                if (tab.Dirty) return;
                tab.Dirty = true;
                item.Header = tab.Header;
            };

            _tabs.Add(tab);
            TabItems.Add(item);
            SelectedTab = item;
            editor.Focus();

            return tab;
        }

        private ContextMenu BuildTabMenu(ScriptTab tab)
        {
            var menu = new ContextMenu();

            var close = new MenuItem { Header = "Закрыть вкладку" };
            close.Click += (s, e) => CloseTab(tab);
            menu.Items.Add(close);

            var reveal = new MenuItem { Header = "Показать в проводнике" };
            reveal.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(tab.FilePath) || !File.Exists(tab.FilePath)) return;
                Process.Start("explorer.exe", "/select,\"" + tab.FilePath + "\"");
            };
            menu.Items.Add(reveal);

            return menu;
        }

        private void CloseTab(ScriptTab tab)
        {
            if (tab == null) return;

            if (tab.Dirty)
            {
                var answer = MessageBox.Show(
                    "Во вкладке «" + tab.Title + "» есть несохранённые изменения. Закрыть?",
                    "Script Runner", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }

            _tabs.Remove(tab);
            TabItems.Remove(tab.Item);

            if (_tabs.Count == 0)
                NewTab(Samples.Starter, "Новый скрипт");
        }

        private sealed class ScriptTab
        {
            public TabItem Item;
            public TextEditor Editor;
            public string FilePath;
            public string Title;
            public bool Dirty;

            public string Header
            {
                get { return Dirty ? Title + " *" : Title; }
            }
        }

        public event EventHandler CloseRequest;
        public void RaiseCloseRequest()
        {
            CloseRequest?.Invoke(this, EventArgs.Empty);
        }
        public event EventHandler HideRequest;
        public void RaiseHideRequest()
        {
            HideRequest?.Invoke(this, EventArgs.Empty);
        }
        public event EventHandler ShowRequest;
        public void RaiseShowRequest()
        {
            ShowRequest?.Invoke(this, EventArgs.Empty);
        }
    }
}