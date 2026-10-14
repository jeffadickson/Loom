using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
namespace Loom.Identity;

public sealed class IdentityEmailSender(IConfiguration configuration,IHostEnvironment environment):IEmailSender
{
    public static void Validate(IConfiguration configuration,IHostEnvironment environment)
    {
        var origin=configuration["Identity:PublicOrigin"];
        if(!Uri.TryCreate(origin,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https") || uri.AbsolutePath!="/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))throw new InvalidOperationException("Identity:PublicOrigin must be a fixed HTTP(S) origin.");
        if(!environment.IsDevelopment()&&uri.Scheme!="https")throw new InvalidOperationException("Identity requires HTTPS outside Development.");
        var pickup=configuration["Identity:Email:PickupDirectory"];
        if(pickup!=null)
        {
            var webRoot=Path.GetFullPath((environment as Microsoft.AspNetCore.Hosting.IWebHostEnvironment)?.WebRootPath??Path.Combine(environment.ContentRootPath,"wwwroot"));
            var path=Path.GetFullPath(pickup);
            if(path.Equals(webRoot,StringComparison.OrdinalIgnoreCase)||path.StartsWith(webRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Email pickup must be outside the web root.");
        }
        if(pickup!=null&&!environment.IsDevelopment())throw new InvalidOperationException("Email pickup is allowed only in Development.");
        if(pickup==null&&(string.IsNullOrWhiteSpace(configuration["Identity:Email:SmtpHost"])||string.IsNullOrWhiteSpace(configuration["Identity:Email:From"])))throw new InvalidOperationException("Configure an email sender or explicit Development pickup directory.");
    }
    public async Task SendEmailAsync(string email,string subject,string htmlMessage)
    {
        Validate(configuration,environment);
        var origin=new Uri(configuration["Identity:PublicOrigin"]!);
        // Identity UI constructs links from the request. Pin their origin before delivery.
        var body=Regex.Replace(htmlMessage,"href=([\"'])(.*?)\\1",match=>
        {
            var url=WebUtility.HtmlDecode(match.Groups[2].Value);
            if(!Uri.TryCreate(url,UriKind.Absolute,out var parsed))return match.Value;
            return "href=\""+WebUtility.HtmlEncode(new Uri(origin,parsed.PathAndQuery).AbsoluteUri)+"\"";
        });
        var pickup=configuration["Identity:Email:PickupDirectory"];
        if(pickup!=null)
        {
            Directory.CreateDirectory(pickup);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(pickup,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var file=Path.Combine(pickup,Guid.NewGuid().ToString("N")+".json");
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write};
            if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            await using var output=new FileStream(file,options);
            await JsonSerializer.SerializeAsync(output,new{to=email,subject,html=body});return;
        }
        using var message=new MailMessage(configuration["Identity:Email:From"]!,email,subject,body){IsBodyHtml=true};
        using var smtp=new SmtpClient(configuration["Identity:Email:SmtpHost"]!,configuration.GetValue("Identity:Email:Port",587)){EnableSsl=true};
        if(configuration["Identity:Email:Username"] is {} username)smtp.Credentials=new NetworkCredential(username,configuration["Identity:Email:Password"]);
        await smtp.SendMailAsync(message);
    }
}
