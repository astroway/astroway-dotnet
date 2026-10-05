// Run with ASTROWAY_API_KEY set: dotnet run --project examples/Quickstart
using System.Globalization;
using System.Text;
using Astroway;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string[] signs = { "Aries", "Taurus", "Gemini", "Cancer", "Leo", "Virgo",
    "Libra", "Scorpio", "Sagittarius", "Capricorn", "Aquarius", "Pisces" };

using var aw = new AstrowayClient(Environment.GetEnvironmentVariable("ASTROWAY_API_KEY")!);

try
{
    var res = await aw.Chart.ComputeAsync(new
    {
        date = "1990-07-14", time = "14:30:00", timezoneOffset = 3,
        latitude = 50.4501, longitude = 30.5234, houseSystem = "P",
    });

    var chart = res.Deserialize<Chart>()!;
    var asc = chart.Houses.Ascendant;
    Console.WriteLine($"ASC: {signs[(int)(asc / 30)]} {asc % 30:F2}°");
    Console.WriteLine($"{chart.Planets[0].Name}: {chart.Planets[0].Longitude:F2}°");

    // The same chart as an SVG wheel, for 1 credit: nothing is computed again.
    var img = await aw.DrawChartAsync(res.Data, renderOptions: new { theme = "css" });
    var svg = img.Data.GetProperty("svg").GetString()!;
    await File.WriteAllTextAsync("wheel.svg", svg);
    Console.WriteLine($"wheel.svg: {Encoding.UTF8.GetByteCount(svg)} bytes");
}
catch (QuotaExceededException)
{
    Console.Error.WriteLine("Out of credits: top up at https://api.astroway.info/pricing/");
}

record Planet(string Name, double Longitude);
record Houses(double Ascendant);
record Chart(Planet[] Planets, Houses Houses);
