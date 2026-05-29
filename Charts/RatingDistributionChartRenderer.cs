using ScottPlot;

namespace StrataReports.Functions.Charts;

public static class RatingDistributionChartRenderer
{
    public static byte[] RenderStarDistribution(IReadOnlyDictionary<int, int> distribution)
    {
        Plot plot = new();
        plot.FigureBackground.Color = Colors.White;

        int[] stars = [1, 2, 3, 4, 5];
        double[] values = stars.Select(s => distribution.TryGetValue(s, out int count) ? (double)count : 0.0).ToArray();
        double[] positions = stars.Select(s => (double)s).ToArray();
        string[] labels = stars.Select(s => $"{s}★").ToArray();

        ScottPlot.Bar[] bars = values.Select((v, i) => new ScottPlot.Bar
        {
            Position = positions[i],
            Value = v,
            FillColor = Color.FromHex("#9BBB59"),
        }).ToArray();

        plot.Add.Bars(bars);
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            positions, labels);
        plot.Axes.Left.Label.Text = "Number of Reviews";
        plot.Title("Star Rating Distribution");
        plot.Layout.Frameless();

        return plot.GetImageBytes(500, 300, ImageFormat.Png);
    }
}
