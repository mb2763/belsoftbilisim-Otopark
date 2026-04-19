namespace Otopark.Core.Session;

public static class UserSession
{
    public static long UserId { get; set; }
    public static long CompanyId { get; set; }
    public static string UserName { get; set; } = "";
    public static bool IsAdmin { get; set; }
}
