using AIROOMCore;

namespace AIROOMApp.ViewModels;

/// <summary>
/// Password export/import for moving AIROOM to another machine. CLI sign-ins
/// and API keys are never part of the file; program paths and the work folder
/// from the file are dropped on import, and schedules and idle chat arrive
/// switched off so nothing fires unattended on the new machine.
/// </summary>
public sealed partial class WorkbenchModel
{
    /// <summary>Importing replaces everything, so it waits for a fully idle app.</summary>
    public bool CanRestore =>
        StorageAvailable && !flowRunning && !roomRunning && BusyAgents.Count == 0 && !liveActive;

    public bool ExportPortable(string path, string password)
    {
        if (Persistence is null) return false;
        try
        {
            File.WriteAllBytes(path, Persistence.ExportPortable(password));
            StatusMessage = $"匯出完成: {path}";
            return true;
        }
        catch (Exception error)
        {
            StatusMessage = $"匯出失敗: {error.Message}";
            return false;
        }
    }

    /// <summary>Decrypts an export to look at; nothing changes.</summary>
    public PortableExport OpenPortable(string path, string password)
    {
        if (Persistence is null) throw WorkspaceError.Missing("%APPDATA%\\AIROOMNative");
        return Persistence.OpenPortable(path, password);
    }

    public void ImportPortable(PortableExport export)
    {
        try
        {
            if (Persistence is null || !CanRestore) return;
            var incoming = Persistence.ImportPortable(export);
            Adopt(incoming);
            StatusMessage = "匯入完成，原本的資料已先備份。請在這台電腦重新登入各 CLI。";
        }
        catch (Exception error)
        {
            StatusMessage = $"匯入失敗: {error.Message}";
        }
    }

    /// <summary>Replaces the live state with the imported one: schedules and idle
    /// chat arrive off (they were paused for the import), the desk starts clean.</summary>
    void Adopt(SavedState incoming)
    {
        var restored = incoming;
        var paused = (restored.AutoChat?.Enabled ?? false)
            || (restored.Schedules ?? []).Any(schedule => schedule.Enabled);
        if (restored.AutoChat is { } chat) chat.Enabled = false;
        foreach (var schedule in restored.Schedules ?? []) schedule.Enabled = false;
        Archive = restored;
        if ((Archive.Rooms ?? []).Count == 0) Archive.Rooms = [new TeamRoom()];
        selectedRoomID = Archive.Rooms![0].Id;
        ApplyMemberSettings();
        Persist();
        RunOnDispatcher(() =>
        {
            OnPropertyChanged(nameof(TopicsVersion));
            OnPropertyChanged(nameof(CallUsageText));
        });
        BumpRooms();
        BumpAutomation();
        BumpFlows();
        RefreshCLIStatuses();
        StatusMessage = paused ? "排程與成員閒聊已暫停，需要時到「排程與閒聊」重新打開。" : StatusMessage ?? "";
    }
}
