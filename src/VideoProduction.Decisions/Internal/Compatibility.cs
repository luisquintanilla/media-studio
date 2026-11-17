using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace VideoProduction.Decisions.Experimental.Internal;

internal static class DiagnosticIds
{
    internal const string UrlFormat = "https://github.com/dotnet/extensions/issues/7764";
    internal static class Experiments
    {
        internal const string AIDecisions = "VPD001";
    }
}

internal static class Throw
{
    internal static T IfNull<T>([NotNull] T? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return value;
    }

    internal static string IfNullOrWhitespace(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    [DoesNotReturn]
    internal static void ArgumentException(string name, string message) => throw new ArgumentException(message, name);

    [DoesNotReturn]
    internal static void ArgumentOutOfRangeException(string name) => throw new ArgumentOutOfRangeException(name);
}
