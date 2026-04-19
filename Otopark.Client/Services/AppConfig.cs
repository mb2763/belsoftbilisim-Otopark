using Microsoft.Extensions.Configuration;
using System.IO;

namespace Otopark.Core.Services;

public static class AppConfig
{
    public static IConfigurationRoot Configuration { get; private set; }

    static AppConfig()
    {
        Configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
    }
}
