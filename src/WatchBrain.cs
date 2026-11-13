using System.Collections.Generic;

// Watches unlocked folders: once a folder's Explorer window has been
// seen open and then stays closed for two consecutive polls, the
// folder is due to be locked again. Pure logic — unit tested.
public class WatchBrain
{
    private class State
    {
        public bool Seen;
        public int Missing;
    }

    private readonly Dictionary<string, State> _states = new Dictionary<string, State>();
    public const int MissingChecksToLock = 2;

    // entries: (id, normalized path, locked) — returns ids to lock now.
    public List<string> Poll(IEnumerable<WatchItem> items, HashSet<string> openPaths)
    {
        var toLock = new List<string>();
        if (openPaths == null) return toLock;
        foreach (var item in items)
        {
            if (item.Locked)
            {
                _states.Remove(item.Id);
                continue;
            }
            State st;
            if (!_states.TryGetValue(item.Id, out st))
            {
                st = new State();
                _states[item.Id] = st;
            }
            if (openPaths.Contains(item.NormalizedPath))
            {
                st.Seen = true;
                st.Missing = 0;
            }
            else if (st.Seen)
            {
                st.Missing++;
                if (st.Missing >= MissingChecksToLock) toLock.Add(item.Id);
            }
        }
        return toLock;
    }

    public void Reset(string id)
    {
        _states.Remove(id);
    }
}

public class WatchItem
{
    public string Id;
    public string NormalizedPath;
    public bool Locked;

    public WatchItem(string id, string normalizedPath, bool locked)
    {
        Id = id;
        NormalizedPath = normalizedPath;
        Locked = locked;
    }
}
