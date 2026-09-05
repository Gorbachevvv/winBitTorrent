using System.Text.Json.Nodes;
using WinBitTorrent.Services;

namespace WinBitTorrent.Infrastructure.Tests;

[CollectionDefinition("Client settings", DisableParallelization = true)]
public sealed class ClientSettingsCollection;

[Collection("Client settings")]
public sealed class OnboardingPreferencesTests
{
    [Fact]
    public void DraftSurvivesFailedCompletionAndSuccessfulCompletionCommitsTogether()
    {
        var originalRoot = Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "WinBitTorrent-SettingsTests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", root);
        var file = Path.Combine(root, "client-settings.json");
        try
        {
            Assert.False(OnboardingPreferences.IsComplete);
            ClientSettings.SetValue("unrelated", "preserved");
            var draft = new OnboardingDraft { Step = 2, Theme = "Dark", DownloadPath = @"C:\My Downloads", Notifications = false };
            OnboardingPreferences.SaveDraft(draft);
            Assert.Equal(draft, OnboardingPreferences.Load());
            Assert.False(OnboardingPreferences.IsComplete);

            // A destination directory forces the atomic file replacement to fail.
            File.Delete(file);
            Directory.CreateDirectory(file);
            Assert.Throws<UnauthorizedAccessException>(() => OnboardingPreferences.Complete(draft));
            Assert.False(OnboardingPreferences.IsComplete);
            Assert.Equal(draft, OnboardingPreferences.Load());
            Assert.Null(ClientSettings.Get<string>("ui.theme"));

            Directory.Delete(file);
            OnboardingPreferences.Complete(draft);
            var persisted = JsonNode.Parse(File.ReadAllText(file))!;
            Assert.True(persisted[OnboardingPreferences.CompletedKey]!.GetValue<bool>());
            Assert.Null(persisted[OnboardingPreferences.DraftKey]);
            Assert.Equal("Dark", persisted["ui.theme"]!.GetValue<string>());
            Assert.False(persisted["notifications.enabled"]!.GetValue<bool>());
            Assert.Equal("preserved", persisted["unrelated"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", originalRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
