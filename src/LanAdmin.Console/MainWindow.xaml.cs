using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LanAdmin.Console.Services;
using LanAdmin.Contracts;
using MessageBox = System.Windows.MessageBox;

namespace LanAdmin.Console;

public partial class MainWindow : Window
{
    private const string UngroupedName = "未分组";
    private static readonly TimeZoneInfo BeijingTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    private readonly ServerApiClient _apiClient;
    private readonly ObservableCollection<DeviceGroupViewModel> _deviceGroups = new();
    private readonly ObservableCollection<DeviceEventDto> _events = new();
    private readonly ObservableCollection<DeviceGroupDto> _groups = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, bool> _groupExpansionStates = new(StringComparer.OrdinalIgnoreCase);

    private List<DeviceDto> _allDevices = [];
    private string? _selectedAgentId;

    public MainWindow()
    {
        InitializeComponent();

        _apiClient = new ServerApiClient();

        DeviceGroupsItemsControl.ItemsSource = _deviceGroups;
        EventsDataGrid.ItemsSource = _events;
        GroupsComboBox.ItemsSource = _groups;
        ManageGroupsListBox.ItemsSource = _groups;

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

            var groups = (await _apiClient.GetGroupsAsync())
                .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            ReplaceItems(_groups, groups);

            _allDevices = (await _apiClient.GetDevicesAsync(null))
                .Select(ToBeijingDevice)
                .OrderBy(device => device.HostName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.AgentId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            RebuildDeviceGroups(SearchTextBox.Text);
            UpdateSelectedDeviceDisplay();
            await RefreshEventsAsync();

            StatusTextBlock.Text = $"上次刷新 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"加载失败: {ex.Message}";
        }
    }

    private async Task RefreshEventsAsync()
    {
        var selectedDevice = GetSelectedDevice();
        if (selectedDevice is not null)
        {
            var events = (await _apiClient.GetEventsAsync(selectedDevice.AgentId))
                .Select(ToBeijingEvent);
            ReplaceItems(_events, events);
            return;
        }

        _selectedAgentId = null;
        var allEvents = (await _apiClient.GetEventsAsync(null))
            .Select(ToBeijingEvent);
        ReplaceItems(_events, allEvents);
    }

    private void RebuildDeviceGroups(string? rawSearchText)
    {
        var searchText = rawSearchText?.Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(searchText);

        var sections = new List<DeviceGroupViewModel>();

        var ungroupedSection = BuildDeviceGroup(null, UngroupedName, hasSearch, searchText);
        if (ungroupedSection is not null)
        {
            sections.Add(ungroupedSection);
        }

        foreach (var group in _groups)
        {
            var section = BuildDeviceGroup(group.Id, group.Name, hasSearch, searchText);
            if (section is not null)
            {
                sections.Add(section);
            }
        }

        ReplaceItems(_deviceGroups, sections);
    }

    private DeviceGroupViewModel? BuildDeviceGroup(long? groupId, string groupName, bool hasSearch, string? searchText)
    {
        var allGroupDevices = _allDevices
            .Where(device => string.Equals(GetEffectiveGroupName(device), groupName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(device => device.HostName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groupMatchesSearch = hasSearch && MatchesSearch(groupName, searchText!);
        var visibleDevices = !hasSearch
            ? allGroupDevices
            : groupMatchesSearch
                ? allGroupDevices
                : allGroupDevices.Where(device => DeviceMatchesSearch(device, searchText!)).ToList();

        if (hasSearch && !groupMatchesSearch && visibleDevices.Count == 0)
        {
            return null;
        }

        var key = groupId.HasValue ? $"group:{groupId.Value}" : "group:ungrouped";
        var isExpanded = hasSearch
            ? groupMatchesSearch || visibleDevices.Count > 0
            : _groupExpansionStates.GetValueOrDefault(key, true);

        return new DeviceGroupViewModel(
            key,
            groupId,
            groupName,
            isExpanded,
            visibleDevices);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void GroupDevicesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid { SelectedItem: DeviceDto device })
        {
            return;
        }

        _selectedAgentId = device.AgentId;
        UpdateSelectedDeviceDisplay();

        try
        {
            var events = (await _apiClient.GetEventsAsync(device.AgentId))
                .Select(ToBeijingEvent);
            ReplaceItems(_events, events);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"加载事件失败: {ex.Message}";
        }
    }

    private async void CreateGroupButton_Click(object sender, RoutedEventArgs e)
    {
        var name = GroupNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请输入分组名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _apiClient.CreateGroupAsync(name);
            GroupNameTextBox.Clear();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "创建分组失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ManageGroupsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ManageGroupsListBox.SelectedItem is DeviceGroupDto group)
        {
            EditGroupNameTextBox.Text = group.Name;
        }
    }

    private async void RenameGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManageGroupsListBox.SelectedItem is not DeviceGroupDto group)
        {
            MessageBox.Show(this, "请先选择分组。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newName = EditGroupNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            MessageBox.Show(this, "请输入新的分组名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _apiClient.RenameGroupAsync(group.Id, newName);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重命名分组失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManageGroupsListBox.SelectedItem is not DeviceGroupDto group)
        {
            MessageBox.Show(this, "请先选择分组。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var affectedDeviceCount = _allDevices.Count(device => string.Equals(device.GroupName, group.Name, StringComparison.OrdinalIgnoreCase));
        var result = MessageBox.Show(
            this,
            $"确认删除分组 {group.Name} 吗？\n该分组下 {affectedDeviceCount} 台设备将自动移到“{UngroupedName}”。",
            "删除分组",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _apiClient.DeleteGroupAsync(group.Id);
            EditGroupNameTextBox.Clear();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "删除分组失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AssignGroupButton_Click(object sender, RoutedEventArgs e)
    {
        var device = GetSelectedDevice();
        if (device is null)
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
        var device = GetSelectedDevice();
        if (device is null)
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

    private async void DeleteDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var device = GetSelectedDevice();
        if (device is null)
        {
            MessageBox.Show(this, "请先选择设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"确认删除设备 {device.HostName} ({device.AgentId}) 吗？\n这会同时删除该设备的事件记录。",
            "删除设备",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _apiClient.DeleteDeviceAsync(device.AgentId);
            _selectedAgentId = null;
            _events.Clear();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "删除设备失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeviceGroupExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { DataContext: DeviceGroupViewModel group })
        {
            _groupExpansionStates[group.Key] = true;
        }
    }

    private void DeviceGroupExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { DataContext: DeviceGroupViewModel group })
        {
            _groupExpansionStates[group.Key] = false;
        }
    }

    private void UpdateSelectedDeviceDisplay()
    {
        var device = GetSelectedDevice();
        if (device is null)
        {
            SelectedDeviceTextBlock.Text = "当前未选中设备";
            CurrentDeviceGroupTextBlock.Text = "未选中设备";
            return;
        }

        SelectedDeviceTextBlock.Text = $"当前设备: {device.HostName} | IP: {device.IpAddress} | 状态: {device.Status}";
        CurrentDeviceGroupTextBlock.Text = $"{device.HostName} 当前所属分组: {GetEffectiveGroupName(device)}";
    }

    private DeviceDto? GetSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(_selectedAgentId))
        {
            return null;
        }

        return _allDevices.FirstOrDefault(device => string.Equals(device.AgentId, _selectedAgentId, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetEffectiveGroupName(DeviceDto device)
    {
        return string.IsNullOrWhiteSpace(device.GroupName) ? UngroupedName : device.GroupName;
    }

    private static bool DeviceMatchesSearch(DeviceDto device, string searchText)
    {
        return MatchesSearch(device.HostName, searchText) ||
               MatchesSearch(device.AgentId, searchText) ||
               MatchesSearch(device.IpAddress, searchText) ||
               MatchesSearch(device.MacAddress, searchText) ||
               MatchesSearch(device.CurrentUser, searchText) ||
               MatchesSearch(device.AgentVersion, searchText);
    }

    private static bool MatchesSearch(string? value, string searchText)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private static DeviceDto ToBeijingDevice(DeviceDto device)
    {
        return device with
        {
            LastSeenAt = TimeZoneInfo.ConvertTime(device.LastSeenAt, BeijingTimeZone)
        };
    }

    private static DeviceEventDto ToBeijingEvent(DeviceEventDto deviceEvent)
    {
        return deviceEvent with
        {
            OccurredAt = TimeZoneInfo.ConvertTime(deviceEvent.OccurredAt, BeijingTimeZone)
        };
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private sealed class DeviceGroupViewModel
    {
        public DeviceGroupViewModel(string key, long? groupId, string displayName, bool isExpanded, IReadOnlyList<DeviceDto> devices)
        {
            Key = key;
            GroupId = groupId;
            DisplayName = displayName;
            IsExpanded = isExpanded;
            Devices = new ObservableCollection<DeviceDto>(devices);

            var onlineCount = devices.Count(device => device.Status == DeviceStatus.Online);
            var offlineCount = devices.Count - onlineCount;
            Summary = $"共 {devices.Count} 台 | 在线 {onlineCount} | 离线 {offlineCount}";
        }

        public string Key { get; }

        public long? GroupId { get; }

        public string DisplayName { get; }

        public string Summary { get; }

        public ObservableCollection<DeviceDto> Devices { get; }

        public bool IsExpanded { get; set; }
    }
}
