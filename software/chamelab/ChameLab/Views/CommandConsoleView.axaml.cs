using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ChameLab.ViewModels;

namespace ChameLab.Views;

public partial class CommandConsoleView : UserControl
{
    private CommandConsoleViewModel? _viewModel;
    public CommandConsoleView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => { if (_viewModel is not null) _viewModel.PropertyChanged -= Changed; _viewModel = null; };
    }
    private void Subscribe()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= Changed;
        _viewModel = DataContext as CommandConsoleViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandConsoleViewModel.Output) && _viewModel?.AutoScroll == true)
            Dispatcher.UIThread.Post(() => OutputScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { Working: false } vm) return;
        switch (e.Key)
        {
            case Key.Up: vm.HistoryStep(-1); break;
            case Key.Down: vm.HistoryStep(1); break;
            case Key.Tab: vm.CompleteCommand(); break;
            case Key.Enter: if (vm.RunCommand.CanExecute(null)) vm.RunCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }
}
