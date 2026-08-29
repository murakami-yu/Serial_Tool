using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SerialTool.App.ViewModels;
using SerialTool.Core.Framing;

namespace SerialTool.App;

/// <summary>协议模板编辑器（v2 字段链模型）：编辑 MainViewModel.Templates，保存时全量校验后落盘并重建解析器。</summary>
public partial class TemplateEditorWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly string _backupJson; // 取消时回滚

    public TemplateEditorWindow(MainViewModel vm)
    {
        _vm = vm;
        _backupJson = JsonSerializer.Serialize(vm.Templates.ToList());
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTemplate))
            Refresh();
    }

    /// <summary>切换模板后刷新列表/字段表显示。</summary>
    private void Refresh()
    {
        List.Items.Refresh();
        FieldsGrid.Items.Refresh();
    }

    /// <summary>字段失焦后刷新列表显示（名称/启用圆点）。</summary>
    private void OnFieldChanged(object sender, RoutedEventArgs e)
        => Refresh();

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var t = new FrameTemplate
        {
            Name = $"新协议 {DateTime.Now:HHmmss}",
            Header = "AA",
            Checksum = "crc16Modbus",
            ChecksumBigEndian = true,
            Fields =
            {
                new FrameField { Kind = "cmd", Size = 1 },
                new FrameField { Kind = "length", Size = 1 },
                new FrameField { Kind = "data" },
            },
        };
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

    private void OnAddField(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTemplate is null) return;
        _vm.SelectedTemplate.Fields.Add(new FrameField { Kind = "fixed", Size = 1 });
        FieldsGrid.Items.Refresh();
    }

    private void OnDeleteField(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTemplate is null) return;
        if (FieldsGrid.SelectedItem is FrameField f)
        {
            _vm.SelectedTemplate.Fields.Remove(f);
            FieldsGrid.Items.Refresh();
        }
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
        _vm.RebuildParser(); // 启用集合可能变化，重建多模板解析器
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
        _vm.RebuildParser();
        Close();
    }
}
