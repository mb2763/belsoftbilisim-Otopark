namespace Otopark.Core.Session;

public static class UserSession
{
    public static long UserId { get; set; }
    public static long CompanyId { get; set; }
    public static string UserName { get; set; } = "";
    public static bool IsAdmin { get; set; }

    /// <summary>
    /// MISAFIR ARAC ISARETLEME YETKISI (24.08.2026).
    /// Web'de "Misafir Arac Isaretleme" (MenuType 68) izni verilen kullanicilarda true.
    /// Yoneticilerde her zaman true. Giriste bir kez cekilir; yetki servisine
    /// ulasilamazsa FALSE kalir (dugme gizli) - yetki isteyen bir ozellik icin
    /// dogru varsayilan budur.
    /// </summary>
    public static bool CanMarkGuestVehicle { get; set; }
}
