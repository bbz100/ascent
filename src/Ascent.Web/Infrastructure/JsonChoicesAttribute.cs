namespace Ascent.Web.Infrastructure;

[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonChoicesAttribute(params string[] values) : Attribute
{
    public string[] Values { get; } = values;
}
