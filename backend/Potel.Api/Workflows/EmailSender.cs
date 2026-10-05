using System.Net;
using System.Net.Mail;

namespace Potel.Api.Workflows;

public interface IEmailSender
{
    bool Configured { get; }
    Task SendAsync(string to, string subject, string body);
}

// Verstuurt e-mail via de SMTP-server uit appsettings.json (sectie "Smtp").
public class SmtpEmailSender(IConfiguration config) : IEmailSender
{
    readonly IConfigurationSection smtp = config.GetSection("Smtp");

    public bool Configured => !string.IsNullOrWhiteSpace(smtp["Host"]);

    public async Task SendAsync(string to, string subject, string body)
    {
        using var client = new SmtpClient(smtp["Host"], smtp.GetValue("Port", 587))
        {
            EnableSsl = smtp.GetValue("EnableSsl", true),
            Credentials = string.IsNullOrEmpty(smtp["User"]) ? null : new NetworkCredential(smtp["User"], smtp["Password"]),
        };
        using var message = new MailMessage(smtp["From"] ?? smtp["User"] ?? "noreply@potel.local", to, subject, body);
        await client.SendMailAsync(message);
    }
}
