using System;
using AvaloniaRichEditor.Documents;

namespace AvaloniaRichEditor.Controls;

public readonly record struct EditorTextPosition(
    int CaretParagraph, int CaretOffset,
    int StartParagraph, int StartOffset,
    int EndParagraph, int EndOffset);

public partial class RichEditor
{
    public EditorTextPosition CaptureTextPosition()
    {
        var paragraphs = GetAllParagraphsInOrder();
        return new(paragraphs.IndexOf(_caretPosition.Paragraph!), _caretPosition.Offset,
            paragraphs.IndexOf(_selectionStart.Paragraph!), _selectionStart.Offset,
            paragraphs.IndexOf(_selectionEnd.Paragraph!), _selectionEnd.Offset);
    }

    /// <summary>Restores the caret and text selection without changing focus or scrolling.
    /// Positions are clamped if the document has changed.</summary>
    public void RestoreTextPosition(EditorTextPosition position)
    {
        var paragraphs = GetAllParagraphsInOrder();
        if (paragraphs.Count == 0) return;
        TextPointer Pointer(int paragraph, int offset)
        {
            var p = paragraphs[Math.Clamp(paragraph, 0, paragraphs.Count - 1)];
            return new TextPointer(p, Math.Clamp(offset, 0, GetParagraphLength(p)));
        }
        _caretPosition = Pointer(position.CaretParagraph, position.CaretOffset);
        _selectionStart = Pointer(position.StartParagraph, position.StartOffset);
        _selectionEnd = Pointer(position.EndParagraph, position.EndOffset);
        ResetCaretBlink();
        _bringCaretIntoView = false;
    }
}
