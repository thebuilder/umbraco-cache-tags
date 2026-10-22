using System.Reflection;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     A stand-in for an interface: each call goes to the handler, by member name (<c>get_Level</c>
///     for a property). A call the handler answers with <c>null</c> returns the default value.
/// </summary>
public class Fake<T> : DispatchProxy
    where T : class
{
    private Func<MethodInfo, object?[], object?> _handler = (_, _) => null;

    public static T Of(Func<MethodInfo, object?[], object?>? handler = null)
    {
        T proxy = Create<T, Fake<T>>();
        ((Fake<T>)(object)proxy)._handler = handler ?? ((_, _) => null);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var result = _handler(method!, args ?? []);
        if (result is null && method!.ReturnType.IsValueType && method.ReturnType != typeof(void))
        {
            return Activator.CreateInstance(method.ReturnType);
        }

        return result;
    }
}
