using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevitDevKit.Core
{
    public sealed class CompileResult
    {
        public bool Success { get; set; }
        public Assembly Assembly { get; set; }
        public IList<CompileMessage> Messages { get; set; }
        public long ElapsedMs { get; set; }

        public IEnumerable<CompileMessage> Errors { get => Messages.Where(m => m.IsError); }
    }
}
