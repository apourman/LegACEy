namespace System.Runtime.CompilerServices;

/// <summary>
/// Records need this marker type, and Demo targets netstandard2.0, which does not ship it. The compiler finds it by name, so it is
/// declared here once for the appraisal records.
/// </summary>
internal static class IsExternalInit
{
}
