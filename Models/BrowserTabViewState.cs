using System.Collections.Generic;

namespace MidFD.Models;

public class BrowserTabViewState
{
    private readonly List<BrowserTabState> _tabs = new();

    public IReadOnlyList<BrowserTabState> Tabs => _tabs;

    public int ActiveTabIndex { get; set; } = -1;
    public int ContextTabIndex { get; set; } = -1;

    public void Add(BrowserTabState tab)
    {
        _tabs.Add(tab);
    }

    public void RemoveAt(int index)
    {
        _tabs.RemoveAt(index);
    }

    public void Insert(int index, BrowserTabState tab)
    {
        _tabs.Insert(index, tab);
    }

    public void Clear()
    {
        _tabs.Clear();
    }

    public int Count => _tabs.Count;

    public bool IsValidIndex(int index)
    {
        return index >= 0 && index < _tabs.Count;
    }

    public BrowserTabState? ActiveTab => IsValidIndex(ActiveTabIndex) ? _tabs[ActiveTabIndex] : null;

    public BrowserTabState? ContextTab => IsValidIndex(ContextTabIndex) ? _tabs[ContextTabIndex] : null;

    public int IndexOf(BrowserTabState tab)
    {
        return _tabs.IndexOf(tab);
    }

    public void AddRange(IEnumerable<BrowserTabState> items)
    {
        _tabs.AddRange(items);
    }

    public int RemoveAll(System.Predicate<BrowserTabState> match)
    {
        return _tabs.RemoveAll(match);
    }
}
