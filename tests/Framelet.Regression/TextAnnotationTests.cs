using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Framelet;

internal static partial class Program
{
    private static void TextAnnotationCompletionPreservesUndo()
    {
        var image = new WriteableBitmap(320, 220, 96, 96, PixelFormats.Bgr32, null);
        var original = new MarkState(EditorTool.Text, 0xFFD3FA85, 4, [new(20, 30)], "Original note", false);
        var edited = original with { Size = 5 };
        var journal = new EditJournal(EditJournal.CurrentVersion, [original], [], [new EditState(0, original, edited)]);
        var editor = new EditorSurface { Tool = EditorTool.Select };
        var window = new Window { Content = editor, Width = 350, Height = 260, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            var field = OpenNote();
            field.Text = "  \r\n  ";
            Press(field, Key.Enter);
            Require(!editor.IsEditingText && !editor.HasAnnotations && editor.CaptureJournal()!.Journal!.Undo.Length == 2,
                "Enter on an empty existing text annotation removes it in one undo step");
            editor.Undo();
            var restored = Note();
            Require(restored.Label == edited.Label && restored.Size == edited.Size && restored.Points.SequenceEqual(edited.Points),
                "Undo restores the text and its original size and position");
            editor.Redo();
            Require(!editor.HasAnnotations, "Redo removes the confirmed empty annotation again");

            field = OpenNote();
            field.Text = "";
            Dismiss(field);
            Require(!editor.IsEditingText && Note().Label == original.Label && editor.CaptureJournal()!.Journal!.Undo.Length == 1,
                "Dismissing an empty text input leaves the original annotation and undo history intact");

            field = OpenNote();
            field.Text = " Rewritten note ";
            Dismiss(field);
            Require(!editor.IsEditingText && Note().Label == "Rewritten note", "Dismissing nonempty text saves the edited annotation");

            field = OpenNote();
            field.Text = "Unsaved text";
            Press(field, Key.Escape);
            Require(!editor.IsEditingText && Note().Label == original.Label && editor.CaptureJournal()!.Journal!.Undo.Length == 1,
                "Escape discards typed changes without creating an undo step");
        }
        finally
        {
            window.Close();
        }

        MarkState Note() => editor.CaptureJournal()!.Journal!.Marks.Single();

        TextBox OpenNote()
        {
            DrainInput();
            editor.Reset(image, journal);
            editor.Redo();
            if (!editor.EditSelectedText())
                throw new InvalidOperationException("Could not open the selected text annotation.");

            DrainInput();
            return PresentationSource.CurrentSources.Cast<PresentationSource>()
                .Where(source => source.RootVisual is not null)
                .SelectMany(source => VisualDescendants(source.RootVisual)).OfType<TextBox>()
                .Single(field => field.Text == original.Label && field.IsVisible);
        }

        static void Dismiss(TextBox field)
        {
            DependencyObject? element = field;
            while (element is not null && element is not Popup)
                element = LogicalTreeHelper.GetParent(element);
            if (element is not Popup popup)
                throw new InvalidOperationException("Could not find the inline text popup.");
            if (popup.StaysOpen)
                throw new InvalidOperationException("An outside click must be able to dismiss the inline text popup.");

            popup.IsOpen = false;
            DrainInput();
        }

        static void Press(TextBox field, Key key) => field.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(field)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

        static void DrainInput() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
