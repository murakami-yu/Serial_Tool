using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SerialTool.App.ViewModels;
using SerialTool.Core.Framing;

namespace SerialTool.App;

/// <summary>协议模板编辑器：直接编辑 MainViewModel.Templates，保存时全量校验后落盘。</summary>
public partial class TemplateEditorWindow : Window
{
    private static readonly string[] ChecksumNames =
        { "none", "xor", "sum8", "crc8", "crc16Modbus", "crc16Ccitt", "crc32" };

    private readonly MainViewModel _vm;
    private readonly string _backupJson; // 取消时回滚
    private bool _syncing;

    public TemplateEditorWindow(MainViewModel vm)
    {
        _vm = vm;
        _backupJson = JsonSerializer.Serialize(vm.Templates.ToList());
        InitializeComponent();
        DataContext = vm;
        ChecksumCombo.ItemsSource = ChecksumNames;
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnVmPropertyChanged;
        SyncEditorFields();
    }

    private void OnVmPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTemplate))
            SyncEditorFields();
    }

    /// <summary>切换模板时同步非绑定字段（端序）并刷新列表名显示。</summary>
    private void SyncEditorFields()
    {
        if (_syncing || _vm.SelectedTemplate is null) return;
        _syncing = true;
        EndianCombo.SelectedIndex = _vm.SelectedTemplate.LengthBigEndian ? 1 : 0;
        _syncing = false;
        List.Items.Refresh();
    }

    private void OnEndianChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm.SelectedTemplate is null) return;
        _vm.SelectedTemplate.LengthBigEndian = EndianCombo.SelectedIndex == 1;
    }

    /// <summary>字段失焦后刷新列表显示（名称等）。</summary>
    private void OnFieldChanged(object sender, RoutedEventArgs e)
        => List.Items.Refresh();

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var t = FrameTemplate.Sample();
        t.Name = $"新协议 {DateTime.Now:HHmmss}";
        _vm.Templates.Add(t);
        _vm.SelectedTemplate = t;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTemplate is null) return;
        var idx = _vm.Templates.IndexOf(_vm.SelectedTemplate);
        _vm.Templates.Remove(_vm.SelectedTemplate);
        if (_vm.Templates.Count > 0)
            _vm.SelectedTemplate = _vm.Templates[Math.Max(0, idx - 1)];
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var t in _vm.Templates)
        {
            try
            {
                t.Validate();
            }
            catch (FormatException ex)
            {
                MessageBox.Show(this, $"模板 [{t.Name}] 无效：{ex.Message}", "校验失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                _vm.SelectedTemplate = t;
                return;
            }
        }
        _vm.SaveTemplates();
        Close();
    }

    /// <summary>取消：回滚打开时的模板快照。</summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        var backup = JsonSerializer.Deserialize<List<FrameTemplate>>(_backupJson) ?? new List<FrameTemplate>();
        _vm.Templates.Clear();
        foreach (var t in backup)
            _vm.Templates.Add(t);
        _vm.SelectedTemplate = _vm.Templates.FirstOrDefault();
        Close();
    }
}
