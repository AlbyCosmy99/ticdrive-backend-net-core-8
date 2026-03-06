using System.Net.Http.Headers;
using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using TicDrive.Context;

namespace TicDrive.Services
{
    public interface IEmailService
    {
        Task SendEmailAsync(string to, string subject, string body);
        bool IsEmailConfirmed(string? email);
        string GetRegistrationMailConfirmation();
    }

    public class EmailService : IEmailService
    {
        private readonly string _senderEmail;
        private readonly string _resendApiKey;
        private readonly string _smtpPassword;
        private readonly string _smtpHost;
        private readonly int _smtpPort;
        private readonly string _mailProvider;
        private readonly HttpClient _httpClient;
        private readonly TicDriveDbContext _dbContext;

        public EmailService(
            IConfiguration config,
            TicDriveDbContext dbContext,
            IHttpClientFactory httpClientFactory)
        {
            _dbContext = dbContext;
            _senderEmail = config["EmailSettings:SenderEmail"] ?? string.Empty;
            _resendApiKey = config["EmailSettings:ResendApiKey"] ?? string.Empty;
            _smtpPassword = config["EmailSettings:SmtpPassword"] ?? string.Empty;
            _smtpHost = config["EmailSettings:SmtpHost"] ?? "smtp.gmail.com";
            _smtpPort = int.TryParse(config["EmailSettings:SmtpPort"], out var port) ? port : 465;

            var configured = (config["EmailSettings:MailProvider"] ?? string.Empty).ToLower();
            _mailProvider = configured switch
            {
                "smtp" => "smtp",
                "resend" => "resend",
                _ => !string.IsNullOrWhiteSpace(_smtpPassword) ? "smtp" : "resend"
            };

            _httpClient = httpClientFactory.CreateClient(nameof(EmailService));
            _httpClient.BaseAddress = new Uri("https://api.resend.com/");
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public async Task SendEmailAsync(string to, string subject, string body)
        {
            if (_mailProvider == "smtp")
            {
                await SendViaSmtpAsync(to, subject, body);
            }
            else
            {
                await SendViaResendAsync(to, subject, body);
            }
        }

        private async Task SendViaSmtpAsync(string to, string subject, string body)
        {
            if (string.IsNullOrWhiteSpace(_senderEmail) || string.IsNullOrWhiteSpace(_smtpPassword))
            {
                throw new InvalidOperationException("Missing EmailSettings:SenderEmail or EmailSettings:SmtpPassword configuration.");
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("TicDrive", _senderEmail));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new TextPart("html") { Text = body };

            using var client = new SmtpClient();
            await client.ConnectAsync(_smtpHost, _smtpPort, SecureSocketOptions.SslOnConnect);
            await client.AuthenticateAsync(_senderEmail, _smtpPassword);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }

        private async Task SendViaResendAsync(string to, string subject, string body)
        {
            if (string.IsNullOrWhiteSpace(_senderEmail))
            {
                throw new InvalidOperationException("Missing EmailSettings:SenderEmail configuration.");
            }

            if (string.IsNullOrWhiteSpace(_resendApiKey))
            {
                throw new InvalidOperationException("Missing EmailSettings:ResendApiKey configuration.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "emails");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _resendApiKey);
            request.Content = JsonContent.Create(new
            {
                from = $"TicDrive <{_senderEmail}>",
                to = new[] { to },
                subject,
                html = body
            });

            try
            {
                using var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Resend rejected the email request with status {(int)response.StatusCode}: {responseBody}");
            }
            catch (TaskCanceledException ex)
            {
                throw new InvalidOperationException("Timed out while sending email with Resend.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException("Unable to reach Resend while sending email.", ex);
            }
        }

        public bool IsEmailConfirmed(string? email)
        {
            return _dbContext.Users.FirstOrDefault(u => u.Email == email)?.EmailConfirmed ?? false;
        }

        public string GetRegistrationMailConfirmation()
        {
            return @"<!DOCTYPE html>
                <html lang=""it"">
                <head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
                <title>Conferma la tua registrazione</title>
                <style>
                    body {{ font-family: 'Segoe UI', Arial, sans-serif; background-color: #f4f4f4; margin: 0; padding: 0; color: #333; }}
                    .email-container {{ max-width: 600px; margin: 40px auto; background-color: #fff; border-radius: 8px; box-shadow: 0 4px 10px rgba(0,0,0,0.1); }}
                    .email-header {{ background-color: #00BF63; padding: 24px; text-align: center; }}
                    .email-header h1 {{ color: #fff; margin: 0; font-size: 26px; }}
                    .email-body {{ padding: 24px; }}
                    .email-body p {{ font-size: 16px; line-height: 1.6; margin: 0 0 16px; }}
                    .confirm-button {{ display: inline-block; padding: 14px 28px; background-color: #00BF63; color: #fff !important;
                        text-decoration: none; border-radius: 6px; font-weight: 600; font-size: 16px; transition: background-color 0.3s ease; }}
                    .confirm-button:hover {{ background-color: #00994e; text-decoration: none; }}
                    .email-footer {{ background-color: #f4f4f4; padding: 16px; text-align: center; font-size: 13px; color: #888; }}
                    a {{ color: #00BF63; word-break: break-word; }} a:hover {{ text-decoration: underline; color: #00994e; }}
                </style>
                </head>
                <body>
                <div class=""email-container"">
                    <div class=""email-header""><h1>TicDrive</h1></div>
                    <div class=""email-body"">
                        <p>Ciao,</p>
                        <p>Grazie per esserti registrato su <strong>TicDrive</strong>. Per completare la registrazione, conferma il tuo indirizzo email cliccando sul pulsante qui sotto:</p>
                        <p style=""text-align: center;"">
                            <a class=""confirm-button"" href=""{0}"" target=""_blank"">Conferma Email</a>
                        </p>
                        <p>Se il pulsante non funziona, copia e incolla questo link nel tuo browser:</p>
                        <p><a href=""{0}"" target=""_blank"">{0}</a></p>
                        <p>Grazie per aver scelto TicDrive!</p>
                        <p>Il team di TicDrive</p>
                    </div>
                    <div class=""email-footer"">&copy; 2024 TicDrive. Tutti i diritti riservati.</div>
                </div>
                </body>
                </html>";
        }

    }
}
