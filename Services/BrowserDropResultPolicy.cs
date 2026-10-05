namespace MidFD.Services;

internal enum BrowserDropItemClassification
{
    Success,
    Skip,
    Fail,
    Cancel,
    NoOp
}

internal readonly record struct BrowserDropItemResult(
    BrowserDropItemClassification Classification,
    bool Partial,
    int NestedSkipCount,
    int NestedFailCount);

internal readonly record struct BrowserDropCounters(
    int SuccessCount = 0,
    int SkipCount = 0,
    int FailCount = 0,
    int CancelCount = 0,
    int NoOpCount = 0,
    int PartialSkipCount = 0,
    int PartialCancelCount = 0,
    int PartialFailCount = 0,
    int NestedSkipCount = 0,
    int NestedFailCount = 0)
{
    public BrowserDropCounters Add(BrowserDropItemResult item)
    {
        return item.Classification switch
        {
            BrowserDropItemClassification.Success => this with { SuccessCount = SuccessCount + 1 },
            BrowserDropItemClassification.Skip => this with
            {
                SkipCount = SkipCount + 1,
                PartialSkipCount = PartialSkipCount + (item.Partial ? 1 : 0)
            },
            BrowserDropItemClassification.Fail => this with
            {
                FailCount = FailCount + 1,
                PartialFailCount = PartialFailCount + (item.Partial ? 1 : 0)
            },
            BrowserDropItemClassification.Cancel => this with
            {
                CancelCount = CancelCount + 1,
                PartialCancelCount = PartialCancelCount + (item.Partial ? 1 : 0)
            },
            BrowserDropItemClassification.NoOp => this with { NoOpCount = NoOpCount + 1 },
            _ => this
        } with
        {
            NestedSkipCount = NestedSkipCount + item.NestedSkipCount,
            NestedFailCount = NestedFailCount + item.NestedFailCount
        };
    }
}

internal static class BrowserDropResultPolicy
{
    public static BrowserDropItemResult ClassifyDirectoryMergeResult(
        int successCount,
        int skipCount,
        int failCount,
        bool canceled)
    {
        if (canceled)
        {
            return new BrowserDropItemResult(
                BrowserDropItemClassification.Cancel,
                successCount > 0,
                skipCount,
                failCount);
        }

        if (failCount > 0)
        {
            return new BrowserDropItemResult(
                BrowserDropItemClassification.Fail,
                successCount > 0,
                skipCount,
                failCount);
        }

        if (successCount > 0 && skipCount > 0)
        {
            return new BrowserDropItemResult(
                BrowserDropItemClassification.Skip,
                true,
                skipCount,
                0);
        }

        if (skipCount > 0)
        {
            return new BrowserDropItemResult(
                BrowserDropItemClassification.Skip,
                false,
                skipCount,
                0);
        }

        return new BrowserDropItemResult(
            BrowserDropItemClassification.Success,
            false,
            0,
            0);
    }

    public static BrowserDropItemClassification ClassifyBrowserDropTypeMismatch(
        bool sourceIsDirectory,
        bool destinationIsDirectory) =>
        sourceIsDirectory != destinationIsDirectory
            ? BrowserDropItemClassification.Fail
            : BrowserDropItemClassification.Success;
}
