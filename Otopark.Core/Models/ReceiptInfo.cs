namespace Otopark.Core.Models;

public class ReceiptInfo
{
    public string ReceiptNo { get; set; } = "";
    public DateTime DateTime { get; set; } = System.DateTime.Now;
    public string Plate { get; set; } = "";
    public string ZoneName { get; set; } = "";
    public DateTime EntryDateTime { get; set; }
    public DateTime? ExitDateTime { get; set; }
    public decimal Fee { get; set; }
    public decimal OldDebt { get; set; }
    public string OperatorName { get; set; } = "";
}
