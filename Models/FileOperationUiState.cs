using System.Threading;

namespace MidFD.Models;

public class FileOperationUiState
{
    public CancellationTokenSource? Cts { get; set; }
    public long CancelRequestedTimestamp { get; set; }
    public string? ActiveOperationName { get; set; }
    public int StatusVersion { get; set; }

    public bool IsBusy => Cts != null || !string.IsNullOrWhiteSpace(ActiveOperationName);

    public bool IsCancelRequested => Cts?.IsCancellationRequested ?? false;

    public void Reset()
    {
        Cts?.Dispose();
        Cts = null;
        ActiveOperationName = null;
        StatusVersion++;
        CancelRequestedTimestamp = 0;
    }
}
