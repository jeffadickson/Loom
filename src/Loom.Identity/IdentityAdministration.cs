using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Loom.Identity;

internal static class IdentityAdministration
{
    public static void Map(WebApplication app)
    {
        var group=app.MapGroup("/identity-admin/api").RequireAuthorization(p=>p.RequireRole("Admin")).RequireRateLimiting("identity");
        group.AddEndpointFilter(async(context,next)=>
        {
            try
            {
                if(context.HttpContext.Request.Method!="GET")await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
                return await next(context);
            }
            catch(AntiforgeryValidationException){return Results.BadRequest(new{error="Refresh the page and try again."});}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        });
        group.MapGet("/token",(HttpContext c,IAntiforgery antiforgery)=>new{token=antiforgery.GetAndStoreTokens(c).RequestToken});
        group.MapGet("/users",async(IdentityDatabase db,int? skip)=>
        {
            var offset=skip??0;if(offset<0)return Results.BadRequest();
            var users=await db.Users.OrderBy(u=>u.Id).Skip(offset).Take(50).Select(u=>new{u.Id,u.Email,u.EmailConfirmed,u.Disabled,u.TwoFactorEnabled}).ToArrayAsync();
            var ids=users.Select(u=>u.Id).ToArray();
            var roles=await (from ur in db.UserRoles join role in db.Roles on ur.RoleId equals role.Id where ids.Contains(ur.UserId) select new{ur.UserId,role.Name}).ToArrayAsync();
            return Results.Ok(new{total=await db.Users.CountAsync(),users=users.Select(u=>new{u.Id,u.Email,u.EmailConfirmed,u.Disabled,u.TwoFactorEnabled,roles=roles.Where(r=>r.UserId==u.Id).Select(r=>r.Name).ToArray()})});
        });
        group.MapGet("/audit",async(IdentityDatabase db)=>await db.Audit.OrderByDescending(a=>a.Id).Take(100).ToArrayAsync());
        group.MapPost("/users",async(CreateAccount request,UserManager<LoomUser> users,IdentityDatabase db,IEmailSender sender,IConfiguration config,HttpContext c)=>
        {
            if(!IdentityModule.ApplicationRoles.Contains(request.Role))return Results.BadRequest(new{error="Unknown role."});
            await using(var tx=await db.Database.BeginTransactionAsync())
            {
                var user=new LoomUser{UserName=request.Email,Email=request.Email};
                IdentityModule.Ensure(await users.CreateAsync(user));IdentityModule.Ensure(await users.AddToRoleAsync(user,request.Role));
                Audit(db,c,user.Id,"create-account:"+request.Role);await db.SaveChangesAsync();await tx.CommitAsync();
                await SendSetup(user,users,sender,config,true);
            }
            return Results.Ok(new{message="Account created. Confirmation and password setup instructions sent."});
        });
        group.MapPut("/users/{id}",async(string id,ChangeAccount request,UserManager<LoomUser> users,IdentityDatabase db,HttpContext c)=>
        {
            if(!IdentityModule.ApplicationRoles.Contains(request.Role))return Results.BadRequest(new{error="Unknown role."});
            await using var tx=await db.Database.BeginTransactionAsync();
            var user=await users.FindByIdAsync(id);if(user==null)return Results.NotFound();
            var roles=await users.GetRolesAsync(user);
            if(roles.Contains("Admin")&&!user.Disabled&&(request.Disabled||request.Role!="Admin")&&(await users.GetUsersInRoleAsync("Admin")).Count(u=>!u.Disabled)<=1)
                return Results.Conflict(new{error="Keep at least one enabled administrator."});
            user.Disabled=request.Disabled;IdentityModule.Ensure(await users.UpdateAsync(user));
            IdentityModule.Ensure(await users.RemoveFromRolesAsync(user,roles));IdentityModule.Ensure(await users.AddToRoleAsync(user,request.Role));
            IdentityModule.Ensure(await users.UpdateSecurityStampAsync(user));
            Audit(db,c,id,$"change-account:{request.Role}:disabled={request.Disabled}");await db.SaveChangesAsync();await tx.CommitAsync();
            return Results.Ok(new{message="Account updated. Existing sessions revoked."});
        });
        group.MapPost("/users/{id}/reset",async(string id,UserManager<LoomUser> users,IdentityDatabase db,IEmailSender sender,IConfiguration config,HttpContext c)=>
        {
            var user=await users.FindByIdAsync(id);if(user==null)return Results.NotFound();
            await SendSetup(user,users,sender,config,!user.EmailConfirmed);
            Audit(db,c,id,"send-password-reset");await db.SaveChangesAsync();return Results.Ok(new{message="Recovery instructions sent."});
        });
    }
    private static void Audit(IdentityDatabase db,HttpContext c,string id,string action)=>db.Audit.Add(new(){Actor=c.User.FindFirstValue(ClaimTypes.NameIdentifier)!,Subject=id,Action=action,At=DateTimeOffset.UtcNow.ToString("O")});
    private static async Task SendSetup(LoomUser user,UserManager<LoomUser> users,IEmailSender sender,IConfiguration config,bool confirm)
    {
        string Token(string token)=>WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var origin=config["Identity:PublicOrigin"]!.TrimEnd('/');
        var reset=QueryHelpers.AddQueryString(origin+"/Identity/Account/ResetPassword","code",Token(await users.GeneratePasswordResetTokenAsync(user)));
        var body="<p>Set or reset your Loom password: <a href=\""+HtmlEncoder.Default.Encode(reset)+"\">Set password</a>.</p>";
        if(confirm)
        {
            var url=QueryHelpers.AddQueryString(origin+"/Identity/Account/ConfirmEmail",new Dictionary<string,string?>{{"userId",user.Id},{"code",Token(await users.GenerateEmailConfirmationTokenAsync(user))}});
            body="<p>First <a href=\""+HtmlEncoder.Default.Encode(url)+"\">confirm your email</a>.</p>"+body;
        }
        await sender.SendEmailAsync(user.Email!,"Your Loom account",body);
    }
    public sealed record CreateAccount(string Email,string Role);
    public sealed record ChangeAccount(string Role,bool Disabled);
}
