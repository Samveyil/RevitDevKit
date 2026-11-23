using RevitDevKit.Core.Output;
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RevitDevKit.Core
{
    // DevKit's settings kept between Revit sessions in %AppData%\RevitDevKit\settings.json: the console's options, and
    // Автотранзакция and the detailed AI instructions of Script Editor. Read once at start-up, written on every change.
    // A setting missing from the file, or a file that cannot be read, keeps its default.
    public static class DevKitSettings
    {
        private static bool _loading;
        private static bool _autoTransaction;
        private static bool _aiDetailed;

        private static string FilePath => Path.Combine(App.DataDirectory, "settings.json");

        // Script Editor's Автотранзакция.
        public static bool AutoTransaction
        {
            get => _autoTransaction;
            set
            {
                if (_autoTransaction == value) return;
                _autoTransaction = value;
                Save();
            }
        }

        // The detailed AI instructions: all of BIMPlugins.ExtStorage with signatures.
        public static bool AiDetailed
        {
            get => _aiDetailed;
            set
            {
                if (_aiDetailed == value) return;
                _aiDetailed = value;
                Save();
            }
        }

        public static void Load()
        {
            ConsoleOptions.Changed -= OnConsoleOptionsChanged;
            ConsoleOptions.Changed += OnConsoleOptionsChanged;

            if (!File.Exists(FilePath)) return;

            _loading = true;
            try
            {
                SettingsFile file;
                using (var stream = File.OpenRead(FilePath))
                    file = (SettingsFile)new DataContractJsonSerializer(typeof(SettingsFile)).ReadObject(stream);

                if (file.ClearOnRun is bool clearOnRun) ConsoleOptions.ClearOnRun = clearOnRun;
                if (file.ClearShownOnRun is bool clearShownOnRun) ConsoleOptions.ClearShownOnRun = clearShownOnRun;
                if (file.HideWarnings is bool hideWarnings) ConsoleOptions.HideWarnings = hideWarnings;
                if (file.AutoScroll is bool autoScroll) ConsoleOptions.AutoScroll = autoScroll;
                if (file.Timestamps is bool timestamps) ConsoleOptions.Timestamps = timestamps;
                if (file.ShowDebug is bool showDebug) ConsoleOptions.ShowDebug = showDebug;
                if (Enum.TryParse(file.OpenPane, out ConsolePaneOpening openPane)) ConsoleOptions.OpenPane = openPane;
                if (file.AutoTransaction is bool autoTransaction) _autoTransaction = autoTransaction;
                if (file.AiDetailed is bool aiDetailed) _aiDetailed = aiDetailed;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Warning("Не удалось прочитать настройки DevKit, взяты настройки по умолчанию: " + ex.Message);
            }
            finally
            {
                _loading = false;
            }
        }

        private static void OnConsoleOptionsChanged(string name) => Save();

        private static void Save()
        {
            if (_loading) return;

            var file = new SettingsFile
            {
                ClearOnRun = ConsoleOptions.ClearOnRun,
                ClearShownOnRun = ConsoleOptions.ClearShownOnRun,
                HideWarnings = ConsoleOptions.HideWarnings,
                AutoScroll = ConsoleOptions.AutoScroll,
                Timestamps = ConsoleOptions.Timestamps,
                ShowDebug = ConsoleOptions.ShowDebug,
                OpenPane = ConsoleOptions.OpenPane.ToString(),
                AutoTransaction = _autoTransaction,
                AiDetailed = _aiDetailed
            };

            try
            {
                // Written next to the file and then moved over it, so Revit closing mid-write never leaves half a file.
                var temp = FilePath + ".tmp";
                using (var stream = File.Create(temp))
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true, "  "))
                    new DataContractJsonSerializer(typeof(SettingsFile)).WriteObject(writer, file);

                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось сохранить настройки DevKit: " + ex.Message);
            }
        }

        [DataContract]
        private sealed class SettingsFile
        {
            [DataMember(Name = "clearOnRun", Order = 0, EmitDefaultValue = false)] public bool? ClearOnRun { get; set; }
            [DataMember(Name = "clearShownOnRun", Order = 1, EmitDefaultValue = false)] public bool? ClearShownOnRun { get; set; }
            [DataMember(Name = "hideWarnings", Order = 2, EmitDefaultValue = false)] public bool? HideWarnings { get; set; }
            [DataMember(Name = "autoScroll", Order = 3, EmitDefaultValue = false)] public bool? AutoScroll { get; set; }
            [DataMember(Name = "timestamps", Order = 4, EmitDefaultValue = false)] public bool? Timestamps { get; set; }
            [DataMember(Name = "showDebug", Order = 5, EmitDefaultValue = false)] public bool? ShowDebug { get; set; }
            [DataMember(Name = "openPane", Order = 6, EmitDefaultValue = false)] public string OpenPane { get; set; }
            [DataMember(Name = "autoTransaction", Order = 7, EmitDefaultValue = false)] public bool? AutoTransaction { get; set; }
            [DataMember(Name = "aiDetailed", Order = 8, EmitDefaultValue = false)] public bool? AiDetailed { get; set; }
        }
    }
}
