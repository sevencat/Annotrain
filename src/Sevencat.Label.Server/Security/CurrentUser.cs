namespace Sevencat.Label.Server.Security;

/// <summary>当前请求的登录用户（由 <see cref="TokenAuthMiddleware"/> 写入）。</summary>
public static class CurrentUser
{
    private const string ItemKey = "annotrain.userId";

    public static void Set(HttpContext context, long userId) => context.Items[ItemKey] = userId;

    public static long? Get(HttpContext context)
        => context.Items.TryGetValue(ItemKey, out object? value) && value is long userId ? userId : null;

    public static long Require(HttpContext context)
        => Get(context) ?? throw new UnauthorizedAccessException("当前请求没有登录用户");
}
