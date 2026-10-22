using System.Reflection;
using System.Runtime.ExceptionServices;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     An interface whose members answer from one function, for the Umbraco services a test needs a
///     member or two of. A <c>null</c> answer gives the member's default value.
/// </summary>
public class Stub<T> : DispatchProxy
    where T : class
{
    private Func<MethodInfo, object?[], object?> _answer = (_, _) => null;

    public static T Of(Func<MethodInfo, object?[], object?>? answer = null)
    {
        T proxy = Create<T, Stub<T>>();
        ((Stub<T>)(object)proxy)._answer = answer ?? ((_, _) => null);
        return proxy;
    }

    /// <summary>A <typeparamref name="T" /> that calls each member on <paramref name="target" />.</summary>
    public static T Forwarding(object target)
        => Of((method, args) =>
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }
        });

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var result = _answer(method!, args ?? []);
        return result is null && method!.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType)
            : result;
    }
}
