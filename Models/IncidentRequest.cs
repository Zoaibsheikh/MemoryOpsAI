namespace MemoryOpsAI.Models
{
    public class IncidentRequest
    {
        public string Customer { get; set; } = string.Empty;
        public string Module { get; set; } = string.Empty;
        public string Issue { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }
}
