namespace AudioLatency.Core;

public static class Statistics
{
    public static double? Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0) return null;
        int middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }

    public static double? StandardDeviation(IEnumerable<double> source)
    {
        var values = source.ToArray();
        if (values.Length == 0) return null;
        double mean = values.Average();
        return Math.Sqrt(values.Average(x => (x - mean) * (x - mean)));
    }
}
