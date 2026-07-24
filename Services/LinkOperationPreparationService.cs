using MidFD.FileOperationHelperProtocol;
using MidFD.Dialogs;

namespace MidFD.Services;

internal sealed record LinkOperationPreparationResult(
    LinkOperationPlan Plan,
    HashSet<string> ExcludedSources,
    HashSet<string> SuccessfulSources,
    HashSet<string> SuccessfulTopLevelSources,
    HashSet<string> PartialTopLevelSources,
    int SkipCount,
    int FailCount,
    bool Canceled);

internal static class LinkOperationPreparationService
{
    public static List<string> EnsureDestinationParents(LinkOperationPlan plan)
    {
        var created = new List<string>();
        foreach (string? parent in plan.Items
                     .Select(item => Path.GetDirectoryName(item.DestinationPath))
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var missing = new Stack<string>();
            string? current = parent;
            while (!string.IsNullOrWhiteSpace(current) && !Directory.Exists(current))
            {
                missing.Push(current);
                current = Path.GetDirectoryName(current);
            }
            while (missing.Count > 0)
            {
                string path = missing.Pop();
                Directory.CreateDirectory(path);
                created.Add(path);
            }
        }
        return created;
    }

    public static void CleanupCreatedParents(IEnumerable<string> paths)
    {
        foreach (string path in paths.OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                    Directory.Delete(path, false);
            }
            catch { }
        }
    }

    public static IReadOnlyList<LinkOperationRoot> BuildCrossVolumeMoveRoots(IEnumerable<LinkOperationRoot> roots)
    {
        return roots
            .Where(root => !FileOperationService.HaveSameStorageRoot(root.SourcePath, root.DestinationPath))
            .ToList();
    }

    public static async Task<LinkOperationPreparationResult> PrepareAsync(
        IReadOnlyList<LinkOperationRoot> roots,
        bool allowHelper,
        Func<LinkOperationPlan, LinkOperationDecision> chooseDecision,
        Func<LinkOperationPlan, List<string>> ensureDestinationParents,
        Action<IEnumerable<string>> cleanupCreatedParents,
        Func<IReadOnlyList<ElevatedLinkCopyItem>, CancellationToken, Task<ElevatedLinkCopyResponse>> copyLinksAsync,
        string itemIdPrefix,
        CancellationToken cancellationToken)
    {
        LinkOperationPlan plan = LinkOperationPlanService.BuildCopyPlan(roots);
        return await PreparePlanAsync(
            plan,
            allowHelper,
            chooseDecision,
            ensureDestinationParents,
            cleanupCreatedParents,
            copyLinksAsync,
            itemIdPrefix,
            cancellationToken);
    }

    public static async Task<LinkOperationPreparationResult> PreparePlanAsync(
        LinkOperationPlan plan,
        bool allowHelper,
        Func<LinkOperationPlan, LinkOperationDecision> chooseDecision,
        Func<LinkOperationPlan, List<string>> ensureDestinationParents,
        Action<IEnumerable<string>> cleanupCreatedParents,
        Func<IReadOnlyList<ElevatedLinkCopyItem>, CancellationToken, Task<ElevatedLinkCopyResponse>> copyLinksAsync,
        string itemIdPrefix,
        CancellationToken cancellationToken)
    {
        var excluded = plan.Items.Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var successfulSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var successfulTopLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partialTopLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (plan.Items.Count == 0 || !allowHelper)
        {
            return new LinkOperationPreparationResult(
                plan,
                allowHelper ? excluded : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                successfulSources,
                successfulTopLevel,
                partialTopLevel,
                0,
                0,
                false);
        }

        LinkOperationDecision decision = chooseDecision(plan);
        if (decision == LinkOperationDecision.Cancel)
        {
            return new LinkOperationPreparationResult(plan, excluded, successfulSources, successfulTopLevel, partialTopLevel, 0, 0, true);
        }
        if (decision == LinkOperationDecision.Skip)
        {
            foreach (LinkOperationPlanItem item in plan.Items)
                partialTopLevel.Add(item.TopLevelSourcePath);
            return new LinkOperationPreparationResult(
                plan,
                excluded,
                successfulSources,
                successfulTopLevel,
                partialTopLevel,
                partialTopLevel.Count,
                0,
                false);
        }

        List<string> createdParents = ensureDestinationParents(plan);
        try
        {
            var helperItems = plan.Items.Select((item, index) => new ElevatedLinkCopyItem
            {
                ItemId = $"{itemIdPrefix}-{index}",
                SourcePath = item.SourcePath,
                DestinationPath = item.DestinationPath,
                ExpectedKind = item.Kind.ToString()
            }).ToList();
            ElevatedLinkCopyResponse response = await copyLinksAsync(helperItems, cancellationToken);
            var byId = response.Results.ToDictionary(result => result.ItemId, StringComparer.Ordinal);
            foreach (var pair in plan.Items.Select((item, index) => (item, index)))
            {
                ElevatedLinkCopyResult result = byId[$"{itemIdPrefix}-{pair.index}"];
                if (result.Status == "success")
                {
                    successfulSources.Add(pair.item.SourcePath);
                    if (pair.item.IsTopLevel)
                        successfulTopLevel.Add(pair.item.TopLevelSourcePath);
                }
                if (result.Status != "success")
                    partialTopLevel.Add(pair.item.TopLevelSourcePath);
            }
            return new LinkOperationPreparationResult(plan, excluded, successfulSources, successfulTopLevel, partialTopLevel, 0, partialTopLevel.Count, false);
        }
        catch (ElevatedLinkCopyCanceledException)
        {
            cleanupCreatedParents(createdParents);
            return new LinkOperationPreparationResult(plan, excluded, successfulSources, successfulTopLevel, partialTopLevel, 0, 0, true);
        }
        catch
        {
            cleanupCreatedParents(createdParents);
            foreach (LinkOperationPlanItem item in plan.Items)
                partialTopLevel.Add(item.TopLevelSourcePath);
            return new LinkOperationPreparationResult(plan, excluded, successfulSources, successfulTopLevel, partialTopLevel, 0, partialTopLevel.Count, false);
        }
    }
}
