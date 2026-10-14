using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.RateLimiting;

namespace Loom.Identity;

public static class IdentityModule
{
    public static readonly string[] ApplicationRoles = ["Admin", "Editor", "Viewer"];
    public static void AddLoomIdentity(this WebApplicationBuilder builder)
    {
        var settings=builder.Configuration.GetSection("Identity");
        var directory=Path.GetFullPath(settings["DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath,"data","identity"));
        var webRoot=Path.GetFullPath(builder.Environment.WebRootPath??Path.Combine(builder.Environment.ContentRootPath,"wwwroot"));
        if(directory.Equals(webRoot,StringComparison.OrdinalIgnoreCase)||directory.StartsWith(webRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Identity data must be outside the web root.");
        Directory.CreateDirectory(directory);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        builder.Services.AddDbContext<IdentityDatabase>(o=>o.UseSqlite("Data Source="+Path.Combine(directory,"accounts.db")+";Pooling=False"));
        builder.Services.AddDataProtection().SetApplicationName(settings["ApplicationName"]??"Loom.Identity")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory,"keys")));
        builder.Services.AddDefaultIdentity<LoomUser>(o=>
        {
            o.User.RequireUniqueEmail=true;o.SignIn.RequireConfirmedEmail=true;o.SignIn.RequireConfirmedAccount=true;
            o.Password.RequiredLength=12;o.Lockout.MaxFailedAccessAttempts=5;
            o.Lockout.DefaultLockoutTimeSpan=TimeSpan.FromMinutes(15);
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<IdentityDatabase>()
            .AddSignInManager<LoomSignInManager>().AddClaimsPrincipalFactory<LoomClaimsFactory>();
        builder.Services.Configure<SecurityStampValidatorOptions>(o=>o.ValidationInterval=TimeSpan.Zero);
        builder.Services.Configure<DataProtectionTokenProviderOptions>(o=>o.TokenLifespan=TimeSpan.FromHours(1));
        builder.Services.ConfigureApplicationCookie(o=>
        {
            o.Cookie.Name="Loom.Identity";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Lax;
            o.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
            o.ExpireTimeSpan=TimeSpan.FromHours(1);o.SlidingExpiration=false;
            o.Events.OnRedirectToAccessDenied=c=> { if(c.Request.Path.StartsWithSegments("/api")||c.Request.Path.StartsWithSegments("/identity-admin/api"))c.Response.StatusCode=403;else c.Response.Redirect(c.RedirectUri);return Task.CompletedTask; };
            o.Events.OnRedirectToLogin=c=> { if(c.Request.Path.StartsWithSegments("/api")||c.Request.Path.StartsWithSegments("/identity-admin/api"))c.Response.StatusCode=401;else c.Response.Redirect(c.RedirectUri);return Task.CompletedTask; };
        });
        builder.Services.AddAntiforgery(o=>o.HeaderName="X-CSRF-TOKEN");
        builder.Services.AddScoped<IdentitySessionRefreshFilter>();
        builder.Services.AddRazorPages().AddMvcOptions(o=>o.Filters.AddService<IdentitySessionRefreshFilter>());builder.Services.AddAuthorization();
        builder.Services.AddTransient<IEmailSender,IdentityEmailSender>();
        builder.Services.AddRateLimiter(o=>o.AddPolicy("identity",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"local",_=>new(){PermitLimit=60,Window=TimeSpan.FromMinutes(1),QueueLimit=0})));
        var authority=settings["Oidc:Authority"];
        if(!string.IsNullOrWhiteSpace(authority))
        {
            builder.Services.AddAuthentication().AddOpenIdConnect("oidc",settings["Oidc:DisplayName"]??"Organization sign-in",o=>
            {
                o.SignInScheme=IdentityConstants.ExternalScheme;o.Authority=authority;
                o.ClientId=settings["Oidc:ClientId"]??throw new InvalidOperationException("Identity:Oidc:ClientId required.");
                o.ClientSecret=settings["Oidc:ClientSecret"];o.ResponseType="code";o.UsePkce=true;
                o.SaveTokens=false;o.GetClaimsFromUserInfoEndpoint=true;o.Scope.Add("email");
            });
        }
        IdentityEmailSender.Validate(builder.Configuration,builder.Environment);
    }
    public static async Task InitializeLoomIdentityAsync(this WebApplication app)
    {
        await using var scope=app.Services.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<IdentityDatabase>();
        // Versioned, immutable EF migrations own this dedicated account database.
        await db.Database.MigrateAsync();
        var roles=scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach(var name in ApplicationRoles)if(!await roles.RoleExistsAsync(name))Ensure(await roles.CreateAsync(new(name)));
    }
    public static async Task CreateAdministratorAsync(this WebApplication app,string email,string password)
    {
        await using var scope=app.Services.CreateAsyncScope();var users=scope.ServiceProvider.GetRequiredService<UserManager<LoomUser>>();
        if((await users.GetUsersInRoleAsync("Admin")).Count!=0)throw new InvalidOperationException("An administrator already exists; use account administration.");
        var db=scope.ServiceProvider.GetRequiredService<IdentityDatabase>();await using var tx=await db.Database.BeginTransactionAsync();
        var user=new LoomUser{UserName=email,Email=email,EmailConfirmed=true};Ensure(await users.CreateAsync(user,password));Ensure(await users.AddToRoleAsync(user,"Admin"));
        db.Audit.Add(new(){Actor="local-bootstrap",Subject=user.Id,Action="bootstrap-admin",At=DateTimeOffset.UtcNow.ToString("O")});await db.SaveChangesAsync();await tx.CommitAsync();
    }
    internal static void Ensure(IdentityResult result)
    {
        if(!result.Succeeded)throw new InvalidOperationException(string.Join(" ",result.Errors.Select(e=>e.Description)));
    }
    public static void UseLoomIdentity(this WebApplication app)
    {
        app.UseAuthorization();
        app.Use(async(c,next)=>
        {
            if(c.Request.Path.StartsWithSegments("/Identity")||c.Request.Path.StartsWithSegments("/identity-admin"))c.Response.Headers.CacheControl="no-store";
            if(!app.Configuration.GetValue<bool>("Identity:AllowRegistration") &&
               (c.Request.Path.Equals("/Identity/Account/Register",StringComparison.OrdinalIgnoreCase)||
                c.Request.Path.Equals("/Identity/Account/ExternalLogin",StringComparison.OrdinalIgnoreCase)&&string.Equals(c.Request.Query["handler"],"Confirmation",StringComparison.OrdinalIgnoreCase)))
            {c.Response.StatusCode=404;return;}
            if(c.User.Identity?.IsAuthenticated==true)
            {
                var users=c.RequestServices.GetRequiredService<UserManager<LoomUser>>();var user=await users.GetUserAsync(c.User);
                if(user==null||user.Disabled){await c.SignOutAsync(IdentityConstants.ApplicationScheme);c.Response.StatusCode=401;return;}
                if(c.Request.Method=="POST" && c.Request.Path.Equals("/Identity/Account/Manage/DeletePersonalData",StringComparison.OrdinalIgnoreCase) && c.User.IsInRole("Admin"))
                {c.Response.StatusCode=403;await c.Response.WriteAsync("Administrators must transfer access and be demoted before deleting their account.");return;}
            }
            await next();
        });
        app.MapRazorPages().RequireRateLimiting("identity");
        IdentityAdministration.Map(app);
    }
}

internal sealed class LoomClaimsFactory(UserManager<LoomUser> users,RoleManager<IdentityRole> roles,IOptions<IdentityOptions> options)
    :UserClaimsPrincipalFactory<LoomUser,IdentityRole>(users,roles,options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(LoomUser user)
    {
        var identity=await base.GenerateClaimsAsync(user);
        if(!identity.HasClaim(c=>c.Type==ClaimTypes.Role))identity.AddClaim(new(ClaimTypes.Role,"Viewer"));
        return identity;
    }
}
internal sealed class LoomSignInManager(UserManager<LoomUser> users,IHttpContextAccessor accessor,IUserClaimsPrincipalFactory<LoomUser> factory,
    IOptions<IdentityOptions> options,ILogger<SignInManager<LoomUser>> logger,IAuthenticationSchemeProvider schemes,IUserConfirmation<LoomUser> confirmation)
    :SignInManager<LoomUser>(users,accessor,factory,options,logger,schemes,confirmation)
{
    public override async Task<bool> CanSignInAsync(LoomUser user)=>!user.Disabled && await base.CanSignInAsync(user);
    public override Task<SignInResult> PasswordSignInAsync(string userName,string password,bool isPersistent,bool lockoutOnFailure)
        =>base.PasswordSignInAsync(userName,password,isPersistent,true);
}
