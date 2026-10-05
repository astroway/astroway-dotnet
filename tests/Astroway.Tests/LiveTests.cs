using Xunit;

namespace Astroway.Tests;

// Runs only with ASTROWAY_API_KEY set; spends about 31 credits.
public class LiveTests
{
    [Fact]
    public async Task ChartThenDrawEqualsRenderFromBirth()
    {
        var key = Environment.GetEnvironmentVariable("ASTROWAY_API_KEY");
        if (string.IsNullOrEmpty(key)) return;
        using var aw = new AstrowayClient(key);
        var birth = new { date = "1990-07-14", time = "14:30:00", timezoneOffset = 3, latitude = 50.4501, longitude = 30.5234, houseSystem = "P" };

        var chart = await aw.Chart.ComputeAsync(birth);
        Assert.Equal(212, (int)chart.Data.GetProperty("houses").GetProperty("ascendant").GetDouble());

        var drawn = await aw.DrawChartAsync(chart.Data, renderOptions: new { theme = "css" });
        var fromBirth = await aw.Render.WheelWesternAsync(new { birth.date, birth.time, birth.timezoneOffset, birth.latitude, birth.longitude, birth.houseSystem, options = new { theme = "css" } });
        var a = drawn.Data.GetProperty("svg").GetString();
        Assert.False(string.IsNullOrEmpty(a));
        Assert.Equal(fromBirth.Data.GetProperty("svg").GetString(), a);
    }
}
