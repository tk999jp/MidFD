using MidFD.Configuration;
using MidFD.Models;

namespace MidFD.Services;

public static class FunctionKeyProfileService
{
    private static readonly FunctionKeyDefinition[] StandardDefinitions =
    {
        new() { KeyNumber = 1, Action = FunctionKeyAction.Help, Label = "Help" },
        new() { KeyNumber = 2, Action = FunctionKeyAction.Rename, Label = "Ren" },
        new() { KeyNumber = 3, Action = FunctionKeyAction.None, VisibleOnFunctionBar = false },
        new() { KeyNumber = 4, Action = FunctionKeyAction.None, VisibleOnFunctionBar = false },
        new() { KeyNumber = 5, Action = FunctionKeyAction.Reload, Label = "Rld" },
        new() { KeyNumber = 6, Action = FunctionKeyAction.None, VisibleOnFunctionBar = false },
        new() { KeyNumber = 7, Action = FunctionKeyAction.None, VisibleOnFunctionBar = false },
        new() { KeyNumber = 8, Action = FunctionKeyAction.None, VisibleOnFunctionBar = false },
        new() { KeyNumber = 9, Action = FunctionKeyAction.None, VisibleOnFunctionBar = false },
        new() { KeyNumber = 10, Action = FunctionKeyAction.Menu, Label = "Menu" },
        new() { KeyNumber = 11, Action = FunctionKeyAction.Top, Label = "Top" },
        new() { KeyNumber = 12, Action = FunctionKeyAction.Bottom, Label = "Btm" }
    };

    private static readonly FunctionKeyDefinition[] FdCompatibleDefinitions =
    {
        new() { KeyNumber = 1, Action = FunctionKeyAction.Help, Label = "Help" },
        new() { KeyNumber = 2, Action = FunctionKeyAction.Execute, Label = "Check" },
        new() { KeyNumber = 3, Action = FunctionKeyAction.Copy, Label = "Copy" },
        new() { KeyNumber = 4, Action = FunctionKeyAction.Edit, Label = "Edit" },
        new() { KeyNumber = 5, Action = FunctionKeyAction.Rename, Label = "Ren" },
        new() { KeyNumber = 6, Action = FunctionKeyAction.Sort, Label = "Sort" },
        new() { KeyNumber = 7, Action = FunctionKeyAction.Filter, Label = "Filter" },
        new() { KeyNumber = 8, Action = FunctionKeyAction.Tree, Label = "Tree" },
        new() { KeyNumber = 9, Action = FunctionKeyAction.Logdisk, Label = "Logd" },
        new() { KeyNumber = 10, Action = FunctionKeyAction.Unpack, Label = "Unpk" },
        new() { KeyNumber = 11, Action = FunctionKeyAction.Top, Label = "Top" },
        new() { KeyNumber = 12, Action = FunctionKeyAction.Bottom, Label = "Btm" }
    };

    public static FunctionKeyProfile ResolveProfile(string? value)
    {
        return string.Equals(value, InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase)
            ? FunctionKeyProfile.FDCompatible
            : FunctionKeyProfile.Standard;
    }

    public static IReadOnlyList<FunctionKeyDefinition> GetDefinitions(string? profileValue)
    {
        return ResolveProfile(profileValue) == FunctionKeyProfile.FDCompatible
            ? FdCompatibleDefinitions
            : StandardDefinitions;
    }

    public static FunctionKeyAction ResolveAction(string? profileValue, int fKey)
    {
        return ResolveDefinition(profileValue, fKey)?.Action ?? FunctionKeyAction.None;
    }

    public static FunctionKeyDefinition? ResolveDefinition(string? profileValue, int fKey)
    {
        return GetDefinitions(profileValue).FirstOrDefault(def => def.KeyNumber == fKey);
    }

    public static int? ResolveKeyNumber(string? profileValue, FunctionKeyAction action)
    {
        FunctionKeyDefinition? definition = GetDefinitions(profileValue).FirstOrDefault(def => def.Action == action);
        return definition?.KeyNumber;
    }
}
