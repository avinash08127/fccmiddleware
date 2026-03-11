namespace VirtualLab.Infrastructure.Diagnostics;

public sealed class ApiTimingStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, List<double>> _samples = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string routeKey, double elapsedMilliseconds)
    {
        lock (_sync)
        {
            if (!_samples.TryGetValue(routeKey, out List<double>? values))
            {
                values = [];
                _samples[routeKey] = values;
            }

            values.Add(elapsedMilliseconds);

            if (values.Count > 200)
            {
                values.RemoveAt(0);
            }
        }
    }

    public IReadOnlyDictionary<string, double> GetP95ByRoute()
    {
        lock (_sync)
        {
            return _samples.ToDictionary(
                pair => pair.Key,
                pair => Percentile(pair.Value, 0.95));
        }
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] ordered = values.OrderBy(value => value).ToArray();
        int index = Math.Clamp((int)Math.Ceiling((ordered.Length * percentile)) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }
}
