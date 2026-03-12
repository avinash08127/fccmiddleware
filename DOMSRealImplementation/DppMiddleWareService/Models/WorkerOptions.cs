namespace DPPMiddleware.Models
{
    public class WorkerOptions
    {
        public string Host { get; set; } = "";
        public int[] Ports { get; set; } = Array.Empty<int>();
    }
}
