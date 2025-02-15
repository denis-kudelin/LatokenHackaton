namespace LatokenHackaton.ASL
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Parameter)]
    internal sealed class AslDescriptionAttribute : Attribute
    {
        public string Description { get; }
        public string? Format { get; }

        public AslDescriptionAttribute(string description)
        {
            Description = description;
        }

        public AslDescriptionAttribute(string description, string format)
        {
            Description = description;
            Format = format;
        }
    }
}

