using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Otopark.Core.Models
{
    public sealed class ParkingVm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class VehicleRowVm
    {
        public string Plate { get; set; } = "";
        public string ParkingName { get; set; } = "";
        public string DurationText { get; set; } = "";
        public string ParkType { get; set; } = "";
        public DateTime EntryDateTime { get; set; }
        public DateTime? ExitDateTime { get; set; }

        public decimal OldDebt { get; set; }
        public decimal CurrentDebt { get; set; }
        public decimal TotalDebt { get; set; }

        //public ImageSource? EntryPlateImage { get; set; }
        //public ImageSource? ExitPlateImage { get; set; }
    }

}
