using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ChameLab.Controls;
using Xunit;

namespace ChameLab.UiTests;

public class SettingRowTests
{
    [AvaloniaFact]
    public void StableNarrowRowDoesNotReapplyBothLayoutsOnEveryMeasure()
    {
        var row = new SettingRow();
        row.Children.Add(new Border { Width = 16, Height = 16 });
        row.Children.Add(new TextBlock { Text = "Setting title" });
        row.Children.Add(new TextBlock { Text = "Description" });
        var input = new TextBox { Text = "Value", Width = 180 };
        row.Children.Add(input);
        var window = new Window { Width = 320, Height = 200, Content = row };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(SettingRow.StackedClass, row.Classes);
            var changes = 0;
            row.Classes.CollectionChanged += (_, _) => changes++;
            for (var i = 0; i < 3; i++)
            {
                row.InvalidateMeasure();
                window.UpdateLayout();
            }
            Assert.Equal(0, changes);
            input.Width = 400;
            window.Width = 500;
            window.UpdateLayout();
            Assert.Contains(SettingRow.StackedClass, row.Classes);
            window.Width = 800;
            window.UpdateLayout();
            Assert.DoesNotContain(SettingRow.StackedClass, row.Classes);
        }
        finally { window.Close(); }
    }
}
