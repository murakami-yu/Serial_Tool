using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SerialTool.App.ViewModels;

/// <summary>一条待发送帧：内容 + 模式 + 循环周期（0/关闭表示仅手动）。</summary>
public partial class SendFrameViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    [ObservableProperty]
    private string _content = string.Empty;

    /// <summary>帧备注：用户注释这条帧的用途。</summary>
    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    private bool _isHex = true;

    /// <summary>循环周期（毫秒），仅循环开启时生效。</summary>
    [ObservableProperty]
    private int _periodMs = 1000;

    [ObservableProperty]
    private bool _isCyclic;

    /// <summary>行号（1 起，集合变动时由 MainViewModel 重排）。</summary>
    [ObservableProperty]
    private int _index;

    /// <summary>下次应发送时刻（循环调度用，不通知 UI）。</summary>
    public DateTime NextDue;

    public SendFrameViewModel(MainViewModel owner) => _owner = owner;

    [RelayCommand]
    private void Send() => _owner.SendFrame(this);
}
