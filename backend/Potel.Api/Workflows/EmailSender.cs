using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;

namespace Potel.Api.Workflows;

// Eén e-mail naar één adres. FromName is de bedrijfsnaam van de werkruimte, ReplyTo haar eigen e-mailadres.
public record OutgoingEmail(string To, string Subject, string Body, string? FromName = null, string? ReplyTo = null);

public interface IEmailSender
{
    bool Configured { get; }
    Task SendAsync(OutgoingEmail mail, CancellationToken ct = default);
}

public static partial class EmailRules
{
    // Precies één gewoon e-mailadres: geen lijst, geen naam ervoor en geen regeleinden.
    public static bool IsSingleAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && PlainAddress().IsMatch(value)
        && MailAddress.TryCreate(value, out var address) && address.Address == value;

    // Eén regel tekst voor in een kopregel, zoals het onderwerp of de afzendernaam: zonder regeleinden of andere stuurtekens.
    public static string OneLine(string? value, int max = 200)
    {
        var s = ControlChars().Replace(value ?? "", " ").Trim();
        if (s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut].TrimEnd();
    }

    [GeneratedRegex(@"^[^\s@,;:<>()\[\]""\\]+@[^\s@,;:<>()\[\]""\\]+$")]
    private static partial Regex PlainAddress();

    [GeneratedRegex(@"[\p{Cc}\u2028\u2029]+")]
    private static partial Regex ControlChars();
}

// Verstuurt e-mail via de SMTP-server uit appsettings.json (sectie "Smtp").
// Alles gaat van het adres van het platform; de werkruimte staat erin als naam en als antwoordadres.
public class SmtpEmailSender(IConfiguration config) : IEmailSender
{
    readonly IConfigurationSection smtp = config.GetSection("Smtp");

    public bool Configured => !string.IsNullOrWhiteSpace(smtp["Host"]);

    // Een leeg "From" (zoals in appsettings.json) valt terug op de gebruiker van de mailserver.
    public string PlatformAddress =>
        new[] { smtp["From"], smtp["User"] }.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))?.Trim() ?? "noreply@potel.local";

    public MailMessage Compose(OutgoingEmail mail)
    {
        if (!EmailRules.IsSingleAddress(mail.To)) throw new ArgumentException("Geen geldig e-mailadres", nameof(mail));
        var name = EmailRules.OneLine(mail.FromName, 100);
        var message = new MailMessage
        {
            From = new MailAddress(PlatformAddress, name == "" ? null : name, Encoding.UTF8),
            Subject = EmailRules.OneLine(mail.Subject),
            Body = mail.Body,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8,
        };
        message.To.Add(new MailAddress(mail.To));
        if (EmailRules.IsSingleAddress(mail.ReplyTo)) message.ReplyToList.Add(new MailAddress(mail.ReplyTo!));
        return message;
    }

    public async Task SendAsync(OutgoingEmail mail, CancellationToken ct = default)
    {
        using var client = new SmtpClient(smtp["Host"], smtp.GetValue("Port", 587))
        {
            EnableSsl = smtp.GetValue("EnableSsl", true),
            Credentials = string.IsNullOrEmpty(smtp["User"]) ? null : new NetworkCredential(smtp["User"], smtp["Password"]),
        };
        using var message = Compose(mail);
        await client.SendMailAsync(message, ct);
    }
}
