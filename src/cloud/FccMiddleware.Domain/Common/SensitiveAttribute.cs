namespace FccMiddleware.Domain.Common;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SensitiveAttribute : Attribute
{
}
