using Otopark.Core.Services;

namespace Otopark.Client.Services;

public static class AppConfigHelper
{
    public static int BolgeId =>
        int.Parse(AppConfig.Configuration["Parking:BolgeId"]);
}
