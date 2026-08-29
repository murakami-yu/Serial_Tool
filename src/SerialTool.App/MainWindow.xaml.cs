using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SerialTool.App.ViewModels;

namespace SerialTool.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as MainViewModel)?.Dispose();
    }

    /// <summary>接收区新内容时滚动到底部。</summary>
    private void RxOutput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) tb.ScrollToEnd();
    }

    /// <summary>Enter 发送（Shift+Enter 换行）。</summary>
    private void TxInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            if (DataContext is MainViewModel vm && vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
        }
    }
}
