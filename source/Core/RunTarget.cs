using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.ExtStorage;
using System;
using System.Linq;
using System.Reflection;

namespace RevitDevKit.Core
{
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
}
