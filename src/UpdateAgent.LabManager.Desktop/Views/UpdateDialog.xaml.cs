using System.Windows;
using UpdateAgent.LabManager.Desktop.ViewModels;
using UpdateAgent.Shared.Updates;

namespace UpdateAgent.LabManager.Desktop.Views;

public partial class UpdateDialog : Window
{
    private readonly AppUpdateInfo _info;

    public UpdateDialog(AppUpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        CurrentVersionText.Text = $"v{info.CurrentVersion}";
        LatestVersionText.Text = $"v{info.LatestVersion}";

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.ReleaseName)) meta.Add(info.ReleaseName);
        var published = UiFormatDate(info.PublishedAt);
        if (!string.IsNullOrEmpty(published)) meta.Add($"{published} 배포");
        if (!string.IsNullOrWhiteSpace(info.AssetName))
        {
            var size = UiFormat.Bytes(info.AssetSizeBytes);
            meta.Add(string.IsNullOrEmpty(size) ? info.AssetName : $"{info.AssetName} · {size}");
        }

        MetaText.Text = string.Join(" · ", meta);
        NotesText.Text = (info.ReleaseNotes ?? "").Trim();
        if (string.IsNullOrWhiteSpace(NotesText.Text))
        {
            NotesText.Text = "릴리스 노트가 제공되지 않았습니다.";
        }

        DownloadButton.Content = string.IsNullOrWhiteSpace(info.AssetUrl) ? "릴리스 열기" : "업데이트 내려받기";
    }

    private static string UiFormatDate(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private void Repository_Click(object sender, RoutedEventArgs e) => ExternalLinks.Open(_info.RepositoryUrl);

    private void Release_Click(object sender, RoutedEventArgs e) => ExternalLinks.Open(_info.ReleaseUrl);

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        ExternalLinks.Open(string.IsNullOrWhiteSpace(_info.AssetUrl) ? _info.ReleaseUrl : _info.AssetUrl);
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
