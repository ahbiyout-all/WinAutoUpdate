using System.Windows;
using UpdateAgent.LabManager.Desktop.Models;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.LabManager.Desktop.Views;

public partial class StartJobDialog : Window
{
    private readonly IReadOnlyList<string> _agentIds;
    private readonly bool _anyMaestro;

    public StartJobDialog(IReadOnlyList<string> agentIds, bool anyMaestro)
    {
        InitializeComponent();
        _agentIds = agentIds;
        _anyMaestro = anyMaestro;

        TargetCountText.Text = $"{agentIds.Count}대";
        MaestroBlock.Visibility = anyMaestro ? Visibility.Visible : Visibility.Collapsed;
        MaestroConfirmCheck.IsChecked = !anyMaestro;
    }

    /// <summary>확인을 누른 경우에만 채워진다.</summary>
    public StartJobsRequest? Result { get; private set; }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_anyMaestro && MaestroConfirmCheck.IsChecked != true)
        {
            WarningText.Text = "Maestro 복원 보호 해제 확인이 필요합니다.";
            WarningText.Visibility = Visibility.Visible;
            return;
        }

        if (!int.TryParse(MaxRebootsBox.Text, out var maxReboots) || maxReboots < 0)
        {
            WarningText.Text = "최대 재부팅 횟수는 0 이상의 숫자여야 합니다.";
            WarningText.Visibility = Visibility.Visible;
            return;
        }

        var policy = new UpdatePolicy
        {
            IncludeSecurity = SecurityCheck.IsChecked == true,
            IncludeCritical = CriticalCheck.IsChecked == true,
            IncludeImportant = ImportantCheck.IsChecked == true,
            IncludeOptional = OptionalCheck.IsChecked == true,
            IncludeDrivers = DriversCheck.IsChecked == true,
            IncludeDefinitionUpdates = DefinitionsCheck.IsChecked == true,
            IncludeFeatureUpdates = false,
            MaxRebootCount = maxReboots == 0 ? 5 : maxReboots,
            MaxRuntimeHours = 12,
            PauseOnUserLogon = PauseOnUserCheck.IsChecked == true,
            RebootPolicy = RebootNeverCheck.IsChecked == true ? RebootPolicy.Never : RebootPolicy.Auto,
            RebootDelaySeconds = 60,
            ShutdownAfterComplete = false,
            OverlayEnabled = OverlayCheck.IsChecked == true,
            RequireMaestroManualConfirmation = true,
            VerifyRestoreMarkerAfterReboot = true,
        };

        Result = new StartJobsRequest
        {
            AgentIds = _agentIds,
            Policy = policy,
            MaestroConfirmed = true,
            ConfirmedBy = "teacher",
            Note = NoteBox.Text.Trim(),
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
