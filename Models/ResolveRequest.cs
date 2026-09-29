namespace MemoryOpsAI.Models
{
    public class ResolveRequest
    {
        public bool Worked { get; set; }
        public string ActualFix { get; set; } = string.Empty;
        public string AiSuggestion { get; set; } = string.Empty;
    }
}
