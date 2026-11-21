using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Framelet;

internal static partial class Program
{
    private static void NumberMarkersStaySequential()
    {
        var image = new WriteableBitmap(220, 140, 96, 96, PixelFormats.Bgr32, null);
        var first = new MarkState(EditorTool.Number, 0xFFD3FA85, 4, [new(120, 30)], "1", false);
        var second = first with { Points = [new(40, 50)], Label = "2" };
        var third = first with { Points = [new(80, 90)], Label = "3" };
        var text = first with { Tool = EditorTool.Text, Points = [new(140, 90)], Label = "42" };
        var editedSecond = second with { Argb = 0xFF3456FA };
        var editor = new EditorSurface { Tool = EditorTool.Number };
        editor.Reset(image, new EditJournal(EditJournal.CurrentVersion, [first, text, second, third], [],
            [new EditState(2, second, editedSecond)]));
        editor.Redo();

        Require(editor.DeleteSelection() && Matches(("1", first), ("2", third)),
            "Deleting the middle number renumbers later marks without moving them or changing text");
        var deleted = editor.CaptureJournal()!.Journal!;
        Require(deleted.IsUsable(), "Deleting and renumbering leaves a valid edit journal");
        editor.Reset(image, deleted);
        editor.Undo();
        Require(Matches(("1", first), ("2", editedSecond), ("3", third)),
            "Undo after reopening restores the mark at its original coordinates, color and number");
        editor.Redo();
        Require(Matches(("1", first), ("2", third)), "Redo deletes and renumbers in one step");

        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        });
        Require(Numbers().Select(mark => mark.Label).SequenceEqual(["1", "2", "3"]),
            "A newly drawn mark continues the sequence after a deletion");

        editor.Reset(image, new EditJournal(EditJournal.CurrentVersion, [first, text, third],
            [new EditState(2, null, third)], []));
        Require(Matches(("1", first), ("2", third)), "Previously saved gaps are corrected when opening the editor");
        editor.Undo();
        editor.Redo();
        Require(Matches(("1", first), ("2", third)), "Replaying a legacy edit cannot reintroduce a gap");

        MarkState[] Numbers() => editor.CaptureJournal()!.Journal!.Marks.Where(mark => mark.Tool == EditorTool.Number).ToArray();

        bool Matches(params (string Label, MarkState Mark)[] expected) => Numbers().Select(mark => (mark.Label, mark.Points[0], mark.Argb))
            .SequenceEqual(expected.Select(entry => ((string?)entry.Label, entry.Mark.Points[0], entry.Mark.Argb)))
            && editor.CaptureJournal()!.Journal!.Marks.Single(mark => mark.Tool == EditorTool.Text).Label == text.Label;
    }
}
