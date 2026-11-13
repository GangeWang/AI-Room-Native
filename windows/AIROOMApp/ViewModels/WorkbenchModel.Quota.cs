using AIROOMCore;

namespace AIROOMApp.ViewModels;

/// <summary>
/// Account quota refresh, ported from the macOS WorkbenchModel's quota section:
/// per vendor account (never per member card), kept in memory only, and a
/// stale sample never masquerades as an exhausted quota.
/// </summary>
public sealed partial class WorkbenchModel
{
    readonly AccountQuotaReader quotaReader = new();
    readonly HashSet<Executor> quotaRefreshing = [];
    Dictionary<Executor, AccountQuota> accountQuotas = [];
    int quotaVersion;

    /// <summary>Raised when quota samples change or age out of freshness.</summary>
    public int QuotaVersion => quotaVersion;

    public bool IsQuotaRefreshing(Executor executor) => quotaRefreshing.Contains(executor);

    public AccountQuota? QuotaFor(Executor executor) =>
        accountQuotas.TryGetValue(executor, out var quota) ? quota : null;

    /// <summary>The account quota of the program a member runs on.</summary>
    public AccountQuota? QuotaOf(MemberID agent) => QuotaFor(ExecutorOf(agent));

    public void RefreshQuota(Executor agent)
    {
        lock (quotaRefreshing)
        {
            if (quotaRefreshing.Contains(agent)) return;
            quotaRefreshing.Add(agent);
        }
        quotaVersion++;
        OnPropertyChanged(nameof(QuotaVersion));
        var settings = Archive.Settings;
        _ = Task.Run(async () =>
        {
            try
            {
                var latest = await quotaReader.QueryAsync(agent, settings);
                // A user can switch accounts or CLI paths while a probe runs.
                RunOnDispatcher(() =>
                {
                    if (Archive.Settings.ConfiguredPath(agent) == settings.ConfiguredPath(agent))
                    {
                        accountQuotas[agent] = latest;
                    }
                    quotaVersion++;
                    OnPropertyChanged(nameof(QuotaVersion));
                });
            }
            finally
            {
                lock (quotaRefreshing) quotaRefreshing.Remove(agent);
                RunOnDispatcher(() => { quotaVersion++; OnPropertyChanged(nameof(QuotaVersion)); });
            }
        });
    }

    /// <summary>Refreshes every executor that can answer (Claude and Codex).</summary>
    public void RefreshAllQuotas()
    {
        foreach (var executor in ExecutorList.All.Where(executor => executor is Executor.Claude or Executor.Codex))
        {
            RefreshQuota(executor);
        }
    }
}
