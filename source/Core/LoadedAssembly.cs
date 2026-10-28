using System;
using System.Reflection;

namespace RevitDevKit.Core
{
    public sealed class LoadedAssembly
    {
        public Assembly Assembly { get; set; }
        public string OriginalPath { get; set; }
        internal IDisposable Owner { get; set; }

        public string DisplayName => System.IO.Path.GetFileName(OriginalPath);
    }
}
