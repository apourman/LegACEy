namespace System.Runtime.CompilerServices;

/// <summary>
/// Records and init properties need this marker type, and GameArt targets netstandard2.0, which does not ship it. The compiler finds it by name.
/// </summary>
internal static class IsExternalInit
{
}
