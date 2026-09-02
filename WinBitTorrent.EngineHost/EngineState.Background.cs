namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private readonly CancellationTokenSource _backgroundLifetime = new();
    private Task? _rssBackgroundWork;
    private Task? _resumeBackgroundWork;
    private Task? _schedulerBackgroundWork;
    private Task? _nativeAlertBackgroundWork;

    private void StartBackgroundServices()
    {
        _rssBackgroundWork = RunRssBackgroundLoopAsync(_backgroundLifetime.Token);
        _resumeBackgroundWork = RunResumeBackgroundLoopAsync(_backgroundLifetime.Token);
        _schedulerBackgroundWork = RunSchedulerBackgroundLoopAsync(_backgroundLifetime.Token);
        _nativeAlertBackgroundWork = RunNativeAlertLoopAsync(_backgroundLifetime.Token);
    }

    private async Task RunNativeAlertLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                // Every native invocation drains libtorrent's alert queue. Keeping that work in
                // EngineHost makes completion, metadata and storage persistence independent of
                // whether the desktop UI happens to be polling at the time.
                InvokeNative("engine.poll", EngineJson.EmptyObject);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                await AppendLogAsync(8, $"Native alert processing failed: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task RunResumeBackgroundLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                InvokeNative(SaveResumeMethod, EngineJson.EmptyObject);
                await CaptureResumeStorageAsync(cancellationToken).ConfigureAwait(false);
                await PersistNativeStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                await AppendLogAsync(8, $"Periodic resume save failed: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task RunRssBackgroundLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
                var minutes = preferences.TryGetProperty("rss_refresh_interval", out var interval) && interval.TryGetInt32(out var parsed)
                    ? Math.Clamp(parsed, 1, 24 * 60) : 30;
                await Task.Delay(TimeSpan.FromMinutes(minutes), cancellationToken).ConfigureAwait(false);
                preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
                if (preferences.TryGetProperty("rss_processing_enabled", out var enabled) && enabled.GetBoolean())
                    await RefreshRssItemAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                await AppendLogAsync(4, $"Background RSS refresh failed: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSchedulerBackgroundLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
                if (preferences.TryGetProperty("scheduler_enabled", out var enabled) && enabled.GetBoolean())
                {
                    var now = DateTime.Now;
                    var from = new TimeSpan(PreferenceInt(preferences, "schedule_from_hour", 8), PreferenceInt(preferences, "schedule_from_min", 0), 0);
                    var to = new TimeSpan(PreferenceInt(preferences, "schedule_to_hour", 20), PreferenceInt(preferences, "schedule_to_min", 0), 0);
                    var inTimeRange = from <= to ? now.TimeOfDay >= from && now.TimeOfDay < to : now.TimeOfDay >= from || now.TimeOfDay < to;
                    var inDayRange = SchedulerDayMatches(PreferenceInt(preferences, "scheduler_days", 0), now.DayOfWeek);
                    InvokeNative(WinBitTorrent.Core.EngineProtocol.EngineRpcMethods.TransferSetAlternativeLimits,
                        EngineJson.Element(new { enabled = inTimeRange && inDayRange }));
                }
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                await AppendLogAsync(4, $"Alternative speed scheduler failed: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static int PreferenceInt(System.Text.Json.JsonElement preferences, string name, int fallback)
        => preferences.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static bool SchedulerDayMatches(int mode, DayOfWeek day) => mode switch
    {
        0 => true,
        1 => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
        2 => day is DayOfWeek.Saturday or DayOfWeek.Sunday,
        >= 3 and <= 9 => day == (DayOfWeek)((mode - 2) % 7),
        _ => true
    };

    private async Task StopBackgroundServicesAsync()
    {
        _backgroundLifetime.Cancel();
        if (_rssBackgroundWork is not null)
        {
            try { await _rssBackgroundWork.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (_resumeBackgroundWork is not null)
        {
            try { await _resumeBackgroundWork.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (_schedulerBackgroundWork is not null)
        {
            try { await _schedulerBackgroundWork.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (_nativeAlertBackgroundWork is not null)
        {
            try { await _nativeAlertBackgroundWork.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await StopSearchServicesAsync().ConfigureAwait(false);
        await StopCreatorServicesAsync().ConfigureAwait(false);
        _backgroundLifetime.Dispose();
    }
}
