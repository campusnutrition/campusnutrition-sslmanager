using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SendGrid.Helpers.Mail;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebAppSSLManager.Models;

namespace WebAppSSLManager
{
    public static class WebAppSSLManager
    {
        private static ILogger _logger;

        //Gets its trigger schedule from the WebAppSSLManager configuration setting or environment variable
        [FunctionName("WebAppSSLManager")]
        public static async Task Run([TimerTrigger("%WebAppSSLManager-Trigger%"
#if DEBUG
            , RunOnStartup=true
#endif
            )]TimerInfo myTimer,
            [SendGrid(ApiKey = "SendGridKey")] IAsyncCollector<SendGridMessage> messageCollector,
            ILogger logger)
        {
            _logger = logger;
            logger.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            Settings.Init(logger);

            MailHelper.Init(logger, messageCollector);
            await MailHelper.SendEmailForActivityStartedAsync();

            AzureHelper.Init(logger);

            var details = new List<(string hostname, string message)>();
            var appProperties = await BuildAppPropertiesListAsync();

            int certsCreated = 0;

            var CertUpdatedMessage    = "Certificate successfully updated";
            var CertNotUpdatedMessage = "Certificate not updated";

            if (appProperties != null && appProperties.Any())
            {
                await CertificatesHelper.InitAsync(logger, Settings.UseStaging ? CertificateMode.Staging : CertificateMode.Production);

                foreach (var appProperty in appProperties)
                {
                    AzureHelper.InitAppProperty(appProperty);
                    CertificatesHelper.InitAppProperty(appProperty);

                    try
                    {                        
                        //Request certificate and install it if all is ok
                        if (await AzureHelper.NeedsNewCertificateAsync() && await CertificatesHelper.GetCertificateAsync())
                        {
                            await AzureHelper.AddCertificateAsync();
                            certsCreated++;
                            details.Add((hostname: appProperty.Hostname, message: CertUpdatedMessage));
                        }
                        else
                        {
                            details.Add((hostname: appProperty.Hostname, message: CertNotUpdatedMessage));
                        }
                    }
                    catch (Exception ex)
                    {
                        var message = $"Unable to complete the processing for {appProperty.Hostname}";
                        logger.LogError(ex, message);
                        details.Add((hostname: appProperty.Hostname, message: ex.Message));
                    }

                    if(Settings.BatchSize > 0 && certsCreated >= Settings.BatchSize)
                    {
                        logger.LogInformation($"Maximum number of certificates ({Settings.BatchSize}) generated this run - exiting.");
                        break;
                    }
                }
            }

            AzureHelper.Dispose();
            await MailHelper.SendEmailForActivityCompletedAsync(details);
        }

        private static async Task<IEnumerable<AppProperty>> BuildAppPropertiesListAsync()
        {
            try
            {
                var appPropertiesStr = await AzureHelper.ReadFileFromBlobStorageToStringAsync(Constants.AppPropertiesFileName);
                var appProps = JsonConvert.DeserializeObject<List<AppProperty>>(appPropertiesStr);
                return appProps;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while building/deserializing App Properties list. Cannot proceed.");
                await MailHelper.SendEmailForErrorAsync(ex, "Error while building/deserializing App Properties list. Cannot proceed.");
                return null;
            }
        }
    }
}
