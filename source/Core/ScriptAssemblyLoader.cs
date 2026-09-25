using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace RevitScriptRunner.Core
{
    /// <summary>
    /// Загрузка скомпилированных и внешних сборок в процесс Revit.
    ///
    /// .NET 8 (Revit 2025+): каждая сборка получает собственный collectible
    /// AssemblyLoadContext, поэтому её можно выгрузить и пересобрать DLL на диске
    /// без перезапуска Revit.
    ///
    /// .NET Framework (Revit ≤ 2024): выгрузка невозможна в принципе, поэтому
    /// грузим байтами / из теневой копии — файл на диске остаётся свободным,
    /// но старые версии остаются в памяти до перезапуска Revit.
    /// </summary>
    public static class ScriptAssemblyLoader
    {
        private static readonly List<IDisposable> Contexts = new List<IDisposable>();
        private static readonly object Sync = new object();

        /// <summary>Загружает сборку из байтов (результат компиляции скрипта).</summary>
        public static Assembly Load(string name, byte[] peBytes, byte[] pdbBytes)
        {
#if NETFRAMEWORK
            return pdbBytes != null && pdbBytes.Length > 0
                ? Assembly.Load(peBytes, pdbBytes)
                : Assembly.Load(peBytes);
#else
            var context = new UnloadableContext(name);
            lock (Sync) Contexts.Add(context);

            using (var pe = new MemoryStream(peBytes))
            using (var pdb = pdbBytes != null && pdbBytes.Length > 0 ? new MemoryStream(pdbBytes) : null)
            {
                return context.LoadFromStream(pe, pdb);
            }
#endif
        }

        /// <summary>
        /// Загружает внешнюю DLL. Исходный файл не блокируется: делается теневая
        /// копия во временной папке, зависимости ищутся рядом с оригиналом.
        /// </summary>
        public static LoadedAssembly LoadFromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("DLL не найдена", path);

            var original = Path.GetFullPath(path);
            var sourceDir = Path.GetDirectoryName(original);
            var shadowDir = Path.Combine(
                Path.GetTempPath(),
                "RevitScriptRunner",
                Path.GetFileNameWithoutExtension(original) + "_" + DateTime.Now.ToString("HHmmss_fff")
            );

            Directory.CreateDirectory(shadowDir);

            var shadowPath = Path.Combine(shadowDir, Path.GetFileName(original));
            CopyWithSiblings(sourceDir, shadowDir);

            Assembly assembly;
            IDisposable owner = null;

#if NETFRAMEWORK
            var bytes = File.ReadAllBytes(shadowPath);
            var pdbPath = Path.ChangeExtension(shadowPath, ".pdb");
            assembly = File.Exists(pdbPath)
                ? Assembly.Load(bytes, File.ReadAllBytes(pdbPath))
                : Assembly.Load(bytes);
#else
            var context = new UnloadableContext(Path.GetFileNameWithoutExtension(original), shadowDir);
            lock (Sync) Contexts.Add(context);
            owner = context;
            assembly = context.LoadFromAssemblyPath(shadowPath);
#endif

            return new LoadedAssembly
            {
                Assembly = assembly,
                OriginalPath = original,
                ShadowPath = shadowPath,
                LoadedAt = DateTime.Now,
                Owner = owner
            };
        }

        /// <summary>Пытается выгрузить сборку (работает только на .NET 8+).</summary>
        public static bool TryUnload(LoadedAssembly loaded)
        {
#if NETFRAMEWORK
            return false;
#else
            var context = loaded == null ? null : loaded.Owner as UnloadableContext;
            if (context == null) return false;

            lock (Sync) Contexts.Remove(context);
            context.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
#endif
        }

        public static bool SupportsUnload
        {
            get
            {
#if NETFRAMEWORK
                return false;
#else
                return true;
#endif
            }
        }

        private static void CopyWithSiblings(string sourceDir, string targetDir)
        {
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir)) return;

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".config", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
                }
                catch {}
            }
        }

#if !NETFRAMEWORK
        private sealed class UnloadableContext : AssemblyLoadContext, IDisposable
        {
            private readonly AssemblyDependencyResolver _resolver;

            public UnloadableContext(string name, string probeDir = null)
                : base("RSR_" + name, isCollectible: true)
            {
                if (probeDir != null)
                {
                    var main = Path.Combine(probeDir, name + ".dll");
                    if (File.Exists(main)) _resolver = new AssemblyDependencyResolver(main);
                }
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                // null => отдаём загрузку в Default: RevitAPI, сам плагин и платформа
                // должны оставаться одними и теми же типами.
                if (_resolver == null) return null;

                var path = _resolver.ResolveAssemblyToPath(assemblyName);
                return path == null ? null : LoadFromAssemblyPath(path);
            }

            public void Dispose()
            {
                try { Unload(); } catch { /* уже выгружен */ }
            }
        }
#endif
    }

    public sealed class LoadedAssembly
    {
        public Assembly Assembly { get; set; }
        public string OriginalPath { get; set; }
        public string ShadowPath { get; set; }
        public DateTime LoadedAt { get; set; }
        internal IDisposable Owner { get; set; }

        public string DisplayName => System.IO.Path.GetFileName(OriginalPath);
    }
}