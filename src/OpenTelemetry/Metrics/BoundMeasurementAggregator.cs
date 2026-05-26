internal sealed class BoundMeasurementAggregator<T> : IDisposable
    where T : struct
{
    private readonly AggregatorStore aggregatorStore;
    private readonly int metricPointIndex;      // -1 means no-op
    private readonly ExemplarFilter exemplarFilter;
    private int disposed;                        // 0 = live, 1 = disposed
    
    internal BoundMeasurementAggregator(
        AggregatorStore store, 
        int index,
        ExemplarFilter filter)
    {
        this.aggregatorStore = store;
        this.metricPointIndex = index;
        this.exemplarFilter = filter;
    }
    
    internal void Update(T value)
    {
        if (this.disposed == 1 || this.metricPointIndex == -1)
            return;   // no-op: disposed or cardinality overflow at bind time
        
        // Exemplar decision happens here at measurement time, not at bind time
        if (this.exemplarFilter.ShouldSample(value))
        {
            var exemplar = BuildExemplarFromCurrentContext(value);
            this.aggregatorStore.UpdateBoundWithExemplar(
                this.metricPointIndex, value, exemplar);
        }
        else
        {
            this.aggregatorStore.UpdateBound(this.metricPointIndex, value);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 0)
        {
            this.aggregatorStore.ReleaseLookup(this.metricPointIndex);
        }
    }
}