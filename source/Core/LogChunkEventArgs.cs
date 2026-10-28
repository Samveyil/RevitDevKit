using System;

namespace RevitDevKit.Core
{
    public sealed class LogChunkEventArgs : EventArgs
    {
        public LogChunkEventArgs(string text, LogLevel level)
        {
            Text = text;
            Level = level;
            Timestamp = DateTime.Now;
        }

        public string Text { get; }
        public LogLevel Level { get; }
        public DateTime Timestamp { get; }
    }
}
