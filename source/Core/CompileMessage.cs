namespace RevitDevKit.Core
{
    public sealed class CompileMessage
    {
        public bool IsError { get; set; }
        public string Id { get; set; }
        public string Message { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        public override string ToString()
        {
            var where = Line > 0 ? "(" + Line + "," + Column + ") " : string.Empty;
            return where + (IsError ? "ошибка " : "предупреждение ") + Id + ": " + Message;
        }
    }
}
