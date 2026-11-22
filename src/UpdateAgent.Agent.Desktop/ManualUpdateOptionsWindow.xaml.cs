using System.Windows;
using System.Windows.Controls;
using UpdateAgent.Core.Windows;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Agent.Desktop;

/// <summary>학생이 수동 업데이트를 시작하기 전 프리셋과 세부 옵션을 고르는 창.</summary>
public partial class ManualUpdateOptionsWindow : Window
{
    private readonly OsInfo _os;
    private readonly UpdatePolicy _teacherPolicy;
    private bool _applyingPreset;

    public ManualUpdateOptions Options { get; private set; } = new();

    public bool MaestroConfirmed => MaestroCheck.IsChecked == true;

    public ManualUpdateOptionsWindow(UpdatePolicy teacherPolicy)
    {
        InitializeComponent();

        _os = WindowsOsInfoProvider.GetCurrent();
        _teacherPolicy = teacherPolicy.SanitizeFor(_os);
        OsText.Text = $"이 PC: {_os.SupportSummary}";

        if (!_os.SupportsOverlay)
        {
            OverlayCheck.IsEnabled = false;
            OverlayCheck.ToolTip = "Server Core에는 전체화면 오버레이를 표시할 수 없습니다.";
        }

        PresetCombo.Items.Add(new ComboBoxItem
        {
            Content = "사용자 설정",
            Tag = UpdatePolicyPresets.Custom,
        });
        foreach (var preset in UpdatePolicyPresets.All)
        {
            PresetCombo.Items.Add(new ComboBoxItem { Content = preset.Label, Tag = preset.Id });
        }

        for (var count = 0; count <= 10; count++)
        {
            MaxRebootsCombo.Items.Add(count);
        }

        PresetCombo.SelectionChanged += PresetCombo_SelectionChanged;
        MaxRebootsCombo.SelectionChanged += DetailChanged;
        foreach (var check in new[]
                 {
                     SecurityCheck, CriticalCheck, ImportantCheck, OptionalCheck,
                     DriversCheck, DefinitionsCheck, OverlayCheck, NoRebootCheck,
                 })
        {
            check.Checked += DetailChanged;
            check.Unchecked += DetailChanged;
        }
        MaestroCheck.Checked += (_, _) => ValidationText.Text = "";

        SelectPreset(UpdatePolicyPresets.Standard);
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingPreset) return;
        // 프리셋을 다시 고르면 이전에 띄운 범위 오류 문구를 지운다.
        ValidationText.Text = "";
        if (PresetCombo.SelectedItem is ComboBoxItem { Tag: string presetId })
        {
            // 체크박스 변경 이벤트가 "사용자 설정"으로 되돌리지 않도록 값 적용 동안 잠근다.
            _applyingPreset = true;
            try
            {
                ApplyPresetValues(presetId);
            }
            finally
            {
                _applyingPreset = false;
            }
        }
    }

    private void DetailChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingPreset) return;
        ValidationText.Text = "";
        SelectPreset(UpdatePolicyPresets.Custom, applyValues: false);
    }

    private void SelectPreset(string presetId, bool applyValues = true)
    {
        _applyingPreset = true;
        try
        {
            for (var i = 0; i < PresetCombo.Items.Count; i++)
            {
                if (PresetCombo.Items[i] is ComboBoxItem { Tag: string id } && id == presetId)
                {
                    PresetCombo.SelectedIndex = i;
                    break;
                }
            }

            if (applyValues)
            {
                ApplyPresetValues(presetId);
            }
            else
            {
                UpdatePresetHint(presetId);
            }
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    private void ApplyPresetValues(string presetId)
    {
        var options = ManualUpdateOptions.FromPreset(presetId, _os).LimitTo(_teacherPolicy, _os);
        SecurityCheck.IsChecked = options.IncludeSecurity;
        CriticalCheck.IsChecked = options.IncludeCritical;
        ImportantCheck.IsChecked = options.IncludeImportant;
        OptionalCheck.IsChecked = options.IncludeOptional;
        DriversCheck.IsChecked = options.IncludeDrivers;
        DefinitionsCheck.IsChecked = options.IncludeDefinitionUpdates;
        OverlayCheck.IsChecked = options.OverlayEnabled && _os.SupportsOverlay;
        NoRebootCheck.IsChecked = options.PreventAutomaticReboot;
        MaxRebootsCombo.SelectedItem = options.MaxRebootCount;
        UpdatePresetHint(presetId);
    }

    private void UpdatePresetHint(string presetId)
    {
        PresetHintText.Text = presetId == UpdatePolicyPresets.Custom
            ? "선택한 항목을 직접 조정합니다. 기능 업데이트는 2차 승인 전까지 선택할 수 없습니다."
            : UpdatePolicyPresets.All.FirstOrDefault(p => p.Id == presetId)?.Description ?? "";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MaestroConfirmed)
        {
            ValidationText.Text = "Maestro 복원 보호를 수동 복구/해제로 전환하고 확인해야 합니다.";
            return;
        }

        var selected = new ManualUpdateOptions
        {
            PresetId = (PresetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? UpdatePolicyPresets.Custom,
            IncludeSecurity = SecurityCheck.IsChecked == true,
            IncludeCritical = CriticalCheck.IsChecked == true,
            IncludeImportant = ImportantCheck.IsChecked == true,
            IncludeOptional = OptionalCheck.IsChecked == true,
            IncludeDrivers = DriversCheck.IsChecked == true,
            IncludeDefinitionUpdates = DefinitionsCheck.IsChecked == true,
            OverlayEnabled = OverlayCheck.IsChecked == true && _os.SupportsOverlay,
            PreventAutomaticReboot = NoRebootCheck.IsChecked == true,
            MaxRebootCount = MaxRebootsCombo.SelectedItem is int count ? count : 5,
        }.LimitTo(_teacherPolicy, _os);

        if (!selected.HasAnyUpdateCategory)
        {
            ValidationText.Text = "업데이트 범위를 하나 이상 선택해야 합니다.";
            return;
        }

        Options = selected;
        DialogResult = true;
        Close();
    }
}
