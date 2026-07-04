using System.Threading;
using MidFD.Models;

namespace MidFD.Coordinators;

public class PreviewRequestCoordinator
{
    private readonly PreviewRequestState _state = new();

    public CancellationToken Token => _state.Cts?.Token ?? CancellationToken.None;

    public int CurrentRequestId => _state.RequestId;

    public bool IsInFlight => _state.InFlight;

    public void Cancel()
    {
        _state.Cancel();
    }

    public CancellationToken StartNewRequest(out int newRequestId)
    {
        return _state.StartNewRequest(out newRequestId);
    }

    public void EndRequest(int reqId)
    {
        _state.EndRequest(reqId);
    }

    public bool IsCurrentRequest(int reqId)
    {
        return _state.RequestId == reqId;
    }
}
