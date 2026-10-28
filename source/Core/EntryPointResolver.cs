using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevitDevKit.Core
{
    public static class EntryPointResolver
    {
        private static readonly string[] EntryMethodNames = { "Main", "Run", "Execute" };

        public static IList<RunTarget> FindAll(Assembly assembly, bool includeMethods = true)
        {
            var result = new List<RunTarget>();

            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract && !type.IsSealed) continue;
                if (type.IsGenericTypeDefinition) continue;

                if (typeof(IExternalCommand).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                {
                    result.Add(new RunTarget { Kind = RunTargetKind.ExternalCommand, Type = type });
                    continue;
                }

                if (!includeMethods) continue;

                foreach (var method in FindEntryMethods(type))
                    result.Add(new RunTarget { Kind = RunTargetKind.Method, Type = type, Method = method });
            }

            return result
                .OrderBy(t => t.Kind == RunTargetKind.ExternalCommand ? 0 : 1)
                .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static RunTarget ResolveScriptEntry(Assembly assembly)
        {
            var targets = FindAll(assembly);
            if (targets.Count == 0) return null;

            var wrapper = targets.FirstOrDefault(t => t.TypeName == ScriptSource.EntryTypeName);
            if (wrapper != null) return wrapper;

            var command = targets.FirstOrDefault(t => t.Kind == RunTargetKind.ExternalCommand);
            if (command != null) return command;

            var byName = EntryMethodNames
                .Select(name => targets.FirstOrDefault(t => t.Method != null && t.Method.Name == name))
                .FirstOrDefault(t => t != null);

            return byName ?? targets[0];
        }

        private static IEnumerable<MethodInfo> FindEntryMethods(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var method in type.GetMethods(flags))
            {
                if (method.IsSpecialName) continue;
                if (!EntryMethodNames.Contains(method.Name)) continue;
                if (method.IsGenericMethodDefinition) continue;
                if (!method.IsStatic && type.GetConstructor(Type.EmptyTypes) == null) continue;
                if (!method.GetParameters().All(IsBindable)) continue;

                yield return method;
            }
        }

        private static bool IsBindable(ParameterInfo parameter)
        {
            var t = parameter.ParameterType;
            return t == typeof(UIApplication)
                   || t == typeof(UIDocument)
                   || t == typeof(Document)
                   || t == typeof(Autodesk.Revit.ApplicationServices.Application)
                   || t == typeof(ExternalCommandData)
                   || parameter.HasDefaultValue;
        }

        public static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var loaderError in ex.LoaderExceptions.Take(5))
                    ConsoleOutput.Warning("Тип не загрузился: " + loaderError.Message);

                return ex.Types.Where(t => t != null);
            }
        }
    }
}
