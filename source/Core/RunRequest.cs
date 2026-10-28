using System;

namespace RevitDevKit.Core
{
    public sealed class RunRequest
    {
        public string Title { get; set; }
        public Func<object> Body { get; set; }

        public bool AutoTransaction { get; set; }

        public string TransactionName { get; set; }

        public Action<Exception> Completed { get; set; }
    }
}
