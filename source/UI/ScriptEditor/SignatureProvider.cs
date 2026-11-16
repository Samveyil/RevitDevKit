using ICSharpCode.AvalonEdit.CodeCompletion;
using RevitDevKit.Core.IntelliSense;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace RevitDevKit.UI.ScriptEditor
{
    // Content of the overloads window: "1 из 3" with arrows, the signature with the current parameter in bold,
    // then the method summary and the current parameter's description, when the assembly has XML docs.
    internal sealed class SignatureProvider : IOverloadProvider
    {
        private readonly ScriptSignatures _signatures;
        private int _selectedIndex;

        public SignatureProvider(ScriptSignatures signatures)
        {
            _signatures = signatures;
            _selectedIndex = signatures.SelectedOverload;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public int Count => _signatures.Overloads.Count;

        // The overload picked with the arrows rather than the one the call resolves to.
        public bool PickedByUser => _selectedIndex != _signatures.SelectedOverload;

        // Whether an overload takes the argument the caret is at.
        public bool Takes(int index, int argumentIndex)
        {
            var overload = _signatures.Overloads[index];
            return argumentIndex < overload.Parameters.Count || overload.HasParamsArray;
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_selectedIndex == value)
                    return;

                _selectedIndex = value;
                foreach (var name in new[] { nameof(SelectedIndex), nameof(CurrentIndexText), nameof(CurrentHeader), nameof(CurrentContent) })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        public string CurrentIndexText => (_selectedIndex + 1) + " из " + Count;

        public object CurrentHeader
        {
            get
            {
                var signature = Current;
                var current = CurrentParameter(signature);
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };

                text.Inlines.Add(new Run(signature.Prefix));
                for (var i = 0; i < signature.Parameters.Count; i++)
                {
                    if (i > 0)
                        text.Inlines.Add(new Run(", "));

                    var parameter = new Run(signature.Parameters[i].Display);
                    if (i == current)
                        parameter.FontWeight = FontWeights.Bold;
                    text.Inlines.Add(parameter);
                }
                text.Inlines.Add(new Run(")"));

                return text;
            }
        }

        public object CurrentContent
        {
            get
            {
                var signature = Current;
                var current = CurrentParameter(signature);
                var parameter = current >= 0 && current < signature.Parameters.Count ? signature.Parameters[current] : null;
                if (signature.Documentation == null && parameter?.Documentation == null)
                    return null;

                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
                if (signature.Documentation != null)
                    text.Inlines.Add(new Run(signature.Documentation));

                if (parameter?.Documentation != null)
                {
                    if (signature.Documentation != null)
                        text.Inlines.Add(new LineBreak());
                    text.Inlines.Add(new Run(parameter.Name + ": ") { FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic });
                    text.Inlines.Add(new Run(parameter.Documentation));
                }

                return text;
            }
        }

        private Signature Current => _signatures.Overloads[_selectedIndex];

        // Arguments past the last parameter of a params array all go to that parameter.
        private int CurrentParameter(Signature signature)
        {
            var index = _signatures.ArgumentIndex;
            return signature.HasParamsArray && index >= signature.Parameters.Count - 1 ? signature.Parameters.Count - 1 : index;
        }
    }
}
