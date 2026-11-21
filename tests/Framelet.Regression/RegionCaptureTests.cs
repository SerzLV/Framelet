using System.Windows.Media;
using System.Windows.Media.Imaging;
using Framelet;
using Drawing = System.Drawing;

internal static partial class Program
{
    private static void RegionSelectionUsesDesktopCoordinates()
    {
        var left = new Drawing.Rectangle(-1600, -200, 1600, 900);
        var right = new Drawing.Rectangle(0, 0, 1920, 1080);
        var background = new Drawing.Rectangle(-200, -100, 600, 500);
        var foreground = new Drawing.Rectangle(-85, 22, 265, 160);
        var session = new RegionSelection([left, right], [foreground, background], singleDisplay: false);
        session.MovePointer(new Drawing.Point(35, 60));
        Require(session.Preview == foreground, "Hover highlights the front window across displays");
        session.Begin(new Drawing.Point(35, 60));
        Require(session.End(new Drawing.Point(35, 60)) is null && session.Selection == foreground,
            "A click selects the highlighted window for adjustment");

        session.Begin(new Drawing.Point(-70, -36));
        session.MovePointer(new Drawing.Point(85, 103));
        var expected = new Drawing.Rectangle(-70, -36, 155, 139);
        Require(session.Preview == expected && session.End(new Drawing.Point(85, 103)) is null && session.Selection == expected,
            "Dragging beyond the first monitor uses the same physical rectangle in every overlay");
        session.Nudge(1, 0);
        session.Nudge(0, 10);
        expected.Offset(1, 10);
        Require(session.Selection == expected, "Arrow steps move the selection without changing its size");
        session.Begin(new Drawing.Point(5, 50));
        Require(session.End(new Drawing.Point(5, 50)) == expected, "A second click inside confirms the adjusted selection");

        session.Begin(new Drawing.Point(5, 50));
        session.MovePointer(new Drawing.Point(8, 53));
        Require(session.Preview == expected && session.End(new Drawing.Point(8, 53)) == expected && session.Selection == expected,
            "A confirming click with three pixels of pointer drift preserves the selected area");

        var edge = new Drawing.Point(expected.Right - 1, 50);
        session.Begin(edge);
        Require(session.End(new Drawing.Point(edge.X + 3, edge.Y + 3)) == expected,
            "A short release outside the frame confirms the area pressed at the edge");

        session.Begin(new Drawing.Point(5, 50));
        session.MovePointer(new Drawing.Point(250, 200));
        Require(session.End(new Drawing.Point(5, 50)) is null && session.Selection == expected,
            "Returning a drag to its origin does not confirm the previous selection");

        session.Begin(new Drawing.Point(85, 88));
        session.MovePointer(new Drawing.Point(-90, 12));
        Require(session.Preview == new Drawing.Rectangle(-90, 12, 175, 76), "A drag can cross in the reverse direction");
        session.CancelDrag();
        Require(session.Selection == expected, "Losing capture restores the confirmed draft");

        var scrolling = new RegionSelection([left, right], [foreground], singleDisplay: true);
        scrolling.MovePointer(new Drawing.Point(-85, 65));
        Require(scrolling.Preview is null, "Scrolling capture has no whole-window target");
        scrolling.Begin(new Drawing.Point(-90, -10));
        scrolling.End(new Drawing.Point(180, 75));
        Require(scrolling.Selection == new Drawing.Rectangle(-90, -10, 90, 85), "Scrolling capture stays on its starting monitor");
        scrolling.Nudge(10, 0);
        Require(scrolling.Selection?.Right == 0, "Keyboard adjustment also respects scrolling capture's monitor bounds");

        var scaled = new RegionSelection([right], [], singleDisplay: false);
        var origin = new Drawing.Point(50, 50);
        var threshold = new Drawing.Size(9, 9);
        var smallMove = new Drawing.Point(58, 56);
        scaled.Begin(origin, dragThreshold: threshold);
        scaled.MovePointer(smallMove);
        Require(scaled.Preview is null && scaled.End(smallMove) is null && scaled.Selection is null,
            "Pointer movement below the display-scaled drag threshold never creates a draft");
        scaled.Begin(origin, dragThreshold: threshold);
        scaled.End(new Drawing.Point(59, 53));
        Require(scaled.Selection == new Drawing.Rectangle(50, 50, 9, 3),
            "A deliberate narrow selection crossing the drag threshold still works without an intermediate move event");
    }

    private static void RegionSelectionResizesFromCorners()
    {
        var left = new Drawing.Rectangle(-160, -20, 160, 140);
        var right = new Drawing.Rectangle(0, 0, 180, 150);
        var original = new Drawing.Rectangle(-30, 20, 50, 40);
        var hitRadius = new Drawing.Size(9, 9);

        foreach (var (corner, expected) in new[]
        {
            (SelectionCorner.TopLeft, Drawing.Rectangle.FromLTRB(-25, 27, 20, 60)),
            (SelectionCorner.TopRight, Drawing.Rectangle.FromLTRB(-30, 27, 25, 60)),
            (SelectionCorner.BottomLeft, Drawing.Rectangle.FromLTRB(-25, 20, 20, 67)),
            (SelectionCorner.BottomRight, Drawing.Rectangle.FromLTRB(-30, 20, 25, 67))
        })
        {
            var session = CreateSelection();
            var grip = RegionSelection.CornerPosition(original, corner);
            grip.Offset(2, -2);
            var hit = session.CornerAt(grip, hitRadius);
            session.Begin(grip, hit);
            var target = new Drawing.Point(grip.X + 5, grip.Y + 7);
            session.MovePointer(target);
            Require(hit == corner && session.Preview == expected && session.Selection == original,
                $"Dragging {corner} previews the resize without jumping or moving the opposite corner");
            Require(session.End(target) is null && session.Selection == expected && session.ResizeCorner is null,
                $"Releasing {corner} keeps the resized selection available for adjustment");
        }

        var across = CreateSelection();
        var upperLeft = original.Location;
        across.Begin(upperLeft, across.CornerAt(upperLeft, hitRadius));
        var pastOpposite = new Drawing.Point(75, 100);
        across.MovePointer(pastOpposite);
        Require(across.Preview == new Drawing.Rectangle(17, 57, 3, 3),
            "A corner can cross a monitor seam and stops at the minimum size before the opposite corner");
        across.CancelDrag();
        Require(across.Selection == original && across.Preview == original && across.ResizeCorner is null,
            "Losing mouse capture restores the original rectangle");
        across.Begin(upperLeft, across.CornerAt(upperLeft, hitRadius));
        Require(across.End(upperLeft) is null && across.Selection == original, "Clicking a corner does not take a screenshot");
        var center = new Drawing.Point(-5, 40);
        across.Begin(center, across.CornerAt(center, hitRadius));
        Require(across.End(center) == original, "Clicking inside the selection still confirms the screenshot");

        var scroll = new RegionSelection([left, right], [], singleDisplay: true);
        scroll.Begin(new Drawing.Point(-60, 20));
        scroll.End(new Drawing.Point(0, 60));
        var bottomRight = new Drawing.Point(-1, 60);
        var edgeGrip = scroll.CornerAt(bottomRight, hitRadius);
        scroll.Begin(bottomRight, edgeGrip);
        scroll.End(new Drawing.Point(80, 150));
        Require(edgeGrip == SelectionCorner.BottomRight && scroll.Selection == new Drawing.Rectangle(-60, 20, 60, 100),
            "Resizing a scrolling capture respects its monitor bounds, even at an edge");

        RegionSelection CreateSelection()
        {
            var selection = new RegionSelection([left, right], [], singleDisplay: false);
            selection.Begin(original.Location);
            selection.End(new Drawing.Point(original.Right, original.Bottom));
            return selection;
        }
    }

    private static void RegionCaptureComposesMonitors()
    {
        var left = SyntheticDisplay(new Drawing.Rectangle(-12, 2, 12, 7), 40, 96);
        var right = SyntheticDisplay(new Drawing.Rectangle(0, 0, 14, 12), 150, 144);
        var rect = new Drawing.Rectangle(-5, 0, 11, 11);
        var image = CaptureService.CropDisplays([left, right], rect);
        var pixels = ToPixels(image);
        Require(image.IsFrozen && image.PixelWidth == rect.Width && image.PixelHeight == rect.Height,
            "Multi-monitor capture yields one frozen bitmap at physical pixel size");

        var exact = true;
        for (var y = 0; y < rect.Height; y++)
        {
            for (var x = 0; x < rect.Width; x++)
            {
                var point = new Drawing.Point(rect.X + x, rect.Y + y);
                var screen = left.Bounds.Contains(point) ? left : right.Bounds.Contains(point) ? right : null;
                var index = (y * rect.Width + x) * 4;
                var blue = screen is null ? 0 : point.X - screen.Bounds.X + (screen == left ? 40 : 150);
                var green = screen is null ? 0 : point.Y - screen.Bounds.Y;
                exact &= pixels[index] == blue && pixels[index + 1] == green;
            }
        }
        Require(exact, "Negative origins, different DPI metadata and the gap between displays preserve exact pixels");

        var cropped = CaptureService.CropDisplays([left, right], new Drawing.Rectangle(2, 1, 4, 5));
        Require(cropped.PixelWidth == 4 && cropped.PixelHeight == 5 && ToPixels(cropped)[0] == 152,
            "Capture on a single monitor crops only its bitmap");
    }

    private static ScreenSnapshot SyntheticDisplay(Drawing.Rectangle bounds, int blue, double dpi)
    {
        var stride = bounds.Width * 4;
        var pixels = new byte[stride * bounds.Height];
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            var index = y * stride + x * 4;
            pixels[index] = (byte)(x + blue);
            pixels[index + 1] = (byte)y;
            pixels[index + 2] = 220;
        }
        var image = BitmapSource.Create(bounds.Width, bounds.Height, dpi, dpi, PixelFormats.Bgr32, null, pixels, stride);
        image.Freeze();
        return new ScreenSnapshot(bounds, image);
    }

    private static void OcrDistinguishesYearsFromPhones()
    {
        const string resume = "Учёба 2015-2020\nWork 2020 – 2024\n1998—2001\nMobile: +371 2123 4567";
        var contacts = ContactExtractor.Find(resume, "LV").Where(match => match.Contact.Kind == ContactKind.Phone).ToArray();
        Require(contacts.Length == 1 && contacts[0].Identity.StartsWith("+37121234567/", StringComparison.Ordinal)
            && resume.AsSpan(contacts[0].Start, contacts[0].Length).SequenceEqual("+371 2123 4567"),
            "OCR ignores CV year ranges and preserves the real phone number and its image span");

        const string phone = "Reception: +371 2015-2020";
        var international = ContactExtractor.Find(phone, "LV").Single(match => match.Contact.Kind == ContactKind.Phone);
        var labelled = ContactExtractor.Find("Tālrunis: 2015-2020", "LV").Single(match => match.Contact.Kind == ContactKind.Phone);
        Require(international.Identity == "+37120152020/" && labelled.Identity == international.Identity,
            "A country code or phone label preserves a genuinely ambiguous Latvian phone number");
    }
}
