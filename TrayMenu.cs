using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Framelet;

internal sealed class TrayMenu : Forms.ContextMenuStrip
{
    private static readonly Drawing.Color Surface = Drawing.Color.FromArgb(24, 31, 41);
    private static readonly Drawing.Color Outline = Drawing.Color.FromArgb(57, 71, 87);
    private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(38, 51, 64);
    private static readonly Drawing.Color Keyboard = Drawing.Color.FromArgb(174, 191, 207);
    private static readonly Drawing.Color TextColor = Drawing.Color.FromArgb(241, 245, 250);
    private static readonly Drawing.Color ExitColor = Drawing.Color.FromArgb(255, 170, 178);

    private readonly Dictionary<string, Forms.ToolStripMenuItem> _commands = [];
    private readonly List<Drawing.Image> _images = [];
    private readonly Drawing.Font _menuFont = new("Segoe UI", 9.5f);
    private readonly Drawing.Font _brandFont = new("Segoe UI", 11f, Drawing.FontStyle.Bold);

    internal TrayMenu(Drawing.Icon icon)
    {
        BackColor = Surface;
        ForeColor = TextColor;
        Font = _menuFont;
        ShowImageMargin = true;
        ShowCheckMargin = false;
        ImageScalingSize = new Drawing.Size(18, 18);
        Renderer = new FrameletTrayRenderer();

        using var original = icon.ToBitmap();
        var brand = new Drawing.Bitmap(original, new Drawing.Size(24, 24));
        _images.Add(brand);
        Items.Add(new Forms.ToolStripLabel("Framelet", brand)
        {
            Font = _brandFont,
            ForeColor = TextColor,
            ImageScaling = Forms.ToolStripItemImageScaling.None,
            Margin = new Forms.Padding(0, 3, 0, 2),
            Padding = new Forms.Padding(2)
        });
        Items.Add(new Forms.ToolStripSeparator());
    }

    internal void AddCommand(string label, string icon, EventHandler handler)
    {
        var image = CreateIcon(icon, label == "TrayExit" ? ExitColor : Drawing.Color.FromArgb(207, 246, 137));
        _images.Add(image);
        var item = new Forms.ToolStripMenuItem(UiText.Get(label), image, handler)
        {
            ForeColor = label == "TrayExit" ? ExitColor : TextColor,
            Name = label,
            ShowShortcutKeys = false
        };
        _commands.Add(label, item);
        Items.Add(item);
    }

    internal void SetShortcut(string label, string shortcut)
    {
        var item = _commands[label];
        item.ShortcutKeyDisplayString = shortcut;
        item.ShowShortcutKeys = shortcut.Length != 0;
    }

    internal void UpdateLanguage()
    {
        foreach (var (key, item) in _commands)
            item.Text = UiText.Get(key);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var image in _images)
                image.Dispose();
            _menuFont.Dispose();
            _brandFont.Dispose();
        }
    }

    private static Drawing.Bitmap CreateIcon(string key, Drawing.Color color)
    {
        var geometry = (Geometry)Application.Current.FindResource(key + "Icon");
        var visual = new DrawingVisual();
        const int size = 20;
        var bounds = geometry.Bounds;
        var scale = (size - 4) / Math.Max(bounds.Width, bounds.Height);
        var transform = new Matrix(scale, 0, 0, scale,
            (size - bounds.Width * scale) / 2 - bounds.Left * scale,
            (size - bounds.Height * scale) / 2 - bounds.Top * scale);
        using (var context = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            var pen = new Pen(brush, 1.7 / scale)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            context.PushTransform(new MatrixTransform(transform));
            context.DrawGeometry(null, pen, geometry);
        }
        var image = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        using var png = new MemoryStream();
        HistoryStore.EncodePng(png, image);
        png.Position = 0;
        using var bitmap = new Drawing.Bitmap(png);
        return new Drawing.Bitmap(bitmap);
    }

    private sealed class FrameletTrayRenderer : Forms.ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(Surface);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            // Icons and labels share the same uninterrupted background.
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            var bounds = e.ToolStrip.ClientRectangle;
            bounds.Width--;
            bounds.Height--;
            using var pen = new Drawing.Pen(Outline);
            e.Graphics.DrawRectangle(pen, bounds);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled || e.ToolStrip is not { } strip)
                return;
            var inset = strip.LogicalToDeviceUnits(5);
            var box = new Drawing.Rectangle(inset - e.Item.Bounds.Left, 1,
                strip.ClientSize.Width - inset * 2, e.Item.Height - 2);
            PaintRounded(e.Graphics, box, Hover, strip.LogicalToDeviceUnits(6));
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            if (e.ToolStrip is not { } strip)
                return;
            using var pen = new Drawing.Pen(Outline);
            var middle = e.Item.Height / 2;
            var inset = strip.LogicalToDeviceUnits(10);
            var left = inset - e.Item.Bounds.Left;
            e.Graphics.DrawLine(pen, left, middle, left + strip.ClientSize.Width - inset * 2, middle);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item is Forms.ToolStripMenuItem { ShowShortcutKeys: true } item
                && e.Text == item.ShortcutKeyDisplayString ? Keyboard : e.Item.ForeColor;
            base.OnRenderItemText(e);
        }

        private static void PaintRounded(Drawing.Graphics graphics, Drawing.Rectangle bounds, Drawing.Color color, int radius)
        {
            if (bounds.Width < radius * 2 || bounds.Height < radius * 2)
                return;
            using var path = new Drawing.Drawing2D.GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            using var fill = new Drawing.SolidBrush(color);
            var old = graphics.SmoothingMode;
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.FillPath(fill, path);
            graphics.SmoothingMode = old;
        }
    }
}
