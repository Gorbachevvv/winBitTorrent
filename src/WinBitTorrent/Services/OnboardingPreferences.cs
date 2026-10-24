using System.Text.Json;

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
    public const string CompletedKey = "onboarding.completed";
    public const string DraftKey = "onboarding.draft";
    public static bool IsComplete => ClientSettings.Get(CompletedKey, false);

    public static OnboardingDraft Load()
    {
        try
        {
            if (ClientSettings.Get<string>(DraftKey) is { } json
                && JsonSerializer.Deserialize<OnboardingDraft>(json) is { } draft)
                return draft with { Step = Math.Clamp(draft.Step, 0, 3) };
        }
        catch (JsonException) { }
        return new OnboardingDraft
        {
            Theme = ClientSettings.Get("ui.theme", "Default")!,
            Notifications = ClientSettings.Get("notifications.enabled", true)
        };
    }

    public static void SaveDraft(OnboardingDraft draft)
        => ClientSettings.SetValue(DraftKey, JsonSerializer.Serialize(draft));

    // Call only after external settings have been applied and verified.
    public static void Complete(OnboardingDraft draft)
        => ClientSettings.SetValues(new Dictionary<string, object?>
        {
            ["ui.theme"] = draft.Theme,
            ["notifications.enabled"] = draft.Notifications,
            [CompletedKey] = true,
            [DraftKey] = null
        });
}
