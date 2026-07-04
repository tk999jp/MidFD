using System;
using System.Threading;

namespace MidFD.Models;

public class PreviewRequestState
{
    public CancellationTokenSource? Cts { get; set; }
    public bool InFlight { get; set; }
    public int RequestId { get; set; }

    public void Cancel()
    {
        Cts?.Cancel();
    }

    public CancellationToken StartNewRequest(out int newRequestId)
    {
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = new CancellationTokenSource();
        RequestId++;
        newRequestId = RequestId;
        InFlight = true;
        return Cts.Token;
    }

    public void EndRequest(int reqId)
    {
        if (RequestId == reqId)
        {
            InFlight = false;
        }
    }
}
