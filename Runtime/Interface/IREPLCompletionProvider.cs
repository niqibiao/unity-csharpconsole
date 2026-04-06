using System.Collections.Generic;

namespace Zh1Zh1.CSharpConsole.Interface
{
    public class CompletionItem
    {
        public string Label;         // "WriteLine"
        public string Kind;          // "Method" / "Property" / "Field" / "Namespace" / "Class"
        public string Detail;        // "void Console.WriteLine(string)"
        public string Accessibility; // Accessibility.ToString()
    }

    public interface IREPLCompletionProvider
    {
        List<CompletionItem> GetCompletions(string code, int cursorPosition, string defines, string defaultUsing);
    }
}
