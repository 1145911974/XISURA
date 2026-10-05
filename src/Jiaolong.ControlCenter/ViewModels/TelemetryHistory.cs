namespace Jiaolong_ControlCenter.ViewModels;

public sealed record TelemetryPoint(int Sequence, DateTimeOffset CapturedAtUtc, double Value);

public sealed class TelemetryHistory
{
    private readonly int capacity;
    private readonly Queue<TelemetryPoint> points = new();

    public TelemetryHistory(int capacity = 60)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public int Count => points.Count;

    public TelemetryPoint this[int index] => points.ElementAt(index);

    public IReadOnlyList<TelemetryPoint> Points => points.ToArray();

    public void Add(TelemetryPoint point)
    {
        points.Enqueue(point);
        while (points.Count > capacity) points.Dequeue();
    }
}
