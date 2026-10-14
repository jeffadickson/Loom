using Microsoft.AspNetCore.Mvc.Filters;

namespace Loom.Identity;

/// <summary>
/// Commits built-in account mutations and their queued emails together. Sign-in and
/// MFA pages are deliberately excluded: failed-login counters must persist independently.
/// </summary>
internal sealed class IdentityEmailTransactionFilter(IdentityDatabase db) : IAsyncPageFilter
{
    private static readonly HashSet<string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Register", "/Account/ExternalLogin", "/Account/ForgotPassword",
        "/Account/ResendEmailConfirmation", "/Account/Manage/Email"
    };

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (context.HttpContext.Request.Method != "POST" ||
            !string.Equals(context.RouteData.Values["area"]?.ToString(), "Identity", StringComparison.OrdinalIgnoreCase) ||
            !Pages.Contains(context.RouteData.Values["page"]?.ToString() ?? ""))
        {
            await next();
            return;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(context.HttpContext.RequestAborted);
        var result = await next();
        if (result.Exception == null)
            await transaction.CommitAsync(context.HttpContext.RequestAborted);
        // Dispose rolls back on handler failure, even when an outer exception filter handles it.
    }
}
