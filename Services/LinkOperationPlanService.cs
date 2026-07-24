namespace MidFD.Services;

internal enum LinkOperationKind
{
    FileSymbolicLink,
    DirectorySymbolicLink,
    Junction,
    Unsupported
}

internal sealed record LinkOperationPlanItem(
    string SourcePath,
    string DestinationPath,
    LinkOperationKind Kind,
    bool IsTopLevel,
    string TopLevelSourcePath);

internal sealed class LinkOperationPlan
{
    public List<LinkOperationPlanItem> Items { get; } = new();
    public int FileSymbolicLinkCount => Items.Count(item => item.Kind == LinkOperationKind.FileSymbolicLink);
    public int DirectorySymbolicLinkCount => Items.Count(item => item.Kind == LinkOperationKind.DirectorySymbolicLink);
    public int JunctionCount => Items.Count(item => item.Kind == LinkOperationKind.Junction);
    public int UnsupportedCount => Items.Count(item => item.Kind == LinkOperationKind.Unsupported);
}

internal sealed record LinkOperationRoot(string SourcePath, string DestinationPath);

internal static class LinkOperationPlanService
{
    public static LinkOperationPlan BuildCopyPlan(IEnumerable<string> sources, string destinationDirectory)
    {
        return BuildCopyPlan(sources.Select(source => new LinkOperationRoot(
            source,
            Path.Combine(destinationDirectory, Path.GetFileName(source)))));
    }

    public static LinkOperationPlan BuildCopyPlan(IEnumerable<LinkOperationRoot> roots)
    {
        var plan = new LinkOperationPlan();
        foreach (LinkOperationRoot root in roots)
        {
            string source = root.SourcePath;
            string destination = root.DestinationPath;
            if (ReparsePointHelper.IsReparsePoint(source))
            {
                Add(plan, source, destination, isTopLevel: true, topLevelSourcePath: source);
                continue;
            }
            if (!Directory.Exists(source)) continue;

            var pending = new Stack<(string Source, string Destination)>();
            pending.Push((source, destination));
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (string file in Directory.EnumerateFiles(current.Source))
                {
                    string target = Path.Combine(current.Destination, Path.GetFileName(file));
                    if (ReparsePointHelper.IsReparsePoint(file)) Add(plan, file, target, isTopLevel: false, topLevelSourcePath: root.SourcePath);
                }
                foreach (string directory in Directory.EnumerateDirectories(current.Source))
                {
                    string target = Path.Combine(current.Destination, Path.GetFileName(directory));
                    if (ReparsePointHelper.IsReparsePoint(directory))
                    {
                        Add(plan, directory, target, isTopLevel: false, topLevelSourcePath: root.SourcePath);
                    }
                    else
                    {
                        pending.Push((directory, target));
                    }
                }
            }
        }
        return plan;
    }

    private static void Add(LinkOperationPlan plan, string source, string destination, bool isTopLevel, string topLevelSourcePath)
    {
        LinkOperationKind kind = ReparsePointHelper.GetReparseTag(source) switch
        {
            0xA000000C when !ReparsePointHelper.IsDirectory(source) => LinkOperationKind.FileSymbolicLink,
            0xA000000C => LinkOperationKind.DirectorySymbolicLink,
            0xA0000003 => LinkOperationKind.Junction,
            _ => LinkOperationKind.Unsupported
        };
        plan.Items.Add(new LinkOperationPlanItem(source, destination, kind, isTopLevel, topLevelSourcePath));
    }
}
