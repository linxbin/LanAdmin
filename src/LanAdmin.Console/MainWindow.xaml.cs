using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LanAdmin.Contracts;
using LanAdmin.Console.Services;
using MessageBox = System.Windows.MessageBox;

namespace LanAdmin.Console;

public partial class MainWindow : Window
{
    private readonly ServerApiClient _apiClient;
    private readonly ObservableCollection<DeviceDto> _devices = new();
    private readonly ObservableCollection<DeviceEventDto> _events = new();
    private readonly ObservableCollection<DeviceGroupDto> _groups = new();
    private readonly DispatcherTimer _refreshTimer;

    public MainWindow()
    {
        InitializeComponent();

        _apiClient = new ServerApiClient();

        DevicesDataGrid.ItemsSource = _devices;
        EventsDataGrid.ItemsSource = _events;
        GroupsComboBox.ItemsSource = _groups;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            _refreshTimer.Start();
            await RefreshAsync();
        };
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusTextBlock.Text = "加载中...";
            var selectedAgentId = (DevicesDataGrid.SelectedItem as DeviceDto)?.AgentId;

            var devices = await _apiClient.GetDevicesAsync(SearchTextBox.Text);
            ReplaceItems(_devices, devices);

            if (!string.IsNullOrWhiteSpace(selectedAgentId))
            {
                var selected = _devices.FirstOrDefault(x => x.AgentId == selectedAgentId);
                if (selected is not null)
                {
                    DevicesDataGrid.SelectedItem = selected;
                }
            }

            var groups = await _apiClient.GetGroupsAsync();
            ReplaceItems(_groups, groups);

            if (DevicesDataGrid.SelectedItem is DeviceDto device)
            {
                var events = await _apiClient.GetEventsAsync(device.AgentId);
                ReplaceItems(_events, events);
            }
            else
            {
                var events = await _apiClient.GetEventsAsync(null);
                ReplaceItems(_events, events);
            }

            StatusTextBlock.Text = $"上次刷新 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"加载失败: {ex.Message}";
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void DevicesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DevicesDataGrid.SelectedItem is not DeviceDto device)
        {
            return;
        }

        try
        {
            var events = await _apiClient.GetEventsAsync(device.AgentId);
            ReplaceItems(_events, events);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"加载事件失败: {ex.Message}";
        }
    }

    private async void CreateGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GroupNameTextBox.Text))
        {
            MessageBox.Show(this, "请输入分组名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _apiClient.CreateGroupAsync(GroupNameTextBox.Text.Trim());
            GroupNameTextBox.Clear();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "创建分组失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AssignGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesDataGrid.SelectedItem is not DeviceDto device)
        {
            MessageBox.Show(this, "请先选择设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (GroupsComboBox.SelectedItem is not DeviceGroupDto group)
        {
            MessageBox.Show(this, "请选择分组。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _apiClient.AssignGroupAsync(device.AgentId, group.Id);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "分组操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ClearGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesDataGrid.SelectedItem is not DeviceDto device)
        {
            MessageBox.Show(this, "请先选择设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _apiClient.AssignGroupAsync(device.AgentId, null);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "分组操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
