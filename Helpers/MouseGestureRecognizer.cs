using System.Drawing;

namespace MidFD.Helpers;

public sealed class MouseGestureRecognizer
{
    private const int MinimumSegmentDistance = 28;
    private const int MaximumDirectionCount = 3;
    private Point _lastSegmentPoint;
    private readonly List<char> _directions = new();

    public bool IsTracking { get; private set; }
    public string GestureText => new(_directions.ToArray());
    public bool HasGesture => _directions.Count > 0;

    public void Begin(Point point)
    {
        IsTracking = true;
        _lastSegmentPoint = point;
        _directions.Clear();
    }

    public void Update(Point point)
    {
        if (!IsTracking || _directions.Count >= MaximumDirectionCount)
        {
            return;
        }

        int dx = point.X - _lastSegmentPoint.X;
        int dy = point.Y - _lastSegmentPoint.Y;
        bool useHorizontal = Math.Abs(dx) >= Math.Abs(dy);
        int distance = useHorizontal ? Math.Abs(dx) : Math.Abs(dy);
        if (distance < MinimumSegmentDistance)
        {
            return;
        }

        char direction = useHorizontal
            ? (dx < 0 ? 'L' : 'R')
            : (dy < 0 ? 'U' : 'D');

        if (_directions.Count == 0 || _directions[^1] != direction)
        {
            _directions.Add(direction);
        }

        _lastSegmentPoint = point;
    }

    public string End(Point point)
    {
        Update(point);
        string gesture = GestureText;
        Cancel();
        return gesture;
    }

    public void Cancel()
    {
        IsTracking = false;
        _directions.Clear();
        _lastSegmentPoint = Point.Empty;
    }
}
