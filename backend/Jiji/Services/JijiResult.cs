namespace RabbitIC.Api.Jiji.Services;

public enum JijiResultKind { Ok, NotFound, Invalid, Conflict, Forbidden, Upstream }

/// <summary>A service answer the endpoint maps to a status: 200, 404, 422, 409, 403, 502.</summary>
public sealed record JijiResult<T>(JijiResultKind Kind, T? Value, string? Error, IReadOnlyList<string>? Issues = null)
{
    public static JijiResult<T> Ok(T value) => new(JijiResultKind.Ok, value, null);
    public static JijiResult<T> Fail(JijiResultKind kind, string error, IReadOnlyList<string>? issues = null) => new(kind, default, error, issues);
    public static JijiResult<T> NotFound() => new(JijiResultKind.NotFound, default, "No such item.");
}
