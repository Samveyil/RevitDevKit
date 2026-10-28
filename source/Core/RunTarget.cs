using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevitDevKit.Core
{
    public enum RunTargetKind
    {
        /// <summary>Класс реализует IExternalCommand.</summary>
        ExternalCommand,

        /// <summary>Метод-точка входа (Main / Run / Execute) в обычном классе.</summary>
        Method
    }

    /// <summary>
    /// Точка входа, которую можно запустить: команда Revit или обычный метод.
    /// </summary>
    public sealed class RunTarget
    {
        public RunTargetKind Kind { get; set; }
        public Type Type { get; set; }
        public MethodInfo Method { get; set; }

        public string TypeName
        {
            get { return Type == null ? "?" : Type.FullName; }
        }

        public string DisplayName
        {
            get
            {
                if (Kind == RunTargetKind.ExternalCommand) return Type.Name + "   (IExternalCommand)";
                return Type.Name + "." + Method.Name + "()";
            }
        }

        public string Details
        {
            get
            {
                if (Kind == RunTargetKind.ExternalCommand) return TypeName;
                var args = string.Join(", ", Method.GetParameters().Select(p => p.ParameterType.Name));
                return TypeName + "." + Method.Name + "(" + args + ")";
            }
        }

        /// <summary>
        /// Выполняет цель. Должен вызываться в валидном контексте Revit API.
        /// Контекст не передаётся параметрами — он берётся из RevitAPI.
        /// </summary>
        public object Invoke()
        {
            if (Kind == RunTargetKind.ExternalCommand) return InvokeCommand();
            return InvokeMethod();
        }

        private object InvokeCommand()
        {
            var commandData = RevitTaskRunner.CurrentCommandData;

            if (commandData == null)
            {
                throw new InvalidOperationException(
                    "Для запуска IExternalCommand нужен живой ExternalCommandData, а его выдаёт " +
                    "только сам Revit. Убедитесь, что на ленте есть кнопка «Ещё раз» — через неё " +
                    "плагин получает контекст команды.");
            }

            var instance = Activator.CreateInstance(Type);
            var command = (IExternalCommand)instance;

            var message = string.Empty;
            var elements = new ElementSet();

            var result = command.Execute(commandData, ref message, elements);

            if (!string.IsNullOrEmpty(message))
                ConsoleOutput.Warning("Команда вернула сообщение: " + message);

            if (elements.Size > 0)
                ConsoleOutput.Warning("Команда вернула " + elements.Size + " проблемных элементов.");

            return result;
        }

        private object InvokeMethod()
        {
            var instance = Method.IsStatic ? null : Activator.CreateInstance(Type);

            var parameters = Method.GetParameters();
            var args = new object[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
                args[i] = BindArgument(parameters[i]);

            return Method.Invoke(instance, args);
        }

        /// <summary>
        /// Подставляет аргументы чужому методу по типу параметра — его сигнатуру
        /// мы не контролируем, поэтому этот путь остаётся.
        /// </summary>
        private static object BindArgument(ParameterInfo parameter)
        {
            var t = parameter.ParameterType;

            if (t == typeof(UIApplication)) return RevitAPI.UIApplication;
            if (t == typeof(Autodesk.Revit.ApplicationServices.Application)) return RevitAPI.Application;
            if (t == typeof(UIDocument)) return RevitAPI.UIDocument;
            if (t == typeof(Document)) return RevitAPI.Document;
            if (t == typeof(ExternalCommandData)) return RevitTaskRunner.CurrentCommandData;

            if (parameter.HasDefaultValue) return parameter.DefaultValue;

            throw new InvalidOperationException(
                "Не знаю, что передать в параметр '" + parameter.Name + "' типа " + t.Name +
                ". Поддерживаются UIApplication, UIDocument, Document, Application, ExternalCommandData.");
        }
    }

    /// <summary>
    /// Ищет в сборке всё, что можно запустить.
    /// </summary>
    public static class EntryPointResolver
    {
        private static readonly string[] EntryMethodNames = { "Main", "Run", "Execute" };

        /// <summary>Все запускаемые цели сборки — для дерева загруженных DLL.</summary>
        public static IList<RunTarget> FindAll(Assembly assembly, bool includeMethods = true)
        {
            var result = new List<RunTarget>();

            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract && !type.IsSealed) continue; // абстрактные — не запустить
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

        /// <summary>
        /// Единственная точка входа скомпилированного скрипта.
        /// Приоритет: сгенерированный класс-обёртка → IExternalCommand → метод Main/Run/Execute.
        /// </summary>
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


        /// <summary>GetTypes() падает, если часть зависимостей не разрешилась — берём что получилось.</summary>
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