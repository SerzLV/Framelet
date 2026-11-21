using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

internal static partial class Program
{
    private static void PrimaryButtonsKeepTheirForeground(Application app)
    {
        var primary = (Style)app.FindResource("PrimaryButton");
        var ink = (SolidColorBrush)app.FindResource("Ink");
        var accent = (SolidColorBrush)app.FindResource("Accent");
        var disabledAccent = (SolidColorBrush)app.FindResource("AccentDisabled");
        var icon = new ShapePath
        {
            Data = Geometry.Parse("M2,2 L12,12"),
            Style = (Style)app.FindResource("Icon")
        };
        var access = new AccessText { Text = "Копировать" };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(access);
        var buttons = new[]
        {
            new Button { Style = primary, Content = "Auto-scroll" },
            new Button { Style = primary, Content = new TextBlock { Text = "Применить" } },
            new Button { Style = primary, Content = content }
        };

        foreach (var button in buttons)
        {
            button.ApplyTemplate();
            button.Measure(new Size(250, 50));
            button.Arrange(new Rect(0, 0, 250, 50));
            button.UpdateLayout();
            var labels = VisualDescendants(button).OfType<TextBlock>().ToArray();
            Require(labels.Length > 0 && labels.All(label => SameColor(label.Foreground, ink)) && SameColor(button.Background, accent),
                $"A primary button's rendered label inherits dark ink ({button.Content}, {button.Foreground} on {button.Background}, " +
                $"{string.Join(", ", labels.Select(label => $"{label.Text}: {label.Foreground}"))})");
            button.IsEnabled = false;
            Require(button.Opacity == 1 && SameColor(button.Background, disabledAccent) &&
                labels.All(label => SameColor(label.Foreground, ink)),
                "A disabled primary button keeps its label readable");
        }
        Require(SameColor(access.Foreground, ink) && SameColor(icon.Stroke, ink), "The access label and icon use the same ink color");
    }

    private static bool SameColor(Brush? brush, SolidColorBrush expected) =>
        brush is SolidColorBrush solid && solid.Color == expected.Color;

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child))
                yield return descendant;
        }
    }
}
