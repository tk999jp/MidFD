using System;
using System.Linq;
using MidFD.Commands;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// External tool definition persistence owned by the application layer.
/// </summary>
internal sealed class ExternalToolDefinitionsApplicationCoordinator
{
    private readonly Func<ExternalToolCommandStore, bool> _save;

    public ExternalToolDefinitionsApplicationCoordinator()
        : this(ExternalToolCommandStorage.Save)
    {
    }

    internal ExternalToolDefinitionsApplicationCoordinator(Func<ExternalToolCommandStore, bool> save)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
    }

    public bool Save(ExternalToolDefinitionsSaveCommandContext? context)
    {
        if (context?.Store is not { } source)
        {
            return false;
        }

        var store = new ExternalToolCommandStore
        {
            SchemaVersion = source.SchemaVersion,
            LoadFailed = source.LoadFailed,
            Tools = source.Tools.Select(CloneDefinition).ToList()
        };

        return _save(store);
    }

    private static ExternalToolCommandDefinition CloneDefinition(ExternalToolCommandDefinition source) => new()
    {
        Id = source.Id,
        DisplayName = source.DisplayName,
        Description = source.Description,
        Alias = source.Alias,
        AltSlot = source.AltSlot,
        ExecutablePath = source.ExecutablePath,
        Arguments = source.Arguments,
        WorkingDirectory = source.WorkingDirectory,
        Enabled = source.Enabled
    };
}
