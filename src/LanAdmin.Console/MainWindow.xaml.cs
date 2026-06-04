using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private readonly HashSet<string> _checkedAgentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceGroupViewModel> _deviceGroupIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _isRestoringGroupUiState;
    private bool _isRefreshingDeviceGroups;
    private bool _isSynchronizingSelection;

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
            var selectedAssignGroupId = (GroupsComboBox.SelectedItem as DeviceGroupDto)?.Id;
            var selectedManageGroupId = (ManageGroupsListBox.SelectedItem as DeviceGroupDto)?.Id;
            var newGroupNameText = GroupNameTextBox.Text;
            var editGroupNameText = EditGroupNameTextBox.Text;
            var shutdownThresholdText = ShutdownThresholdTextBox.Text;

            var groups = (await _apiClient.GetGroupsAsync())
                .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            ReplaceItems(_groups, groups);
            RestoreGroupUiState(selectedAssignGroupId, selectedManageGroupId, newGroupNameText, editGroupNameText);
            ShutdownThresholdTextBox.Text = shutdownThresholdText;

            _allDevices = (await _apiClient.GetDevicesAsync(null))
                .Select(ToBeijingDevice)
                .OrderByDescending(device => device.Status == DeviceStatus.Online)
                .ThenBy(device => device.HostName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.AgentId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _checkedAgentIds.IntersectWith(_allDevices.Select(device => device.AgentId));

            _isRefreshingDeviceGroups = true;
            RebuildDeviceGroups(SearchTextBox.Text);
            RestoreSelectedDeviceState(refreshEvents: false);
            _isRefreshingDeviceGroups = false;
            UpdateSelectedDeviceDisplay();
            await RefreshEventsAsync();

            StatusTextBlock.Text = $"上次刷新 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"加载失败: {ex.Message}";
        }
    }

    private void RestoreGroupUiState(long? selectedAssignGroupId, long? selectedManageGroupId, string newGroupNameText, string editGroupNameText)
    {
        _isRestoringGroupUiState = true;
        try
        {
            GroupsComboBox.SelectedItem = selectedAssignGroupId.HasValue
                ? _groups.FirstOrDefault(group => group.Id == selectedAssignGroupId.Value)
                : null;

            ManageGroupsListBox.SelectedItem = selectedManageGroupId.HasValue
                ? _groups.FirstOrDefault(group => group.Id == selectedManageGroupId.Value)
                : null;
        }
        finally
        {
            _isRestoringGroupUiState = false;
        }

        GroupNameTextBox.Text = newGroupNameText;

        if (ManageGroupsListBox.SelectedItem is null && !selectedManageGroupId.HasValue)
        {
            EditGroupNameTextBox.Text = editGroupNameText;
        }
        else if (ManageGroupsListBox.SelectedItem is not null)
        {
            EditGroupNameTextBox.Text = editGroupNameText;
        }
        else
        {
            EditGroupNameTextBox.Clear();
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

        _deviceGroupIndex.Clear();
        foreach (var section in sections)
        {
            _deviceGroupIndex[section.Key] = section;
        }

        ReplaceItems(_deviceGroups, sections);
    }

    private DeviceGroupViewModel? BuildDeviceGroup(long? groupId, string groupName, bool hasSearch, string? searchText)
    {
        var allGroupDevices = _allDevices
            .Where(device => string.Equals(GetEffectiveGroupName(device), groupName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(device => device.Status == DeviceStatus.Online)
            .ThenBy(device => device.HostName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.AgentId, StringComparer.OrdinalIgnoreCase)
            .Select(device => new DeviceRowViewModel(
                $"group:{groupId?.ToString() ?? "ungrouped"}",
                device,
                _checkedAgentIds.Contains(device.AgentId),
                string.Equals(_selectedAgentId, device.AgentId, StringComparison.OrdinalIgnoreCase),
                HandleDeviceCheckedChanged,
                HandleDeviceSelectionChanged))
            .ToList();

        var groupMatchesSearch = hasSearch && MatchesSearch(groupName, searchText!);
        List<DeviceRowViewModel> visibleDevices = !hasSearch
            ? allGroupDevices
            : groupMatchesSearch
                ? allGroupDevices
                : allGroupDevices.Where(device => DeviceMatchesSearch(device.Device, searchText!)).ToList();

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

    private async void CreateGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetValidatedGroupName(GroupNameTextBox.Text, out var name))
        {
            return;
        }

        try
        {
            await _apiClient.CreateGroupAsync(name!);
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
        if (_isRestoringGroupUiState)
        {
            return;
        }

        if (ManageGroupsListBox.SelectedItem is DeviceGroupDto group)
        {
            EditGroupNameTextBox.Text = group.Name;
        }
        else
        {
            EditGroupNameTextBox.Clear();
        }
    }

    private async void RenameGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManageGroupsListBox.SelectedItem is not DeviceGroupDto group)
        {
            MessageBox.Show(this, "请先选择分组。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetValidatedGroupName(EditGroupNameTextBox.Text, out var newName))
        {
            return;
        }

        try
        {
            await _apiClient.RenameGroupAsync(group.Id, newName!);
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
        var selectedAgentIds = GetCheckedAgentIds();
        if (selectedAgentIds.Count == 0)
        {
            MessageBox.Show(this, "请先选择至少一台设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (GroupsComboBox.SelectedItem is not DeviceGroupDto group)
        {
            MessageBox.Show(this, "请选择分组。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _apiClient.AssignGroupsAsync(selectedAgentIds, group.Id);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "分组操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ClearGroupButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedAgentIds = GetCheckedAgentIds();
        if (selectedAgentIds.Count == 0)
        {
            MessageBox.Show(this, "请先选择至少一台设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _apiClient.AssignGroupsAsync(selectedAgentIds, null);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "分组操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        var checkedDevices = GetCheckedDevices();
        if (checkedDevices.Count == 0)
        {
            MessageBox.Show(this, "请先勾选至少一台设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var prompt = checkedDevices.Count == 1
            ? $"确认删除设备 {checkedDevices[0].HostName} ({checkedDevices[0].AgentId}) 吗？\n这会同时删除该设备的事件记录。"
            : $"确认删除已勾选的 {checkedDevices.Count} 台设备吗？\n这会同时删除这些设备的事件记录。";
        var result = MessageBox.Show(this, prompt, "删除设备", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var device in checkedDevices)
            {
                await _apiClient.DeleteDeviceAsync(device.AgentId);
            }

            foreach (var device in checkedDevices)
            {
                _checkedAgentIds.Remove(device.AgentId);
            }

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
        var checkedDevices = GetCheckedDevices();
        SelectedDeviceCountTextBlock.Text = $"当前已勾选 {checkedDevices.Count} 台设备";

        var device = GetSelectedDevice();
        if (checkedDevices.Count == 0 && device is null)
        {
            SelectedDeviceTextBlock.Text = "当前未选中设备";
            CurrentDeviceGroupTextBlock.Text = "未选中设备";
            return;
        }

        if (checkedDevices.Count > 1)
        {
            SelectedDeviceTextBlock.Text = $"当前已勾选 {checkedDevices.Count} 台设备";
            CurrentDeviceGroupTextBlock.Text = "已勾选多台设备，可跨分组批量加入或移出分组";
            return;
        }

        if (checkedDevices.Count == 1)
        {
            var checkedDevice = checkedDevices[0];
            SelectedDeviceTextBlock.Text = $"当前勾选设备: {checkedDevice.HostName} | IP: {checkedDevice.IpAddress} | 状态: {checkedDevice.Status} | 运行时长: {FormatUptime(checkedDevice.UptimeSeconds)}";
            CurrentDeviceGroupTextBlock.Text = $"{checkedDevice.HostName} 当前所属分组: {GetEffectiveGroupName(checkedDevice)} | 关机阈值: {checkedDevice.ShutdownThresholdDays} 天";
            return;
        }

        if (device is null)
        {
            SelectedDeviceTextBlock.Text = "当前未选中设备";
            CurrentDeviceGroupTextBlock.Text = "未选中设备";
            return;
        }

        SelectedDeviceTextBlock.Text = $"当前设备: {device.HostName} | IP: {device.IpAddress} | 状态: {device.Status} | 运行时长: {FormatUptime(device.UptimeSeconds)}";
        CurrentDeviceGroupTextBlock.Text = $"{device.HostName} 当前所属分组: {GetEffectiveGroupName(device)} | 关机阈值: {device.ShutdownThresholdDays} 天";
    }

    private DeviceDto? GetSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(_selectedAgentId))
        {
            return null;
        }

        return _allDevices.FirstOrDefault(device => string.Equals(device.AgentId, _selectedAgentId, StringComparison.OrdinalIgnoreCase));
    }

    private List<string> GetCheckedAgentIds()
    {
        return _checkedAgentIds
            .Where(agentId => _allDevices.Any(device => string.Equals(device.AgentId, agentId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private List<DeviceDto> GetCheckedDevices()
    {
        return _allDevices
            .Where(device => _checkedAgentIds.Contains(device.AgentId))
            .ToList();
    }

    private List<DeviceDto> GetDevicesForThresholdOperation()
    {
        var checkedDevices = GetCheckedDevices();
        if (checkedDevices.Count > 0)
        {
            return checkedDevices;
        }

        var selectedDevice = GetSelectedDevice();
        return selectedDevice is null ? [] : [selectedDevice];
    }

    private List<DeviceDto> GetDevicesForManualReminderOperation()
    {
        return GetDevicesForThresholdOperation();
    }

    private static bool TryGetValidatedGroupName(string rawText, out string? validatedName)
    {
        validatedName = rawText.Trim();
        if (string.IsNullOrWhiteSpace(validatedName))
        {
            MessageBox.Show("请输入分组名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            validatedName = null;
            return false;
        }

        if (validatedName.Length > 64)
        {
            MessageBox.Show("分组名称长度不能超过 64 个字符。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            validatedName = null;
            return false;
        }

        if (validatedName.Any(char.IsControl) || validatedName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            MessageBox.Show("分组名称包含非法字符。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            validatedName = null;
            return false;
        }

        return true;
    }

    private bool TryGetValidatedShutdownThresholdDays(out int shutdownThresholdDays)
    {
        shutdownThresholdDays = 0;
        var rawText = ShutdownThresholdTextBox.Text.Trim();
        if (!int.TryParse(rawText, out shutdownThresholdDays))
        {
            MessageBox.Show(this, "请输入有效的关机阈值天数。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (shutdownThresholdDays < ShutdownThresholdDefaults.MinDays || shutdownThresholdDays > ShutdownThresholdDefaults.MaxDays)
        {
            MessageBox.Show(
                this,
                $"关机阈值必须在 {ShutdownThresholdDefaults.MinDays} 到 {ShutdownThresholdDefaults.MaxDays} 天之间。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private async void SetShutdownThresholdButton_Click(object sender, RoutedEventArgs e)
    {
        var devices = GetDevicesForThresholdOperation();
        if (devices.Count == 0)
        {
            MessageBox.Show(this, "请先勾选设备，或单击选中一台设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetValidatedShutdownThresholdDays(out var shutdownThresholdDays))
        {
            return;
        }

        try
        {
            if (devices.Count == 1)
            {
                await _apiClient.SetShutdownThresholdAsync(devices[0].AgentId, shutdownThresholdDays);
            }
            else
            {
                await _apiClient.SetShutdownThresholdsAsync(devices.Select(device => device.AgentId).ToList(), shutdownThresholdDays);
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "设置关机阈值失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PromptShutdownReminderButton_Click(object sender, RoutedEventArgs e)
    {
        var devices = GetDevicesForManualReminderOperation();
        if (devices.Count == 0)
        {
            MessageBox.Show(this, "请先勾选设备，或单击选中一台设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = await _apiClient.PromptShutdownReminderAsync(devices.Select(device => device.AgentId).ToList());
            await RefreshAsync();

            if (result.SentCount == 0)
            {
                MessageBox.Show(this, "选中的设备当前均不在线，未发送关机提醒。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (result.OfflineAgentIds.Count == 0)
            {
                MessageBox.Show(this, $"已向 {result.SentCount} 台在线设备发送关机提醒。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBox.Show(
                this,
                $"已向 {result.SentCount} 台在线设备发送关机提醒，{result.OfflineAgentIds.Count} 台设备未发送（当前离线）。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "发送关机提醒失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetShutdownThresholdButton_Click(object sender, RoutedEventArgs e)
    {
        ShutdownThresholdTextBox.Text = ShutdownThresholdDefaults.DefaultDays.ToString();
        SetShutdownThresholdButton_Click(sender, e);
    }

    private void HandleDeviceCheckedChanged(DeviceRowViewModel deviceRow)
    {
        if (deviceRow.IsChecked)
        {
            _checkedAgentIds.Add(deviceRow.AgentId);
        }
        else
        {
            _checkedAgentIds.Remove(deviceRow.AgentId);
        }

        UpdateSelectedDeviceDisplay();
    }

    private void HandleDeviceSelectionChanged(DeviceRowViewModel deviceRow, bool isSelected)
    {
        if (_isSynchronizingSelection || _isRefreshingDeviceGroups)
        {
            return;
        }

        if (isSelected)
        {
            SetSelectedAgentId(deviceRow.AgentId);
            RefreshEventsForCurrentSelection();
            return;
        }

        if (string.Equals(_selectedAgentId, deviceRow.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            SetSelectedAgentId(null);
            RefreshEventsForCurrentSelection();
        }
    }

    private void SetSelectedAgentId(string? agentId)
    {
        var selectedRows = _deviceGroups
            .SelectMany(group => group.Devices)
            .Where(device => device.IsSelected)
            .ToList();
        var selectionAlreadyApplied =
            (!string.IsNullOrWhiteSpace(agentId) &&
             selectedRows.Count == 1 &&
             string.Equals(selectedRows[0].AgentId, agentId, StringComparison.OrdinalIgnoreCase)) ||
            (string.IsNullOrWhiteSpace(agentId) && selectedRows.Count == 0);

        if (string.Equals(_selectedAgentId, agentId, StringComparison.OrdinalIgnoreCase) && selectionAlreadyApplied)
        {
            UpdateSelectedDeviceDisplay();
            return;
        }

        _isSynchronizingSelection = true;
        try
        {
            _selectedAgentId = agentId;

            foreach (var device in _deviceGroups.SelectMany(group => group.Devices))
            {
                device.SetSelectedSilently(
                    !string.IsNullOrWhiteSpace(agentId) &&
                    string.Equals(device.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        UpdateSelectedDeviceDisplay();
    }

    private void RestoreSelectedDeviceState(bool refreshEvents)
    {
        var restoredAgentId = _allDevices.Any(device =>
            string.Equals(device.AgentId, _selectedAgentId, StringComparison.OrdinalIgnoreCase))
            ? _selectedAgentId
            : null;

        SetSelectedAgentId(restoredAgentId);

        if (refreshEvents)
        {
            RefreshEventsForCurrentSelection();
        }
    }

    private async void RefreshEventsForCurrentSelection()
    {
        try
        {
            await RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"加载事件失败: {ex.Message}";
        }
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

    private static string FormatUptime(long uptimeSeconds)
    {
        var uptime = TimeSpan.FromSeconds(Math.Max(0, uptimeSeconds));
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}天 {uptime.Hours}小时";
        }

        if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours}小时 {uptime.Minutes}分钟";
        }

        return $"{Math.Max(0, (int)uptime.TotalMinutes)}分钟";
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

    private sealed class DeviceRowViewModel : INotifyPropertyChanged
    {
        private readonly Action<DeviceRowViewModel> _checkedChanged;
        private readonly Action<DeviceRowViewModel, bool> _selectionChanged;
        private bool _isChecked;
        private bool _isSelected;

        public DeviceRowViewModel(
            string groupKey,
            DeviceDto device,
            bool isChecked,
            bool isSelected,
            Action<DeviceRowViewModel> checkedChanged,
            Action<DeviceRowViewModel, bool> selectionChanged)
        {
            GroupKey = groupKey;
            Device = device;
            _isChecked = isChecked;
            _isSelected = isSelected;
            _checkedChanged = checkedChanged;
            _selectionChanged = selectionChanged;
        }

        public string GroupKey { get; }

        public DeviceDto Device { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }

                _isChecked = value;
                OnPropertyChanged();
                _checkedChanged(this);
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
                _selectionChanged(this, value);
            }
        }

        public string AgentId => Device.AgentId;

        public string HostName => Device.HostName;

        public DeviceStatus Status => Device.Status;

        public string IpAddress => Device.IpAddress;

        public string MacAddress => Device.MacAddress;

        public string CurrentUser => Device.CurrentUser;

        public DateTimeOffset LastSeenAt => Device.LastSeenAt;

        public string AgentVersion => Device.AgentVersion;

        public string UptimeDisplay => FormatUptime(Device.UptimeSeconds);

        public string ShutdownThresholdDisplay => $"{Device.ShutdownThresholdDays} 天";

        public void SetSelectedSilently(bool value)
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class DeviceGroupViewModel : INotifyPropertyChanged
    {
        private bool _areAllDevicesChecked;
        private bool _isSynchronizingCheckedState;

        public DeviceGroupViewModel(string key, long? groupId, string displayName, bool isExpanded, IReadOnlyList<DeviceRowViewModel> devices)
        {
            Key = key;
            GroupId = groupId;
            DisplayName = displayName;
            IsExpanded = isExpanded;
            Devices = new ObservableCollection<DeviceRowViewModel>(devices);

            foreach (var device in Devices)
            {
                device.PropertyChanged += Device_PropertyChanged;
            }

            var onlineCount = devices.Count(device => device.Status == DeviceStatus.Online);
            var offlineCount = devices.Count - onlineCount;
            Summary = $"共 {devices.Count} 台 | 在线 {onlineCount} | 离线 {offlineCount}";
            RefreshSelectionState();
        }

        public string Key { get; }

        public long? GroupId { get; }

        public string DisplayName { get; }

        public string Summary { get; }

        public ObservableCollection<DeviceRowViewModel> Devices { get; }

        public bool IsExpanded { get; set; }

        public bool AreAllDevicesChecked
        {
            get => _areAllDevicesChecked;
            set
            {
                if (_areAllDevicesChecked == value)
                {
                    return;
                }

                SetAreAllDevicesChecked(value);
            }
        }

        public void RefreshSelectionState()
        {
            SetAreAllDevicesChecked(Devices.Count > 0 && Devices.All(device => device.IsChecked), updateChildren: false);
        }

        private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DeviceRowViewModel.IsChecked))
            {
                RefreshSelectionState();
            }
        }

        private void SetAreAllDevicesChecked(bool value, bool updateChildren = true)
        {
            if (_isSynchronizingCheckedState)
            {
                _areAllDevicesChecked = value;
                OnPropertyChanged(nameof(AreAllDevicesChecked));
                return;
            }

            _isSynchronizingCheckedState = true;
            try
            {
                _areAllDevicesChecked = value;
                OnPropertyChanged(nameof(AreAllDevicesChecked));

                if (updateChildren)
                {
                    foreach (var device in Devices)
                    {
                        device.IsChecked = value;
                    }
                }
            }
            finally
            {
                _isSynchronizingCheckedState = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
