using System.Windows;

namespace SerialTool.App;

/// <summary>外观设置对话框：各界面背景色集中管理（主窗状态栏「外观…」入口）。
/// 纯绑定视图——ColorChipButton 双向绑定 Appearance 单例，无自有状态；
/// 改色即时生效（各窗口 Background 绑定同一单例）并由 MainViewModel 订阅落盘。</summary>
public partial class AppearanceWindow : Window
{
    public AppearanceWindow()
    {
        InitializeComponent();
    }
}
