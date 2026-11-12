using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage.Extensions;
using RevitDevKit.Core.Compiler;
using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitDevKit.Core.Ribbon
{
    public sealed class PreviewState
    {
        public string FilePath { get; internal set; }
        public DateTime AppliedAt { get; internal set; }
        public IReadOnlyList<string> ApplicationTypes { get; internal set; } = Array.Empty<string>();

        internal RibbonMerge Merge { get; set; }
    }

    // Shows the ribbon of a freshly built add-in on top of the one Revit loaded from .addin:
    // the new build runs OnStartup into a draft ribbon, and the existing buttons are updated
    // from it. Nothing is persisted - "Revert" or a Revit restart brings back the original.
    public static class RibbonPreview
    {
        private static UIControlledApplication _application;

        private static readonly Dictionary<string, PreviewState> Applied =
            new Dictionary<string, PreviewState>(StringComparer.OrdinalIgnoreCase);

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
            Applied.Clear();
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveDependency;
        }

        public static PreviewState Find(string path)
        {
            Applied.TryGetValue(Path.GetFullPath(path), out var state);
            return state;
        }

        // Must run in a Revit API context: OnStartup of the add-in may call any API.
        public static PreviewState Apply(string path)
        {
            if (_application == null)
                throw new InvalidOperationException("DevKit не инициализирован — перезапустите Revit.");

            RibbonAccess.Verify();
            PackResources.FollowComponents();

            var original = Path.GetFullPath(path);
            if (!File.Exists(original))
                throw new FileNotFoundException("DLL не найдена", original);

            // Always compared with the ribbon from .addin, not with the previous preview.
            Revert(original);

            var folder = CopyToShadow(original);
            var assembly = folder.Load(Path.Combine(folder.Directory, Path.GetFileName(original)));

            var types = EntryPointResolver.SafeGetTypes(assembly)
                .Where(t => typeof(IExternalApplication).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();

            var state = new PreviewState
            {
                FilePath = original,
                AppliedAt = DateTime.Now,
                ApplicationTypes = types.Select(t => t.FullName).ToList()
            };

            if (types.Count == 0)
            {
                ConsoleOutput.Warning("В " + Path.GetFileName(original) + " нет классов IExternalApplication.");
                return state;
            }

            RibbonDraft draft;
            try
            {
                draft = RunDraft(types, folder);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(Unwrap(ex), "OnStartup новой версии упал — лента не изменена:");
                return state;
            }

            PackResources.Refresh(draft.Items(), folder);

            // The live add-in may be built under another name (BIMPlugins.dll vs Dbg_BIMPlugins.dll),
            // so its buttons are recognised by their command class as well as by the DLL name.
            var commandFiles = draft.CommandFiles();
            var merge = new RibbonMerge(button =>
                commandFiles.Contains(RibbonDraft.FileName(button.AssemblyName) ?? string.Empty) ||
                folder.Loaded.Any(a => !button.ClassName.IsNullOrEmpty() && a.GetType(button.ClassName) != null));
            merge.Apply(draft);
            // Every button of the new build runs through a proxy, also one the add-in moved to a panel of its own:
            // Revit would load a second copy's DLL by its name and get the first copy, whose XAML is no longer found.
            ProxyCommands.Rebind(merge.Adopted.Concat(draft.Items().OfType<PushButton>()).Distinct(), folder, Path.GetDirectoryName(original));

            ConsoleOutput.Info("Лента: обновлено " + merge.Updated + ", добавлено " + merge.Added + ", убрано " + merge.Removed + ".");

            state.Merge = merge;
            Applied[original] = state;
            return state;
        }

        private static RibbonDraft RunDraft(IList<Type> types, ShadowFolder folder)
        {
            var instances = new List<IExternalApplication>();
            try
            {
                using (PackResources.Pin(folder))
                {
                    return RibbonDraft.Run(() =>
                    {
                        foreach (var type in types)
                        {
                            var instance = (IExternalApplication)Activator.CreateInstance(type);
                            instances.Add(instance);

                            var result = instance.OnStartup(_application);
                            if (result != Result.Succeeded)
                                throw new InvalidOperationException(type.FullName + ".OnStartup вернул " + result + ".");
                        }
                    });
                }
            }
            finally
            {
                // The draft instance only describes its ribbon: its events and updaters must not stay alive.
                foreach (var instance in instances)
                {
                    try { instance.OnShutdown(_application); }
                    catch (Exception ex) { ConsoleOutput.Exception(Unwrap(ex), instance.GetType().FullName + ".OnShutdown упал:"); }
                }
            }
        }

        // The copy of an assembly Revit itself loaded, not one of the shadow copies.
        internal static Assembly LiveAssembly(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().LastOrDefault(a =>
                !a.IsDynamic &&
                string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase) &&
                !Folders.Any(f => f.Contains(a)));

        public static void Revert(string path)
        {
            var key = Path.GetFullPath(path);
            if (!Applied.TryGetValue(key, out var state)) return;

            Applied.Remove(key);
            state.Merge?.Undo();
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

        // Assembly.LoadFile keeps every shadow copy separate, so references of a copied DLL are
        // resolved here: first from its own folder, then from the newest copies.
        private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name).Name;

                var own = args.RequestingAssembly == null
                    ? null
                    : Folders.LastOrDefault(f => f.Contains(args.RequestingAssembly));

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

            public IReadOnlyCollection<Assembly> Loaded => _loaded.Values;

            public bool Owns(string path) =>
                !path.IsNullOrEmpty() &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Directory, StringComparison.OrdinalIgnoreCase);

            public Assembly Load(string path)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (_loaded.TryGetValue(name, out var loaded)) return loaded;

                loaded = Assembly.LoadFile(path);
                _loaded[name] = loaded;
                return loaded;
            }

            // A dependency that did not change (MaterialDesign, say) is the copy already loaded, the installed
            // add-in's or one from an earlier build: with two copies of a library, XAML may take its types from
            // one and the code from the other, and styles, casts and resources fail.
            public Assembly TryLoad(string name)
            {
                if (_loaded.TryGetValue(name, out var loaded)) return loaded;

                var path = Path.Combine(Directory, name + ".dll");
                if (!File.Exists(path)) return null;

                var same = SameFile(name, path);
                if (same != null)
                {
                    _loaded[name] = same;
                    return same;
                }

                return Load(path);
            }

            private static Assembly SameFile(string name, string path)
            {
                try
                {
                    var length = new FileInfo(path).Length;
                    string hash = null;
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.IsDynamic || !string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) continue;

                        var location = assembly.Location;
                        if (location.IsNullOrEmpty() || !File.Exists(location) || new FileInfo(location).Length != length) continue;

                        hash ??= Hash(path);
                        if (Hash(location) == hash) return assembly;
                    }

                    // Not loaded yet, but the same file sits next to an installed add-in that will load it later:
                    // it is loaded from there now, so the add-in gets this very assembly instead of a second copy.
                    foreach (var folder in LiveFolders())
                    {
                        var candidate = Path.Combine(folder, name + ".dll");
                        if (!File.Exists(candidate) || new FileInfo(candidate).Length != length) continue;

                        hash ??= Hash(path);
                        if (Hash(candidate) == hash) return Assembly.LoadFrom(candidate);
                    }
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Debug("Не удалось сравнить " + name + " с загруженной: " + ex.Message);
                }

                return null;
            }

            // Folders the add-ins Revit loaded live in: not DevKit's shadow copies, nor .NET's own.
            private static IEnumerable<string> LiveFolders()
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !Folders.Any(f => f.Contains(a)))
                    .Select(a => { try { return a.Location; } catch { return null; } })
                    .Where(l => !l.IsNullOrEmpty() && !l.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                                && !l.StartsWith(ShadowBase, StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetDirectoryName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            private static readonly Dictionary<string, Tuple<DateTime, string>> Hashes =
                new Dictionary<string, Tuple<DateTime, string>>(StringComparer.OrdinalIgnoreCase);

            private static string Hash(string path)
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (Hashes.TryGetValue(path, out var cached) && cached.Item1 == stamp) return cached.Item2;

                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    var hash = Convert.ToBase64String(sha.ComputeHash(stream));
                    Hashes[path] = Tuple.Create(stamp, hash);
                    return hash;
                }
            }
        }
    }
}
