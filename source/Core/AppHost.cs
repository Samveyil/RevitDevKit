using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitDevKit.Core
{
    public sealed class HostedApp
    {
        public string FilePath { get; internal set; }
        public string ShadowDirectory { get; internal set; }
        public DateTime LoadedAt { get; internal set; }
        public IReadOnlyList<string> ApplicationTypes { get; internal set; } = Array.Empty<string>();

        internal List<IExternalApplication> Instances { get; } = new List<IExternalApplication>();
        internal RibbonFootprint Ribbon { get; set; }

        public string DisplayName => Path.GetFileName(FilePath);
    }

    // Runs IExternalApplication from a DLL that can be rebuilt and reloaded without restarting Revit.
    // Each reload: OnShutdown of the old instances, their ribbon items are removed, a fresh shadow
    // copy is loaded and OnStartup runs again with DevKit's own UIControlledApplication.
    public static class AppHost
    {
        private static UIControlledApplication _application;

        private static readonly Dictionary<string, HostedApp> Hosted =
            new Dictionary<string, HostedApp>(StringComparer.OrdinalIgnoreCase);

        // Every shadow copy and the assemblies loaded from it, newest last.
        private static readonly List<ShadowFolder> Folders = new List<ShadowFolder>();

        // One folder per Revit process, so a second running Revit never loses its copies.
        private static string ShadowBase => Path.Combine(Path.GetTempPath(), "RevitDevKit", "Apps");
        private static string ShadowRoot => Path.Combine(ShadowBase, System.Diagnostics.Process.GetCurrentProcess().Id.ToString());

        public static void Initialize(UIControlledApplication application)
        {
            _application = application;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
            DeleteOldShadowCopies();
        }

        public static void Shutdown()
        {
            foreach (var app in Hosted.Values)
                StopInstances(app);

            Hosted.Clear();
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveDependency;
        }

        public static HostedApp Find(string path)
        {
            Hosted.TryGetValue(Path.GetFullPath(path), out var app);
            return app;
        }

        // Must run in a Revit API context: OnStartup of the add-in may call any API.
        public static HostedApp Reload(string path)
        {
            if (_application == null)
                throw new InvalidOperationException("DevKit не инициализирован — перезапустите Revit.");

            var original = Path.GetFullPath(path);
            if (!File.Exists(original))
                throw new FileNotFoundException("DLL не найдена", original);

            Unload(original);

            var folder = CopyToShadow(original);
            var assembly = folder.Load(Path.Combine(folder.Directory, Path.GetFileName(original)));

            var types = EntryPointResolver.SafeGetTypes(assembly)
                .Where(t => typeof(IExternalApplication).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();

            var app = new HostedApp
            {
                FilePath = original,
                ShadowDirectory = folder.Directory,
                LoadedAt = DateTime.Now,
                ApplicationTypes = types.Select(t => t.FullName).ToList()
            };

            if (types.Count == 0)
            {
                ConsoleOutput.Warning("В " + app.DisplayName + " нет классов IExternalApplication.");
                return app;
            }

            var before = RibbonFootprint.Snapshot();

            foreach (var type in types)
            {
                try
                {
                    var instance = (IExternalApplication)Activator.CreateInstance(type);
                    app.Instances.Add(instance);

                    var result = instance.OnStartup(_application);
                    if (result == Result.Succeeded)
                        ConsoleOutput.Success(type.FullName + ".OnStartup — " + result);
                    else
                        ConsoleOutput.Warning(type.FullName + ".OnStartup вернул " + result);
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Exception(Unwrap(ex), type.FullName + ".OnStartup упал:");
                }
            }

            // Recorded even after a failed OnStartup: whatever it managed to add is removed next time.
            app.Ribbon = RibbonFootprint.Diff(before);
            var rebound = ProxyCommands.Rebind(app.Ribbon.PushButtons(), folder, Path.GetDirectoryName(original));

            ConsoleOutput.Info("Лента: панелей " + app.Ribbon.PanelCount + ", кнопок " + rebound + ".");

            Hosted[original] = app;
            return app;
        }

        public static void Unload(string path)
        {
            var key = Path.GetFullPath(path);
            if (!Hosted.TryGetValue(key, out var app)) return;

            StopInstances(app);
            app.Ribbon?.Remove();
            Hosted.Remove(key);
        }

        private static void StopInstances(HostedApp app)
        {
            foreach (var instance in app.Instances)
            {
                try
                {
                    instance.OnShutdown(_application);
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Exception(Unwrap(ex), instance.GetType().FullName + ".OnShutdown упал:");
                }
            }

            app.Instances.Clear();
        }

        private static Exception Unwrap(Exception ex) =>
            ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;

        // The whole build folder is copied: dependencies, icons and other files next to the DLL
        // must be found next to the shadow copy, and the originals stay unlocked for the next build.
        private static ShadowFolder CopyToShadow(string original)
        {
            var target = Path.Combine(ShadowRoot,
                Path.GetFileNameWithoutExtension(original) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));

            CopyDirectory(Path.GetDirectoryName(original), target);

            var folder = new ShadowFolder(target);
            Folders.Add(folder);
            return folder;
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);

            foreach (var file in Directory.GetFiles(source))
            {
                try { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true); }
                catch (Exception ex) { ConsoleOutput.Debug("Не скопирован " + file + ": " + ex.Message); }
            }

            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        // Copies of finished Revit sessions are no longer locked by anyone.
        private static void DeleteOldShadowCopies()
        {
            try
            {
                if (!Directory.Exists(ShadowBase)) return;

                foreach (var dir in Directory.GetDirectories(ShadowBase))
                {
                    if (int.TryParse(Path.GetFileName(dir), out var pid) && IsRunning(pid)) continue;

                    try { Directory.Delete(dir, true); }
                    catch { }
                }
            }
            catch { }
        }

        private static bool IsRunning(int pid)
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(pid))
                    return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        // Assembly.LoadFile keeps every shadow copy separate, so references of a hosted DLL are
        // resolved here: first from its own folder, then from the newest copies.
        private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name).Name;

                var own = args.RequestingAssembly == null
                    ? null
                    : Folders.FirstOrDefault(f => f.Contains(args.RequestingAssembly));

                if (own != null)
                    return own.TryLoad(name);

                for (var i = Folders.Count - 1; i >= 0; i--)
                {
                    var found = Folders[i].TryLoad(name);
                    if (found != null) return found;
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось подгрузить " + args.Name + ": " + ex.Message);
            }

            return null;
        }

        internal sealed class ShadowFolder
        {
            private readonly Dictionary<string, Assembly> _loaded =
                new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

            public ShadowFolder(string directory) => Directory = directory;

            public string Directory { get; }

            public bool Contains(Assembly assembly) => _loaded.Values.Contains(assembly);

            public bool Owns(string path) =>
                !string.IsNullOrEmpty(path) &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Directory, StringComparison.OrdinalIgnoreCase);

            public Assembly Load(string path)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (_loaded.TryGetValue(name, out var loaded)) return loaded;

                loaded = Assembly.LoadFile(path);
                _loaded[name] = loaded;
                return loaded;
            }

            public Assembly TryLoad(string name)
            {
                if (_loaded.TryGetValue(name, out var loaded)) return loaded;

                var path = Path.Combine(Directory, name + ".dll");
                return File.Exists(path) ? Load(path) : null;
            }
        }
    }
}
