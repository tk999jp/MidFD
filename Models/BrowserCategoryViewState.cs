using System.Collections.Generic;
using System.Linq;
using MidFD.Configuration;

namespace MidFD.Models;

public class BrowserCategoryViewState
{
    private readonly List<BrowserTabCategoryDefinition> _categories = new();

    public IReadOnlyList<BrowserTabCategoryDefinition> Categories => _categories;

    public string? ActiveCategoryId { get; set; }
    public string? ContextCategoryId { get; set; }

    public void Add(BrowserTabCategoryDefinition category)
    {
        _categories.Add(category);
    }

    public void RemoveAt(int index)
    {
        _categories.RemoveAt(index);
    }

    public void Insert(int index, BrowserTabCategoryDefinition category)
    {
        _categories.Insert(index, category);
    }

    public void Clear()
    {
        _categories.Clear();
    }

    public int Count => _categories.Count;

    public bool IsValidIndex(int index)
    {
        return index >= 0 && index < _categories.Count;
    }

    public BrowserTabCategoryDefinition? ActiveCategory =>
        _categories.FirstOrDefault(c => c.Id == ActiveCategoryId);

    public int IndexOf(BrowserTabCategoryDefinition category)
    {
        return _categories.IndexOf(category);
    }

    public int FindIndex(System.Predicate<BrowserTabCategoryDefinition> match)
    {
        return _categories.FindIndex(match);
    }

    public BrowserTabCategoryDefinition? FirstOrDefault(System.Func<BrowserTabCategoryDefinition, bool> predicate)
    {
        return _categories.FirstOrDefault(predicate);
    }

    public BrowserTabCategoryDefinition? FirstOrDefault()
    {
        return _categories.FirstOrDefault();
    }

    public void AddRange(IEnumerable<BrowserTabCategoryDefinition> items)
    {
        _categories.AddRange(items);
    }

    public int RemoveAll(System.Predicate<BrowserTabCategoryDefinition> match)
    {
        return _categories.RemoveAll(match);
    }
}
