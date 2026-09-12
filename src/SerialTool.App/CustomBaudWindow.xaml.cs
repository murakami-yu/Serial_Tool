using System.Windows;

namespace SerialTool.App;

/// <summary>自定义波特率输入对话框：波特率下拉「自定义…」选项触发；
/// 确定后 Value 回写主窗（插入下拉列表并选中、持久化），取消则主窗选择回退原值。</summary>
public partial class CustomBaudWindow : Window
{
    public string Value { get; private set; } = "";

    public CustomBaudWindow(string current)
    {
        InitializeComponent();
        Input.Text = current;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var t = Input.Text.Trim();
        if (!int.TryParse(t, out var v) || v <= 0)
        {
            Err.Text = "请输入正整数（如 748800）";
            Err.Visibility = Visibility.Visible;
            return;
        }
        Value = t;
        DialogResult = true;
    }
}
