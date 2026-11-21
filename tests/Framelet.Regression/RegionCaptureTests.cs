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
