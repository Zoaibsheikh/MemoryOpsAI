namespace MemoryOpsAI.Models
{
    public class AgentResponse
    {
        public int IncidentId { get; set; }
        public string RecommendedFix { get; set; } = string.Empty;
        public string Reasoning { get; set; } = string.Empty;
        public List<string> SimilarPastIncidents { get; set; } = new();
        public string Status { get; set; } = "Logged"; // Logged -> Analyzed -> Resolved
    }
}
