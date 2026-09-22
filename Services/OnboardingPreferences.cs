namespace WinBitTorrent.Services;

public sealed record OnboardingDraft
{
    public int Step { get; init; }
    public string Theme { get; init; } = "Default";
    public string DownloadPath { get; init; } = "";
    public bool? Startup { get; init; }
    public bool Notifications { get; init; } = true;
}

public static class OnboardingPreferences
{
    public static bool IsComplete => ClientSettings.Current.Onboarding.Completed;

    public static OnboardingDraft Load()
    {
        if (ClientSettings.Current.Onboarding.Draft is { } draft)
            return draft with { Step = Math.Clamp(draft.Step, 0, 3) };
        return new OnboardingDraft
        {
            Theme = ClientSettings.Current.Ui.Theme,
            Notifications = ClientSettings.Current.Notifications.Enabled
        };
    }

    public static void SaveDraft(OnboardingDraft draft)
    {
        ClientSettings.Current.Onboarding.Draft = draft;
        ClientSettings.Save();
    }

    // Call only after external settings have been applied and verified.
    public static void Complete(OnboardingDraft draft)
    {
        var settings = ClientSettings.Current;
        settings.Ui.Theme = draft.Theme;
        settings.Notifications.Enabled = draft.Notifications;
        settings.Onboarding.Completed = true;
        settings.Onboarding.Draft = null;
        ClientSettings.Save();
    }
}
