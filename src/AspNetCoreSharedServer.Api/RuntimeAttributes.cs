#if !NET
namespace System.Runtime.Versioning;

[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum |
    AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field |
    AttributeTargets.Event | AttributeTargets.Interface | AttributeTargets.Delegate, AllowMultiple = true, Inherited = false)]
internal sealed class SupportedOSPlatformAttribute : Attribute
{
    public SupportedOSPlatformAttribute(string platformName) => PlatformName = platformName;
    public string PlatformName { get; }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
internal sealed class SupportedOSPlatformGuardAttribute : Attribute
{
    public SupportedOSPlatformGuardAttribute(string platformName) => PlatformName = platformName;
    public string PlatformName { get; }
}
#endif