using System.Collections.Concurrent;
using System.Reflection;
using SabhaMujic.Contracts;
using StreamJsonRpc;

namespace SabhaMujic.Client.Rpc;

/// <summary>
/// Client proxy for one contract interface over whichever <see cref="JsonRpc"/> connection is current. Every method must
/// return <see cref="Task"/> or <see cref="Task{T}"/>; a trailing <see cref="CancellationToken"/> is forwarded as JSON-RPC
/// cancellation. All failures surface as <see cref="SabhaMujicException"/>.
/// </summary>
public class RpcProxy : DispatchProxy
{
    private static readonly ConcurrentDictionary<MethodInfo, CallShape> Shapes = new();
    private static readonly MethodInfo InvokeTypedMethod =
        typeof(RpcProxy).GetMethod(nameof(InvokeTypedAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private Func<JsonRpc?> _connection = () => null;
    private Type _api = typeof(object);

    public static T Create<T>(Func<JsonRpc?> connection)
        where T : class
    {
        var proxy = Create<T, RpcProxy>();
        var rpcProxy = (RpcProxy)(object)proxy;
        rpcProxy._connection = connection;
        rpcProxy._api = typeof(T);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var shape = Shapes.GetOrAdd(targetMethod, m => CallShape.For(_api, m));
        args ??= [];
        var ct = shape.HasCancellationToken && args[^1] is CancellationToken token ? token : CancellationToken.None;
        var rpcArgs = shape.HasCancellationToken ? args[..^1] : args;
        return shape.ResultType is null
            ? InvokeVoidAsync(shape, rpcArgs, ct)
            : shape.InvokeTyped.Invoke(this, [shape, rpcArgs, ct]);
    }

    private async Task InvokeVoidAsync(CallShape shape, object?[] args, CancellationToken ct)
    {
        var rpc = Current();
        try
        {
            await rpc.InvokeWithCancellationAsync(shape.RpcName, args, shape.ArgumentTypes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw RpcProtocol.ToSabhaException(ex);
        }
    }

    private async Task<T> InvokeTypedAsync<T>(CallShape shape, object?[] args, CancellationToken ct)
    {
        var rpc = Current();
        try
        {
            return await rpc.InvokeWithCancellationAsync<T>(shape.RpcName, args, shape.ArgumentTypes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw RpcProtocol.ToSabhaException(ex);
        }
    }

    private JsonRpc Current() =>
        _connection() ?? throw new SabhaMujicException(ErrorCode.Unavailable, "Not connected to the SabhaMujic service.");

    private sealed record CallShape(string RpcName, Type[] ArgumentTypes, bool HasCancellationToken, Type? ResultType, MethodInfo InvokeTyped)
    {
        public static CallShape For(Type api, MethodInfo method)
        {
            var parameters = method.GetParameters();
            var hasCt = parameters.Length > 0 && parameters[^1].ParameterType == typeof(CancellationToken);
            var argTypes = (hasCt ? parameters[..^1] : parameters).Select(p => p.ParameterType).ToArray();
            Type? resultType = method.ReturnType == typeof(Task) ? null
                : method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                    ? method.ReturnType.GetGenericArguments()[0]
                    : throw new NotSupportedException($"{api.Name}.{method.Name} must return Task or Task<T>.");
            return new CallShape(RpcProtocol.MethodName(api, method.Name), argTypes, hasCt, resultType,
                resultType is null ? InvokeTypedMethod : InvokeTypedMethod.MakeGenericMethod(resultType));
        }
    }
}
