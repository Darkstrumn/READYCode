// Licensed under the MIT License. See LICENSE in the project root.

using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ReadyCode.PocketReady;

namespace ReadyCode.Editor;

/// <summary>Word-based highlighting for the extended dialect; does not split identifiers like ROM tokenization.</summary>
public sealed class PocketReadyKeywordColorizer : DocumentColorizingTransformer
{
    private static readonly HashSet<string> Words = new(PocketReadyLanguage.Keywords, StringComparer.OrdinalIgnoreCase);
    public Brush KeywordBrush { get; set; } = Brushes.RoyalBlue;

    protected override void ColorizeLine(DocumentLine line)
    {
        string text = CurrentContext.Document.GetText(line);
        bool quoted = false;
        for (int index = 0; index < text.Length;)
        {
            if (text[index] == '"') { quoted = !quoted; index++; continue; }
            if (quoted || !(char.IsLetter(text[index]) || text[index] == '_')) { index++; continue; }
            int start = index++;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '$')) index++;
            string word = text[start..index];
            if (word.Equals("REM", StringComparison.OrdinalIgnoreCase))
            {
                ChangeLinePart(line.Offset + start, line.EndOffset, run => run.TextRunProperties.SetForegroundBrush(Brushes.ForestGreen));
                break;
            }
            if (Words.Contains(word))
                ChangeLinePart(line.Offset + start, line.Offset + index, run => run.TextRunProperties.SetForegroundBrush(KeywordBrush));
        }
    }
}
