using System;
using System.Collections.Generic;
using System.IO;

namespace RevitScriptRunner.Core
{
    /// <summary>
    /// Отдаёт актуальную версию внешней DLL: если файл на диске изменился с прошлой
    /// загрузки, грузит его заново теневой копией, иначе возвращает уже загруженный.
    ///
    /// Так пересобранная команда подхватывается при следующем запуске, а повторные
    /// запуски без пересборки не плодят копии сборки в памяти (на .NET Framework их
    /// не выгрузить до перезапуска Revit).
    /// </summary>
    public static class CachedAssemblyLoader
    {
        private static readonly Dictionary<string, Entry> Cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Sync = new object();

        public static LoadedAssembly Load(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists)
                throw new FileNotFoundException("DLL не найдена", file.FullName);

            lock (Sync)
            {
                if (Cache.TryGetValue(file.FullName, out var cached) &&
                    cached.WriteTime == file.LastWriteTimeUtc && cached.Length == file.Length)
                    return cached.Loaded;

                var loaded = ScriptAssemblyLoader.LoadFromFile(file.FullName);

                if (cached != null)
                {
                    ConsoleOutput.Debug(ScriptAssemblyLoader.TryUnload(cached.Loaded)
                        ? "Предыдущая версия " + loaded.DisplayName + " выгружена."
                        : "Загружена новая версия " + loaded.DisplayName + "; прежняя останется в памяти до перезапуска Revit.");
                }

                Cache[file.FullName] = new Entry(loaded, file.LastWriteTimeUtc, file.Length);
                return loaded;
            }
        }

        /// <summary>Забывает DLL и выгружает её, где это возможно.</summary>
        public static void Forget(string path)
        {
            var key = Path.GetFullPath(path);

            lock (Sync)
            {
                if (!Cache.TryGetValue(key, out var cached)) return;

                Cache.Remove(key);
                ScriptAssemblyLoader.TryUnload(cached.Loaded);
            }
        }

        private sealed class Entry
        {
            public Entry(LoadedAssembly loaded, DateTime writeTime, long length)
            {
                Loaded = loaded;
                WriteTime = writeTime;
                Length = length;
            }

            public LoadedAssembly Loaded { get; }
            public DateTime WriteTime { get; }
            public long Length { get; }
        }
    }
}
