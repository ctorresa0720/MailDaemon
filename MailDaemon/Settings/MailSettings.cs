namespace MailDaemon.Settings
{
    public class MailSettings
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public bool EnableSsl { get; set; }

        public string FromEmail { get; set; } = "";
        public string FromName { get; set; } = "";
    }
}
