namespace LTAI.Tests;

internal static class PolicyExtensions
{
    public static T Apply<T>(this T obj, Action<T> action)
    {
        action(obj);
        return obj;
    }
}
