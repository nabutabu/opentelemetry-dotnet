public sealed class BoundCounter<T> : IDisposable
    where T : struct
{
    private readonly BoundMeasurementAggregator<T> aggregator;
    
    internal BoundCounter(BoundMeasurementAggregator<T> aggregator);
    
    public void Add(T value)
    {
        this.aggregator.Update(value);
    }
    
    public void Dispose()
    {
        this.aggregator.Dispose();
    }
}