using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanAdmin.Console.Services;
using LanAdmin.Contracts;
using Microsoft.Win32;
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
            await RefreshReminderStyleAsync();
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
            SelectedDeviceTextBlock.Text = $"当前勾选设备: {checkedDevice.HostName} | IP: {checkedDevice.IpAddress} | 状态: {FormatDeviceStatus(checkedDevice.Status)} | 运行时长: {FormatUptime(checkedDevice.UptimeSeconds)}";
            CurrentDeviceGroupTextBlock.Text = $"{checkedDevice.HostName} 当前所属分组: {GetEffectiveGroupName(checkedDevice)} | 关机阈值: {checkedDevice.ShutdownThresholdDays} 天";
            return;
        }

        if (device is null)
        {
            SelectedDeviceTextBlock.Text = "当前未选中设备";
            CurrentDeviceGroupTextBlock.Text = "未选中设备";
            return;
        }

        SelectedDeviceTextBlock.Text = $"当前设备: {device.HostName} | IP: {device.IpAddress} | 状态: {FormatDeviceStatus(device.Status)} | 运行时长: {FormatUptime(device.UptimeSeconds)}";
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

    private async Task RefreshReminderStyleAsync()
    {
        try
        {
            var style = await _apiClient.GetReminderStyleAsync();
            ApplyReminderStyleToControls(style);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"加载弹窗样式失败: {ex.Message}";
        }
    }

    private async void SaveReminderStyleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildReminderStyleFromControls(out var style))
        {
            return;
        }

        try
        {
            var saved = await _apiClient.SaveReminderStyleAsync(style!);
            ApplyReminderStyleToControls(saved);
            StatusTextBlock.Text = $"弹窗样式已保存 {DateTime.Now:HH:mm:ss}";
            MessageBox.Show(this, "弹窗样式已保存，在线 Agent 会在下一次配置同步后使用新样式。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存弹窗样式失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ReloadReminderStyleButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshReminderStyleAsync();
    }

    private void PreviewReminderStyleButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryBuildReminderStyleFromControls(out var style))
        {
            RenderReminderStylePreview(style!);
        }
    }

    private async void UploadReminderBackgroundImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var result = await _apiClient.UploadReminderBackgroundImageAsync(dialog.FileName);
            if (string.IsNullOrWhiteSpace(result.Url))
            {
                MessageBox.Show(this, "背景图片上传成功，但服务端没有返回图片地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReminderBackgroundImageUrlTextBox.Text = result.Url;
            StatusTextBlock.Text = $"背景图片已上传 {DateTime.Now:HH:mm:ss}";
            MessageBox.Show(this, "背景图片已上传，请保存弹窗样式后下发给 Agent。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "上传背景图片失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetReminderStyleButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyReminderStyleToControls(ReminderStyleDefaults.CreateDefault());
    }

    private void ApplyReminderStyleToControls(ReminderStyleDto style)
    {
        ReminderTitleTextBox.Text = style.Title;
        ReminderContentTextBox.Text = style.ContentTemplate;
        ReminderButtonTextBox.Text = style.ButtonText;
        ReminderWidthTextBox.Text = style.Width.ToString(CultureInfo.InvariantCulture);
        ReminderHeightTextBox.Text = style.Height.ToString(CultureInfo.InvariantCulture);
        ReminderCornerRadiusTextBox.Text = style.CornerRadius.ToString(CultureInfo.InvariantCulture);
        ReminderBorderWidthTextBox.Text = style.BorderWidth.ToString(CultureInfo.InvariantCulture);
        ReminderBackgroundColorTextBox.Text = style.BackgroundColor;
        ReminderBorderColorTextBox.Text = style.BorderColor;
        ReminderButtonBackgroundColorTextBox.Text = style.ButtonBackgroundColor;
        ReminderBackgroundImageUrlTextBox.Text = style.BackgroundImageUrl;
        ReminderTopMostCheckBox.IsChecked = style.TopMost;
        ReminderTitleFontSizeTextBox.Text = style.TitleFontSize.ToString(CultureInfo.InvariantCulture);
        ReminderTitleColorTextBox.Text = style.TitleColor;
        ReminderContentFontSizeTextBox.Text = style.ContentFontSize.ToString(CultureInfo.InvariantCulture);
        ReminderContentColorTextBox.Text = style.ContentColor;
        ReminderButtonFontSizeTextBox.Text = style.ButtonFontSize.ToString(CultureInfo.InvariantCulture);
        ReminderButtonTextColorTextBox.Text = style.ButtonTextColor;

        SetComboBoxValue(ReminderPositionComboBox, style.Position);
        SetComboBoxValue(ReminderBackgroundImageLayoutComboBox, style.BackgroundImageLayout);
        SetComboBoxValue(ReminderIconTypeComboBox, style.IconType);
        SetComboBoxValue(ReminderTitleFontStyleComboBox, style.TitleFontStyle);
        SetComboBoxValue(ReminderContentFontStyleComboBox, style.ContentFontStyle);
        SetComboBoxValue(ReminderButtonFontStyleComboBox, style.ButtonFontStyle);
        RenderReminderStylePreview(style);
    }

    private bool TryBuildReminderStyleFromControls(out ReminderStyleDto? style)
    {
        style = null;

        if (!TryParseInt(ReminderWidthTextBox.Text, ReminderStyleDefaults.MinWidth, ReminderStyleDefaults.MaxWidth, "宽度", out var width) ||
            !TryParseInt(ReminderHeightTextBox.Text, ReminderStyleDefaults.MinHeight, ReminderStyleDefaults.MaxHeight, "高度", out var height) ||
            !TryParseInt(ReminderCornerRadiusTextBox.Text, ReminderStyleDefaults.MinCornerRadius, ReminderStyleDefaults.MaxCornerRadius, "圆角", out var cornerRadius) ||
            !TryParseInt(ReminderBorderWidthTextBox.Text, ReminderStyleDefaults.MinBorderWidth, ReminderStyleDefaults.MaxBorderWidth, "边框宽度", out var borderWidth) ||
            !TryParseDouble(ReminderTitleFontSizeTextBox.Text, ReminderStyleDefaults.MinFontSize, ReminderStyleDefaults.MaxFontSize, "标题字号", out var titleFontSize) ||
            !TryParseDouble(ReminderContentFontSizeTextBox.Text, ReminderStyleDefaults.MinFontSize, ReminderStyleDefaults.MaxFontSize, "正文字号", out var contentFontSize) ||
            !TryParseDouble(ReminderButtonFontSizeTextBox.Text, ReminderStyleDefaults.MinFontSize, ReminderStyleDefaults.MaxFontSize, "按钮字号", out var buttonFontSize))
        {
            return false;
        }

        style = ReminderStyleDefaults.CreateDefault();
        style.Title = ReminderTitleTextBox.Text.Trim();
        style.ContentTemplate = ReminderContentTextBox.Text.Trim();
        style.ButtonText = ReminderButtonTextBox.Text.Trim();
        style.Width = width;
        style.Height = height;
        style.Position = GetComboBoxValue(ReminderPositionComboBox, "BottomRight");
        style.CornerRadius = cornerRadius;
        style.BorderWidth = borderWidth;
        style.BorderColor = ReminderBorderColorTextBox.Text.Trim();
        style.BackgroundColor = ReminderBackgroundColorTextBox.Text.Trim();
        style.BackgroundImageUrl = ReminderBackgroundImageUrlTextBox.Text.Trim();
        style.BackgroundImageLayout = GetComboBoxValue(ReminderBackgroundImageLayoutComboBox, "Zoom");
        style.IconType = GetComboBoxValue(ReminderIconTypeComboBox, "Warning");
        style.TitleFontSize = titleFontSize;
        style.TitleFontStyle = GetComboBoxValue(ReminderTitleFontStyleComboBox, "Bold");
        style.TitleColor = ReminderTitleColorTextBox.Text.Trim();
        style.ContentFontSize = contentFontSize;
        style.ContentFontStyle = GetComboBoxValue(ReminderContentFontStyleComboBox, "Regular");
        style.ContentColor = ReminderContentColorTextBox.Text.Trim();
        style.ButtonFontSize = buttonFontSize;
        style.ButtonFontStyle = GetComboBoxValue(ReminderButtonFontStyleComboBox, "Bold");
        style.ButtonTextColor = ReminderButtonTextColorTextBox.Text.Trim();
        style.ButtonBackgroundColor = ReminderButtonBackgroundColorTextBox.Text.Trim();
        style.TopMost = ReminderTopMostCheckBox.IsChecked == true;
        return true;
    }

    private static bool TryParseInt(string rawValue, int min, int max, string displayName, out int value)
    {
        if (!int.TryParse(rawValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
            value < min ||
            value > max)
        {
            MessageBox.Show($"{displayName}必须在 {min} 到 {max} 之间。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private static bool TryParseDouble(string rawValue, double min, double max, string displayName, out double value)
    {
        if (!double.TryParse(rawValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            value < min ||
            value > max)
        {
            MessageBox.Show($"{displayName}必须在 {min.ToString(CultureInfo.InvariantCulture)} 到 {max.ToString(CultureInfo.InvariantCulture)} 之间。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private static void SetComboBoxValue(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            var itemValue = item.Tag?.ToString() ?? item.Content?.ToString();
            if (string.Equals(itemValue, value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string GetComboBoxValue(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? item.Content?.ToString() ?? fallback
            : fallback;
    }

    private void RenderReminderStylePreview(ReminderStyleDto style)
    {
        ReminderPreviewBorder.Width = style.Width;
        ReminderPreviewBorder.Height = style.Height;
        ReminderPreviewBorder.CornerRadius = new CornerRadius(style.CornerRadius);
        ReminderPreviewBorder.BorderThickness = new Thickness(style.BorderWidth);
        ReminderPreviewBorder.BorderBrush = ParseBrush(style.BorderColor, "#DC2626");
        ReminderPreviewBorder.Background = CreatePreviewBackground(style);
        RenderPreviewIcon(style);

        ReminderPreviewTitleTextBlock.Text = style.Title;
        ReminderPreviewTitleTextBlock.FontFamily = new FontFamily(style.TitleFontFamily);
        ReminderPreviewTitleTextBlock.FontSize = style.TitleFontSize;
        ApplyTextBlockFontStyle(ReminderPreviewTitleTextBlock, style.TitleFontStyle);
        ReminderPreviewTitleTextBlock.Foreground = ParseBrush(style.TitleColor, "#B91C1C");

        ReminderPreviewContentTextBlock.Text = BuildPreviewContent(style.ContentTemplate);
        ReminderPreviewContentTextBlock.FontFamily = new FontFamily(style.ContentFontFamily);
        ReminderPreviewContentTextBlock.FontSize = style.ContentFontSize;
        ApplyTextBlockFontStyle(ReminderPreviewContentTextBlock, style.ContentFontStyle);
        ReminderPreviewContentTextBlock.Foreground = ParseBrush(style.ContentColor, "#7F1D1D");

        ReminderPreviewButton.Content = style.ButtonText;
        ReminderPreviewButton.FontFamily = new FontFamily(style.ButtonFontFamily);
        ReminderPreviewButton.FontSize = style.ButtonFontSize;
        ApplyControlFontStyle(ReminderPreviewButton, style.ButtonFontStyle);
        ReminderPreviewButton.Foreground = ParseBrush(style.ButtonTextColor, "#FFFFFF");
        ReminderPreviewButton.Background = ParseBrush(style.ButtonBackgroundColor, "#DC2626");
        ReminderPreviewButton.BorderBrush = ParseBrush(style.BorderColor, "#DC2626");
    }

    private void RenderPreviewIcon(ReminderStyleDto style)
    {
        if (string.Equals(style.IconType, "None", StringComparison.OrdinalIgnoreCase))
        {
            ReminderPreviewIconColumn.Width = new GridLength(0);
            ReminderPreviewIconTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        ReminderPreviewIconColumn.Width = new GridLength(52);
        ReminderPreviewIconTextBlock.Visibility = Visibility.Visible;
        ReminderPreviewIconTextBlock.Text = style.IconType.Trim().ToUpperInvariant() switch
        {
            "INFORMATION" => "ℹ",
            "ERROR" => "✕",
            "SUCCESS" => "✓",
            _ => "⚠"
        };
        ReminderPreviewIconTextBlock.Foreground = ParseBrush(style.BorderColor, "#DC2626");
    }

    private Brush CreatePreviewBackground(ReminderStyleDto style)
    {
        var fallback = ParseBrush(style.BackgroundColor, "#FFFFFF");
        var imageUrl = style.BackgroundImageUrl?.Trim();
        if (string.IsNullOrWhiteSpace(imageUrl) || !TryBuildPreviewImageUri(imageUrl, out var imageUri))
        {
            return fallback;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = imageUri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            return new ImageBrush(bitmap)
            {
                Stretch = ParseStretch(style.BackgroundImageLayout),
                TileMode = string.Equals(style.BackgroundImageLayout, "Tile", StringComparison.OrdinalIgnoreCase)
                    ? TileMode.Tile
                    : TileMode.None,
                Viewport = string.Equals(style.BackgroundImageLayout, "Tile", StringComparison.OrdinalIgnoreCase)
                    ? new Rect(0, 0, 128, 128)
                    : new Rect(0, 0, 1, 1),
                ViewportUnits = string.Equals(style.BackgroundImageLayout, "Tile", StringComparison.OrdinalIgnoreCase)
                    ? BrushMappingMode.Absolute
                    : BrushMappingMode.RelativeToBoundingBox
            };
        }
        catch
        {
            return fallback;
        }
    }

    private bool TryBuildPreviewImageUri(string imageUrl, out Uri imageUri)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out imageUri!))
        {
            return imageUri.Scheme is "http" or "https" or "file";
        }

        if (_apiClient.BaseAddress is null)
        {
            return false;
        }

        return Uri.TryCreate(_apiClient.BaseAddress, imageUrl.TrimStart('/'), out imageUri!);
    }

    private static Brush ParseBrush(string value, string fallback)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
        }
        catch
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)!);
        }
    }

    private static Stretch ParseStretch(string value)
    {
        return value.Trim().ToUpperInvariant() switch
        {
            "STRETCH" => Stretch.Fill,
            "CENTER" or "NONE" or "TILE" => Stretch.None,
            _ => Stretch.Uniform
        };
    }

    private static void ApplyTextBlockFontStyle(TextBlock textBlock, string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        textBlock.FontWeight = normalized is "BOLD" or "BOLDITALIC" ? FontWeights.Bold : FontWeights.Normal;
        textBlock.FontStyle = normalized is "ITALIC" or "BOLDITALIC" ? FontStyles.Italic : FontStyles.Normal;
    }

    private static void ApplyControlFontStyle(Control control, string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        control.FontWeight = normalized is "BOLD" or "BOLDITALIC" ? FontWeights.Bold : FontWeights.Normal;
        control.FontStyle = normalized is "ITALIC" or "BOLDITALIC" ? FontStyles.Italic : FontStyles.Normal;
    }

    private static string BuildPreviewContent(string template)
    {
        return template
            .Replace("{uptime}", "8天 3小时 15分钟", StringComparison.OrdinalIgnoreCase)
            .Replace("{hostName}", Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            .Replace("{currentUser}", Environment.UserName, StringComparison.OrdinalIgnoreCase)
            .Replace("{thresholdDays}", ShutdownThresholdDefaults.DefaultDays.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
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

    private static string FormatDeviceStatus(DeviceStatus status)
    {
        return status == DeviceStatus.Online ? "在线" : "离线";
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

        public string StatusDisplay => FormatDeviceStatus(Device.Status);

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

        public bool HasDevices => Devices.Count > 0;

        public Visibility DevicesVisibility => HasDevices ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EmptyStateVisibility => HasDevices ? Visibility.Collapsed : Visibility.Visible;

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
