namespace Ascent.Web.Infrastructure;

// A serialization constraint, independent of assessment meaning or eligibility.
[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonClockTimeAttribute : Attribute;
