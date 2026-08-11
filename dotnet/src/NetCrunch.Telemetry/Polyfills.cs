#if NETSTANDARD2_0

// The C# compiler lowers `init` accessors and `required` members onto attributes
// it expects to find in the framework. netstandard2.0 predates all of them, so
// they are declared here instead — the compiler is happy with any definition in
// the right namespace, and none of this ships as public surface.
//
// Declaring them is the standard workaround rather than a trick; it is what
// lets the same source file serve both targets without a single #if in it.

namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    /// <summary>Enables <c>init</c> accessors and <c>record</c> types.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }

    /// <summary>Enables the <c>required</c> modifier on members.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>Marks a member as depending on a compiler feature.</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    using System.ComponentModel;

    /// <summary>Tells the compiler a constructor sets all required members.</summary>
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}

#endif
