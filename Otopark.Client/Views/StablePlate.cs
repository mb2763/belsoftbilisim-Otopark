namespace Otopark.Client.Views
{
    internal sealed class StablePlate
    {
        public string Plate { get; }
        public double Score { get; }

        public StablePlate(string plate, double score)
        {
            Plate = plate;
            Score = score;
        }
    }
}
