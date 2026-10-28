using System.Collections.Generic;

namespace RevitDevKit.Core
{
    public sealed class PreparedSource
    {
        public string Code { get; set; }

        public int LineOffset { get; set; }

        public IList<string> ExtraReferences { get; set; }
    }
}
