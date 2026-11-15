using System.Reflection;
using System.Text.Json;
using SabhaMujic.Contracts;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace SabhaMujic.Client.Rpc;

/// <summary>
/// The JSON-RPC wire protocol shared by the service and the desktop client:
/// header-delimited framing (with a size guard), System.Text.Json with web defaults,
/// method names <c>"&lt;Interface&gt;.&lt;Method&gt;"</c> (the same keys as <c>OperationMap</c>), and
/// <see cref="SabhaMujicException"/> carried as an error with <see cref="ErrorInfo"/> data.
/// </summary>
public static class RpcProtocol
{
    /// <summary>JSON-RPC error code for application errors; <c>error.data</c> is an <see cref="ErrorInfo"/>.</summary>
    public const int ApplicationErrorCode = -32000;

    public const string InternalErrorMessage = "An internal error occurred in the SabhaMujic service.";

    /// <summary>The five client-facing contract interfaces, in a fixed order.</summary>
    public static IReadOnlyList<Type> ApiInterfaces { get; } =
        [typeof(IAuthApi), typeof(ILibraryApi), typeof(IPlaybackApi), typeof(IPlaylistApi), typeof(IAdminApi)];

    public static string MethodPrefix(Type api) => api.Name + ".";

    public static string MethodName(Type api, string method) => MethodPrefix(api) + method;

    public static SystemTextJsonFormatter CreateFormatter() =>
        new() { JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    /// <summary>Creates a header-delimited handler whose inbound side rejects frames above <paramref name="maxMessageBytes"/>.</summary>
    public static IJsonRpcMessageHandler CreateMessageHandler(Stream duplexStream, int maxMessageBytes) =>
        new HeaderDelimitedMessageHandler(duplexStream, new FramingGuardStream(duplexStream, maxMessageBytes), CreateFormatter());

    /// <summary>Converts a failed remote call into the exception clients are expected to handle.</summary>
    public static SabhaMujicException ToSabhaException(Exception exception) => exception switch
    {
        SabhaMujicException s => s,
        RemoteInvocationException { DeserializedErrorData: ErrorInfo info } => new SabhaMujicException(info.Code, info.Message),
        RemoteMethodNotFoundException => new SabhaMujicException(ErrorCode.IncompatibleClient, "The service does not support this operation."),
        RemoteInvocationException { ErrorCode: (int)JsonRpcErrorCode.InvalidParams } =>
            new SabhaMujicException(ErrorCode.InvalidArgument, "The request arguments were not valid."),
        ConnectionLostException or ObjectDisposedException or IOException =>
            new SabhaMujicException(ErrorCode.Unavailable, "The connection to the SabhaMujic service was lost."),
        _ => new SabhaMujicException(ErrorCode.Internal, InternalErrorMessage),
    };

    internal static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: { } inner })
        {
            exception = inner;
        }

        return exception is AggregateException { InnerExceptions.Count: 1 } agg ? Unwrap(agg.InnerExceptions[0]) : exception;
    }
}

/// <summary>
/// <see cref="JsonRpc"/> with SabhaMujic error mapping: <see cref="SabhaMujicException"/> becomes an application error carrying
/// <see cref="ErrorInfo"/>; any other exception becomes <see cref="ErrorCode.Internal"/> with a fixed message, so no type
/// names, messages or stack traces leak to the other side.
/// </summary>
public class SabhaJsonRpc : JsonRpc
{
    public SabhaJsonRpc(IJsonRpcMessageHandler messageHandler)
        : base(messageHandler)
    {
    }

    /// <summary>Called for unexpected (non-<see cref="SabhaMujicException"/>) exceptions thrown by local RPC methods, for logging.</summary>
    public Action<Exception>? UnexpectedException { get; set; }

    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        var ex = RpcProtocol.Unwrap(exception);
        if (ex is OperationCanceledException)
        {
            return new JsonRpcError.ErrorDetail { Code = JsonRpcErrorCode.RequestCanceled, Message = "The request was canceled." };
        }

        ErrorInfo info;
        if (ex is SabhaMujicException sabha)
        {
            info = sabha.ToErrorInfo();
        }
        else
        {
            UnexpectedException?.Invoke(ex);
            info = new ErrorInfo(ErrorCode.Internal, RpcProtocol.InternalErrorMessage);
        }

        return new JsonRpcError.ErrorDetail { Code = (JsonRpcErrorCode)RpcProtocol.ApplicationErrorCode, Message = info.Message, Data = info };
    }

    protected override Type? GetErrorDetailsDataType(JsonRpcError error) =>
        error.Error?.Code == (JsonRpcErrorCode)RpcProtocol.ApplicationErrorCode ? typeof(ErrorInfo) : null;
}
