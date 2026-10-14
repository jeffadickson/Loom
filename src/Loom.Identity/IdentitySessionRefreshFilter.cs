using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Loom.Identity;

// Identity's built-in MFA management pages rotate the security stamp without
// renewing the current cookie. With per-request validation, renew that cookie
// after successful management actions so recovery-code redirects remain usable.
// Enrollment also initializes the authenticator key on GET, rotating the stamp.
// Other sessions keep their old stamp and are revoked on their next request.
internal sealed class IdentitySessionRefreshFilter(
    UserManager<LoomUser> users, SignInManager<LoomUser> signIn) : IAsyncPageFilter
{
    private static readonly HashSet<string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Manage/EnableAuthenticator",
        "/Account/Manage/ResetAuthenticator",
        "/Account/Manage/Disable2fa",
        "/Account/Manage/GenerateRecoveryCodes"
    };

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var result = await next();
        if ((context.HttpContext.Request.Method != "POST" &&
             !string.Equals(context.RouteData.Values["page"]?.ToString(), "/Account/Manage/EnableAuthenticator", StringComparison.OrdinalIgnoreCase)) ||
            context.HttpContext.User.Identity?.IsAuthenticated != true ||
            !string.Equals(context.RouteData.Values["area"]?.ToString(), "Identity", StringComparison.OrdinalIgnoreCase) ||
            !Pages.Contains(context.RouteData.Values["page"]?.ToString() ?? "") ||
            result.Exception != null || !context.ModelState.IsValid ||
            (result.Result is not RedirectToPageResult && result.Result is not PageResult))
            return;
        var user = await users.GetUserAsync(context.HttpContext.User);
        if (user is { Disabled: false })
            await signIn.RefreshSignInAsync(user);
    }
}
