namespace WinBitTorrent.Services;

/// <summary>
/// Local update preferences shared by the settings window and update prompts.
/// </summary>
public static class UpdatePreferences
{
    public const string CheckOnStartupKey = "updates.checkOnStartup";

    public static bool CheckOnStartup
    {
        get => ClientSettings.Get(CheckOnStartupKey, true);
        set => ClientSettings.SetValue(CheckOnStartupKey, value);
    }
}
