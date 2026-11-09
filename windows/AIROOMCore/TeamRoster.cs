namespace AIROOMCore;

public enum RosterErrorKind
{
    BuiltIn,
    Unknown,
    InProject,
}

/// <summary>Adding, removing and re-seating members. Every member has their own
/// id; several may run on the same program.</summary>
public static class TeamRoster
{
    /// <summary>Adds a member with an open desk topic.</summary>
    public static void AddMember(this SavedState state, MemberCard card)
    {
        if (state.Members.Any(member => member.Agent == card.Agent)) return;
        state.Members.Add(card.Normalized());
        state.Conversations.Add(new Conversation(card.Agent));
    }

    /// <summary>
    /// Removes an added member and their desk topics. Room and project history
    /// keep the lines they wrote, under the name they had.
    /// </summary>
    public static void RemoveMember(this SavedState state, MemberID agent)
    {
        if (agent.IsBuiltIn)
        {
            throw new NativeCLIError("內建的兩位成員不能刪除，可以改用「隱藏人物卡」或關掉「啟用成員」。");
        }
        var index = state.Members.FindIndex(member => member.Agent == agent);
        if (index < 0) throw new NativeCLIError("找不到這位成員。");
        state.FormerMembers[agent.RawValue] = state.Members[index].Name;
        state.Members.RemoveAt(index);
        state.Conversations.RemoveAll(conversation => conversation.Agent == agent);
    }

    /// <summary>
    /// Moves a member to another program. Their CLI conversations belonged to
    /// the old program, so each topic and room starts a new one next time
    /// (with the full context, as on a first message).
    /// </summary>
    public static void SetExecutor(this SavedState state, Executor executor, MemberID agent)
    {
        var index = state.Members.FindIndex(member => member.Agent == agent);
        if (index < 0 || state.Members[index].Executor == executor) return;
        state.Members[index].Executor = executor;
        foreach (var topic in state.Conversations.Where(topic => topic.Agent == agent))
        {
            topic.SessionID = null;
            topic.SessionThrough = null;
            topic.SessionProfile = null;
            topic.SessionNotes = [];
        }
    }

    /// <summary>A member's name, including members who were removed.</summary>
    public static string MemberName(this SavedState state, MemberID agent) =>
        state.Members.FirstOrDefault(member => member.Agent == agent)?.Name
        ?? state.FormerMembers.GetValueOrDefault(agent.RawValue, agent.RawValue);
}
