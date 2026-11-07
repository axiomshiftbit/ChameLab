using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using ChameLab.Controls;
using Xunit;

namespace ChameLab.UiTests;

public class ActionRowTests
{
    [AvaloniaFact]
    public void StableRowDoesNotToggleButtonStylesOnEveryMeasure()
    {
        var row = new ActionRow();
        var label = new TextBlock { Text = "Import dictionary" };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new PathIcon { Width = 16, Height = 16 });
        content.Children.Add(label);
        var button = new Button { Content = content };
        row.Children.Add(button);
        var window = new Window { Width = 400, Height = 200, Content = row };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var changes = 0;
            button.Classes.CollectionChanged += (_, _) => changes++;
            for (var i = 0; i < 3; i++)
            {
                row.InvalidateMeasure();
                window.UpdateLayout();
            }
            Assert.Equal(0, changes);
            label.FontSize = 48;
            window.UpdateLayout();
            Assert.Contains(ActionRow.IconicClass, button.Classes);
            label.Text = "I";
            label.FontSize = 14;
            window.UpdateLayout();
            Assert.DoesNotContain(ActionRow.IconicClass, button.Classes);
            window.Width = 80;
            label.Text = "Import dictionary";
            window.UpdateLayout();
            Assert.Contains(ActionRow.IconicClass, button.Classes);
            window.Width = 600;
            window.UpdateLayout();
            Assert.DoesNotContain(ActionRow.IconicClass, button.Classes);
        }
        finally { window.Close(); }
    }
}
