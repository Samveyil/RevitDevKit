using BIMPlugins.ExtStorage.Extensions;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace RevitDevKit.Core
{
    public static class ConsoleOutput
    {
        private static readonly object Sync = new object();
        private static TextWriter _originalOut;
        private static TextWriter _originalError;
        private static ConsoleTraceListener _traceListener;
        private static bool _installed;

        public static event EventHandler<LogChunkEventArgs> ChunkWritten;

        public static event EventHandler Cleared;

        public static void Install()
        {
            lock (Sync)
            {
                if (_installed)
                {
                    EnsureTraceListener();
                    return;
                }

                _originalOut = Console.Out;
                _originalError = Console.Error;

                Console.SetOut(new RedirectingWriter(LogLevel.Info, _originalOut));
                Console.SetError(new RedirectingWriter(LogLevel.Error, _originalError));

                _traceListener = new ConsoleTraceListener();
                Trace.Listeners.Add(_traceListener);

                _installed = true;
            }
        }

        // Trace.Listeners is process-wide and other add-ins may clear it after start-up;
        // without our listener Debug.WriteLine silently stops reaching the console.
        public static void EnsureTraceListener()
        {
            lock (Sync)
            {
                if (!_installed || _traceListener == null) return;
                if (Trace.Listeners.Contains(_traceListener)) return;

                Trace.Listeners.Add(_traceListener);
                Debug("Перехват Debug/Trace был снят другой надстройкой — восстановлен.");
            }
        }

        public static void Uninstall()
        {
            lock (Sync)
            {
                if (!_installed) return;

                if (_originalOut != null) Console.SetOut(_originalOut);
                if (_originalError != null) Console.SetError(_originalError);
                if (_traceListener != null) Trace.Listeners.Remove(_traceListener);

                _traceListener = null;
                _installed = false;
            }
        }

        public static void Write(string text, LogLevel level = LogLevel.Info)
        {
            if (text.IsNullOrEmpty())
                return;
            
            ChunkWritten?.Invoke(null, new LogChunkEventArgs(text, level));
        }

        public static void WriteLine(string text = "", LogLevel level = LogLevel.Info)
        {
            Write((text ?? string.Empty) + Environment.NewLine, level);
        }

        public static void WriteObject(object value, LogLevel level = LogLevel.Info)
        {
            WriteLine(Format(value), level);
        }

        public static void Info(string text) => WriteLine(text, LogLevel.Info);
        public static void Success(string text) => WriteLine(text, LogLevel.Success);
        public static void Warning(string text) => WriteLine(text, LogLevel.Warning);
        public static void Error(string text) => WriteLine(text, LogLevel.Error);
        public static void Debug(string text) => WriteLine(text, LogLevel.Debug);
        public static void System(string text) => WriteLine(text, LogLevel.System);

        public static void Banner(string text)
        {
            var pad = Math.Max(3, 58 - text.Length);
            WriteLine("── " + text + " " + new string('─', pad), LogLevel.System);
        }

        public static void Exception(Exception ex, string prefix = null)
        {
            if (ex == null)
                return;
            
            if (!prefix.IsNullOrEmpty())
                WriteLine(prefix, LogLevel.Error);

            var current = ex;
            var depth = 0;
            while (current != null)
            {
                var indent = new string(' ', depth * 2);
                WriteLine(indent + current.GetType().FullName + ": " + current.Message, LogLevel.Error);

                if (!current.StackTrace.IsNullOrEmpty())
                {
                    var lines = current.StackTrace.Split('\n');
                    foreach (var line in lines)
                        WriteLine(indent + "  " + line.TrimEnd('\r'), LogLevel.Debug);
                }

                current = current.InnerException;
                depth++;
                if (current != null)
                    WriteLine(new string(' ', depth * 2) + "--- внутреннее исключение ---", LogLevel.Error);
            }
        }

        public static void Clear()
        {
            Cleared?.Invoke(null, EventArgs.Empty);
        }

        public static string Format(object value)
        {
            if (value == null)
                return "null";

            var asString = value as string;
            if (asString != null)
                return asString;

            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable != null)
            {
                var sb = new StringBuilder();
                var count = 0;
                sb.Append("[");
                foreach (var item in enumerable)
                {
                    if (count >= 200)
                    {
                        sb.Append(", … (показаны первые 200)");
                        break;
                    }

                    if (count > 0)
                        sb.Append(", ");

                    sb.Append(Format(item));
                    count++;
                }
                sb.Append("]");
                return sb.ToString();
            }

            var formattable = value as IFormattable;
            if (formattable != null)
                return formattable.ToString(null, CultureInfo.InvariantCulture);

            return value.ToString();
        }

        private sealed class RedirectingWriter(LogLevel level, TextWriter passthrough) : TextWriter
        {
            private readonly LogLevel _level = level;
            private readonly TextWriter _passthrough = passthrough;

            public override Encoding Encoding { get { return Encoding.UTF8; } }

            public override void Write(char value)
            {
                Write(value.ToString());
            }

            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                ConsoleOutput.Write(value, _level);

                try
                {
                    if (_passthrough != null)
                        _passthrough.Write(value);
                }
                catch { } // the original stream is best effort
            }

            public override void WriteLine(string value)
            {
                Write((value ?? string.Empty) + Environment.NewLine);
            }

            public override void WriteLine()
            {
                Write(Environment.NewLine);
            }
        }

        private sealed class ConsoleTraceListener : TraceListener
        {
            public override void Write(string message) => ConsoleOutput.Write(message, LogLevel.Debug);

            public override void WriteLine(string message) => ConsoleOutput.WriteLine(message, LogLevel.Debug);
        }
    }
}
